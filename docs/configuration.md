# Référence de configuration — Pulumi.dev.yaml

Toute la configuration de l'environnement est centralisée dans un seul fichier :  
`infra/Ecommerce.Infra/Pulumi.dev.yaml`

Ce document liste **toutes les clés disponibles**, leur valeur par défaut, leur type et leur effet.  
Pour les procédures de déploiement, voir [kubernetes.md](kubernetes.md).  
Pour l'architecture des ressources Pulumi, voir [infrastructure.md](infrastructure.md).

---

## Sommaire

- [Comment fonctionne la configuration](#comment-fonctionne-la-configuration)
- [Images Docker](#images-docker)
- [Réseau — Ports exposés](#réseau--ports-exposés)
- [Réservation de stock](#réservation-de-stock)
- [Réplicas fixes](#réplicas-fixes)
- [Ressources CPU et mémoire](#ressources-cpu-et-mémoire)
- [Secrets et credentials](#secrets-et-credentials)
- [Ingress](#ingress)
- [Observabilité](#observabilité)
- [Mode presale](#mode-presale)
- [HPA — HorizontalPodAutoscaler](#hpa--horizontalpodautoscaler)
- [CNPG — Bases de données PostgreSQL](#cnpg--bases-de-données-postgresql)
- [KEDA — Autoscaling réactif](#keda--autoscaling-réactif)
- [Relations entre clés](#relations-entre-clés)
- [Passer en production](#passer-en-production)

---

## Comment fonctionne la configuration

### Format des clés

Les clés suivent le format `namespace:clé`. Chaque namespace correspond à un `new Config("namespace")` dans le code C#.

```yaml
# Pulumi.dev.yaml
config:
  orderApi:image: localhost/ecommerce/order-api:dev
  keda:queueLength: "5"
```

```csharp
// EcommerceStack.cs — lecture
var orderApiCfg = new Config("orderApi");
var image = orderApiCfg.Get("image") ?? "localhost/ecommerce/order-api:dev";

var kedaCfg = new Config("keda");
var queueLength = kedaCfg.GetInt32("queueLength") ?? 5;
```

### Types

Toutes les valeurs YAML sont des chaînes — `"1"` et non `1`.  
Le code C# convertit via `.GetInt32()`, `.GetBoolean()`, etc. La valeur après `??` est le fallback si la clé est absente du fichier.

### Modifier une valeur

```bash
cd infra/Ecommerce.Infra

# Modifier directement (éditeur ou commande)
pulumi config set keda:queueLength 10

# Appliquer
pulumi up --yes
```

---

## Images Docker

| Clé | Défaut | Description |
|-----|--------|-------------|
| `orderApi:image` | `localhost/ecommerce/order-api:dev` | Image du service order-api |
| `inventoryApi:image` | `localhost/ecommerce/inventory-api:dev` | Image du service inventory-api |
| `gateway:image` | `localhost/ecommerce/gateway:dev` | Image du reverse proxy / gateway |

Le préfixe `localhost/` est imposé par Podman pour les images locales.  
Pour un registry distant (prod), utiliser `ghcr.io/org/service:tag`.

```bash
# Rebuilder + recharger dans Kind après une modification du code
podman build -t localhost/ecommerce/order-api:dev -f docker/order-api/Dockerfile .
kind load docker-image localhost/ecommerce/order-api:dev --name ecommerce
kubectl rollout restart deployment/order-api -n ecommerce
```

---

## Réseau — Ports exposés

| Clé | Défaut | Description |
|-----|--------|-------------|
| `gateway:nodePort` | `30080` | NodePort Kind → `http://localhost:30080` |
| `observability:grafanaNodePort` | `30030` | NodePort Grafana → `http://localhost:30030` |
| `observability:jaegerNodePort` | `30686` | NodePort Jaeger UI → `http://localhost:30686` |

> **Important** : ces ports doivent correspondre aux `extraPortMappings` dans `kind-config.yaml`.  
> Modifier un NodePort nécessite un `pulumi up` ; modifier `kind-config.yaml` nécessite de recréer le cluster.

---

## Réservation de stock

Paramètres du mécanisme de réservation temporaire d'inventaire dans inventory-api.

| Clé | Défaut | Type | Description |
|-----|--------|------|-------------|
| `reservation:ttlMinutes` | `10` | int | Durée de vie d'une réservation (minutes) |
| `reservation:checkIntervalSeconds` | `30` | int | Fréquence du job d'expiration (secondes) |

Quand un client ajoute au panier, inventory-api réserve le stock pendant `ttlMinutes`.  
Le job d'expiration libère les réservations expirées et invalide le cache Redis.

---

## Réplicas fixes

Nombre de pods déployés par `pulumi up` **quand aucun autoscaler n'est actif**.

| Clé | Défaut | Description |
|-----|--------|-------------|
| `replicas:orderApi` | `1` | Réplicas order-api (ignoré si `hpa:orderApiEnabled=true`) |
| `replicas:inventoryApi` | `1` | Réplicas inventory-api (ignoré si KEDA ScaledObject actif) |
| `replicas:gateway` | `1` | Réplicas gateway (ignoré si `hpa:gatewayEnabled=true`) |
| `replicas:rabbitmq` | `1` | Réplicas RabbitMQ (**garder à 1** sans clustering configuré) |

> **Interaction avec HPA/KEDA** : quand un HPA ou un ScaledObject KEDA est actif, Pulumi pose  
> `IgnoreChanges = ["spec.replicas"]` sur le Deployment — la valeur `replicas:xxx` est ignorée  
> et c'est l'autoscaler qui contrôle le nombre de pods.

---

## Ressources CPU et mémoire

Définit les `requests` (garanti par le scheduler) et `limits` (plafond, OOMKill/throttle si dépassé) de chaque container.

### order-api

| Clé | Défaut | Description |
|-----|--------|-------------|
| `resources:orderApiCpuRequest` | `100m` | CPU garanti (0.1 vCPU) |
| `resources:orderApiCpuLimit` | `500m` | CPU max (0.5 vCPU) |
| `resources:orderApiMemoryRequest` | `128Mi` | RAM garantie |
| `resources:orderApiMemoryLimit` | `256Mi` | RAM max (OOMKill si dépassé) |

### inventory-api

| Clé | Défaut | Description |
|-----|--------|-------------|
| `resources:inventoryApiCpuRequest` | `100m` | CPU garanti |
| `resources:inventoryApiCpuLimit` | `500m` | CPU max |
| `resources:inventoryApiMemoryRequest` | `128Mi` | RAM garantie |
| `resources:inventoryApiMemoryLimit` | `256Mi` | RAM max |

### gateway

| Clé | Défaut | Description |
|-----|--------|-------------|
| `resources:gatewayCpuRequest` | `50m` | CPU garanti |
| `resources:gatewayCpuLimit` | `250m` | CPU max |
| `resources:gatewayMemoryRequest` | `64Mi` | RAM garantie |
| `resources:gatewayMemoryLimit` | `128Mi` | RAM max |

> **Format** : `100m` = 0.1 vCPU, `500m` = 0.5 vCPU. Mémoire : `Mi` (mébioctets), `Gi` (gibioctets).  
> Le HPA calcule le pourcentage CPU par rapport à `request`, pas à `limit`.  
> Sans `request` défini, le HPA ne peut pas fonctionner.

---

## Secrets et credentials

Les valeurs sensibles sont lues par `SecretsResources` pour créer les K8s Secrets natifs.

### Clés disponibles

| Clé | Défaut | Sensible | Description |
|-----|--------|----------|-------------|
| `secrets:orderDbUser` | `postgres` | Non | Utilisateur PostgreSQL order-db |
| `secrets:orderDbPassword` | `postgres` | **Oui** | Mot de passe PostgreSQL order-db |
| `secrets:orderDbName` | `order_db` | Non | Nom de la base order-db |
| `secrets:inventoryDbUser` | `postgres` | Non | Utilisateur PostgreSQL inventory-db |
| `secrets:inventoryDbPassword` | `postgres` | **Oui** | Mot de passe PostgreSQL inventory-db |
| `secrets:inventoryDbName` | `inventory_db` | Non | Nom de la base inventory-db |
| `secrets:rabbitmqUser` | `guest` | Non | Utilisateur RabbitMQ |
| `secrets:rabbitmqPassword` | `guest` | **Oui** | Mot de passe RabbitMQ |

### Chiffrer les valeurs sensibles

```bash
cd infra/Ecommerce.Infra

pulumi config set --secret secrets:orderDbPassword     "<mot_de_passe>"
pulumi config set --secret secrets:inventoryDbPassword "<mot_de_passe>"
pulumi config set --secret secrets:rabbitmqPassword    "<mot_de_passe>"
```

Les valeurs chiffrées sont stockées au format `secure: <encrypted>` dans `Pulumi.dev.yaml`.  
La clé de chiffrement est l'`encryptionsalt` en tête du fichier — **ne jamais la supprimer**.

### Secrets K8s créés

| Nom K8s | Namespace | Consommateurs |
|---------|-----------|---------------|
| `order-db-credentials` | ecommerce | init containers, order-api, postgres-exporter-order |
| `inventory-db-credentials` | ecommerce | init containers, inventory-api, postgres-exporter-inventory |
| `rabbitmq-credentials` | ecommerce | rabbitmq pod, order-api, inventory-api |
| `order-db-pg-password` | ecommerce | CNPG bootstrap superuser (initdb) |
| `inventory-db-pg-password` | ecommerce | CNPG bootstrap superuser (initdb) |
| `keda-rabbitmq-secret` | ecommerce | KEDA operator (TriggerAuthentication) |

> **Cohérence obligatoire** : `secrets:orderDbPassword` doit être **identique** à la valeur utilisée  
> lors du `initdb` CNPG. Si les deux divergent, les connexions échouent avec `password authentication failed`.

---

## Ingress

L'Ingress est **désactivé en dev** — les services sont exposés directement via NodePorts.  
En production, l'Ingress installe nginx-ingress + cert-manager + certificats Let's Encrypt.

| Clé | Défaut | Description |
|-----|--------|-------------|
| `ingress:enabled` | `false` | Activer l'Ingress (prod) |
| `ingress:domain` | `wizzz.com` | Domaine principal |
| `ingress:acmeEmail` | `ops@wizzz.com` | Email Let's Encrypt |
| `ingress:certManagerVersion` | `v1.16.2` | Version du chart cert-manager |
| `ingress:nginxVersion` | `4.11.3` | Version du chart nginx-ingress |
| `ingress:monitoringBasicAuthHtpasswd` | _(vide)_ | Hash htpasswd pour protéger Grafana/Jaeger (prod) |

Voir [production.md](production.md) pour la procédure complète d'activation.

---

## Observabilité

Stack OTel Collector + Jaeger + Prometheus + Grafana, déployée dans le namespace `monitoring`.

| Clé | Défaut | Description |
|-----|--------|-------------|
| `observability:grafanaNodePort` | `30030` | NodePort pour Grafana (dev) |
| `observability:jaegerNodePort` | `30686` | NodePort pour Jaeger UI (dev) |
| `observability:otelVersion` | `0.153.0` | Version de l'image OTel Collector Contrib |
| `observability:jaegerVersion` | `1.76.0` | Version de l'image Jaeger all-in-one |
| `observability:prometheusVersion` | `v3.11.3` | Version de l'image Prometheus |
| `observability:grafanaVersion` | `13.0.1-security-01` | Version de l'image Grafana |
| `observability:grafanaAdminPassword` | _(vide)_ | Mot de passe admin Grafana (prod) |

> **Changer une version** : mettre à jour la clé + pré-charger la nouvelle image dans Kind  
> (`podman pull <image>:<tag>` + `kind load docker-image <image>:<tag> --name ecommerce`).

Voir [observability.md](observability.md) pour la configuration des dashboards et des sources de données.

---

## Mode presale

Pré-scale les services **avant** un pic de trafic prévu (flash sale, promo, événement) pour éliminer le cold-start.

### Principe

Quand `presale:enabled = true`, les minReplicas de chaque autoscaler sont surchargés :

```
presale:enabled = false (normal)          presale:enabled = true (avant le pic)
─────────────────────────────────────     ──────────────────────────────────────────
inventory-api : min = keda:inventoryApiMin  inventory-api : min = presale:inventoryApiMin
order-api     : min = hpa:orderApiMin       order-api     : min = presale:orderApiMin
gateway       : min = hpa:gatewayMin        gateway       : min = presale:gatewayMin
```

### Clés

| Clé | Défaut | Description |
|-----|--------|-------------|
| `presale:enabled` | `false` | Activer le mode presale |
| `presale:inventoryApiMin` | `3` | minReplicaCount KEDA en mode presale |
| `presale:orderApiMin` | `3` | minReplicas HPA order-api en mode presale |
| `presale:gatewayMin` | `2` | minReplicas HPA gateway en mode presale |

### Activation

```bash
cd infra/Ecommerce.Infra

# Avant le flash sale (Pulumi garantit la cohérence IaC)
pulumi config set presale:enabled true
pulumi up --yes

# Après le flash sale
pulumi config set presale:enabled false
pulumi up --yes
```

### Activation d'urgence (sans pulumi up)

```bash
# Effet immédiat via patch kubectl direct
dotnet nuke PresaleStart
dotnet nuke PresaleStop
```

> ⚠️ Un `pulumi up` ultérieur avec `presale:enabled=false` écrasera les patches kubectl.

---

## HPA — HorizontalPodAutoscaler

**order-api** et **gateway** utilisent le HPA natif Kubernetes basé sur le CPU.  
**inventory-api** n'utilise pas le HPA — voir [KEDA](#keda--autoscaling-réactif).

**Prérequis** : [Metrics Server](kubernetes.md#metrics-server-hpa) installé dans le cluster.

### order-api

| Clé | Défaut | Description |
|-----|--------|-------------|
| `hpa:orderApiEnabled` | `true` | Activer le HPA pour order-api |
| `hpa:orderApiMin` | `1` | minReplicas (ignoré si `presale:enabled=true`) |
| `hpa:orderApiMax` | `4` | maxReplicas |
| `hpa:orderApiCpu` | `70` | Seuil CPU % déclenchant le scale-out |

### gateway

| Clé | Défaut | Description |
|-----|--------|-------------|
| `hpa:gatewayEnabled` | `true` | Activer le HPA pour gateway |
| `hpa:gatewayMin` | `1` | minReplicas (ignoré si `presale:enabled=true`) |
| `hpa:gatewayMax` | `3` | maxReplicas |
| `hpa:gatewayCpu` | `70` | Seuil CPU % déclenchant le scale-out |

### Comportement avec Pulumi

Quand un HPA est actif, Pulumi pose `IgnoreChanges = ["spec.replicas"]` sur le Deployment.  
La clé `replicas:orderApi` est ignorée — c'est le HPA qui décide du nombre de pods.

```bash
# Vérifier l'état des HPA
kubectl get hpa -n ecommerce
# NAME          TARGETS    MINPODS   MAXPODS   REPLICAS
# order-api     3%/70%     1         4         1
# gateway       5%/70%     1         3         1
# keda-hpa-inventory-api  ... (géré par KEDA, voir ci-dessous)
```

---

## CNPG — Bases de données PostgreSQL

[CloudNativePG](https://cloudnative-pg.io/) remplace les StatefulSets PostgreSQL par des clusters gérés avec failover automatique et un Pooler PgBouncer intégré.

### Clés

| Clé | Défaut | Description |
|-----|--------|-------------|
| `cnpg:version` | `0.23.2` | Version du **chart Helm** cloudnative-pg (≠ version opérateur) |
| `cnpg:orderInstances` | `1` | Pods PostgreSQL pour order-db (dev=1, prod=3) |
| `cnpg:inventoryInstances` | `1` | Pods PostgreSQL pour inventory-db |
| `cnpg:poolerInstances` | `1` | Pods PgBouncer par cluster (dev=1, prod=2) |

### Correspondance chart ↔ opérateur

| Chart (`cnpg:version`) | Opérateur | Image opérateur |
|------------------------|-----------|-----------------|
| `0.22.0` | `1.24.0` | `ghcr.io/cloudnative-pg/cloudnative-pg:1.24.0` |
| `0.23.x` | `1.25.x` | `ghcr.io/cloudnative-pg/cloudnative-pg:1.25.x` |
| `0.28.x` | `1.29.x` | `ghcr.io/cloudnative-pg/cloudnative-pg:1.29.x` |

> ⚠️ La clé `cnpg:version` est la version du **chart Helm**, pas de l'opérateur.  
> Ces deux numéros de version sont indépendants.

### Architecture des services

CNPG crée automatiquement les services suivants pour chaque cluster :

```
order-db-rw:5432      → primary seulement   (init containers, postgres_exporter)
order-db-ro:5432      → replicas seulement  (lectures optionnelles — si instances > 1)
order-db-r:5432       → tous les pods       (load-balanced, lectures)
order-db-pooler:5432  → PgBouncer           (ConnectionStrings__OrderDb des apps)
```

### Pooler PgBouncer — session mode

Le Pooler utilise `poolMode: session` (requis pour la compatibilité EF Core + Npgsql prepared statements).

| Paramètre PgBouncer | Valeur | Raison |
|---------------------|--------|--------|
| `poolMode` | `session` | Npgsql prépare les requêtes — interdit en mode transaction |
| `max_client_conn` | `1000` | Connexions app → PgBouncer (côté applicatif) |
| `default_pool_size` | `200` | Connexions PgBouncer → PostgreSQL |
| `max_connections` (PG) | `200` | Limite PostgreSQL (CNPG parameter) |

**Calcul du pool** : en mode session, chaque connexion Npgsql = 1 connexion PgBouncer = 1 connexion PostgreSQL.

```
keda:inventoryApiMax=8 pods × Maximum Pool Size=25 (Npgsql) = 200 connexions max
default_pool_size=200 (PgBouncer) = max_connections=200 (PostgreSQL)
```

> ⚠️ **Marge prod** : PostgreSQL réserve 3 connexions superuser + postgres_exporter en ajoute 1-2.  
> En prod avec 8 pods max, réduire à `keda:inventoryApiMax: "7"` (175 connexions) pour garder de la marge.

### Vérifier les clusters

```bash
# État des clusters (READY=True après ~60 s)
kubectl get cluster -n ecommerce

# Pods PostgreSQL
kubectl get pods -n ecommerce -l cnpg.io/cluster

# Poolers PgBouncer
kubectl get pooler -n ecommerce
kubectl get pods -n ecommerce -l cnpg.io/poolerName

# Logs du cluster
kubectl logs -n ecommerce -l cnpg.io/cluster=order-db -c postgres --tail=50
```

---

## KEDA — Autoscaling réactif

**KEDA** scale inventory-api sur la profondeur de la queue RabbitMQ plutôt que sur le CPU.  
Réaction ~5 s vs ~75 s pour un HPA CPU — adapté aux bursts de messages (flash sale).

### Clés

| Clé | Défaut | Description |
|-----|--------|-------------|
| `keda:version` | `2.17.0` | Version du chart Helm KEDA |
| `keda:queueName` | `product-added-to-cart` | Nom de la queue RabbitMQ surveillée |
| `keda:queueLength` | `5` | Messages par réplica → scale-out si `depth > N × replicas` |
| `keda:inventoryApiMin` | `1` | minReplicaCount (ignoré si `presale:enabled=true`) |
| `keda:inventoryApiMax` | `8` | maxReplicaCount |
| `keda:pollingInterval` | `5` | Secondes entre chaque lecture de la queue |
| `keda:cooldownPeriod` | `60` | Secondes d'inactivité avant scale-in |

### Formule de scaling

```
replicas_cible = ceil(profondeur_queue / keda:queueLength)

Exemple : 35 messages, queueLength=5 → ceil(35/5) = 7 pods
          5  messages, queueLength=5 → ceil(5/5)  = 1 pod
          0  messages                → scale-in après cooldownPeriod secondes
```

### Nom de la queue

Le nom est généré par MassTransit `KebabCaseEndpointNameFormatter` :  
`ProductAddedToCartEvent` → `product-added-to-cart`

> ⚠️ `DefaultEndpointNameFormatter` produirait `ProductAddedToCart` (PascalCase).  
> Vérifier le nom réel dans l'UI RabbitMQ (`http://localhost:15672` → Queues) après démarrage.

### Tester l'autoscaling

```bash
# 1. Observer le ScaledObject en temps réel
kubectl get scaledobject inventory-api -n ecommerce -w

# 2. Vérifier les métriques KEDA lues (profondeur actuelle de la queue)
kubectl describe scaledobject inventory-api -n ecommerce

# 3. Envoyer des messages de test (depuis l'UI RabbitMQ ou k6)
# → http://localhost:15672 → Queues → product-added-to-cart → Publish message

# 4. Observer le scale-out (pods supplémentaires apparaissent)
kubectl get pods -n ecommerce -l app=inventory-api -w

# 5. Attendre 60 s d'inactivité → scale-in automatique
```

### Vérifier

```bash
# ScaledObject KEDA (READY=True, ACTIVE=True si messages en queue)
kubectl get scaledobject -n ecommerce

# HPA interne créé automatiquement par KEDA
kubectl get hpa -n ecommerce
# NAME                     REFERENCE             TARGETS   MINPODS   MAXPODS
# keda-hpa-inventory-api   Deployment/inventory-api  0/5   1         8

# Pods KEDA dans leur namespace
kubectl get pods -n keda
```

---

## Relations entre clés

### Priorité de contrôle du nombre de pods

```
presale:enabled = true
    └── surcharge les minReplicas de tous les autoscalers
            presale:inventoryApiMin → ScaledObject KEDA minReplicaCount
            presale:orderApiMin     → HPA order-api minReplicas
            presale:gatewayMin      → HPA gateway minReplicas

presale:enabled = false (normal)
    ├── inventory-api : keda:inventoryApiMin ≤ pods ≤ keda:inventoryApiMax  (KEDA)
    ├── order-api     : hpa:orderApiMin ≤ pods ≤ hpa:orderApiMax            (HPA)
    └── gateway       : hpa:gatewayMin ≤ pods ≤ hpa:gatewayMax             (HPA)
```

### Cohérence obligatoire des mots de passe

Le mot de passe PostgreSQL est utilisé à **deux endroits distincts** qui doivent rester synchronisés :

| Usage | Source de la valeur | Effet |
|-------|--------------------|----|
| CNPG `postInitSQL` (`ALTER USER postgres PASSWORD`) | `secrets:orderDbPassword` | Définit le mot de passe au niveau PostgreSQL lors du `initdb` |
| Connection string Npgsql dans `order-db-credentials` | `secrets:orderDbPassword` | Utilisé par l'application pour se connecter |

Les deux lisent la même clé `secrets:orderDbPassword` — toute modification est automatiquement cohérente.

### Contraintes pool de connexions

```
keda:inventoryApiMax  ×  Maximum Pool Size (Npgsql)  ≤  max_connections CNPG
        8             ×          25                  =       200           ✓

# En prod avec marge superuser (3 réservées) + postgres_exporter (~2) :
# disponible = 200 - 3 - 2 = 195 connexions utilisateur
# 8 × 25 = 200 > 195 → risque de "too many connections" en pic absolu
# Recommandation prod : keda:inventoryApiMax: "7"  (7×25=175 < 195 ✓)
```

---

## Passer en production

Créer un fichier `infra/Ecommerce.Infra/Pulumi.prod.yaml` avec les surcharges prod :

```yaml
config:
  # Images depuis le registry de production
  orderApi:image: ghcr.io/org/order-api:v1.0.0
  inventoryApi:image: ghcr.io/org/inventory-api:v1.0.0
  gateway:image: ghcr.io/org/gateway:v1.0.0

  # Ingress + TLS
  ingress:enabled: "true"
  ingress:domain: "wizzz.com"
  ingress:acmeEmail: "ops@wizzz.com"

  # PostgreSQL HA (3 instances = 1 primary + 2 replicas)
  cnpg:version: "0.23.2"
  cnpg:orderInstances: "3"
  cnpg:inventoryInstances: "3"
  cnpg:poolerInstances: "2"

  # Autoscaling — réduire inventoryApiMax pour garder de la marge de connexions
  keda:inventoryApiMax: "7"    # 7×25=175 < 197 connexions disponibles
  hpa:orderApiMax: "6"
  hpa:gatewayMax: "4"

  # Ressources augmentées
  resources:orderApiCpuRequest: "200m"
  resources:orderApiCpuLimit: "1000m"
  resources:orderApiMemoryRequest: "256Mi"
  resources:orderApiMemoryLimit: "512Mi"

  # Mots de passe — définis via --secret, jamais en clair ici
  # pulumi config set --secret secrets:orderDbPassword <pwd> --stack prod
```

```bash
cd infra/Ecommerce.Infra
pulumi stack init prod
pulumi stack select prod

# Chiffrer les secrets
pulumi config set --secret secrets:orderDbPassword     "<password>"
pulumi config set --secret secrets:inventoryDbPassword "<password>"
pulumi config set --secret secrets:rabbitmqPassword    "<password>"
pulumi config set --secret observability:grafanaAdminPassword "<password>"
pulumi config set --secret ingress:monitoringBasicAuthHtpasswd "<htpasswd-hash>"

# Déployer
pulumi up --stack prod
```

Voir [production.md](production.md) pour la procédure complète (DNS, certificats TLS, registry).
