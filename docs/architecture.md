# Architecture

## Diagramme général

```
┌──────────────────────────────────────────────────────────────────┐
│  Client HTTP (curl / navigateur / Scalar UI)                     │
└─────────────────────────┬────────────────────────────────────────┘
                          │ localhost:30080  (NodePort Kind)
              ┌───────────▼────────────┐
              │   Gateway (YARP :8080) │
              │   /orders/**           │
              │   /inventory/**        │
              └──────┬──────────┬──────┘
                     │          │
        ┌────────────▼──┐  ┌───▼──────────────┐
        │  Order API    │  │  Inventory API   │
        │  :8080        │  │  :8080           │
        └───────┬───────┘  └────────┬─────────┘
                │                   │
                └────────┬──────────┘
                         │ MassTransit / RabbitMQ
                ┌────────▼──────────┐
                │    RabbitMQ       │
                │    :5672 (AMQP)   │
                │    :15672 (UI)    │
                └───────────────────┘

        ┌───────────────────┐    ┌──────────────────────┐
        │  order-db         │    │  inventory-db        │
        │  PostgreSQL :5432 │    │  PostgreSQL :5432    │
        │  StatefulSet      │    │  StatefulSet         │
        └───────────────────┘    └──────────────────────┘
```

---

## Flux métier principal

```
1. POST /api/carts  (via Gateway → Order API)
   └─ AddToCartCommandHandler
      └─ Publish(ProductAddedToCartEvent) → RabbitMQ

2. RabbitMQ → Inventory API
   └─ ProductAddedToCartConsumer
      └─ ReserveProductCommandHandler
         └─ product.Reserve(qty)
            └─ Reservation créée avec TTL (ex : 10 min)

3. ReservationExpiryService  (background, toutes les N secondes)
   └─ détecte Reservation.IsExpired
      └─ product.ReleaseReservation(qty)
         └─ Publish(ProductReservationExpiredEvent) → RabbitMQ

4. RabbitMQ → Order API
   └─ ProductReservationExpiredConsumer
      └─ cart.RemoveItem(productId)
```

---

## Clean Architecture par service

Chaque service respecte la règle de dépendance : `Domain ← Application ← Infrastructure ← Api`

| Couche | Dépendances | Contenu |
|---|---|---|
| `Domain` | aucune | Entités, Domain events, Exceptions, Enums |
| `Application` | Domain, Contracts | Commands/Queries MediatR, Validators FluentValidation, Consumers MassTransit |
| `Infrastructure` | Application | EF Core DbContext + Migrations, MassTransit config, Background services |
| `Api` | Application, Infrastructure | Program.cs, Minimal API endpoints, appsettings |

### Pipelines transversaux (MediatR)

- **`ValidationBehaviour<,>`** : exécute FluentValidation avant chaque handler — retourne `400` si invalide
- **`LoggingBehaviour<,>`** : log structuré automatique de chaque commande/query (durée, paramètres)

---

## Structure du projet

```
pulumi-k8s/
├── src/
│   ├── Services/
│   │   ├── Order/                    # Service commande
│   │   │   ├── Order.Domain/         # Entités : Cart, CartItem
│   │   │   ├── Order.Application/    # Commands, Queries, Consumers
│   │   │   ├── Order.Infrastructure/ # EF Core, MassTransit, Migrations
│   │   │   └── Order.Api/            # Minimal API, Program.cs
│   │   └── Inventory/                # Service inventaire
│   │       ├── Inventory.Domain/     # Entités : Product, Reservation
│   │       ├── Inventory.Application/
│   │       ├── Inventory.Infrastructure/ # ReservationExpiryService
│   │       └── Inventory.Api/
│   ├── Shared/
│   │   └── Ecommerce.Contracts/      # Events MassTransit partagés
│   │       ├── ProductAddedToCartEvent
│   │       └── ProductReservationExpiredEvent
│   └── Gateway/
│       └── Ecommerce.Gateway/        # YARP reverse proxy
├── tests/
│   ├── Order.Application.IntegrationTests/
│   └── Inventory.Application.IntegrationTests/
├── infra/
│   └── Ecommerce.Infra/              # Pulumi C# — déploiement Kind
│       ├── EcommerceStack.cs
│       └── Resources/
│           ├── SecretsResources.cs
│           ├── DatabaseResources.cs  (StatefulSet)
│           ├── MessagingResources.cs
│           ├── OrderServiceResources.cs
│           ├── InventoryServiceResources.cs
│           ├── GatewayResources.cs
│           └── HpaArgs.cs
├── docker/                           # Dockerfiles multi-stage
├── docker-compose.yml                # Stack dev locale (podman-compose)
├── kind-config.yaml                  # Config cluster Kind
└── Ecommerce.sln
```

---

## Endpoints

### Gateway (`:30080` en K8s, `:8080` en local)

| Préfixe | Service cible |
|---|---|
| `/orders/**` | Order API |
| `/inventory/**` | Inventory API |
| `/health/orders` · `/health/inventory` | Proxy YARP vers le `/health` des APIs |
| `/health/live` | Liveness probe — **process gateway seul** (jamais l'aval) |
| `/health/ready` | Readiness probe — dépendances **propres** de la gateway (exclut l'aval) |
| `/health` · `/health/upstream` | Agrégat santé de l'aval — dashboards/monitoring, **pas** les probes |

> ⚠️ Les probes de la gateway sont volontairement **« shallow »** : elles ne dépendent
> **pas** de la santé d'order-api/inventory-api. Sinon une panne aval ferait crashlooper
> (liveness) et déréférencer (readiness) la gateway, propageant l'incident au lieu de le
> contenir. YARP renvoie déjà 503 par route via son `HealthCheck` actif/passif par cluster.

### Order API

| Méthode | Route | Description |
|---|---|---|
| `POST` | `/api/carts` | Ajouter un produit au panier (crée ou met à jour) |
| `GET` | `/api/carts/{id}` | Récupérer un panier par ID |
| `GET` | `/health` | Liveness probe |
| `GET` | `/health/ready` | Readiness probe (DB + RabbitMQ) |

**Exemple — ajouter au panier via Gateway (K8s) :**

```bash
curl -X POST http://localhost:30080/orders/api/carts \
  -H "Content-Type: application/json" \
  -d '{
    "customerId": "11111111-0000-0000-0000-000000000001",
    "productId":  "1816247d-ed4e-4f22-b4e9-5bcc1cecd2da",
    "productName": "Montre Connectée",
    "unitPrice": 29.99,
    "quantity": 2
  }'
```

### Inventory API

| Méthode | Route | Description |
|---|---|---|
| `GET` | `/api/products` | Lister tous les produits et leur stock |
| `GET` | `/api/products/{id}` | Récupérer un produit |
| `GET` | `/api/products/{id}/reservations` | Lister les réservations actives d'un produit |
| `GET` | `/health` | Liveness probe |
| `GET` | `/health/ready` | Readiness probe (DB + RabbitMQ) |

---

## Configuration applicative

### Réservation de stock (Inventory API)

| Clé config | Description | Défaut |
|---|---|---|
| `Reservation:TtlMinutes` | Durée de vie d'une réservation | `10` min |
| `Reservation:CheckIntervalSeconds` | Intervalle du background service | `30` s |

En développement local (`appsettings.Development.json`), ces valeurs sont réduites (2 min / 10 sec).  
En Kubernetes, elles sont configurées dans `infra/Ecommerce.Infra/Pulumi.dev.yaml` :

```yaml
reservation:ttlMinutes: "10"
reservation:checkIntervalSeconds: "30"
```

---

## Décisions de design notables

| Sujet | Choix | Raison |
|---|---|---|
| Messaging | MassTransit + RabbitMQ | Abstraction bus, nommage queues automatique |
| ORM | EF Core + Npgsql | Clean Architecture, migrations versionnées |
| API style | Minimal API | Légèreté, handlers explicites |
| Reverse proxy | YARP | Intégration .NET native, config dynamic |
| Logs | Serilog JSON compact | Adapté aux log aggregators (Loki, ELK) |
| Traces | OpenTelemetry OTLP | Compatible Jaeger, Tempo, Datadog |
| Stockage DB en K8s | StatefulSet + PVC | Données persistées entre `pulumi up` |
