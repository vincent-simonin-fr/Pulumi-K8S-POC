# Kubernetes & Déploiement

## Sommaire

- [Créer le cluster Kind](#créer-le-cluster-kind)
- [Construire et charger les images](#construire-et-charger-les-images)
- [Déployer avec Pulumi](#déployer-avec-pulumi)
- [Vérifier le déploiement](#vérifier-le-déploiement)
- [Arrêter et relancer le cluster](#arrêter-et-relancer-le-cluster)
- [Metrics Server (HPA)](#metrics-server-hpa)
- [Reset complet](#reset-complet)

---

## Créer le cluster Kind

> **Windows + Podman** : `KIND_EXPERIMENTAL_PROVIDER=podman` est obligatoire pour que Kind utilise Podman comme backend. Définir cette variable en permanence dans les variables d'environnement système.

```bash
set KIND_EXPERIMENTAL_PROVIDER=podman

kind create cluster --name ecommerce --config kind-config.yaml
kubectl config use-context kind-ecommerce
```

**Contenu de `kind-config.yaml`** (à la racine du projet) :

```yaml
kind: Cluster
apiVersion: kind.x-k8s.io/v1alpha4
nodes:
  - role: control-plane
    extraPortMappings:
      - containerPort: 30080   # NodePort du Gateway
        hostPort: 30080
        protocol: TCP
      - containerPort: 30030   # NodePort Grafana
        hostPort: 30030
        protocol: TCP
      - containerPort: 30686   # NodePort Jaeger UI
        hostPort: 30686
        protocol: TCP
```

> **Note** : modifier `kind-config.yaml` nécessite de recréer le cluster (voir [Reset complet](#reset-complet)).  
> Les extraPortMappings sont fixes à la création du nœud Kind.

---

## Construire et charger les images

Kind s'exécute dans un container Podman isolé : les images locales Podman ne sont pas visibles directement.  
Il faut les charger explicitement avec `kind load docker-image`.

### Images applicatives

```bash
# Depuis la racine du projet
podman build -t localhost/ecommerce/order-api:dev     -f docker/order-api/Dockerfile .
podman build -t localhost/ecommerce/inventory-api:dev -f docker/inventory-api/Dockerfile .
podman build -t localhost/ecommerce/gateway:dev       -f docker/gateway/Dockerfile .

kind load docker-image localhost/ecommerce/order-api:dev     --name ecommerce
kind load docker-image localhost/ecommerce/inventory-api:dev --name ecommerce
kind load docker-image localhost/ecommerce/gateway:dev       --name ecommerce
```

> **Important (Podman)** : Podman préfixe les images locales avec `localhost/`. Ce préfixe est requis dans les manifests K8s et dans `Pulumi.dev.yaml`.

### Images publiques (pré-charger pour éviter les timeouts)

Kind tire les images depuis internet au moment du déploiement. Les pré-charger évite les timeouts Helm et les erreurs `ImagePullBackOff`.

```bash
# Init containers (psql) + messagerie + cache
podman pull postgres:16-alpine
podman pull rabbitmq:4.3.1-management-alpine
podman pull redis:7-alpine
kind load docker-image postgres:16-alpine                --name ecommerce
kind load docker-image rabbitmq:4.3.1-management-alpine  --name ecommerce
kind load docker-image redis:7-alpine                    --name ecommerce

# CNPG (opérateur + PostgreSQL 16 bookworm) — images venant de ghcr.io
# L'image bookworm (non alpine) est la seule officiellement supportée par CNPG.
# L'opérateur tourne dans cnpg-system, PostgreSQL tourne dans ecommerce.
podman pull ghcr.io/cloudnative-pg/cloudnative-pg:1.24.0
kind load docker-image ghcr.io/cloudnative-pg/cloudnative-pg:1.24.0 --name ecommerce
podman pull ghcr.io/cloudnative-pg/postgresql:16.6-bookworm
kind load docker-image ghcr.io/cloudnative-pg/postgresql:16.6-bookworm --name ecommerce
# PgBouncer — utilisé par les Poolers CNPG (version distincte de l'opérateur)
podman pull ghcr.io/cloudnative-pg/pgbouncer:1.23.0
kind load docker-image ghcr.io/cloudnative-pg/pgbouncer:1.23.0 --name ecommerce

# KEDA (operator + metrics server + webhooks) — images venant de ghcr.io
podman pull ghcr.io/kedacore/keda:2.17.0
kind load docker-image ghcr.io/kedacore/keda:2.17.0 --name ecommerce
podman pull ghcr.io/kedacore/keda-metrics-apiserver:2.17.0
kind load docker-image ghcr.io/kedacore/keda-metrics-apiserver:2.17.0 --name ecommerce
podman pull ghcr.io/kedacore/keda-admission-webhooks:2.17.0
kind load docker-image ghcr.io/kedacore/keda-admission-webhooks:2.17.0 --name ecommerce

# Observabilité
podman pull otel/opentelemetry-collector-contrib:0.153.0
podman pull jaegertracing/all-in-one:1.76.0
podman pull prom/prometheus:v3.11.3
podman pull grafana/grafana:13.0.1-security-01
podman pull prometheuscommunity/postgres-exporter:v0.16.0
podman pull registry.k8s.io/kube-state-metrics/kube-state-metrics:v2.13.0
podman pull quay.io/prometheus/node-exporter:v1.9.1

kind load docker-image otel/opentelemetry-collector-contrib:0.153.0      --name ecommerce
kind load docker-image jaegertracing/all-in-one:1.76.0                   --name ecommerce
kind load docker-image prom/prometheus:v3.11.3                           --name ecommerce
kind load docker-image grafana/grafana:13.0.1-security-01                --name ecommerce
kind load docker-image prometheuscommunity/postgres-exporter:v0.16.0     --name ecommerce
kind load docker-image registry.k8s.io/kube-state-metrics/kube-state-metrics:v2.13.0 --name ecommerce
kind load docker-image quay.io/prometheus/node-exporter:v1.9.1           --name ecommerce
```

> Le script `scripts/k8s_complete_launch.cmd` fait tout cela automatiquement.

### Vérifier que les images sont dans Kind

```bash
podman exec ecommerce-control-plane crictl images
```

---

## Déployer avec Pulumi

```bash
cd infra/Ecommerce.Infra

# Première fois uniquement
pulumi login --local
pulumi stack init dev

# Déployer (ou mettre à jour)
pulumi up --yes

# Afficher les outputs (URLs)
pulumi stack output
```

### Mettre à jour après un changement de code applicatif

```bash
# 1. Rebuilder l'image modifiée
podman build -t localhost/ecommerce/inventory-api:dev -f docker/inventory-api/Dockerfile .

# 2. La recharger dans Kind
kind load docker-image localhost/ecommerce/inventory-api:dev --name ecommerce

# 3. Forcer le redémarrage du pod (pas de re-pull nécessaire, ImagePullPolicy: IfNotPresent)
kubectl rollout restart deployment/inventory-api -n ecommerce

# OU redéployer tout via Pulumi
pulumi up --yes
```

---

## Vérifier le déploiement

### État des pods — namespace ecommerce

```bash
kubectl get pods -n ecommerce -w
```

État attendu après un déploiement réussi :

```
NAME                                      READY   STATUS    RESTARTS
order-db-1                                1/1     Running   0          ← CNPG Cluster (primary)
order-db-pooler-xxx                       1/1     Running   0          ← CNPG Pooler (PgBouncer)
inventory-db-1                            1/1     Running   0          ← CNPG Cluster (primary)
inventory-db-pooler-xxx                   1/1     Running   0          ← CNPG Pooler (PgBouncer)
rabbitmq-xxx                              1/1     Running   0
redis-xxx                                 1/1     Running   0
order-api-xxx                             1/1     Running   0
inventory-api-xxx                         1/1     Running   0
gateway-xxx                               1/1     Running   0
postgres-exporter-order-xxx               1/1     Running   0
postgres-exporter-inventory-xxx           1/1     Running   0
```

### État des pods — namespace cnpg-system

```bash
kubectl get pods -n cnpg-system
```

```
NAME                                      READY   STATUS
cloudnative-pg-xxx                        1/1     Running   ← opérateur CNPG
```

### État des pods — namespace keda

```bash
kubectl get pods -n keda
```

```
NAME                                      READY   STATUS
keda-operator-xxx                         1/1     Running
keda-metrics-apiserver-xxx                1/1     Running
keda-admission-webhooks-xxx               1/1     Running
```

### État des pods — namespace monitoring

```bash
kubectl get pods -n monitoring
```

```
NAME                                 READY   STATUS
otel-collector-xxx                   1/1     Running
jaeger-xxx                           1/1     Running
prometheus-xxx                       1/1     Running
grafana-xxx                          1/1     Running
kube-state-metrics-xxx               1/1     Running
node-exporter-xxx                    1/1     Running   ← DaemonSet
```

### KEDA — ScaledObject et HPA interne

```bash
# ScaledObject inventory-api
kubectl get scaledobject -n ecommerce
# NAME            SCALETARGETKIND   SCALETARGETNAME   READY   ACTIVE
# inventory-api   Deployment        inventory-api     True    False

# HPA interne créé par KEDA
kubectl get hpa -n ecommerce
# keda-hpa-inventory-api   Deployment/inventory-api   0/5   1   8   ...
```

### Secrets et Services

```bash
kubectl get secrets -n ecommerce
# Attendu : order-db-credentials, inventory-db-credentials,
#           rabbitmq-credentials, keda-rabbitmq-secret

kubectl get svc -n ecommerce
# Attendu (CNPG crée automatiquement les services -rw, -ro, -r, -pooler) :
#   order-db-rw, order-db-ro, order-db-r, order-db-pooler,
#   inventory-db-rw, inventory-db-ro, inventory-db-r, inventory-db-pooler,
#   rabbitmq, redis, order-api, inventory-api, gateway

kubectl get cluster -n ecommerce
# Attendu : order-db READY=True, inventory-db READY=True

kubectl get pooler -n ecommerce
# Attendu : order-db-pooler, inventory-db-pooler
```

### Health checks applicatifs

```bash
curl http://localhost:30080/health             # Gateway
curl http://localhost:30080/health/orders      # Order API
curl http://localhost:30080/health/inventory   # Inventory API
```

### Logs

```bash
# Logs en direct
kubectl logs -n ecommerce deploy/order-api -f
kubectl logs -n ecommerce deploy/inventory-api -f

# Logs du pod précédent (si CrashLoopBackOff)
kubectl logs -n ecommerce deploy/order-api --previous
```

### HPA (order-api et gateway)

```bash
kubectl get hpa -n ecommerce
# TARGETS doit afficher XX%/70% (pas <unknown>)
```

---

## Arrêter et relancer le cluster

Podman peut stopper et relancer le container Kind sans perdre l'état du cluster ni les données PostgreSQL.

```bash
# Arrêter (cluster mis en pause, tout est conservé)
podman stop ecommerce-control-plane

# Relancer
podman start ecommerce-control-plane

# Vérifier que kubectl fonctionne à nouveau
kubectl get nodes
kubectl get pods -n ecommerce
```

> **Note** : après un redémarrage, les pods mettent 30-60 secondes à être à nouveau `Running`.  
> Aucun `pulumi up` n'est nécessaire — l'état K8s est conservé dans etcd.

---

## Metrics Server (HPA)

Le HorizontalPodAutoscaler pour order-api et gateway nécessite le Metrics Server pour lire les métriques CPU/mémoire.

> **inventory-api** utilise KEDA (pas de Metrics Server requis pour son scaling).

### Installation

```bash
kubectl apply -f https://github.com/kubernetes-sigs/metrics-server/releases/latest/download/components.yaml

# Kind utilise des certificats auto-signés — patch obligatoire
kubectl patch deployment metrics-server -n kube-system \
  --type=json \
  -p="[{\"op\":\"add\",\"path\":\"/spec/template/spec/containers/0/args/-\",\"value\":\"--kubelet-insecure-tls\"}]"

# Vérifier (attendre ~60s)
kubectl get deployment metrics-server -n kube-system
# READY doit être 1/1
```

### Si l'image ne se télécharge pas (réseau lent / offline)

```bash
podman pull registry.k8s.io/metrics-server/metrics-server:v0.8.1
kind load docker-image registry.k8s.io/metrics-server/metrics-server:v0.8.1 --name ecommerce
kubectl rollout restart deployment metrics-server -n kube-system
```

---

## Reset complet

Procédure pour repartir d'un état totalement propre.

### Étape 1 — Annuler l'état Pulumi

```bash
cd infra/Ecommerce.Infra

# Si pulumi up tourne encore
pulumi cancel

# Supprimer le stack (--force ignore les ressources encore listées dans l'état)
pulumi stack rm dev --force
```

### Étape 2 — Supprimer le cluster

```bash
kind delete cluster --name ecommerce
```

### Étape 3 — Recréer le cluster

```bash
set KIND_EXPERIMENTAL_PROVIDER=podman
kind create cluster --name ecommerce --config kind-config.yaml
kubectl config use-context kind-ecommerce
```

### Étape 4 — Recharger toutes les images

```bash
# Init containers / messagerie / cache
podman pull postgres:16-alpine && kind load docker-image postgres:16-alpine --name ecommerce
podman pull rabbitmq:4.3.1-management-alpine && kind load docker-image rabbitmq:4.3.1-management-alpine --name ecommerce
podman pull redis:7-alpine && kind load docker-image redis:7-alpine --name ecommerce

# CNPG
podman pull ghcr.io/cloudnative-pg/cloudnative-pg:1.24.0 && kind load docker-image ghcr.io/cloudnative-pg/cloudnative-pg:1.24.0 --name ecommerce
podman pull ghcr.io/cloudnative-pg/postgresql:16.6-bookworm && kind load docker-image ghcr.io/cloudnative-pg/postgresql:16.6-bookworm --name ecommerce

# KEDA
podman pull ghcr.io/kedacore/keda:2.17.0 && kind load docker-image ghcr.io/kedacore/keda:2.17.0 --name ecommerce
podman pull ghcr.io/kedacore/keda-metrics-apiserver:2.17.0 && kind load docker-image ghcr.io/kedacore/keda-metrics-apiserver:2.17.0 --name ecommerce
podman pull ghcr.io/kedacore/keda-admission-webhooks:2.17.0 && kind load docker-image ghcr.io/kedacore/keda-admission-webhooks:2.17.0 --name ecommerce

# Services applicatifs (rebuild si code modifié)
kind load docker-image localhost/ecommerce/order-api:dev     --name ecommerce
kind load docker-image localhost/ecommerce/inventory-api:dev --name ecommerce
kind load docker-image localhost/ecommerce/gateway:dev       --name ecommerce

# Observabilité
podman pull otel/opentelemetry-collector-contrib:0.153.0 && kind load docker-image otel/opentelemetry-collector-contrib:0.153.0 --name ecommerce
podman pull jaegertracing/all-in-one:1.76.0 && kind load docker-image jaegertracing/all-in-one:1.76.0 --name ecommerce
podman pull prom/prometheus:v3.11.3 && kind load docker-image prom/prometheus:v3.11.3 --name ecommerce
podman pull grafana/grafana:13.0.1-security-01 && kind load docker-image grafana/grafana:13.0.1-security-01 --name ecommerce
podman pull prometheuscommunity/postgres-exporter:v0.16.0 && kind load docker-image prometheuscommunity/postgres-exporter:v0.16.0 --name ecommerce
podman pull registry.k8s.io/kube-state-metrics/kube-state-metrics:v2.13.0 && kind load docker-image registry.k8s.io/kube-state-metrics/kube-state-metrics:v2.13.0 --name ecommerce
podman pull quay.io/prometheus/node-exporter:v1.9.1 && kind load docker-image quay.io/prometheus/node-exporter:v1.9.1 --name ecommerce
```

> Le script `scripts/k8s_complete_launch.cmd` fait tout cela automatiquement depuis la racine du projet.

### Étape 5 — Redéployer

```bash
cd infra/Ecommerce.Infra
pulumi login --local
pulumi stack init dev
pulumi up --yes
```

### Étape 6 — Vérifier

```bash
kubectl get pods -n ecommerce
kubectl get pods -n keda
kubectl get pods -n monitoring
curl http://localhost:30080/health
```
