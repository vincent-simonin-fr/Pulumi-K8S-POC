## Context

`KubePrometheusStackResources.cs` déploie Prometheus + Grafana + exporters, mais
**Alertmanager est désactivé** et aucune `PrometheusRule` n'est définie. Les métriques
nécessaires existent déjà (OTel app, CNPG, RabbitMQ, postgres_exporter,
kube-state-metrics, node-exporter) — il manque les règles + la notification.

## Goals / Non-Goals

**Goals:**
- Alertmanager actif + récepteur (Slack/PagerDuty) routé par sévérité.
- Jeu de PrometheusRule infra + applicatif.
- Seuils configurables ; dev sans récepteur réel.

**Non-Goals:**
- SLO/error budgets formels.
- Récepteur réel en dev.

## Decisions

- **Alertmanager du chart kube-prometheus-stack** (l'activer) plutôt qu'un déploiement
  séparé : intégré, géré par l'Operator. Alternative écartée : Alertmanager standalone.
- **PrometheusRule via CRD** (découvertes par l'Operator) appliquées avec le même pattern
  kubectl/Command que les ServiceMonitors. Alternative écartée : règles inline dans les
  values du chart — moins lisible/évolutif.
- **Récepteur via secret** (`alerting:receiverSecret`/`--secret`) injecté dans la config
  Alertmanager ; routes par `severity` (critical/warning).
- **Seuils en config** (`alerting:p95LatencyMs`, `alerting:pgPoolWarnPct`…) pour ajuster
  sans recompiler.
- **Activé prod, minimal dev** : en dev, soit Alertmanager off, soit récepteur "null".

## Risks / Trace-offs

- [Alertes trop bruyantes = fatigue → ignorées] → seuils + `for:` (durée) calibrés ;
  commencer conservateur, affiner.
- [Faux positifs au démarrage (pods pas encore Ready)] → `for:` suffisant + exclure les
  phases de déploiement.
- [Secret récepteur exposé] → `--secret` uniquement, jamais dans le YAML versionné.
- [Couverture incomplète] → démarrer par les règles critiques (CrashLoop, CNPG primary,
  RabbitMQ quorum), étendre ensuite (latence, saturation).

## Migration Plan

1. Activer Alertmanager + configurer un récepteur de test (webhook/Slack canal de test).
2. Ajouter les PrometheusRule critiques, vérifier le passage `firing` (ex. tuer un pod).
3. Ajouter les règles applicatives (latence p95, saturation pool) avec seuils config.
4. Brancher le récepteur prod réel (PagerDuty/Slack) via secret + routes par sévérité.
5. Rollback : désactiver Alertmanager / retirer les règles (sans impact sur les apps).

## Open Questions

- Récepteur cible prod : Slack, PagerDuty, Opsgenie ? (routage par sévérité)
- Seuils initiaux (p95, % saturation pool, fenêtres `for:`).
- Faut-il des silences/maintenance windows pour les déploiements planifiés ?
