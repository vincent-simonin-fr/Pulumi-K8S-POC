## 1. Pré-requis cloud

- [ ] 1.1 Provisionner un bucket object storage (S3/GCS/Azure) + lifecycle/rétention
- [ ] 1.2 Configurer l'identité de charge (IRSA / Workload Identity) du ServiceAccount CNPG
- [ ] 1.3 Ajouter la config Pulumi (`cnpg:backupEnabled`, bucket, région, rétention) — prod on / dev off

## 2. Backup + WAL archiving

- [ ] 2.1 Ajouter `spec.backup.barmanObjectStore` au YAML des Clusters CNPG (DatabaseResources.cs), gardé par config
- [ ] 2.2 Activer le WAL archiving (paramètres CNPG) vers l'object storage
- [ ] 2.3 Créer une ressource `ScheduledBackup` par cluster (base backup quotidien)

## 3. Validation backup

- [ ] 3.1 `pulumi up` prod → vérifier `kubectl get backups -n ecommerce` (Completed) + WAL archivés
- [ ] 3.2 Vérifier l'application de la rétention

## 4. Restauration (PITR)

- [ ] 4.1 Documenter la procédure de restauration (nouveau Cluster avec `bootstrap.recovery`)
- [ ] 4.2 Tester un PITR sur un cluster jetable (restaurer à un instant avant un DROP volontaire)
- [ ] 4.3 Ajouter un test/checklist DR reproductible

## 5. Infra & documentation

- [ ] 5.1 Mettre à jour Pulumi : `Pulumi.prod.yaml` (backup on) / `Pulumi.dev.yaml` (off)
- [ ] 5.2 Documenter dans `production.md` (backup + PITR) et `infrastructure.md`
- [ ] 5.3 (Lien) Alerte sur échec de backup — voir `observability-alerting`
