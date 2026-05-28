# Observabilite

## Sommaire

- [Architecture](#architecture)
- [Acces aux UIs](#acces-aux-uis)
- [Dashboards Grafana](#dashboards-grafana)
- [Jaeger — traces distribuees](#jaeger--traces-distribuees)
- [Verification du pipeline](#verification-du-pipeline)
- [Kubernetes Dashboard](#kubernetes-dashboard)

---

## Architecture

```
order-api  ──┐
inventory  ──┤  OTLP gRPC :4317   ┌──────────────────────────┐
gateway    ──┘ ─────────────────► │  OpenTelemetry Collector  │
                                  │  namespace: monitoring    │
                                  └──────┬──────────┬─────────┘
                              traces    │          │  metriques
                                        ▼          ▼
                                     Jaeger   Prometheus ◄── postgres_exporter x2
                                     :16686     :9090    ◄── kube-state-metrics
                                        │          │     ◄── node-exporter
                                        └────┬─────┘
                                             ▼
                                          Grafana
                                        4 dashboards
```

| Composant               | Role                                              | Namespace   |
|-------------------------|---------------------------------------------------|-------------|
| OTel Collector          | Recoit OTLP, route vers Jaeger + Prometheus       | monitoring  |
| Jaeger all-in-one       | Stockage et visualisation des traces              | monitoring  |
| Prometheus              | Scrape 5 jobs (collector, PG x2, KSM, node)       | monitoring  |
| Grafana                 | 4 dashboards provisionnes automatiquement         | monitoring  |
| postgres_exporter x2    | Metriques PostgreSQL (order-db + inventory-db)    | ecommerce   |
| kube-state-metrics      | Etat pods/deployments/HPA en metriques Prometheus | monitoring  |
| node-exporter           | CPU/RAM/Disque/Reseau du noeud Kind               | monitoring  |

### Signaux exportes par les APIs

| Service       | Traces | Metriques runtime | Logs    |
|---------------|--------|-------------------|---------|
| order-api     | OTLP   | OTLP + .NET GC    | stdout JSON (Serilog) |
| inventory-api | OTLP   | OTLP + .NET GC    | stdout JSON (Serilog) |
| gateway       | OTLP   | —                 | stdout JSON (Serilog) |

---

## Acces aux UIs

> **Prerequis** : cluster Kind cree avec les extraPortMappings (voir [kubernetes.md](kubernetes.md)).

| UI           | URL                          | Acces     |
|--------------|------------------------------|-----------|
| **Grafana**  | http://localhost:30030       | Sans login (auth anonyme activee en dev) |
| **Jaeger**   | http://localhost:30686       | Sans login |
| Prometheus   | `kubectl port-forward` uniquement | — |

```bash
kubectl port-forward -n monitoring svc/prometheus 9090:9090
# http://localhost:9090
```

---

## Dashboards Grafana

Les 4 dashboards sont provisionnes automatiquement au demarrage de Grafana.
Acces : http://localhost:30030 → **Dashboards** → **Browse**.

### 1. Services — RED Metrics

Panels : Request Rate / Error Rate 5xx / P95 Latency (stats production),
Rate + Errors + Latence P50/P95/P99 over time, Top 10 routes lentes, Requetes actives.

**Metriques cles** (prefix `ecommerce_`) :
```promql
# Taux de requetes par service
sum(rate(ecommerce_http_server_request_duration_seconds_count[5m])) by (service_name)

# Taux d'erreurs 5xx
sum(rate(ecommerce_http_server_request_duration_seconds_count{http_response_status_code=~"5.."}[5m])) by (service_name)

# Latence P95 globale
histogram_quantile(0.95, sum(rate(ecommerce_http_server_request_duration_seconds_bucket[5m])) by (le))
```

### 2. PostgreSQL

Panels : Connexions actives / Cache hit ratio / Commits/s / Taille DB (stats),
Connexions par base, Transactions (commits vs rollbacks), Cache hit over time, Deadlocks/min,
Tuples INSERT/UPDATE/DELETE.

**Metriques cles** (postgres_exporter, pas de prefix) :
```promql
# Connexions actives
pg_stat_activity_count{state="active", datname!~"template.*|postgres"}

# Cache hit ratio
100 * rate(pg_stat_database_blks_hit{datname="order_db"}[5m]) /
  (rate(pg_stat_database_blks_hit{datname="order_db"}[5m]) + rate(pg_stat_database_blks_read{datname="order_db"}[5m]))
```

### 3. .NET Runtime

Panels : GC Gen0/Gen1/Gen2 par minute / Exceptions par minute (stats),
Heap size par generation (Gen0/Gen1/Gen2/LOH/POH), Allocation rate, Working set memory,
GC pause ratio, Thread pool threads et queue length.

> **Note implementation** : utilise le meter natif `System.Runtime` integre a .NET 9/10
> (via `.AddMeter("System.Runtime")` dans Program.cs) — pas de package externe requis.

**Metriques cles** (prefix `ecommerce_`, meter `System.Runtime`) :
```promql
# GC Gen0 par minute
60 * sum(rate(ecommerce_dotnet_gc_collections_total{gc_heap_generation="gen0"}[5m]))

# Heap size par generation (derniere collecte GC)
ecommerce_dotnet_gc_last_collection_heap_size_bytes by (gc_heap_generation, service_name)

# Allocation rate (bytes/s)
sum(rate(ecommerce_dotnet_gc_heap_total_allocated_bytes_total[5m])) by (service_name)

# Working set memory
ecommerce_dotnet_process_memory_working_set_bytes by (service_name)

# Exceptions par minute
60 * sum(rate(ecommerce_dotnet_exceptions_total[5m])) by (service_name)

# Thread pool threads
ecommerce_dotnet_thread_pool_thread_count by (service_name)
```

### 4. Kubernetes & Infrastructure

Panels :
- **Pods** : etat par namespace, pods avec le plus de restarts, restarts over time
- **HPA** : current / desired / max replicas (order-api, inventory-api, gateway)
- **Resources** : CPU et Memory requests par pod (ecommerce)
- **Node** : gauges CPU % / RAM % / Disk %, network in/out

**Metriques cles** (kube-state-metrics et node-exporter, pas de prefix) :
```promql
# Etat des pods
kube_pod_status_phase{namespace="ecommerce"}

# HPA replicas
kube_horizontalpodautoscaler_status_current_replicas{namespace="ecommerce"}

# CPU noeud %
100 - (avg(rate(node_cpu_seconds_total{mode="idle"}[5m])) * 100)

# RAM noeud %
100 * (1 - node_memory_MemAvailable_bytes / node_memory_MemTotal_bytes)
```

---

## Jaeger — traces distribuees

### Trace de bout en bout

```
gateway  (span racine)
  └── order-api  (span enfant via HTTP)
        └── PostgreSQL  (span EF Core)
        └── RabbitMQ   (span MassTransit)
```

### Generer des traces de test

```bash
# Ajouter au panier → trace gateway → order-api → PostgreSQL
curl -X POST http://localhost:30080/orders \
  -H "Content-Type: application/json" \
  -d '{"customerId":"11111111-0000-0000-0000-000000000001","productId":"1816247d-ed4e-4f22-b4e9-5bcc1cecd2da","productName":"Montre","unitPrice":29.99,"quantity":2}'

# Lister les produits → trace gateway → inventory-api → PostgreSQL
curl http://localhost:30080/inventory
```

---

## Verification du pipeline

```bash
# 1. Pods monitoring + exporters
kubectl get pods -n monitoring
kubectl get pods -n ecommerce | findstr exporter
# ATTENDU : otel-collector, jaeger, prometheus, grafana, ksm, node-exporter → Running
#           postgres-exporter-order, postgres-exporter-inventory → Running

# 2. Prometheus targets (tous UP)
kubectl port-forward -n monitoring svc/prometheus 9090:9090
# http://localhost:9090/targets → 5 jobs : otel-collector, postgres-order,
#   postgres-inventory, kube-state-metrics, node-exporter

# 3. Grafana dashboards
# http://localhost:30030 → Dashboards → Browse → 4 dashboards dans "ecommerce"

# 4. Traces Jaeger
# http://localhost:30686 → Service: gateway → Find Traces

# 5. Logs du collecteur (si traces manquantes)
kubectl logs -n monitoring deploy/otel-collector -f
```

---

## Kubernetes Dashboard

Interface web pour l'administration du cluster (non integre a Pulumi, installation ad-hoc via Helm).

### Installation

```bash
helm repo add kubernetes-dashboard https://kubernetes.github.io/dashboard/
helm repo update
helm upgrade --install kubernetes-dashboard kubernetes-dashboard/kubernetes-dashboard \
  --namespace kubernetes-dashboard --create-namespace
```

### ServiceAccount admin

```bash
kubectl apply -f - <<EOF
apiVersion: v1
kind: ServiceAccount
metadata:
  name: admin-user
  namespace: kubernetes-dashboard
---
apiVersion: rbac.authorization.k8s.io/v1
kind: ClusterRoleBinding
metadata:
  name: admin-user
roleRef:
  apiGroup: rbac.authorization.k8s.io
  kind: ClusterRole
  name: cluster-admin
subjects:
- kind: ServiceAccount
  name: admin-user
  namespace: kubernetes-dashboard
EOF
```

### Acces

```bash
# Generer un token (valable 1h par defaut)
kubectl -n kubernetes-dashboard create token admin-user

# Port-forward via kubectl proxy (plus simple que de cibler un service precis)
kubectl proxy
# Ouvrir : http://localhost:8001/api/v1/namespaces/kubernetes-dashboard/services/https:kubernetes-dashboard-kong-proxy:443/proxy/

# Alternative : port-forward direct sur le service Kong
kubectl port-forward -n kubernetes-dashboard svc/kubernetes-dashboard-kong-proxy 8443:443
# Ouvrir https://localhost:8443 et coller le token
```

> **Note** : depuis la v7, le Dashboard utilise Kong comme gateway interne — le nom du service est
> `kubernetes-dashboard-kong-proxy` (et non plus `kubernetes-dashboard`).
> Accepter l'avertissement de certificat auto-signe.
