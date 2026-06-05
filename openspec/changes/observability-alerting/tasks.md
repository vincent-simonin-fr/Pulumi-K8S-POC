## 1. Alertmanager + récepteur

- [x] 1.1 Activer Alertmanager dans `KubePrometheusStackResources.cs` (values du chart)
- [x] 1.2 Config récepteur (Slack) + routes par sévérité, credential via `--secret` (`BuildAlertmanagerValues`)
- [ ] 1.3 Vérifier la délivrance avec une alerte de test (récepteur de test) — *au `pulumi up` (prod/webhook)*

## 2. PrometheusRule — infrastructure

- [x] 2.1 Règle pods CrashLoopBackOff (`PodCrashLooping`) / not Ready (`PodNotReady`)
- [x] 2.2 Règle CNPG primary down (`CNPGNoPrimary`, `CNPGInstanceUnreachable`) / replication lag (`CNPGReplicationLag`)
- [x] 2.3 Règle RabbitMQ down / quorum perdu (`RabbitMQDown`)
- [x] 2.4 Règle échec de backup CNPG (`CNPGBackupFailing`, lien avec cnpg-backups)

## 3. PrometheusRule — applicatif

- [x] 3.1 Règle latence p95 > seuil (`HighRequestLatencyP95`, métriques OTel)
- [x] 3.2 Règle saturation pool PostgreSQL (`PostgresConnectionPoolSaturation`)
- [x] 3.3 Seuils configurables via Pulumi (`alerting:p95LatencyMs`, `alerting:pgPoolWarnPct`)

## 4. Validation

- [x] 4.1 Déclencher une règle → `firing` + reçue par Alertmanager (validé live : `PodCrashLooping`
      pending→firing à 5m, alerte reçue par Alertmanager)
- [x] 4.2 Routage par sévérité validé (alerte `critical` → récepteur `critical`). *Envoi Slack réel
      = au `pulumi up` prod avec `alerting:slackWebhook`.*
- [x] 4.3 Checklist de validation des alertes (cf. `docs/observability.md` § Alerting → « Valider une alerte »)

## 5. Infra & documentation

- [x] 5.1 Pulumi : `Pulumi.prod.yaml` (alerting on + récepteur) / `Pulumi.dev.yaml` (off + seuils)
- [x] 5.2 Documenter dans `observability.md` (liste des alertes + runbook) et `production.md`
- [x] 5.3 Préchargement Nuke : image Alertmanager ajoutée à `PreloadImageList` (`build/Build.cs`)
