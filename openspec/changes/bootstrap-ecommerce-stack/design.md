# Design : Bootstrap de la stack Ecommerce

## Architecture globale

```
Client
  └─ Gateway (YARP :8080)
       ├─ /orders/** → OrderApi (:5001)
       └─ /inventory/** → InventoryApi (:5002)

OrderApi                     InventoryApi
  Domain                       Domain
  Application ←── MassTransit ──→ Application
  Infrastructure               Infrastructure
    EF + PostgreSQL               EF + PostgreSQL
    MassTransit/RabbitMQ          MassTransit/RabbitMQ
                                  ReservationExpiryService
  Api (Minimal API)            Api (Minimal API)
```

## Clean Architecture par service

Chaque service respecte la règle de dépendance (Domain ← Application ← Infrastructure ← Api) :

| Couche | Dépendances | Contenu |
|--------|-------------|---------|
| `Domain` | aucune | Entités, Domain events, Exceptions, Enums |
| `Application` | Domain, Contracts | Commands/Queries MediatR, Validators, Consumers |
| `Infrastructure` | Application | EF Core DbContext, Migrations, MassTransit config, Background services |
| `Api` | Application, Infrastructure | Program.cs, Endpoints, appsettings |

## Flux d'événements

```
1. POST /api/carts (OrderApi)
   → AddToCartCommandHandler
   → publishEndpoint.Publish(ProductAddedToCartEvent)

2. RabbitMQ → InventoryApi
   → ProductAddedToCartConsumer
   → ReserveProductCommandHandler
   → product.Reserve(qty)
   → Reservation.Create(productId, cartId, qty, ttl)

3. ReservationExpiryService (background, toutes les CheckIntervalSeconds)
   → détecte Reservation.IsExpired
   → product.ReleaseReservation(qty)
   → publishEndpoint.Publish(ProductReservationExpiredEvent)

4. RabbitMQ → OrderApi
   → ProductReservationExpiredConsumer
   → cart.RemoveItem(productId)
```

## Décisions de design

### Pipeline MediatR
- `ValidationBehaviour<,>` : validation FluentValidation avant chaque handler
- `LoggingBehaviour<,>` : log structuré automatique de chaque commande/query

### EF Core
- snake_case pour les colonnes (`HasColumnName`)
- `IApplicationDbContext` comme interface pour faciliter les tests
- Migrations gérées par un K8s Job (Pulumi) en production, auto-apply en dev

### MassTransit
- `AddConsumer<T>()` + `cfg.ConfigureEndpoints(ctx)` — nommage automatique des queues
- Les consumers sont dans la couche Application (indépendants de l'infrastructure)

### Observabilité
- Serilog avec `RenderedCompactJsonFormatter` (JSON sur stdout, adapté aux log aggregators)
- OpenTelemetry avec export OTLP (Jaeger, Grafana Tempo, etc.)
- Health checks : `/health` (liveness) et `/health/ready` (readiness avec DB + RabbitMQ)

### Infrastructure Pulumi
- `ComponentResource` par préoccupation (DatabaseResources, MessagingResources, etc.)
- Images chargées dans Kind via `kind load docker-image`
- NodePort 30080 pour l'accès local au Gateway
