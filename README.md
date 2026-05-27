# Ecommerce — Stack .NET 10 + Pulumi + Kubernetes (Kind / Podman)

Architecture microservices e-commerce de démonstration en **Clean Architecture** avec communication event-driven via **RabbitMQ / MassTransit**, déployée localement sur **Kind** avec **Podman Desktop** et provisionnée via **Pulumi**.

---

## Architecture

```
┌──────────────────────────────────────────────────────────────────┐
│  Client HTTP                                                      │
└─────────────────────────┬────────────────────────────────────────┘
                          │
              ┌───────────▼────────────┐
              │   Gateway (YARP :8080) │
              └──────┬──────────┬──────┘
                     │          │
        ┌────────────▼──┐  ┌───▼──────────────┐
        │  Order API    │  │  Inventory API   │
        │  :5001        │  │  :5002           │
        └───────┬───────┘  └────────┬─────────┘
                │                   │
                └────────┬──────────┘
                         │ RabbitMQ (events)
                ┌────────▼──────────┐
                │    RabbitMQ       │
                │    :5672 / :15672 │
                └───────────────────┘

        ┌─────────────┐    ┌──────────────────┐
        │  order-db   │    │  inventory-db    │
        │  PostgreSQL │    │  PostgreSQL      │
        └─────────────┘    └──────────────────┘
```

### Flux métier principal

1. `POST /orders/add-to-cart` → **OrderApi** crée/met à jour le panier
2. OrderApi publie `ProductAddedToCartEvent` vers RabbitMQ
3. **InventoryApi** consomme l'événement et réserve le stock (`Reservation:TtlMinutes`)
4. Après expiration, InventoryApi publie `ProductReservationExpiredEvent`
5. **OrderApi** consomme l'événement et retire le produit du panier

---

## Prérequis

| Outil                                                          | Version min | Notes                       |
| -------------------------------------------------------------- | ----------- | --------------------------- |
| [Podman Desktop](https://podman-desktop.io)                    | 1.10+       | Moteur de containers        |
| [Kind](https://kind.sigs.k8s.io)                               | 0.23+       | Kubernetes in Docker/Podman |
| [kubectl](https://kubernetes.io/docs/tasks/tools/)             | 1.29+       | CLI Kubernetes              |
| [Pulumi CLI](https://www.pulumi.com/docs/install/)             | 3.x         | IaC                         |
| [.NET SDK](https://dotnet.microsoft.com)                       | 10.0        | SDK .NET                    |
| [podman-compose](https://github.com/containers/podman-compose) | 1.x         | Pour le dev local sans K8s  |

---

## Démarrage rapide — Dev local (podman-compose)

```bash
# 1. Cloner le projet
git clone <repo> && cd Ecommerce

# 2. Démarrer la stack complète
podman compose up -d

# 3. Vérifier que tout est healthy
podman compose ps

# 4. Accès aux APIs
# Gateway      → http://localhost:8080/inventory
# Order API    → http://localhost:5001/scalar  (doc OpenAPI)
# Inventory API→ http://localhost:5002/scalar  (doc OpenAPI)
# RabbitMQ UI  → http://localhost:15672  (guest/guest)
```

---

## Démarrage sur Kind (Kubernetes local via Podman)

### 1. Créer le cluster Kind

```bash
# Créer un cluster Kind avec mapping du port 30080
cat <<EOF > kind-config.yaml
kind: Cluster
apiVersion: kind.x-k8s.io/v1alpha4
nodes:
  - role: control-plane
    extraPortMappings:
      - containerPort: 30080
        hostPort: 30080
        protocol: TCP
EOF

kind create cluster --name ecommerce --config kind-config.yaml
kubectl config use-context kind-ecommerce
```

### 2. Construire et charger les images

```bash
# Depuis la racine du projet
podman build -t ecommerce/order-api:dev     -f docker/order-api/Dockerfile .
podman build -t ecommerce/inventory-api:dev -f docker/inventory-api/Dockerfile .
podman build -t ecommerce/gateway:dev       -f docker/gateway/Dockerfile .

# Charger dans Kind (Kind utilise sa propre registry interne)
kind load docker-image ecommerce/order-api:dev     --name ecommerce
kind load docker-image ecommerce/inventory-api:dev --name ecommerce
kind load docker-image ecommerce/gateway:dev       --name ecommerce
```

### 3. Déployer avec Pulumi

```bash
cd infra/Ecommerce.Infra

# Initialiser Pulumi (local backend, pas de compte requis)
pulumi login --local
pulumi stack init dev

# Déployer
pulumi up --stack dev

# Vérifier les outputs
pulumi stack output
```

### 4. Accéder à la stack

```
Gateway      → http://localhost:30080
Order        → http://localhost:30080/orders/...
Inventory    → http://localhost:30080/inventory/...
```

---

## Endpoints

### Order API

| Méthode | Route             | Description                                       |
| ------- | ----------------- | ------------------------------------------------- |
| `POST`  | `/api/carts`      | Ajouter un produit au panier (crée ou met à jour) |
| `GET`   | `/api/carts/{id}` | Récupérer un panier                               |

**Exemple — ajouter au panier :**

```bash
curl -X POST http://localhost:5001/api/carts \
  -H "Content-Type: application/json" \
  -d '{
    "customerId": "11111111-0000-0000-0000-000000000001",
    "productId":  "1816247d-ed4e-4f22-b4e9-5bcc1cecd2da",
    "productName": "Montre Connectée",
    "unitPrice": 29.99,
    "quantity": 2
  }'

  curl -X POST http://localhost:8080/orders/   -H "Content-Type: application/json"   -d "{\"customerId\": \"11111111-0000-0000-0000-000000000001\", \"productId\": \"1816247d-ed4e-4f22-b4e9-5bcc1cecd2da\", \"productName\": \"Montre Connectee\", \"unitPrice\": 29.99, \"quantity\": 2}"
```

### Inventory API

| Méthode | Route                             | Description                            |
| ------- | --------------------------------- | -------------------------------------- |
| `GET`   | `/api/products`                   | Lister tous les produits et leur stock |
| `GET`   | `/api/products/{id}`              | Récupérer un produit                   |
| `GET`   | `/api/products/{id}/reservations` | Lister les réservations d'un produit   |

---

## Configuration

### Durée de réservation

La durée de réservation est configurable dans `appsettings.json` d'InventoryApi ou via variables d'environnement :

```json
{
    "Reservation": {
        "TtlMinutes": 10,
        "CheckIntervalSeconds": 30
    }
}
```

| Variable                           | Description                      | Défaut |
| ---------------------------------- | -------------------------------- | ------ |
| `Reservation:TtlMinutes`           | Durée de réservation en minutes  | `10`   |
| `Reservation:CheckIntervalSeconds` | Intervalle du background service | `30`   |

En développement (`appsettings.Development.json`) ces valeurs sont réduites (2 min / 10 sec) pour faciliter les tests.

---

## Tests

```bash
# Tests d'intégration (requiert Docker/Podman pour Testcontainers)
dotnet test tests/Order.Application.IntegrationTests
dotnet test tests/Inventory.Application.IntegrationTests
```

Les tests utilisent **Testcontainers** pour démarrer PostgreSQL et RabbitMQ dans des containers éphémères, et **Respawn** pour réinitialiser la base entre chaque test.

---

## Structure du projet

```
Ecommerce/
├── src/
│   ├── Services/
│   │   ├── Order/               # OrderApi — Clean Architecture
│   │   │   ├── Order.Domain/
│   │   │   ├── Order.Application/
│   │   │   ├── Order.Infrastructure/
│   │   │   └── Order.Api/
│   │   └── Inventory/           # InventoryApi — Clean Architecture
│   │       ├── Inventory.Domain/
│   │       ├── Inventory.Application/
│   │       ├── Inventory.Infrastructure/
│   │       └── Inventory.Api/
│   ├── Shared/
│   │   └── Ecommerce.Contracts/ # Events MassTransit partagés
│   └── Gateway/
│       └── Ecommerce.Gateway/   # YARP reverse proxy
├── tests/
│   ├── Order.Application.IntegrationTests/
│   └── Inventory.Application.IntegrationTests/
├── infra/
│   └── Ecommerce.Infra/         # Pulumi C# — déploiement Kind
├── docker/                       # Dockerfiles multi-stage
├── docker-compose.yml            # Stack dev locale (podman-compose)
└── Ecommerce.sln
```

---

## Migrations EF Core

```bash
# Order API
dotnet ef migrations add InitialCreate \
  --project src/Services/Order/Order.Infrastructure \
  --startup-project src/Services/Order/Order.Api

# Inventory API
dotnet ef migrations add InitialCreate \
  --project src/Services/Inventory/Inventory.Infrastructure \
  --startup-project src/Services/Inventory/Inventory.Api
```

En développement, les migrations sont appliquées automatiquement au démarrage.
Sur Kubernetes, un **Job init container** les applique avant le démarrage du pod.

---

## Documentation OpenAPI

En environnement `Development`, chaque API expose sa documentation via **Scalar** :

- Order API → `http://localhost:5001/scalar`
- Inventory API → `http://localhost:5002/scalar`

---

## Observabilité

| Composant          | Technologie            | Endpoint                   |
| ------------------ | ---------------------- | -------------------------- |
| Logs structurés    | Serilog (JSON compact) | stdout                     |
| Traces distribuées | OpenTelemetry → OTLP   | `OTLP:4317`                |
| Métriques          | OpenTelemetry → OTLP   | `OTLP:4317`                |
| Health checks      | ASP.NET Core           | `/health`, `/health/ready` |

Pour activer Jaeger ou une stack OTLP, configurer `OpenTelemetry:Endpoint` dans les appsettings.

---

## Supprimer le cluster

```bash
kind delete cluster --name ecommerce
```
