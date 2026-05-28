# Observabilité

## Sommaire

- [Architecture](#architecture)
- [Accès aux UIs](#accès-aux-uis)
- [Grafana — métriques](#grafana--métriques)
- [Jaeger — traces distribuées](#jaeger--traces-distribuées)
- [Dashboards recommandés](#dashboards-recommandés)
- [Vérification du pipeline](#vérification-du-pipeline)

---

## Architecture

```
order-api  ─┐
inventory   ─┤  OTLP gRPC :4317   ┌──────────────────────────┐
gateway    ─┘ ──────────────────► │  OpenTelemetry Collector  │
                                  │  namespace: monitoring    │
                                  └──────┬──────────┬─────────┘
                              traces    │          │  métriques
                                        ▼          ▼
                                     Jaeger   Prometheus
                                     :4317      :8889 (scrape)
                                     :16686     :9090
                                        │          │
                                        └────┬─────┘
                                             ▼
                                          Grafana
                                          :3000
```

| Composant            | Rôle                                           | Namespace   |
|----------------------|------------------------------------------------|-------------|
| OTel Collector       | Reçoit OTLP, route vers Jaeger + Prometheus    | monitoring  |
| Jaeger all-in-one    | Stockage et visualisation des traces           | monitoring  |
| Prometheus           | Scrape les métriques depuis le collecteur      | monitoring  |
| Grafana              | Dashboards métriques + exploration des traces  | monitoring  |

### Signaux exportés par les APIs

| Service      | Traces | Métriques | Logs    |
|--------------|--------|-----------|---------|
| order-api    | ✅ OTLP | ✅ OTLP   | stdout JSON (Serilog) |
| inventory-api| ✅ OTLP | ✅ OTLP   | stdout JSON (Serilog) |
| gateway      | ✅ OTLP | —         | stdout JSON (Serilog) |

> Les métriques du gateway ne sont pas encore configurées (traces suffisent pour le proxy).
> Les logs JSON stdout peuvent être collectés avec Loki si besoin.

---

## Accès aux UIs

> **Prérequis** : cluster Kind créé avec les extraPortMappings (voir [kubernetes.md](kubernetes.md)).

| UI           | URL                          | Accès     |
|--------------|------------------------------|-----------|
| **Grafana**  | http://localhost:30030       | Sans login (auth anonyme activée en dev) |
| **Jaeger**   | http://localhost:30686       | Sans login |
| Prometheus   | `kubectl port-forward` uniquement | — |

### Accès à Prometheus (si besoin)

```bash
kubectl port-forward -n monitoring svc/prometheus 9090:9090
# → http://localhost:9090
```

---

## Grafana — métriques

### Explorer les métriques

1. Ouvrir http://localhost:30030
2. Menu gauche → **Explore** (icône loupe)
3. Sélectionner la datasource **Prometheus**
4. Chercher des métriques préfixées `ecommerce_` :

```promql
# Durée des requêtes HTTP (percentile 95)
histogram_quantile(0.95,
  sum(rate(ecommerce_http_server_request_duration_seconds_bucket[5m]))
  by (le, http_route, service_name)
)

# Taux de requêtes par route
sum(rate(ecommerce_http_server_request_duration_seconds_count[1m]))
  by (http_route, service_name)

# Taux d'erreurs (status 5xx)
sum(rate(ecommerce_http_server_request_duration_seconds_count{http_response_status_code=~"5.."}[1m]))
  by (service_name)
```

---

## Jaeger — traces distribuées

### Rechercher une trace

1. Ouvrir http://localhost:30686
2. **Service** → sélectionner `gateway`, `order-api` ou `inventory-api`
3. **Find Traces** → cliquer sur une trace pour voir le détail

### Trace de bout en bout

Une requête via le gateway génère une trace distribuée avec plusieurs spans :

```
gateway  (span racine)
  └── order-api  (span enfant via HTTP)
        └── PostgreSQL  (span EF Core)
        └── RabbitMQ   (span MassTransit)
```

Le propagateur W3C TraceContext (activé par défaut dans .NET OTel) assure le chaînage automatique des spans entre services.

### Générer des traces de test

```bash
# Ajouter au panier → trace gateway → order-api → PostgreSQL
curl -X POST http://localhost:30080/orders \
  -H "Content-Type: application/json" \
  -d '{
    "customerId": "11111111-0000-0000-0000-000000000001",
    "productId": "1816247d-ed4e-4f22-b4e9-5bcc1cecd2da",
    "productName": "Montre Connectée",
    "unitPrice": 29.99,
    "quantity": 2
  }'

# Lister les produits → trace gateway → inventory-api → PostgreSQL
curl http://localhost:30080/inventory
```

---

## Dashboards recommandés

Importer depuis Grafana.com : **Dashboards** → **Import** → entrer l'ID.

| Dashboard                          | ID     | Description                          |
|------------------------------------|--------|--------------------------------------|
| ASP.NET Core                       | 19924  | Requêtes, latences, erreurs par route |
| .NET Runtime                       | 14792  | GC, threads, allocations mémoire     |
| OpenTelemetry Collector            | 15983  | Santé du collecteur                  |

### Import rapide

1. http://localhost:30030 → **Dashboards** → **New** → **Import**
2. Entrer l'ID du dashboard
3. Sélectionner la datasource **Prometheus**
4. Cliquer **Import**

---

## Vérification du pipeline

```bash
# 1. Vérifier que les pods monitoring sont Running
kubectl get pods -n monitoring
# ATTENDU : otel-collector, jaeger, prometheus, grafana → Running

# 2. Envoyer une requête de test
curl -X POST http://localhost:30080/orders \
  -H "Content-Type: application/json" \
  -d '{"customerId":"11111111-0000-0000-0000-000000000001","productId":"1816247d-ed4e-4f22-b4e9-5bcc1cecd2da","productName":"Test","unitPrice":10,"quantity":1}'

# 3. Vérifier les traces dans Jaeger
# http://localhost:30686 → Service: gateway → Find Traces

# 4. Vérifier les métriques dans Prometheus
kubectl port-forward -n monitoring svc/prometheus 9090:9090
# http://localhost:9090 → Targets → otel-collector doit être UP

# 5. Logs du collecteur (si les traces n'arrivent pas)
kubectl logs -n monitoring deploy/otel-collector -f
```

### Vérifier que les APIs envoient bien vers le collecteur

```bash
# L'env var doit pointer vers le collecteur
kubectl exec -n ecommerce deploy/order-api -- env | findstr OpenTelemetry
# ATTENDU : OpenTelemetry__Endpoint=http://otel-collector.monitoring.svc.cluster.local:4317
```
