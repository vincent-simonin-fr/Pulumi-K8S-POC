# Ecommerce — .NET 10 · Pulumi · Kubernetes

Stack microservices e-commerce de démonstration en **Clean Architecture**, communication event-driven via **RabbitMQ / MassTransit**, déployée localement sur **Kind** avec **Podman** et provisionnée via **Pulumi C#**.

---

## Sommaire

| Documentation | Contenu |
|---|---|
| **Ce fichier** | Prérequis, démarrage rapide |
| [Architecture](docs/architecture.md) | Diagramme, flux métier, structure projet, endpoints, configuration |
| [Kubernetes & Déploiement](docs/kubernetes.md) | Cluster Kind, build images, `pulumi up`, reset, arrêt/redémarrage, Metrics Server |
| [Infrastructure Pulumi](docs/infrastructure.md) | Code Pulumi, secrets, HPA, StatefulSet PostgreSQL, ressources CPU/RAM |
| [Debugging](docs/debugging.md) | VS 2022 + Pulumi, `kubectl` diagnostics, problèmes courants |
| [Dev local & Tests](docs/dev-local.md) | `podman-compose`, tests d'intégration, migrations EF Core, OpenAPI, observabilité |

---

## Vue d'ensemble

```
Client HTTP
    │
    ▼
Gateway (YARP) ─── NodePort 30080 ─── localhost:30080
    ├── /orders/**    → Order API    → order-db   (PostgreSQL)
    └── /inventory/** → Inventory API → inventory-db (PostgreSQL)
                  ↕ events
               RabbitMQ :5672
```

Le flux principal : ajout panier → réservation stock → expiration automatique → libération stock.  
Voir [Architecture](docs/architecture.md) pour les détails.

---

## Prérequis

| Outil | Version | Rôle |
|---|---|---|
| [Podman Desktop](https://podman-desktop.io) | 1.10+ | Moteur de containers |
| [Kind](https://kind.sigs.k8s.io) | 0.23+ | Kubernetes local |
| [kubectl](https://kubernetes.io/docs/tasks/tools/) | 1.29+ | CLI Kubernetes |
| [Pulumi CLI](https://www.pulumi.com/docs/install/) | 3.x | Infrastructure as Code |
| [.NET SDK](https://dotnet.microsoft.com) | 10.0 | Build applicatif et Pulumi C# |

> **Windows** : définir `KIND_EXPERIMENTAL_PROVIDER=podman` (variable d'environnement permanente) avant toute commande `kind`.

---

## Démarrage rapide — Kind + Pulumi

### 1. Créer le cluster

```bash
# kind-config.yaml est à la racine du projet
set KIND_EXPERIMENTAL_PROVIDER=podman
kind create cluster --name ecommerce --config kind-config.yaml
kubectl config use-context kind-ecommerce
```

### 2. Construire et charger les images

```bash
# Images applicatives
podman build -t localhost/ecommerce/order-api:dev     -f docker/order-api/Dockerfile .
podman build -t localhost/ecommerce/inventory-api:dev -f docker/inventory-api/Dockerfile .
podman build -t localhost/ecommerce/gateway:dev       -f docker/gateway/Dockerfile .

# Images publiques (pré-chargées pour éviter les timeouts)
podman pull postgres:16-alpine
podman pull rabbitmq:4.3.1-management-alpine

kind load docker-image postgres:16-alpine                --name ecommerce
kind load docker-image rabbitmq:4.3.1-management-alpine  --name ecommerce
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

curl http://localhost:30080/health         # gateway
curl http://localhost:30080/health/orders      # order-api
curl http://localhost:30080/health/inventory   # inventory-api
```

**Accès** : `http://localhost:30080`

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
