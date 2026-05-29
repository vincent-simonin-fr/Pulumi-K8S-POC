# Tests de charge (k6)

## Sommaire

- [Installation](#installation)
- [Structure](#structure)
- [Lancer un scénario](#lancer-un-scénario)
- [Scénarios disponibles](#scénarios-disponibles)
- [Intégration Prometheus → Grafana](#intégration-prometheus--grafana)
- [Ce qu'observer dans Grafana](#ce-quobserver-dans-grafana)

---

## Installation

```bash
# Windows
winget install k6

# macOS
brew install k6

# Linux
snap install k6
```

---

## Structure

```
tests/Ecommerce.LoadTests/
├── helpers/
│   ├── endpoints.js      # requêtes HTTP réutilisables (addToCart, getInventory, mixedWorkload)
│   └── thresholds.js     # SLOs partagés (p95 < 500ms, errors < 1%)
└── scenarios/
    ├── baseline.js       # 1 VU, 2 min — référence avant tout test
    ├── load.js           # 0→50 VU, charge nominale
    ├── stress.js         # 0→200 VU, recherche du point de rupture
    └── spike.js          # pic brutal à 300 VU (flash sale)
```

---

## Lancer un scénario

Depuis la racine du projet :

```bash
# Dev local (Kind — NodePort 30080)
k6 run tests/Ecommerce.LoadTests/scenarios/baseline.js

# Prod (domaine configurable via BASE_URL)
k6 run tests/Ecommerce.LoadTests/scenarios/load.js \
   --env BASE_URL=https://wizzz.com

# Avec export des métriques vers Prometheus (voir section Intégration)
K6_PROMETHEUS_RW_SERVER_URL=http://localhost:9090/api/v1/write \
k6 run --out experimental-prometheus-rw \
   tests/Ecommerce.LoadTests/scenarios/load.js
```

---

## Scénarios disponibles

### baseline.js — Référence

**Quand** : avant tout autre test, après chaque déploiement.

| Paramètre | Valeur |
|---|---|
| VU | 1 |
| Durée | 2 min |
| Charge | GET /inventory + POST /orders alternés |
| SLOs | p(95) < 500ms, errors < 1% |

### load.js — Charge nominale

**Quand** : validation du comportement sous charge réaliste.

| Étape | VU | Durée |
|---|---|---|
| Warm-up | 0 → 50 | 2 min |
| Maintien | 50 | 5 min |
| Cool-down | 50 → 0 | 1 min |

Charge : 80% `GET /inventory` + 20% `POST /orders`.

### stress.js — Point de rupture

**Quand** : dimensionnement du cluster, validation de l'HPA.

| Étape | VU | Durée |
|---|---|---|
| Palier 1 | 0 → 50 | 2 min |
| Palier 2 | 50 → 100 | 2 min |
| Palier 3 | 100 → 150 | 2 min |
| Palier 4 | 150 → 200 | 2 min |
| Récupération | 200 → 0 | 2 min |

SLOs assouplis (p95 < 2s, errors < 10%) — l'objectif est d'observer la dégradation, pas de la masquer.

### spike.js — Flash sale

**Quand** : simulation d'un pic de trafic soudain (promo, événement).

| Étape | VU | Durée |
|---|---|---|
| Stable | 1 | 30s |
| Spike | 1 → 300 | 10s |
| Maintien pic | 300 | 1 min |
| Retour | 300 → 1 | 10s |
| Récupération | 1 | 1 min |

---

## Intégration Prometheus → Grafana

Prometheus est configuré avec `--web.enable-remote-write-receiver` (activé dans `ObservabilityResources.cs`).
k6 peut y pousser ses métriques directement, sans infrastructure supplémentaire.

```bash
# 1. Ouvrir un tunnel vers Prometheus (dev Kind)
kubectl port-forward -n monitoring svc/prometheus 9090:9090

# 2. Lancer k6 avec l'output Prometheus
K6_PROMETHEUS_RW_SERVER_URL=http://localhost:9090/api/v1/write \
k6 run --out experimental-prometheus-rw \
   tests/Ecommerce.LoadTests/scenarios/load.js
```

Les métriques k6 apparaissent dans Prometheus avec le préfixe `k6_` :

| Métrique k6 | Description |
|---|---|
| `k6_vus` | Utilisateurs virtuels actifs |
| `k6_http_reqs_total` | Requêtes totales |
| `k6_http_req_duration_seconds` | Distribution des temps de réponse |
| `k6_http_req_failed_total` | Requêtes en erreur |
| `k6_http_req_blocked_seconds` | Temps bloqué (connexion TCP) |

---

## Ce qu'observer dans Grafana

Ouvrir Grafana (http://localhost:30030) **pendant** l'exécution du test.

### Dashboard Services — RED Metrics
- **Request rate** : doit augmenter proportionnellement aux VU
- **Error rate 5xx** : doit rester < 1% en LoadScenario
- **P95 Latency** : doit rester < 500ms en LoadScenario

### Dashboard .NET Runtime
- **Thread pool queue length** : si > 0, saturation du thread pool
- **GC Gen2 / min** : si augmente fortement, pression mémoire excessive
- **Working set memory** : ne doit pas croître indéfiniment (memory leak)

### Dashboard PostgreSQL
- **Connexions actives** : ne doit pas atteindre `max_connections` (100 par défaut)
- **Cache hit ratio** : doit rester > 95% même sous charge

### Dashboard Kubernetes
- **HPA current / desired** : le HPA doit scaler avant la saturation
- **Pod restarts** : des restarts = OOMKill (augmenter les memory limits)
