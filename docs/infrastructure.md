# Infrastructure Pulumi

## Sommaire

- [Structure du code](#structure-du-code)
- [Configuration — Pulumi.dev.yaml](#configuration--pulumidevyaml)
- [Secrets K8s](#secrets-k8s)
- [Bases de données — CNPG Cluster + Pooler](#bases-de-données--cnpg-cluster--pooler)
- [Cache Redis](#cache-redis)
- [HPA — HorizontalPodAutoscaler](#hpa--horizontalpodautoscaler)
- [KEDA — Scaling réactif (inventory-api)](#keda--scaling-réactif-inventory-api)
- [Mode presale](#mode-presale)
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
    ├── CnpgResources.cs           # Helm cloudnative-pg operator (namespace cnpg-system)
    ├── DatabaseResources.cs       # Cluster CNPG × 2 + Pooler × 2 + postgres_exporter
    ├── MessagingResources.cs      # Deployment RabbitMQ + Service
    ├── CacheResources.cs          # Deployment Redis + Service
    ├── KedaResources.cs           # Helm KEDA + Secret AMQP + ScaledObject inventory-api
    ├── ObservabilityResources.cs  # OTel Collector, Jaeger, Prometheus, Grafana, dashboards
    ├── OrderServiceResources.cs   # Deployment + Service + HPA order-api
    ├── InventoryServiceResources.cs # Deployment + Service (scaling géré par KEDA)
    ├── GatewayResources.cs        # Deployment + Service NodePort + HPA gateway
    └── IngressResources.cs        # cert-manager + nginx-ingress + Ingress rules (prod)
```

### Ordre de création (dépendances)

```
SecretsResources   CnpgResources (Helm CNPG operator)
    ↓ (DependsOn combiné)
DatabaseResources   MessagingResources   CacheResources
    ↓                  └── kubectl apply Cluster + Pooler YAML (Pulumi.Command)
KedaResources (Helm KEDA → Secret AMQP → kubectl ScaledObject)
    ↓
OrderServiceResources   InventoryServiceResources   GatewayResources
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

  # Nombre de replicas fixes (ignoré si HPA/KEDA activé)
  replicas:orderApi: "1"
  replicas:inventoryApi: "1"
  replicas:gateway: "1"
  replicas:db: "1"
  replicas:rabbitmq: "1"

  # Ressources CPU/RAM
  resources:orderApiCpuRequest: "100m"
  resources:orderApiCpuLimit: "500m"
  # ... (voir fichier complet pour inventory et gateway)

  # HPA (order-api + gateway uniquement — inventory-api est géré par KEDA)
  hpa:orderApiEnabled: "true"
  hpa:orderApiMin: "1"
  hpa:orderApiMax: "4"
  hpa:orderApiCpu: "70"
  hpa:gatewayEnabled: "true"
  hpa:gatewayMin: "1"
  hpa:gatewayMax: "3"
  hpa:gatewayCpu: "70"

  # KEDA — scaling réactif inventory-api sur profondeur queue RabbitMQ
  keda:version: "2.17.0"
  keda:queueName: "product-added-to-cart"
  keda:queueLength: "5"        # messages par réplica → scale-out si queue > N*replicas
  keda:inventoryApiMax: "8"
  keda:pollingInterval: "5"    # secondes entre chaque lecture de la queue
  keda:cooldownPeriod: "60"    # secondes d'inactivité avant scale-in

  # Mode presale (flash sale, promo)
  presale:enabled: "false"
  presale:inventoryApiMin: "3"
  presale:orderApiMin: "3"
  presale:gatewayMin: "2"

  # Secrets (credentials DB et RabbitMQ)
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

// Lit "keda:queueName"
var kedaCfg = new Config("keda");
var queue = kedaCfg.Get("queueName") ?? "product-added-to-cart";
```

---

## Secrets K8s

Les secrets sont créés comme des **K8s Secrets natifs** par `SecretsResources`.  
Chaque pod consomme les secrets via `envFrom.secretRef` — aucune valeur en clair dans les manifests.

### Secrets créés

| Nom K8s | Clés injectées | Consommateurs |
|---|---|---|
| `order-db-credentials` | `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB`, `ConnectionStrings__OrderDb` | init containers, order-api, postgres-exporter-order |
| `inventory-db-credentials` | `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB`, `ConnectionStrings__InventoryDb` | init containers, inventory-api, postgres-exporter-inventory |
| `rabbitmq-credentials` | `RABBITMQ_DEFAULT_USER`, `RABBITMQ_DEFAULT_PASS`, `RabbitMQ__Username`, `RabbitMQ__Password` | rabbitmq, order-api, inventory-api |
| `keda-rabbitmq-secret` | `amqp` (URL AMQP complète) | KEDA operator (TriggerAuthentication) |
| `order-db-pg-password` | `username`, `password` | CNPG — bootstrap superuser postgres (initdb) |
| `inventory-db-pg-password` | `username`, `password` | CNPG — bootstrap superuser postgres (initdb) |
| `order-db-superuser` | `username`, `password` | Auto-créé par CNPG — Pooler authQuery |
| `inventory-db-superuser` | `username`, `password` | Auto-créé par CNPG — Pooler authQuery |

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

## Bases de données — CNPG Cluster + Pooler

PostgreSQL est géré par l'**opérateur CloudNativePG (CNPG)**, remplaçant les StatefulSets manuels.  
CNPG fournit HA, failover automatique, PVCs gérés et un Pooler PgBouncer intégré.

### Architecture

```
order-db-pg-password (Secret bootstrap)
    │
    ▼  kubectl apply --server-side
order-db (Cluster CNPG)
    │  CNPG crée automatiquement :
    ├── order-db-rw:5432       ← primary  (init containers, postgres_exporter)
    ├── order-db-ro:5432       ← replicas (lectures, optionnel)
    └── order-db-superuser     ← Secret auto-créé (credentials PgBouncer authQuery)

order-db-pooler (Pooler CNPG / PgBouncer)
    └── order-db-pooler:5432  ← point d'entrée app (ConnectionStrings__OrderDb)
```

### Ressources créées par DB

```
order-db-pg-password   Secret K8s (bootstrap superuser postgres pour initdb)
order-db               Cluster CNPG → pods order-db-1, order-db-2... + PVCs
order-db-rw            Service ClusterIP (primary — init containers, exporter)
order-db-ro            Service ClusterIP (replicas, si instances > 1)
order-db-r             Service ClusterIP (tout pod)
order-db-pooler        Pooler CNPG → pod(s) PgBouncer
order-db-pooler        Service ClusterIP (PgBouncer — connection strings app)
```

### Configuration — Pulumi.dev.yaml

```yaml
cnpg:version: "1.24.0"       # version du chart Helm cloudnative-pg
cnpg:orderInstances: "1"     # pods postgres order-db   (dev=1, prod=3)
cnpg:inventoryInstances: "1" # pods postgres inventory-db
cnpg:poolerInstances: "1"    # pods PgBouncer par cluster (dev=1, prod=2)
```

### PgBouncer — Session mode

Le Pooler utilise `poolMode: session` pour la compatibilité EF Core / Npgsql.

> **Pourquoi session et non transaction ?** Npgsql active les prepared statements par défaut.  
> Le mode transaction PgBouncer interdit les prepared statements entre transactions → erreur.  
> En mode session, chaque connexion client obtient une connexion PG dédiée pour sa durée de vie.

Paramètres PgBouncer :

| Paramètre | Valeur | Description |
|---|---|---|
| `poolMode` | `session` | Compatible EF Core / prepared statements |
| `max_client_conn` | `1000` | Connexions app → PgBouncer |
| `default_pool_size` | `20` | Connexions PgBouncer → PostgreSQL par user |
| `authQuery` | pg_shadow | PgBouncer valide les credentials via le superuser |

### Vérifier

```bash
# Clusters CNPG (READY=True après ~60 s)
kubectl get cluster -n ecommerce

# Poolers (pods PgBouncer Running)
kubectl get pooler -n ecommerce

# Connexion directe via service -rw
kubectl exec -n ecommerce deploy/order-api -- \
  psql -h order-db-rw -U postgres -d order_db -c "SELECT count(*) FROM pg_stat_activity"

# Connexion via Pooler
kubectl exec -n ecommerce deploy/order-api -- \
  psql -h order-db-pooler -U postgres -d order_db -c "SELECT 1"
```

### Procédure de migration dev (StatefulSets → CNPG)

> ⚠️ **Perte de données** — les données des anciens StatefulSets ne sont pas migrées.  
> Utiliser uniquement pour l'environnement de développement.

```bash
# 1. Supprimer les anciens StatefulSets et PVCs
kubectl delete statefulset order-db inventory-db -n ecommerce
kubectl delete pvc data-order-db-0 data-inventory-db-0 -n ecommerce

# 2. Pré-charger les images CNPG dans Kind
podman pull ghcr.io/cloudnative-pg/cloudnative-pg:1.25.1
kind load docker-image ghcr.io/cloudnative-pg/cloudnative-pg:1.25.1 --name ecommerce
podman pull ghcr.io/cloudnative-pg/postgresql:16.6-bookworm
kind load docker-image ghcr.io/cloudnative-pg/postgresql:16.6-bookworm --name ecommerce

# 3. Déployer
cd infra/Ecommerce.Infra
pulumi up --yes
```

### Production — next steps

Pour la production, configurer dans `Pulumi.prod.yaml` :

```yaml
cnpg:orderInstances: "3"     # 1 primary + 2 replicas (streaming replication)
cnpg:inventoryInstances: "3"
cnpg:poolerInstances: "2"    # 2 pods PgBouncer pour la HA du pooler

# Backup S3 (ajouter dans DatabaseResources.cs — ScheduledBackup CNPG)
# Voir : https://cloudnative-pg.io/documentation/current/backup_recovery/
```

---

## Cache Redis

`CacheResources` déploie un **Redis 7** dans le namespace `ecommerce`.

### Rôle

inventory-api utilise un pattern **cache-aside** devant `GET /api/products` :
- Lecture → vérifie Redis → si absent, requête PostgreSQL + mise en cache (TTL 30 s)
- Réservation stock → invalide le cache (cache actif, pas seulement TTL)
- Expiration réservation → invalide le cache

Ce pattern a éliminé la saturation du pool PostgreSQL observée sous spike (300 VU).

### Configuration

```csharp
// inventory-api — DependencyInjection.cs
// Redis si ConnectionStrings__Redis est défini, MemoryCache sinon (dev sans K8s)
var redisCs = configuration.GetConnectionString("Redis");
if (!string.IsNullOrWhiteSpace(redisCs))
    services.AddStackExchangeRedisCache(opts => { opts.Configuration = redisCs; opts.InstanceName = "inventory:"; });
else
    services.AddDistributedMemoryCache();
```

### Ressources K8s

| Ressource | Valeur |
|---|---|
| Image | `redis:7-alpine` |
| Service | `redis.ecommerce.svc.cluster.local:6379` |
| Persistance | Désactivée (`--save ""`) — cache éphémère, les données sont dans PostgreSQL |
| CPU | 50m request / 200m limit |
| RAM | 64Mi request / 128Mi limit |

### Vérifier

```bash
kubectl get pod -n ecommerce -l app=redis
kubectl exec -n ecommerce deploy/redis -- redis-cli ping
# PONG
```

---

## HPA — HorizontalPodAutoscaler

Le HPA scale automatiquement **order-api** et **gateway** en fonction du CPU.  

> **inventory-api n'utilise pas l'HPA natif** — son scaling est géré par **KEDA** (voir section suivante).

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
# order-api   TARGETS : 3%/70%   → en dessous du seuil
# gateway     TARGETS : 75%/70%  → scale-out en cours
```

---

## KEDA — Scaling réactif (inventory-api)

**KEDA** (Kubernetes Event-Driven Autoscaling) scale inventory-api sur la **profondeur de la queue RabbitMQ** plutôt que sur le CPU.

### Pourquoi KEDA pour inventory-api

| Dimension | HPA CPU | KEDA RabbitMQ |
|---|---|---|
| Signal | CPU *après* saturation | Queue depth *avant* saturation |
| Temps de réaction | ~75 s | ~5 s |
| Type de scaling | Consequence-based | Intent-based |
| Scénario idéal | Charge CPU progressive | Burst de messages (flash sale) |

### Architecture

```
POST /orders → OrderApi publie ProductAddedToCartEvent
                    │
                    ▼
              Queue RabbitMQ: product-added-to-cart
                    │
                    ▼  (poll toutes les 5 s)
              KEDA operator
                    │  ScaledObject → HPA interne géré par KEDA
                    ▼
              inventory-api Deployment
              (spec.replicas ignoré par Pulumi)
```

### Ressources Pulumi créées (KedaResources.cs)

1. **Helm KEDA** — namespace `keda`, chart `kedacore/keda`, WaitForJobs=true, timeout 600 s
2. **Secret `keda-rabbitmq-secret`** — URL AMQP `amqp://user:pass@rabbitmq.ecommerce.svc.cluster.local:5672/`
3. **TriggerAuthentication + ScaledObject** — appliqués via `kubectl apply` (contourne le cache GVK du provider Pulumi)

### Configuration

```yaml
# Pulumi.dev.yaml
keda:queueName: "product-added-to-cart"   # vérifier dans RabbitMQ Management UI
keda:queueLength: "5"                     # messages par réplica → scale-out si queue > N*replicas
keda:inventoryApiMax: "8"
keda:pollingInterval: "5"
keda:cooldownPeriod: "60"
```

> **Vérifier le nom de la queue** : ouvrir `http://localhost:15672` (RabbitMQ Management) → onglet Queues  
> après un premier démarrage de l'application. Si différent de `product-added-to-cart`, mettre à jour `keda:queueName`.

### Vérifier

```bash
# ScaledObject + HPA interne KEDA
kubectl get scaledobject -n ecommerce
kubectl get hpa -n ecommerce    # keda-hpa-inventory-api créé automatiquement

# Pods KEDA dans leur namespace
kubectl get pods -n keda

# Détail du ScaledObject
kubectl describe scaledobject inventory-api -n ecommerce
```

### Pré-chargement images (Kind)

Les images KEDA viennent de `ghcr.io` — les pré-charger pour éviter les timeouts :

```bash
podman pull ghcr.io/kedacore/keda:2.17.0
kind load docker-image ghcr.io/kedacore/keda:2.17.0 --name ecommerce

podman pull ghcr.io/kedacore/keda-metrics-apiserver:2.17.0
kind load docker-image ghcr.io/kedacore/keda-metrics-apiserver:2.17.0 --name ecommerce

podman pull ghcr.io/kedacore/keda-admission-webhooks:2.17.0
kind load docker-image ghcr.io/kedacore/keda-admission-webhooks:2.17.0 --name ecommerce
```

### Récupérer une release Helm KEDA en échec

```bash
helm uninstall keda -n keda
# (pré-charger les images si pas déjà fait)
pulumi up --yes
```

---

## Mode presale

Le mode presale pré-scale les services **avant** un pic de trafic prévu (flash sale, promo, événement marketing) pour éviter le cold-start.

### Principe

Quand activé, les `minReplicas` / `minReplicaCount` sont forcés aux valeurs presale :
- **inventory-api** (KEDA ScaledObject) : `minReplicaCount` → 3
- **order-api** (HPA natif) : `minReplicas` → 3
- **gateway** (HPA natif) : `minReplicas` → 2

Les pods sont Ready **avant** le premier hit — pas de cold-start pendant le pic.

### Activation via Pulumi (event planifié, cohérence IaC)

```bash
cd infra/Ecommerce.Infra

# Avant le flash sale
pulumi config set presale:enabled true
pulumi up --yes

# Après le flash sale
pulumi config set presale:enabled false
pulumi up --yes
```

### Activation via Nuke (urgence, effet en secondes)

```bash
# Avant le flash sale (patch direct kubectl, sans pulumi up)
dotnet nuke PresaleStart

# Après le flash sale
dotnet nuke PresaleStop
```

> ⚠️ Le prochain `pulumi up` avec `presale:enabled=false` écrasera les patches kubectl.

### Valeurs presale configurables

```yaml
# Pulumi.dev.yaml
presale:inventoryApiMin: "3"   # minReplicaCount ScaledObject KEDA
presale:orderApiMin: "3"       # minReplicas HPA
presale:gatewayMin: "2"        # minReplicas HPA
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

**Pourquoi c'est obligatoire** : le HPA (et KEDA en mode CPU) ne peut pas calculer le pourcentage d'utilisation sans `requests.cpu` défini.

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

### Changer le seuil KEDA

```bash
pulumi config set keda:queueLength 10    # scale-out si queue > 10 msg/réplica
pulumi up --yes
```

### Scaler manuellement (HPA/KEDA désactivé ou urgence)

```bash
# order-api / gateway (HPA désactivé temporairement)
kubectl scale deployment order-api --replicas=2 -n ecommerce

# inventory-api — patcher le ScaledObject KEDA
kubectl patch scaledobject inventory-api -n ecommerce \
  --type=merge -p '{"spec":{"minReplicaCount":2}}'
```

> ⚠️ Le prochain `pulumi up` réinitialisera ces valeurs.
