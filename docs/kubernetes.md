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
```

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

```bash
podman pull postgres:16-alpine
podman pull rabbitmq:4.3.1-management-alpine

kind load docker-image postgres:16-alpine                --name ecommerce
kind load docker-image rabbitmq:4.3.1-management-alpine  --name ecommerce
```

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
podman build -t localhost/ecommerce/order-api:dev -f docker/order-api/Dockerfile .

# 2. La recharger dans Kind
kind load docker-image localhost/ecommerce/order-api:dev --name ecommerce

# 3. Forcer le redémarrage du pod (pas de re-pull nécessaire, ImagePullPolicy: IfNotPresent)
kubectl rollout restart deployment/order-api -n ecommerce

# OU redéployer tout via Pulumi
pulumi up --yes
```

---

## Vérifier le déploiement

### État des pods

```bash
# Vue d'ensemble (attendre STATUS=Running, READY=1/1)
kubectl get pods -n ecommerce -w

# Détails d'un pod (events, conditions)
kubectl describe pod -n ecommerce <nom-du-pod>
```

### État attendu après un déploiement réussi

```
NAME                             READY   STATUS    RESTARTS
order-db-0                       1/1     Running   0          ← StatefulSet
inventory-db-0                   1/1     Running   0          ← StatefulSet
rabbitmq-xxx                     1/1     Running   0
order-api-xxx                    1/1     Running   0
inventory-api-xxx                1/1     Running   0
gateway-xxx                      1/1     Running   0
```

### Secrets et Services

```bash
# Vérifier les secrets créés par Pulumi
kubectl get secrets -n ecommerce
# Attendu : order-db-credentials, inventory-db-credentials, rabbitmq-credentials

# Vérifier les services
kubectl get svc -n ecommerce
# Attendu : order-db, order-db-headless, inventory-db, inventory-db-headless,
#           rabbitmq, order-api, inventory-api, gateway
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

### HPA (si Metrics Server installé)

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

Le HorizontalPodAutoscaler nécessite le Metrics Server pour lire les métriques CPU/mémoire.

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
podman pull postgres:16-alpine
podman pull rabbitmq:4.3.1-management-alpine

kind load docker-image postgres:16-alpine                --name ecommerce
kind load docker-image rabbitmq:4.3.1-management-alpine  --name ecommerce
kind load docker-image localhost/ecommerce/order-api:dev     --name ecommerce
kind load docker-image localhost/ecommerce/inventory-api:dev --name ecommerce
kind load docker-image localhost/ecommerce/gateway:dev       --name ecommerce
```

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
curl http://localhost:30080/health
```
