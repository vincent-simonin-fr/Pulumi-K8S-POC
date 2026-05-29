# Ecommerce — .NET 10 · Pulumi · Kubernetes

Stack microservices e-commerce de démonstration en **Clean Architecture**, communication event-driven via **RabbitMQ / MassTransit**, déployée localement sur **Kind** avec **Podman** et provisionnée via **Pulumi C#**.

---

## Sommaire

| Documentation | Contenu |
|---|---|
| **Ce fichier** | Prérequis, démarrage rapide |
| [Architecture](docs/architecture.md) | Diagramme, flux métier, structure projet, endpoints, configuration |
| [Kubernetes & Déploiement](docs/kubernetes.md) | Cluster Kind, build images, `pulumi up`, reset, arrêt/redémarrage, Metrics Server |
| [Infrastructure Pulumi](docs/infrastructure.md) | Code Pulumi, secrets, HPA, KEDA, Redis, StatefulSet PostgreSQL, presale, ressources CPU/RAM |
| [Déploiement en production](docs/production.md) | Stack prod, secrets, Ingress, DNS, Let's Encrypt, opérations |
| [Observabilité](docs/observability.md) | OTel Collector, Jaeger, Prometheus, Grafana, 4 dashboards |
| [Debugging](docs/debugging.md) | VS 2022 + Pulumi, `kubectl` diagnostics, problèmes courants |
| [Dev local & Tests](docs/dev-local.md) | `podman-compose`, tests d'intégration, migrations EF Core, OpenAPI, observabilité |
| [k9s](docs/k9s.md) | Interface terminal Kubernetes — logs, shell, describe sans Dashboard |
| [Tests de charge](docs/load-testing.md) | k6 — scénarios baseline/load/stress/spike, intégration Prometheus |

---

## Vue d'ensemble

```
Client HTTP
    │
    ▼
Gateway (YARP) ─── NodePort 30080 ─── localhost:30080
    ├── /orders/**    → Order API    → order-db   (PostgreSQL)
    └── /inventory/** → Inventory API → inventory-db (PostgreSQL)
                              │
                              ├── Redis (cache GET /inventory, TTL 30s)
                              └── RabbitMQ :5672 (ProductAddedToCartEvent)
                                      │
                                   KEDA ──► scale inventory-api
                                   (queue depth, ~5s reaction)
```

Le flux principal : ajout panier → réservation stock → expiration automatique → libération stock.  
Voir [Architecture](docs/architecture.md) pour les détails.

### Scaling

| Service | Mécanisme | Signal |
|---|---|---|
| order-api | HPA natif | CPU > 70% |
| inventory-api | **KEDA** | Queue RabbitMQ depth (réaction ~5s) |
| gateway | HPA natif | CPU > 70% |

---

## Prérequis

| Outil | Version | Rôle |
|---|---|---|
| [Podman Desktop](https://podman-desktop.io) | 1.10+ | Moteur de containers |
| [Kind](https://kind.sigs.k8s.io) | 0.23+ | Kubernetes local |
| [kubectl](https://kubernetes.io/docs/tasks/tools/) | 1.29+ | CLI Kubernetes |
| [Helm](https://helm.sh/docs/intro/install/) | 3.x | Gestionnaire de packages K8s (KEDA) |
| [Pulumi CLI](https://www.pulumi.com/docs/install/) | 3.x | Infrastructure as Code |
| [.NET SDK](https://dotnet.microsoft.com) | 10.0 | Build applicatif et Pulumi C# |
| [k6](https://k6.io/docs/get-started/installation/) | 0.50+ | Tests de charge (optionnel) |

> **Windows** : définir `KIND_EXPERIMENTAL_PROVIDER=podman` (variable d'environnement permanente) avant toute commande `kind`.

---

## Démarrage rapide — script automatisé

```bash
# Depuis la racine du projet — fait tout : cluster, images, pulumi up
scripts\k8s_complete_launch.cmd
```

---

## Démarrage rapide — étapes manuelles

### 1. Créer le cluster

```bash
set KIND_EXPERIMENTAL_PROVIDER=podman
kind create cluster --name ecommerce --config kind-config.yaml
kubectl config use-context kind-ecommerce
```

### 2. Pré-charger les images

```bash
# Infra
podman pull postgres:16-alpine && kind load docker-image postgres:16-alpine --name ecommerce
podman pull rabbitmq:4.3.1-management-alpine && kind load docker-image rabbitmq:4.3.1-management-alpine --name ecommerce
podman pull redis:7-alpine && kind load docker-image redis:7-alpine --name ecommerce

# KEDA — pré-charger pour éviter le timeout du Helm install
podman pull ghcr.io/kedacore/keda:2.17.0 && kind load docker-image ghcr.io/kedacore/keda:2.17.0 --name ecommerce
podman pull ghcr.io/kedacore/keda-metrics-apiserver:2.17.0 && kind load docker-image ghcr.io/kedacore/keda-metrics-apiserver:2.17.0 --name ecommerce
podman pull ghcr.io/kedacore/keda-admission-webhooks:2.17.0 && kind load docker-image ghcr.io/kedacore/keda-admission-webhooks:2.17.0 --name ecommerce

# Images applicatives
podman build -t localhost/ecommerce/order-api:dev     -f docker/order-api/Dockerfile .
podman build -t localhost/ecommerce/inventory-api:dev -f docker/inventory-api/Dockerfile .
podman build -t localhost/ecommerce/gateway:dev       -f docker/gateway/Dockerfile .
kind load docker-image localhost/ecommerce/order-api:dev     --name ecommerce
kind load docker-image localhost/ecommerce/inventory-api:dev --name ecommerce
kind load docker-image localhost/ecommerce/gateway:dev       --name ecommerce
```

### 3. Déployer

```bash
cd infra/Ecommerce.Infra
pulumi login --local
pulumi stack init dev
pulumi up --yes
```

### 4. Vérifier

```bash
kubectl get pods -n ecommerce -w          # attendre que tous soient Running
kubectl get pods -n keda                  # KEDA operator
kubectl get scaledobject -n ecommerce     # ScaledObject inventory-api

curl http://localhost:30080/health         # gateway
curl http://localhost:30080/health/orders      # order-api
curl http://localhost:30080/health/inventory   # inventory-api
```

**Accès** :
- Gateway : `http://localhost:30080`
- Grafana : `http://localhost:30030`
- Jaeger : `http://localhost:30686`

---

## Arrêter / relancer sans perdre les données

```bash
# Stopper (cluster conservé, données PostgreSQL préservées)
podman stop ecommerce-control-plane

# Relancer
podman start ecommerce-control-plane
```

---

## Réinitialisation complète

Voir [Kubernetes & Déploiement → Reset complet](docs/kubernetes.md#reset-complet).
