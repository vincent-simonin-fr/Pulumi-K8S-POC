## ADDED Requirements

### Requirement: Sauvegarde continue vers object storage
Le système SHALL configurer, pour chaque cluster CNPG (`order-db`, `inventory-db`),
l'archivage WAL continu et des base backups vers un object storage (Barman).

#### Scenario: WAL archivé en continu
- **WHEN** des écritures sont effectuées sur la base
- **THEN** les segments WAL sont archivés vers l'object storage configuré

#### Scenario: Base backup planifié
- **WHEN** l'horaire de la ScheduledBackup est atteint
- **THEN** un base backup complet est poussé vers l'object storage
- **AND** il apparaît dans `kubectl get backups -n ecommerce`

### Requirement: Rétention des sauvegardes
Le système SHALL appliquer une politique de rétention configurable (ex. 30 jours) aux
sauvegardes et WAL.

#### Scenario: Purge au-delà de la rétention
- **WHEN** une sauvegarde dépasse la fenêtre de rétention
- **THEN** elle est éligible à la purge selon la politique configurée

### Requirement: Restauration Point-In-Time (PITR)
Le système SHALL permettre de restaurer un cluster à un instant T à partir d'un base
backup + WAL (recovery bootstrap CNPG).

#### Scenario: Restauration à un instant précédent
- **WHEN** un nouveau cluster est créé avec `bootstrap.recovery` ciblant un timestamp
- **THEN** la base est restaurée à cet instant (avant l'incident, ex. un DROP TABLE)

### Requirement: Accès object storage sans clé statique
Le système SHALL accéder à l'object storage via identité de charge (IRSA / Workload
Identity), sans credential statique committé.

#### Scenario: Accès par identité de charge
- **WHEN** CNPG pousse une sauvegarde
- **THEN** l'authentification utilise l'identité du ServiceAccount (pas de clé en clair)

### Requirement: Désactivé en dev
Le système SHALL laisser les backups désactivés par défaut en dev (pas d'object storage
local), activables par configuration.

#### Scenario: Dev sans backup
- **WHEN** la stack dev est déployée sans config backup
- **THEN** les clusters CNPG démarrent normalement, sans archivage
