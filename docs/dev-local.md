# Développement local & Tests

## Sommaire

- [Stack locale avec podman-compose](#stack-locale-avec-podman-compose)
- [Tests d'intégration](#tests-dintégration)
- [Migrations EF Core](#migrations-ef-core)
- [Documentation OpenAPI (Scalar)](#documentation-openapi-scalar)
- [Observabilité](#observabilité)

---

## Stack locale avec podman-compose

Pour développer sans Kubernetes, la stack complète peut être démarrée avec `podman-compose`.  
Les services tournent directement en containers Podman, accessible sur `localhost`.

```bash
# Démarrer tous les services (DB, RabbitMQ, APIs, Gateway)
podman compose up -d

# Vérifier l'état
podman compose ps

# Arrêter
podman compose down

# Arrêter et supprimer les volumes (repart de zéro)
podman compose down -v
```

### Accès

| Service | URL |
|---|---|
| Gateway | `http://localhost:8080` |
| Order API (OpenAPI) | `http://localhost:5001/scalar` |
| Inventory API (OpenAPI) | `http://localhost:5002/scalar` |
| RabbitMQ Management UI | `http://localhost:15672` (guest / guest) |
| Redis | `localhost:6379` (CLI : `podman exec -it <redis-container> redis-cli`) |
| Grafana | `http://localhost:3000` (sans login) |
| Jaeger UI | `http://localhost:16686` |
| Prometheus | `http://localhost:9090` |

> **Redis** : en dev local avec podman-compose, inventory-api se connecte à Redis via `ConnectionStrings__Redis: "redis:6379"`.  
> En cas d'indisponibilité Redis, l'API bascule automatiquement sur `IMemoryCache` (fallback sans dégradation fonctionnelle).

### Exemple d'appel direct (dev local, sans gateway)

```bash
# Ajouter au panier via Order API directement
curl -X POST http://localhost:5001/api/carts \
  -H "Content-Type: application/json" \
  -d '{
    "customerId": "11111111-0000-0000-0000-000000000001",
    "productId":  "1816247d-ed4e-4f22-b4e9-5bcc1cecd2da",
    "productName": "Montre Connectée",
    "unitPrice": 29.99,
    "quantity": 2
  }'

# Récupérer un panier
curl http://localhost:5001/api/carts/11111111-0000-0000-0000-000000000001

# Lister les produits et leur stock
curl http://localhost:5002/api/products
```

---

## Tests d'intégration

Les tests utilisent **Testcontainers** pour démarrer PostgreSQL et RabbitMQ dans des containers éphémères.  
Aucune infrastructure externe n'est nécessaire — les containers sont créés et détruits par le test runner.

**Respawn** réinitialise la base de données entre chaque test (truncate rapide sans recréer les tables).

### Lancer les tests

```bash
# Tests du service Order
dotnet test tests/Order.Application.IntegrationTests

# Tests du service Inventory
dotnet test tests/Inventory.Application.IntegrationTests

# Tous les tests avec rapport
dotnet test --logger "console;verbosity=detailed"
```

### Prérequis

- Podman Desktop en cours d'exécution (Testcontainers utilise le socket Podman)
- Variable `DOCKER_HOST` pointant vers le socket Podman si nécessaire :

```bash
# Windows avec Podman Desktop
set DOCKER_HOST=npipe:////./pipe/podman-machine-default
```

### Structure des tests

```
tests/
├── Order.Application.IntegrationTests/
│   ├── Fixtures/
│   │   └── IntegrationTestWebAppFactory.cs   # Setup WebApplicationFactory + Testcontainers
│   └── Features/
│       └── Carts/
│           └── AddToCartTests.cs
└── Inventory.Application.IntegrationTests/
    └── ...
```

---

## Migrations EF Core

Les migrations sont appliquées **automatiquement au démarrage** de l'application (`MigrateAsync()` dans `Program.cs`) dans tous les environnements.

> En Kubernetes, l'init container `wait-for-db` attend que PostgreSQL soit prêt avant que l'API démarre ses migrations.

### Créer une nouvelle migration

```bash
# Order API
dotnet ef migrations add <NomMigration> \
  --project src/Services/Order/Order.Infrastructure \
  --startup-project src/Services/Order/Order.Api

# Inventory API
dotnet ef migrations add <NomMigration> \
  --project src/Services/Inventory/Inventory.Infrastructure \
  --startup-project src/Services/Inventory/Inventory.Api
```

### Supprimer la dernière migration (si pas encore appliquée)

```bash
dotnet ef migrations remove \
  --project src/Services/Order/Order.Infrastructure \
  --startup-project src/Services/Order/Order.Api
```

### Appliquer manuellement (dev local)

```bash
dotnet ef database update \
  --project src/Services/Order/Order.Infrastructure \
  --startup-project src/Services/Order/Order.Api \
  --connection "Host=localhost;Port=5432;Database=order_db;Username=postgres;Password=postgres"
```

### Générer le SQL sans appliquer

```bash
dotnet ef migrations script \
  --project src/Services/Order/Order.Infrastructure \
  --startup-project src/Services/Order/Order.Api \
  --output migrations-order.sql
```

---

## Documentation OpenAPI (Scalar)

En environnement `Development`, chaque API expose sa documentation interactive via **Scalar** :

| API | URL |
|---|---|
| Order API | `http://localhost:5001/scalar` |
| Inventory API | `http://localhost:5002/scalar` |

Scalar offre une UI moderne pour tester les endpoints directement depuis le navigateur.

> En Kubernetes, les APIs ne sont pas exposées directement — passer par le Gateway (`http://localhost:30080`).

---

## Observabilité

### Logs structurés (Serilog)

Les APIs produisent des logs JSON sur `stdout` avec `RenderedCompactJsonFormatter` (Serilog).  
Format compatible avec les aggregators : **Loki**, **ELK**, **Datadog**.

```bash
# Voir les logs en direct (K8s)
kubectl logs -n ecommerce deploy/order-api -f

# Filtrer par niveau
kubectl logs -n ecommerce deploy/order-api | python -c "
import sys, json
for line in sys.stdin:
    try:
        log = json.loads(line)
        if log.get('@l') in ('Error', 'Fatal', 'Warning'):
            print(line.strip())
    except: pass
"
```

### Traces distribuées et métriques (OpenTelemetry)

Les APIs exportent traces et métriques via **OTLP** vers un backend configurable.

| Composant | Technologie | Export |
|---|---|---|
| Traces | OpenTelemetry → OTLP | `OTLP_ENDPOINT:4317` |
| Métriques | OpenTelemetry → OTLP | `OTLP_ENDPOINT:4317` |
| Logs | Serilog JSON compact | stdout |

### Observabilité avec podman-compose

La stack OTel Collector + Jaeger + Prometheus + Grafana est incluse dans `docker-compose.yml`.  
Elle démarre automatiquement avec `podman compose up -d`.

```bash
# Vérifier que les containers observabilité sont up
podman compose ps | grep -E "jaeger|otel|prometheus|grafana"
```

Voir [docs/observability.md](observability.md) pour les détails (dashboards, PromQL, traces).

### Health checks

| Endpoint | Type | Vérifie |
|---|---|---|
| `/health` | Liveness | L'application répond |
| `/health/ready` | Readiness | DB + RabbitMQ accessibles |

```bash
# Exemple
curl http://localhost:5001/health/ready
# {"status":"Healthy","results":{"database":{"status":"Healthy"},"rabbitmq":{"status":"Healthy"}}}
```

En Kubernetes, ces endpoints sont utilisés par les probes K8s :
- **Liveness** : si `/health` échoue 3 fois, le pod est redémarré
- **Readiness** : si `/health/ready` échoue, le pod est retiré du Service (pas de trafic)
