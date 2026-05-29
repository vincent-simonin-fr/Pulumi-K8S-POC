## ADDED Requirements

### Requirement: CNPG Cluster replaces StatefulSet
Un `Cluster` CNPG SHALL remplacer chaque StatefulSet PostgreSQL. Les clusters sont appliqués via `kubectl apply --server-side -f -` (Pulumi.Command) pour contourner le cache GVK du provider Pulumi.Kubernetes. Deux clusters sont créés : `order-db` (database `order_db`) et `inventory-db` (database `inventory_db`).

#### Scenario: Cluster creation on first deploy
- **WHEN** `pulumi up` est exécuté avec `CnpgResources` (Helm) déjà `Ready`
- **THEN** les `Cluster` CRDs `order-db` et `inventory-db` sont créées dans le namespace `ecommerce`, CNPG initialise PostgreSQL via `initdb`, les services `order-db-rw`, `order-db-ro`, `order-db-r` (et équivalents inventory) sont créés automatiquement

#### Scenario: Cluster is accessible on -rw service
- **WHEN** le cluster est en état `Ready`
- **THEN** `order-db-rw:5432` répond à une connexion psql avec les credentials du secret de bootstrap

### Requirement: Cluster instances configurable
Le nombre d'instances SHALL être configurable par cluster via `Pulumi.*.yaml` (`cnpg:orderInstances`, `cnpg:inventoryInstances`). Défaut dev : `1`. Prod recommandé : `3` (1 primary + 2 replicas avec streaming replication).

#### Scenario: Single instance in dev
- **WHEN** `cnpg:orderInstances: "1"` est défini
- **THEN** un seul pod `order-db-1` est créé, le service `order-db-rw` pointe vers lui

#### Scenario: Three instances in prod
- **WHEN** `cnpg:orderInstances: "3"` est défini
- **THEN** trois pods `order-db-1`, `order-db-2`, `order-db-3` sont créés, le primary est elected automatiquement, les replicas reçoivent les WAL par streaming replication

### Requirement: PostgreSQL max_connections elevated
Le cluster PostgreSQL SHALL démarrer avec `max_connections=200` (vs 100 par défaut) pour accommoder les connexions PgBouncer + exporters + migrations directes.

#### Scenario: PG accepts 200 connections
- **WHEN** le cluster est initialisé avec `max_connections: "200"`
- **THEN** `SHOW max_connections` retourne `200`

### Requirement: Bootstrap password provided via secret
Le mot de passe superuser PostgreSQL SHALL être fourni à CNPG via un K8s Secret dédié (`order-db-pg-password`, `inventory-db-pg-password`) contenant la clé `password`. CNPG lit ce secret au `initdb` et l'applique au compte postgres.

#### Scenario: App connects with configured password
- **WHEN** le cluster est initialisé avec `order-db-pg-password` contenant `password: postgres`
- **THEN** une connexion `psql -U postgres -W postgres` réussit contre `order-db-rw:5432`

### Requirement: Init container uses -rw service for health check
L'init container `wait-for-dependencies` des pods applicatifs SHALL utiliser le service `{cluster}-rw` (ex: `order-db-rw`) pour le health check psql, et non l'ancien hostname StatefulSet (`order-db`).

#### Scenario: Init container waits for CNPG primary
- **WHEN** un pod `order-api` démarre
- **THEN** l'init container attend que `psql -h order-db-rw -U postgres -d order_db -c 'SELECT 1'` réussisse avant de laisser démarrer le container principal
