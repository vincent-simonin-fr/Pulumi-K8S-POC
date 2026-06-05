## Why

CNPG 3 instances protège d'une **panne de nœud** (failover), mais **pas** d'un
`DROP TABLE`, d'une corruption logique ou d'une suppression de cluster. **HA ≠ DR.**
Aujourd'hui il n'y a **aucune sauvegarde** : une erreur humaine ou une corruption =
perte de données irréversible. C'est le manque le plus critique pour de la vraie prod.

## What Changes

- Activer les **backups CNPG** (Barman) vers un **object storage** (S3/GCS/Azure) :
  `spec.backup.barmanObjectStore` sur les Clusters `order-db` et `inventory-db`.
- Activer le **WAL archiving** continu → permet le **Point-In-Time-Recovery (PITR)**.
- Ajouter une **`ScheduledBackup`** (base backup périodique, ex. quotidien) par cluster.
- Définir la **rétention** (ex. 30 jours) et l'accès object storage par **identité de
  charge** (IRSA / Workload Identity), pas de clé statique.
- **Dev** : désactivé par défaut (pas d'object storage local) ; activable via config.

## Capabilities

### New Capabilities
- `cnpg-backups`: sauvegarde continue (base backups + WAL) des clusters CNPG vers
  object storage, rétention, et restauration PITR.

### Modified Capabilities
<!-- Aucune spec métier modifiée : ajout d'une capacité d'infra/DR. -->

## Impact

- **Infra Pulumi** : `DatabaseResources.cs` (ajout `backup.barmanObjectStore` au YAML
  Cluster CNPG + ressource `ScheduledBackup`) ; secret/role d'accès à l'object storage.
- **Config** : bucket, région, rétention, identité (IRSA/WI) dans `Pulumi.prod.yaml`.
- **Object storage** : un bucket S3/GCS dédié (prérequis cloud).
- **Services applicatifs** : aucun impact (transparent côté apps).
- **Docs** : `production.md` (procédure backup + **PITR/restore**), `infrastructure.md`.

## Non-goals

- Pas de backup en **dev** par défaut (pas d'object storage local).
- Ne couvre pas la sauvegarde de RabbitMQ / Redis (hors périmètre).
- Pas de changement de comportement métier.
