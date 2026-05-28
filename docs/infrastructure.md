# Infrastructure Pulumi

## Sommaire

- [Structure du code](#structure-du-code)
- [Configuration — Pulumi.dev.yaml](#configuration--pulumidevyaml)
- [Secrets K8s](#secrets-k8s)
- [Bases de données — StatefulSet](#bases-de-données--statefulset)
- [HPA — HorizontalPodAutoscaler](#hpa--horizontalpodautoscaler)
- [Ressources CPU / RAM](#ressources-cpu--ram)
- [Modifier la configuration sans redéployer](#modifier-la-configuration-sans-redéployer)

---

## Structure du code

```
infra/Ecommerce.Infra/
├── EcommerceStack.cs              # Point d'entrée — orchestre toutes les ressources
├── Pulumi.dev.yaml                # Configuration de l'environnement dev
├── Pulumi.yaml                    # Métadonnées du projet Pulumi
└── Resources/
    ├── HpaArgs.cs                 # DTO partagé pour la config HPA
    ├── SecretsResources.cs        # K8s Secrets (credentials DB + RabbitMQ)
    ├── DatabaseResources.cs       # StatefulSet PostgreSQL × 2 + Services
    ├── MessagingResources.cs      # Deployment RabbitMQ + Service
    ├── OrderServiceResources.cs   # Deployment + Service + HPA order-api
    ├── InventoryServiceResources.cs
    └── GatewayResources.cs        # Deployment + Service NodePort + HPA gateway
```

### Ordre de création (dépendances)

```
SecretsResources
    ↓ (DependsOn)
DatabaseResources  MessagingResources
    ↓
OrderServiceResources  InventoryServiceResources  GatewayResources
```

---

## Configuration — Pulumi.dev.yaml

Toute la configuration de l'environnement est centralisée dans `infra/Ecommerce.Infra/Pulumi.dev.yaml`.  
Le format des clés est `namespace:clé` — chaque namespace correspond à un `new Config("namespace")` dans le code C#.

```yaml
config:
  # Images Docker (Podman préfixe les images locales avec localhost/)
  orderApi:image: localhost/ecommerce/order-api:dev
  inventoryApi:image: localhost/ecommerce/inventory-api:dev
  gateway:image: localhost/ecommerce/gateway:dev

  # Port NodePort exposé sur Kind
  gateway:nodePort: "30080"

  # Réservation de stock
  reservation:ttlMinutes: "10"
  reservation:checkIntervalSeconds: "30"

  # Nombre de replicas fixes (ignoré si HPA activé)
  replicas:orderApi: "1"
  replicas:inventoryApi: "1"
  replicas:gateway: "1"
  replicas:db: "1"
  replicas:rabbitmq: "1"

  # Ressources CPU/RAM
  resources:orderApiCpuRequest: "100m"
  resources:orderApiCpuLimit: "500m"
  resources:orderApiMemoryRequest: "128Mi"
  resources:orderApiMemoryLimit: "256Mi"
  # ... (voir fichier complet pour inventory et gateway)

  # HPA
  hpa:orderApiEnabled: "true"
  hpa:orderApiMin: "1"
  hpa:orderApiMax: "4"
  hpa:orderApiCpu: "70"
  # ...

  # Secrets (identifiants DB et RabbitMQ)
  secrets:orderDbUser: postgres
  secrets:orderDbName: order_db
  # ⚠️ Mots de passe : utiliser --secret pour chiffrer
  # pulumi config set --secret secrets:orderDbPassword <mot_de_passe>
```

### Lire un namespace dans le code

```csharp
// Lit "orderApi:image" dans Pulumi.dev.yaml
var orderApiCfg = new Config("orderApi");
var image = orderApiCfg.Get("image") ?? "localhost/ecommerce/order-api:dev";

// Lit "hpa:orderApiEnabled"
var hpaCfg = new Config("hpa");
var hpaEnabled = hpaCfg.GetBoolean("orderApiEnabled") ?? false;
```

---

## Secrets K8s

Les secrets sont créés comme des **K8s Secrets natifs** par `SecretsResources`.  
Chaque pod consomme les secrets via `envFrom.secretRef` — aucune valeur en clair dans les manifests.

### Secrets créés

| Nom K8s | Clés injectées | Consommateurs |
|---|---|---|
| `order-db-credentials` | `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB`, `ConnectionStrings__OrderDb` | order-db, order-api |
| `inventory-db-credentials` | `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB`, `ConnectionStrings__InventoryDb` | inventory-db, inventory-api |
| `rabbitmq-credentials` | `RABBITMQ_DEFAULT_USER`, `RABBITMQ_DEFAULT_PASS`, `RabbitMQ__Username`, `RabbitMQ__Password` | rabbitmq, order-api, inventory-api |

### Vérifier les secrets

```bash
kubectl get secrets -n ecommerce
kubectl get secret order-db-credentials -n ecommerce -o jsonpath='{.data.POSTGRES_USER}' | base64 -d
```

### Définir des mots de passe chiffrés (production)

```bash
cd infra/Ecommerce.Infra
pulumi config set --secret secrets:orderDbPassword     <mot_de_passe>
pulumi config set --secret secrets:inventoryDbPassword <mot_de_passe>
pulumi config set --secret secrets:rabbitmqPassword    <mot_de_passe>
```

Les valeurs sont chiffrées dans `Pulumi.dev.yaml` (format `secure: <encrypted>`).

### Migration vers ESO (External Secrets Operator) en production

Pour la production, remplacer les K8s Secrets natifs par ESO pointant vers AWS/Azure/GCP :
1. Installer ESO via Helm (`chart: external-secrets`)
2. Créer un `ClusterSecretStore` avec le provider cloud
3. Remplacer chaque `Secret` par un `ExternalSecret` — les noms (`order-db-credentials`, etc.) restent identiques, les pods ne changent pas.

---

## Bases de données — StatefulSet

PostgreSQL utilise un **StatefulSet** (et non un Deployment) pour garantir :
- Un seul pod actif à la fois (`order-db-0`)
- Un PVC stable et lié au pod (`data-order-db-0`)
- Un arrêt ordonné avant redémarrage (évite la corruption WAL)

### Ressources créées par DB

```
order-db-headless    Service ClusterIP: None  (requis par le StatefulSet pour les DNS pods)
order-db             Service ClusterIP        (utilisé par order-api)
order-db             StatefulSet              (pod: order-db-0)
data-order-db-0      PersistentVolumeClaim    (1 Gi, géré par le StatefulSet)
```

### Hook preStop — arrêt gracieux

Avant que Kubernetes envoie `SIGTERM`, le hook force un checkpoint PostgreSQL :

```bash
pg_ctl stop -D "$PGDATA" -m fast || true
```

`TerminationGracePeriodSeconds: 60` laisse le temps au checkpoint de se terminer.

### Modifier la taille du PVC

`VolumeClaimTemplates` est immuable dans un StatefulSet. Pour changer la taille :

```bash
# 1. Supprimer le StatefulSet sans supprimer les pods ni les PVCs
kubectl delete sts order-db -n ecommerce --cascade=orphan

# 2. Modifier la taille dans DatabaseResources.cs
# 3. Redéployer — Pulumi recrée le StatefulSet avec les nouveaux PVCs
pulumi up --yes
```

---

## HPA — HorizontalPodAutoscaler

Le HPA scale automatiquement les pods en fonction du CPU (et optionnellement de la mémoire).

**Prérequis** : [Metrics Server installé](kubernetes.md#metrics-server-hpa).

### Configuration dans Pulumi.dev.yaml

```yaml
hpa:orderApiEnabled: "true"   # activer/désactiver
hpa:orderApiMin: "1"          # replicas minimum
hpa:orderApiMax: "4"          # replicas maximum
hpa:orderApiCpu: "70"         # seuil CPU en % avant scale-out
# hpa:orderApiMemory: "80"    # seuil mémoire (optionnel)
```

### Comportement avec Pulumi

Quand le HPA est actif, Pulumi ignore les changements sur `spec.replicas` du Deployment  
(via `IgnoreChanges = ["spec.replicas"]`) pour ne pas écraser la décision du HPA.

### Vérifier

```bash
kubectl get hpa -n ecommerce
# TARGETS : 3%/70%  → en dessous du seuil, pas de scale-out
# TARGETS : 75%/70% → scale-out en cours
```

### Désactiver le HPA temporairement

```yaml
# Pulumi.dev.yaml
hpa:orderApiEnabled: "false"
```

```bash
pulumi up --yes
```

---

## Ressources CPU / RAM

Définies dans `Pulumi.dev.yaml`, appliquées à chaque container via `resources.requests` et `resources.limits`.

| Clé | Valeur par défaut | Description |
|---|---|---|
| `resources:orderApiCpuRequest` | `100m` | CPU garanti par le scheduler |
| `resources:orderApiCpuLimit` | `500m` | Plafond CPU (throttle si dépassé) |
| `resources:orderApiMemoryRequest` | `128Mi` | RAM garantie |
| `resources:orderApiMemoryLimit` | `256Mi` | Plafond RAM (OOMKill si dépassé) |

> `100m` = 0.1 vCPU. Format mémoire : `Mi` (mébioctets), `Gi` (gibioctets).

**Pourquoi c'est obligatoire** : le HPA ne peut pas calculer le pourcentage d'utilisation sans `requests.cpu` défini.

### Ajuster pour une machine avec peu de ressources

```yaml
resources:orderApiCpuRequest: "50m"
resources:orderApiCpuLimit: "200m"
resources:orderApiMemoryRequest: "64Mi"
resources:orderApiMemoryLimit: "128Mi"
```

---

## Modifier la configuration sans redéployer

### Changer une valeur de config

```bash
cd infra/Ecommerce.Infra

# Ex : augmenter le TTL de réservation à 20 min
pulumi config set reservation:ttlMinutes 20

pulumi up --yes
```

### Scaler manuellement (HPA désactivé)

```bash
kubectl scale deployment order-api --replicas=2 -n ecommerce
```

Si le HPA est activé, il reprendra le contrôle au prochain cycle.
