## 1. Alertmanager + récepteur

- [ ] 1.1 Activer Alertmanager dans `KubePrometheusStackResources.cs` (values du chart)
- [ ] 1.2 Config récepteur (Slack/PagerDuty/webhook) + routes par sévérité, credential via `--secret`
- [ ] 1.3 Vérifier la délivrance avec une alerte de test (récepteur de test)

## 2. PrometheusRule — infrastructure

- [ ] 2.1 Règle pods CrashLoopBackOff / not Ready (kube-state-metrics)
- [ ] 2.2 Règle CNPG primary down / replication lag
- [ ] 2.3 Règle RabbitMQ quorum perdu / nœud down
- [ ] 2.4 Règle échec de backup CNPG (lien avec cnpg-backups)

## 3. PrometheusRule — applicatif

- [ ] 3.1 Règle latence p95 > seuil (métriques OTel http_server_request_duration)
- [ ] 3.2 Règle saturation pool PostgreSQL (connexions ~ max_connections)
- [ ] 3.3 Seuils configurables via Pulumi (`alerting:*`)

## 4. Validation

- [ ] 4.1 Déclencher chaque règle critique (ex. supprimer un pod) → alerte `firing` + notif reçue
- [ ] 4.2 Vérifier le routage par sévérité (critical vs warning)
- [ ] 4.3 Ajouter des tests/checklist de validation des alertes

## 5. Infra & documentation

- [ ] 5.1 Mettre à jour Pulumi : `Pulumi.prod.yaml` (alerting on + récepteur) / `Pulumi.dev.yaml` (off)
- [ ] 5.2 Documenter dans `observability.md` (liste des alertes + runbook) et `production.md`
- [ ] 5.3 Préchargement Nuke / images si Alertmanager nécessite une image non préchargée
