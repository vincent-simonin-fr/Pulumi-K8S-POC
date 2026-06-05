## Why

L'observabilité actuelle = **dashboards Grafana** (tu *vois* les métriques), mais
**zéro alerte** : Alertmanager est désactivé et il n'existe aucune `PrometheusRule`.
Rien ne te **réveille** quand un pod crashe, qu'un primary CNPG tombe ou que la latence
explose. En prod, l'observabilité sans alerting ne sert qu'au post-mortem.

## What Changes

- **Activer Alertmanager** (chart kube-prometheus-stack, actuellement off).
- Ajouter un **jeu de `PrometheusRule`** couvrant au minimum :
  - pods en `CrashLoopBackOff` / non Ready,
  - **CNPG primary down** / réplication en retard,
  - **RabbitMQ quorum perdu** / nœud down,
  - **latence p95** applicative au-dessus d'un seuil,
  - **saturation du pool PG** (connexions proches de `max_connections`),
  - échec de **backup CNPG** (lien avec `cnpg-backups`).
- Configurer un **récepteur** (Slack / PagerDuty / webhook) avec routage par sévérité.
- **Dev** : alerting minimal ou désactivé ; **prod** : actif avec récepteur réel.

## Capabilities

### New Capabilities
- `observability-alerting`: règles d'alerte Prometheus + Alertmanager + routage vers un
  récepteur, pour être notifié des incidents (pas seulement les observer).

### Modified Capabilities
<!-- Aucune spec métier modifiée : extension de la capacité observabilité. -->

## Impact

- **Infra Pulumi** : `KubePrometheusStackResources.cs` (activer `alertmanager` + config
  récepteur/routes) ; nouvelles `PrometheusRule` (ConfigMap/CRD via le pattern existant).
- **Secrets** : URL/token du récepteur (Slack/PagerDuty) en `--secret` (jamais committé).
- **Config** : seuils (latence p95, saturation pool) + sévérités dans `Pulumi.*.yaml`.
- **Services applicatifs** : aucun impact direct (les métriques existent déjà : OTel,
  CNPG, RabbitMQ, postgres_exporter, kube-state-metrics).
- **Docs** : `observability.md` (alertes + runbook), `production.md`.

## Non-goals

- Pas de récepteur réel en **dev** (au plus un log/webhook factice).
- Ne couvre pas les SLO/error budgets formels (peut venir après).
- Pas de changement de comportement métier.
