# Observabilite

## Sommaire

- [Architecture](#architecture)
- [Acces aux UIs](#acces-aux-uis)
- [Dashboards Grafana](#dashboards-grafana)
- [Alerting (Alertmanager + PrometheusRule)](#alerting-alertmanager--prometheusrule)
- [Jaeger — traces distribuees](#jaeger--traces-distribuees)
- [Verification du pipeline](#verification-du-pipeline)
- [Kubernetes Dashboard](#kubernetes-dashboard)

---

## Architecture

Deux briques distinctes :
- **Tracing** : OTel Collector + Jaeger, gérés directement par Pulumi (`ObservabilityResources`).
- **Métriques** : chart Helm **kube-prometheus-stack** (Prometheus Operator + Grafana +
  node-exporter + kube-state-metrics), gérés par `KubePrometheusStackResources`.

```
order-api  ──┐
inventory  ──┤  OTLP gRPC :4317   ┌──────────────────────────┐
gateway    ──┘ ─────────────────► │  OpenTelemetry Collector  │
                                  │  namespace: monitoring    │
                                  └──────┬──────────┬─────────┘
                              traces    │          │  metriques :8889
                                        ▼          ▼
                                     Jaeger   Prometheus (Operator)
                                     :16686     ▲   ▲   ▲   ▲
                                        │       │   │   │   └── ServiceMonitor rabbitmq (x3 nœuds)
                                        │       │   │   └────── ServiceMonitor cnpg (x2)
                                        │       │   └────────── ServiceMonitor postgres-exporter (x2)
                                        │       └────────────── ServiceMonitor otel / argocd
                                        │       (+ node-exporter, kube-state-metrics : chart)
                                        └────┬──────────────┘
                                             ▼
                                          Grafana (chart)
                                        6 dashboards (sidecar)
```

**Le modèle de scrape** : avec le Prometheus Operator, chaque cible déclare un
**ServiceMonitor** (CRD) que l'Operator découvre automatiquement — plus de
`scrape_configs` central à éditer + reload. Voir `ServiceMonitorResources.cs`.

| Composant               | Role                                              | Namespace   | Géré par |
|-------------------------|---------------------------------------------------|-------------|----------|
| OTel Collector          | Recoit OTLP, route vers Jaeger + Prometheus       | monitoring  | Pulumi (ObservabilityResources) |
| Jaeger all-in-one       | Stockage et visualisation des traces              | monitoring  | Pulumi (ObservabilityResources) |
| Prometheus (Operator)   | Scrape via ServiceMonitors (découverte auto)      | monitoring  | chart kube-prometheus-stack |
| Grafana                 | 6 dashboards (sidecar) + datasource Jaeger        | monitoring  | chart kube-prometheus-stack |
| node-exporter           | CPU/RAM/Disque/Reseau des noeuds                  | monitoring  | chart kube-prometheus-stack |
| kube-state-metrics      | Etat pods/deployments/HPA en metriques Prometheus | monitoring  | chart kube-prometheus-stack |
| postgres_exporter x2    | Metriques PostgreSQL (order-db + inventory-db)    | ecommerce   | Pulumi (DatabaseResources) |
| ServiceMonitors (6)     | Déclarent les cibles à scraper pour l'Operator    | monitoring  | Pulumi (ServiceMonitorResources) |

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
| **Grafana**  | http://localhost:30030       | admin / mot de passe généré (voir ci-dessous) |
| **Jaeger**   | http://localhost:30686       | Sans login |
| Prometheus   | `kubectl port-forward` uniquement | — |

Mot de passe Grafana (généré par le chart) — depuis **Git Bash** :

```bash
kubectl get secret kube-prometheus-stack-grafana -n monitoring \
  -o jsonpath="{.data.admin-password}" | base64 -d
```

Prometheus (UI / targets) :

```bash
kubectl port-forward -n monitoring svc/kube-prometheus-stack-prometheus 9090:9090
# http://localhost:9090  ·  http://localhost:9090/targets
```

> En prod : Grafana passe en ClusterIP derrière l'Ingress (`grafana.{domain}`),
> mot de passe via `pulumi config set --secret observability:grafanaAdminPassword`.
> Voir [access.md](access.md) pour tous les accès/credentials.

---

## Dashboards Grafana

Les 6 dashboards sont chargés automatiquement par le **sidecar** du Grafana du chart
(tout ConfigMap labellisé `grafana_dashboard: "1"` — voir `GrafanaDashboardsResources.cs`).
Acces : http://localhost:30030 → **Dashboards** → **Browse**.

Les 6 : Services (RED), PostgreSQL, .NET Runtime, Kubernetes & Infra, CNPG, RabbitMQ.
Tous référencent la datasource Prometheus par `uid: prometheus` (fixé dans le chart).

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

### 5. CNPG (PostgreSQL HA)

Panels : état des clusters, réplication streaming, connexions, cache, transactions par
cluster (label `cluster=order-db|inventory-db` posé par le ServiceMonitor cnpg).
Métriques `cnpg_*` exposées par chaque instance Postgres (port 9187).

### 6. RabbitMQ (Cluster & Messaging)

Panels : nœuds up, messages ready/unacked, débit publish/deliver/ack, connexions &
channels par nœud, mémoire/disque par nœud. Métriques `rabbitmq_*` du plugin natif
(port 15692), scrapées sur les 3 nœuds via le ServiceMonitor rabbitmq.

---

## Alerting (Alertmanager + PrometheusRule)

Les dashboards te permettent de **voir** ; l'alerting te **réveille**. Alertmanager est
fourni par le chart kube-prometheus-stack, et les règles sont des `PrometheusRule`
(CRD) découvertes par l'Operator (`AlertingResources.cs`).

- **Dev** : `alerting:enabled=false` par défaut (pas d'Alertmanager, pas de notif).
- **Prod** : `alerting:enabled=true` + webhook Slack (secret), routage par **sévérité**.

### Activer / configurer

```bash
# Activer (dev, pour valider les règles localement — sans envoi externe)
pulumi config set alerting:enabled true

# Prod : récepteur Slack (SECRET, jamais committé)
pulumi config set --secret alerting:slackWebhook https://hooks.slack.com/services/XXX/YYY/ZZZ
pulumi config set alerting:slackChannel "#alerts"

# Seuils ajustables sans recompiler
pulumi config set alerting:p95LatencyMs 500    # latence p95
pulumi config set alerting:pgPoolWarnPct 80    # saturation pool PG
```

Sans `slackWebhook`, Alertmanager tourne mais les récepteurs sont « null » : les alertes
passent `firing` (visibles dans l'UI) mais **rien n'est envoyé dehors** — idéal pour valider.

### Règles déployées

| Alerte | Sévérité | Condition (résumé) | `for` |
|---|---|---|---|
| `PodCrashLooping` | critical | un conteneur `ecommerce` en `CrashLoopBackOff` | 5m |
| `PodNotReady` | warning | un pod `ecommerce` non Ready | 15m |
| `CNPGInstanceUnreachable` | critical | `cnpg_collector_up == 0` (instance injoignable) | 2m |
| `CNPGNoPrimary` | critical | toutes les instances d'un cluster en recovery (pas de primary) | 2m |
| `CNPGReplicationLag` | warning | lag de réplication > 30s | 5m |
| `CNPGBackupFailing` | warning | dernier backup échoué plus récent que le dernier dispo | 15m |
| `RabbitMQDown` | critical | aucun nœud RabbitMQ sain scrapé (nœud down / quorum perdu) | 2m |
| `HighRequestLatencyP95` | warning | p95 d'un service > seuil (`p95LatencyMs`) | 10m |
| `PostgresConnectionPoolSaturation` | warning | connexions > `pgPoolWarnPct`% de `max_connections` | 5m |

> Routage : `severity=critical` et `severity=warning` partent vers le même canal Slack par
> défaut ; en prod on peut router `critical → PagerDuty` (ajouter un récepteur + une route).

### Accéder à Alertmanager

```bash
kubectl port-forward -n monitoring svc/kube-prometheus-stack-alertmanager 9093:9093
# → http://localhost:9093   (alertes actives, silences, statut des récepteurs)
```

Les règles et leur état sont aussi dans Prometheus : http://localhost:9090/alerts.

### Runbook — réponse aux alertes

| Alerte | Première action |
|---|---|
| `PodCrashLooping` | `kubectl logs <pod> -n ecommerce --previous` ; vérifier creds dynamiques Vault (cf. `docs/vault.md`, 28P01) |
| `CNPGNoPrimary` | `kubectl get cluster -n ecommerce` ; `kubectl cnpg status <cluster>` ; vérifier le failover |
| `CNPGBackupFailing` | console MinIO + `kubectl get backups -n ecommerce` ; cf. `docs/backups.md` |
| `RabbitMQDown` | `kubectl get pods -n ecommerce -l app=rabbitmq` ; en cluster, vérifier le quorum |
| `HighRequestLatencyP95` | Jaeger (traces lentes) + dashboard Services ; vérifier DB/pool |
| `PostgresConnectionPoolSaturation` | dashboard PostgreSQL ; vérifier le Pooler / fuites de connexions |

### Valider une alerte (test)

```bash
# Provoquer un VRAI CrashLoopBackOff (conteneur qui démarre puis sort en erreur).
# ⚠️ `set image <image-bidon>` donne un ImagePullBackOff — un reason DIFFÉRENT de
#    CrashLoopBackOff → la règle PodCrashLooping ne matcherait PAS. Il faut un crash :
kubectl patch deploy/order-api -n ecommerce --type=json \
  -p='[{"op":"add","path":"/spec/template/spec/containers/0/command","value":["/bin/sh","-c","exit 1"]}]'

# Suivre la montée : pending (expr vraie) puis firing après le `for: 5m`
kubectl get pods -n ecommerce -l app=order-api -w        # → CrashLoopBackOff en ~1-2 min
# → http://localhost:9090/alerts        : PodCrashLooping pending → firing (~5 min)
# → http://localhost:9093 (Alertmanager): l'alerte arrive ; notif Slack si webhook configuré

# Rollback (retire le command override) :
kubectl patch deploy/order-api -n ecommerce --type=json \
  -p='[{"op":"remove","path":"/spec/template/spec/containers/0/command"}]'
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
# 1. Pods monitoring (chart kube-prometheus-stack + OTel/Jaeger)
kubectl get pods -n monitoring
# ATTENDU : kube-prometheus-stack-operator, prometheus-kube-prometheus-stack-prometheus-0,
#           kube-prometheus-stack-grafana, ...-kube-state-metrics, ...-node-exporter,
#           otel-collector, jaeger → Running

# 2. ServiceMonitors déclarés
kubectl get servicemonitors -n monitoring
# ATTENDU : otel-collector, postgres-exporters, cnpg-order, cnpg-inventory,
#           rabbitmq, argocd (+ ceux du chart)

# 3. Prometheus targets (toutes UP)
kubectl port-forward -n monitoring svc/kube-prometheus-stack-prometheus 9090:9090
# http://localhost:9090/targets → toutes les cibles des ServiceMonitors ci-dessus

# 4. Grafana dashboards
# http://localhost:30030 → Dashboards → Browse → 6 dashboards

# 5. Traces Jaeger
# http://localhost:30686 → Service: gateway → Find Traces

# 6. Logs du collecteur (si traces manquantes)
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
