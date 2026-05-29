## ADDED Requirements

### Requirement: PgBouncer Pooler deployed per cluster
Un `Pooler` CNPG (PgBouncer) SHALL être déployé devant chaque cluster (`order-db-pooler`, `inventory-db-pooler`) dans le namespace `ecommerce`. Le Pooler est appliqué via `kubectl apply --server-side -f -` dans le même `Pulumi.Command` que le Cluster correspondant.

#### Scenario: Pooler service created
- **WHEN** le Pooler CNPG est appliqué et le cluster est Ready
- **THEN** un service `order-db-pooler:5432` est disponible dans le namespace `ecommerce` et accepte des connexions PostgreSQL

#### Scenario: Pooler proxies to primary
- **WHEN** une connexion arrive sur `order-db-pooler:5432`
- **THEN** PgBouncer la route vers `order-db-rw:5432` (primary du cluster)

### Requirement: Session pooling mode
Le Pooler SHALL utiliser `poolMode: session` pour la compatibilité EF Core (prepared statements activés par défaut dans Npgsql). En mode session, chaque connexion client obtient une connexion PG dédiée pour sa durée de vie.

#### Scenario: EF Core migrations work through pooler
- **WHEN** `MigrateAsync()` s'exécute au démarrage de l'API en se connectant à `order-db-pooler:5432`
- **THEN** les migrations EF Core s'appliquent sans erreur de prepared statement ni timeout de connexion

#### Scenario: Multiple pods connect without max_connections error
- **WHEN** 4 réplicas `inventory-api` se connectent simultanément à `inventory-db-pooler:5432` avec `Maximum Pool Size=20`
- **THEN** aucun pod ne reçoit d'erreur `53300: sorry, too many clients already`

### Requirement: Auth via pg_shadow query
Le Pooler SHALL authentifier les clients via une `authQuery` exécutée par un utilisateur de confiance, en utilisant le secret superuser créé automatiquement par CNPG (`{cluster}-superuser`).

#### Scenario: Client authentication succeeds
- **WHEN** un client se connecte à `order-db-pooler:5432` avec `Username=postgres;Password=postgres`
- **THEN** PgBouncer valide les credentials via `SELECT usename, passwd FROM pg_shadow WHERE usename=$1` et établit la connexion

### Requirement: Connection string points to pooler
Les connection strings ASP.NET Core (`ConnectionStrings__OrderDb`, `ConnectionStrings__InventoryDb`) SHALL pointer vers les services Pooler (`Host=order-db-pooler`, `Host=inventory-db-pooler`) avec `Maximum Pool Size=20;Minimum Pool Size=0`.

#### Scenario: API uses pooler by default
- **WHEN** `order-api` démarre
- **THEN** toutes les requêtes EF Core passent par `order-db-pooler:5432` → PgBouncer → `order-db-rw:5432`

### Requirement: Pooler instances configurable
Le nombre d'instances PgBouncer SHALL être configurable via `cnpg:poolerInstances` (défaut : `1` dev, `2` prod recommandé).

#### Scenario: Single pooler in dev
- **WHEN** `cnpg:poolerInstances: "1"` est défini
- **THEN** un seul pod PgBouncer est déployé par cluster

### Requirement: postgres_exporter uses -rw service
Le `postgres_exporter` Prometheus SHALL utiliser `{cluster}-rw` comme `DATA_SOURCE_URI` (connexion directe, pas via Pooler) pour éviter les interférences avec le pool PgBouncer.

#### Scenario: Exporter scrapes metrics directly
- **WHEN** Prometheus scrape le target `postgres-exporter-order`
- **THEN** les métriques `pg_stat_activity_count`, `pg_stat_database_*` sont disponibles et reflètent l'état réel du cluster primary
