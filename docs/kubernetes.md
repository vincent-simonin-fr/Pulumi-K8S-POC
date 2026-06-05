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
# Liste épinglée UNIQUE : build/Build.cs → PreloadImageList (infra, observabilité
# kube-prometheus-stack, CNPG, KEDA, metrics-server, Vault + VSO). Source de vérité —
# on évite de dupliquer/maintenir la liste de versions dans la doc.
dotnet nuke PreloadImages
```

> `dotnet nuke Launch` fait tout cela automatiquement (cluster + images + build + pulumi up).
> L'ancien script `scripts/k8s_complete_launch.cmd` est conservé pour mémoire.

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

### Étape 1 — Réinitialiser l'état Pulumi

```bash
cd infra/Ecommerce.Infra

# Si pulumi up tourne encore
pulumi cancel

# ⚠️ pulumi stack rm SUPPRIME aussi Pulumi.dev.yaml (toute la config du stack :
#    versions de charts, secrets chiffrés, flags...). On le sauvegarde avant,
#    on le restaure après, puis on recrée un stack vierge.
cp Pulumi.dev.yaml Pulumi.dev.yaml.bak        # PowerShell : Copy-Item Pulumi.dev.yaml Pulumi.dev.yaml.bak

# Supprimer le stack (--force ignore les ressources encore listées dans l'état)
pulumi stack rm dev --force

# Restaurer la config et recréer un stack vierge
mv -f Pulumi.dev.yaml.bak Pulumi.dev.yaml     # PowerShell : Move-Item -Force Pulumi.dev.yaml.bak Pulumi.dev.yaml
pulumi stack init dev
pulumi stack select dev
```

> **Pourquoi cette gymnastique ?** `stack rm` remet l'état Pulumi à zéro (utile quand
> l'état est désynchronisé du cluster — ex. cluster recréé hors Pulumi), mais il efface
> le fichier de config au passage. La sauvegarde/restauration garde tous tes réglages.

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
# Liste épinglée UNIQUE : build/Build.cs → PreloadImageList (infra, observabilité
# kube-prometheus-stack, CNPG, KEDA, metrics-server, Vault + VSO). Source de vérité —
# pas de liste dupliquée dans la doc (évite la dérive de versions).
dotnet nuke PreloadImages

# Images applicatives (rebuild + load)
dotnet nuke BuildImages
```

> `dotnet nuke Launch` fait tout cela automatiquement depuis la racine du projet.
> L'ancien script `scripts/k8s_complete_launch.cmd` est conservé pour mémoire.

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
