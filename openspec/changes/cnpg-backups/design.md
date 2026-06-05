## Context

Les Clusters CNPG (`DatabaseResources.cs`) sont déployés sans `spec.backup`. Le failover
HA est en place mais aucune sauvegarde → pas de protection contre corruption/erreur
humaine. CNPG intègre Barman (object store) : WAL archiving + base backups + PITR via
`bootstrap.recovery`. Ce change l'active en prod.

## Goals / Non-Goals

**Goals:**
- WAL archiving + base backups (ScheduledBackup) vers object storage, par cluster.
- Rétention configurable + PITR testé.
- Accès par identité de charge.

**Non-Goals:**
- Backups en dev (pas d'object storage local).
- Backup RabbitMQ/Redis.

## Decisions

- **Barman object store natif CNPG** plutôt qu'un outil tiers : intégré, supporté,
  pilote le WAL + base backups + PITR sans plugin. Alternative écartée : `pg_dump` cron
  (pas de PITR, lourd).
- **`ScheduledBackup` CRD** pour la planification (quotidien par défaut), appliqué via
  le même pattern kubectl/Command que les Cluster/Pooler.
- **Identité de charge (IRSA/WI)** pour l'accès au bucket — pas de clé S3 statique.
  Alternative écartée : secret de clés S3 — rejeté (secret durable à protéger).
- **Activé par config prod** (`cnpg:backupEnabled` + bucket/région/rétention) ; dev off.
- **Buckets séparés ou préfixes par cluster** (order-db / inventory-db) pour isoler les
  séries de WAL.

## Risks / Trade-offs

- [Backups non testés = fausse sécurité] → inclure un **test de PITR** dans la procédure
  (restaurer dans un cluster jetable et valider).
- [Coût/volume WAL sur object storage] → rétention + lifecycle policy du bucket.
- [Mauvaise identité/permissions bucket = échec silencieux d'archivage] → alerte sur
  l'échec de backup (lien avec `observability-alerting`) + vérifier `kubectl get backups`.
- [Restore = nouveau cluster, pas in-place] → documenter clairement la procédure (CNPG
  restaure via un nouveau Cluster en mode recovery).

## Migration Plan

1. Provisionner le bucket + l'identité de charge (IRSA/WI).
2. Ajouter `backup.barmanObjectStore` aux Clusters + `ScheduledBackup` (gardé par config).
3. `pulumi up` prod → vérifier l'archivage WAL + un premier base backup.
4. **Tester un PITR** sur un cluster de restauration jetable.
5. Rollback : retirer la config backup (les clusters continuent sans archivage).

## Open Questions

- Fréquence des base backups (quotidien suffisant ? + RPO visé via WAL).
- Rétention exacte (30j ? conformité ?).
- Un bucket commun avec préfixes vs un bucket par cluster.
