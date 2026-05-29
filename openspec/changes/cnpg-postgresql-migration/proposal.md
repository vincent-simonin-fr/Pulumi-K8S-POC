## Why

Les StatefulSets PostgreSQL manuels actuels n'offrent ni haute disponibilité, ni connection pooling natif, ni gestion des backups. Avec KEDA qui peut scaler inventory-api jusqu'à 4 réplicas, chaque pod ouvre son propre pool Npgsql (jusqu'à 100 connexions par défaut), ce qui provoque des crashs `53300: sorry, too many clients already` dès que le scaling s'active — montré en production dev lors des tests de charge.

## What Changes

- **Nouveau** : opérateur CloudNativePG (CNPG) installé via Helm dans le namespace `cnpg-system`
- **Remplacement** : StatefulSet `order-db` → CNPG `Cluster` CRD `order-db` (1 instance dev, 3 prod)
- **Remplacement** : StatefulSet `inventory-db` → CNPG `Cluster` CRD `inventory-db` (1 instance dev, 3 prod)
- **Nouveau** : CNPG `Pooler` CRD `order-db-pooler` (PgBouncer session mode devant order-db)
- **Nouveau** : CNPG `Pooler` CRD `inventory-db-pooler` (PgBouncer session mode devant inventory-db)
- **Modifié** : Connection strings → `Host=order-db-pooler` / `Host=inventory-db-pooler` + `Maximum Pool Size=20`
- **Modifié** : Init containers → health-check sur `order-db-rw` / `inventory-db-rw` (services CNPG primaries)
- **Modifié** : postgres_exporter → `DATA_SOURCE_URI` pointe vers `order-db-rw` / `inventory-db-rw`
- **Modifié** : `k8s_complete_launch.cmd` → pré-chargement images CNPG operator + PostgreSQL 16 bookworm
- **BREAKING** : Données PostgreSQL perdues lors de la migration dev (fresh init acceptable — schéma recréé par EF Core)

## Capabilities

### New Capabilities

- `cnpg-operator`: Installation de l'opérateur CloudNativePG via Helm, gestionnaire du cycle de vie des Cluster et Pooler CRDs
- `pg-cluster`: Cluster PostgreSQL CNPG haute disponibilité (primary + replicas configurable) avec streaming replication, auto-failover et `max_connections=200`
- `pg-pooler`: PgBouncer connection pooler (mode session) devant chaque cluster, limitant les connexions effectives à PG à `default_pool_size=20` quelle que soit l'échelle applicative

### Modified Capabilities

- `database-persistence`: Passage de StatefulSet manuel à Cluster CNPG — même garantie de persistance (PVC), mais gestion du cycle de vie déléguée à l'opérateur CNPG

## Impact

- **Impacté** : `infra/Ecommerce.Infra/Resources/DatabaseResources.cs` — remplacement complet des StatefulSets
- **Nouveau** : `infra/Ecommerce.Infra/Resources/CnpgResources.cs` — Helm release CNPG
- **Impacté** : `infra/Ecommerce.Infra/Resources/SecretsResources.cs` — connection strings + secrets CNPG bootstrap
- **Impacté** : `infra/Ecommerce.Infra/Resources/OrderServiceResources.cs` — hostname init container
- **Impacté** : `infra/Ecommerce.Infra/Resources/InventoryServiceResources.cs` — hostname init container
- **Impacté** : `infra/Ecommerce.Infra/EcommerceStack.cs` — ajout CnpgResources, ordre de dépendances
- **Impacté** : `infra/Ecommerce.Infra/Pulumi.dev.yaml` — config cnpg:version, instances, poolMode
- **Impacté** : `scripts/k8s_complete_launch.cmd` — images CNPG operator + postgresql:16-bookworm
- **Impacté** : `docs/infrastructure.md` — mise à jour architecture DB
- **Services applicatifs** : OrderApi + InventoryApi — aucun changement de code applicatif (connection string uniquement)
- **Gateway** : non impacté
- **Contracts MassTransit** : aucun nouveau contrat nécessaire

## Non-goals

- Migration des données existantes (dev : fresh init, prod : procédure documentée séparément)
- Backups CNPG vers S3/GCS (documenté comme next step prod, non implémenté ici)
- Read replicas (CNPG les crée mais le code applicatif n'est pas mis à jour pour les utiliser)
- Mode transaction PgBouncer (incompatible EF Core prepared statements sans refacto — session mode retenu)
- Kubernetes Dashboard ou monitoring CNPG via PodMonitor (hors périmètre)
