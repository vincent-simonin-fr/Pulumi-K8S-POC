## Context

Le projet utilise deux bases PostgreSQL (`order_db`, `inventory_db`) déployées comme StatefulSets Kubernetes manuels via Pulumi C#. Ce pattern n'offre ni HA, ni connection pooling, ni gestion des backups. Le scaling KEDA d'`inventory-api` jusqu'à 4 réplicas provoque `53300: sorry, too many clients already` car chaque pod Npgsql maintient un pool de 100 connexions par défaut et PostgreSQL plafonne à `max_connections=100`.

L'opérateur CloudNativePG (CNPG) est le standard K8s-natif pour PostgreSQL : CNCF Sandbox, adopté par EDB, utilisé en production sur AKS/EKS/GKE. Il gère le cycle de vie complet (HA, failover, backups, métriques) via des CRDs K8s (`Cluster`, `Pooler`).

Même problème de cache GVK Pulumi que pour KEDA : le provider Kubernetes ne connaît pas les CRDs CNPG installées par Helm lors du même `pulumi up`. Solution : `kubectl apply --server-side` via `Pulumi.Command.Local.Command` (pattern déjà éprouvé avec KEDA).

## Goals / Non-Goals

**Goals:**
- Remplacer les StatefulSets par des `Cluster` CNPG (HA-ready, gestion opérateur)
- Résoudre le crash `too many clients` via PgBouncer (`Pooler` CNPG, session mode)
- Rester transparent pour le code applicatif (pas de changement dans OrderApi/InventoryApi)
- Pérenniser l'architecture pour la production (3 instances, backup S3)

**Non-Goals:**
- Migration des données existantes (fresh init acceptable en dev)
- Activation des backups S3/PITR (next step prod documenté)
- Mode transaction PgBouncer (incompatible EF Core prepared statements sans refacto)
- Read replicas applicatives (CNPG les crée mais le code ne les utilise pas)
- Testcontainers — rester sur PostgreSQL standard (pas CNPG) pour les tests d'intégration

## Decisions

### D1 — PgBouncer session mode plutôt que transaction mode

**Retenu** : `poolMode: session`

**Raison** : EF Core avec Npgsql utilise des prepared statements (pipeline de performance). Le mode `transaction` de PgBouncer rend les prepared statements incompatibles, nécessitant `No Prepared Statements=true` + refacto des migrations (connexion directe séparée pour `MigrateAsync()`). Le mode `session` est transparent pour EF Core : chaque connexion applicative map sur une connexion PgBouncer pour sa durée de vie. Le gain est sur le plafonnement (`max_client_conn=200` côté app, `default_pool_size=20` connexions réelles vers PG).

**Alternative écartée** : transaction mode — gain de connexions supérieur mais rupture applicative (2 connection strings, refacto Program.cs).

---

### D2 — Pulumi.Command pour les CRDs CNPG (même pattern que KEDA)

**Retenu** : `Pulumi.Command.Local.Command` avec `kubectl apply --server-side -f -` via stdin

**Raison** : Le provider `Pulumi.Kubernetes` met en cache le discovery API (GVK list) au démarrage. Les CRDs CNPG (`Cluster`, `Pooler`) installées par Helm ne sont pas visibles dans ce cache. `ConfigGroup` échoue avec "failed to determine if GVK is namespaced". `kubectl` interroge l'API server directement à chaque exécution — solution éprouvée avec KEDA.

**Alternative écartée** : Deux `pulumi up` séquentiels (premier pour le Helm, second pour les CRDs) — trop contraignant pour l'automatisation.

---

### D3 — PostgreSQL 16 bookworm (pas alpine)

**Retenu** : `ghcr.io/cloudnative-pg/postgresql:16.6-bookworm`

**Raison** : CNPG ne fournit pas d'images alpine (debian uniquement). PostgreSQL 16 pour la cohérence avec le StatefulSet actuel (`postgres:16-alpine`). Bookworm inclut les outils système nécessaires à CNPG (pg_basebackup, pg_rewind, barman-cloud).

---

### D4 — Authentification PgBouncer via authQuery sur pg_shadow

**Retenu** : `authQuery: "SELECT usename, passwd FROM pg_shadow WHERE usename=$1"` + `authQuerySecret: {cluster}-superuser`

**Raison** : CNPG crée automatiquement le secret `{cluster}-superuser` à l'initialisation du cluster. Ce secret contient les credentials postgres superuser que PgBouncer utilise pour vérifier les mots de passe clients via la table `pg_shadow`. Standard CNPG documenté.

**Alternative écartée** : trust authentication (insécurisé), userlist.txt statique (maintenance manuelle).

---

### D5 — Séparation CnpgResources.cs / DatabaseResources.cs

**Retenu** : Nouvelle classe `CnpgResources` (Helm operator) séparée de `DatabaseResources` (Cluster + Pooler).

**Raison** : L'opérateur CNPG est indépendant des bases de données. En production, un seul opérateur gère plusieurs clusters. La séparation permet de mettre à jour l'opérateur sans toucher aux bases.

**Ordre de dépendances** :
```
CnpgResources (Helm) → DatabaseResources (Cluster + Pooler) → ServiceResources (apps)
```

## Risks / Trade-offs

**[R1] Temps d'initialisation CNPG plus long que StatefulSet** → Init container `wait-for-dependencies` attend `{cluster}-rw:5432` (psql SELECT 1). CNPG prend ~30-60s pour `initdb`. Acceptable car le mécanisme de retry existe déjà.

**[R2] Secret `{cluster}-superuser` créé après le Cluster** → Le `Pooler` référence ce secret mais il n'existe pas au moment de l'apply. Kubernetes réconcilie en retry automatiquement. Le Pooler sera en état `NotReady` pendant ~30-60s puis `Ready`. Pas d'impact sur les apps (init container attend `{cluster}-rw` direct, pas le pooler).

**[R3] Perte de données dev lors de la migration** → Les PVCs StatefulSet (`data-order-db-0`, `data-inventory-db-0`) sont supprimées avec le StatefulSet. CNPG crée de nouvelles PVCs. EF Core `MigrateAsync()` recrée le schéma. Les données de test sont perdues — acceptable.

**[R4] Images CNPG sur ghcr.io** → Mêmes contraintes que KEDA : timeout si non pré-chargées. Solution : ajouter `podman pull + kind load` dans `k8s_complete_launch.cmd`.

**[R5] Pulumi state incohérent après suppression StatefulSet** → Si `pulumi up` précédent a créé des StatefulSets trackés dans l'état Pulumi, leur suppression (retrait du code) + création simultanée des Clusters CNPG peut créer des conditions de course. Mitigation : `pulumi destroy` ou suppression manuelle des StatefulSets avant `pulumi up`.

## Migration Plan

### Dev (Kind) — procédure

```bash
# 1. Supprimer les StatefulSets existants (libère les PVCs)
kubectl delete statefulset order-db inventory-db -n ecommerce
kubectl delete pvc data-order-db-0 data-inventory-db-0 -n ecommerce

# 2. Pré-charger les images CNPG dans Kind
podman pull ghcr.io/cloudnative-pg/cloudnative-pg:1.24.0
kind load docker-image ghcr.io/cloudnative-pg/cloudnative-pg:1.24.0 --name ecommerce
podman pull ghcr.io/cloudnative-pg/postgresql:16.6-bookworm
kind load docker-image ghcr.io/cloudnative-pg/postgresql:16.6-bookworm --name ecommerce

# 3. Déployer
pulumi up --yes

# 4. Vérifier
kubectl get cluster -n ecommerce
kubectl get pooler -n ecommerce
kubectl get pods -n cnpg-system
```

### Production — considérations (hors périmètre de ce change)

- Pg_dump + restore avant migration
- Blue/green : créer nouveaux clusters CNPG en parallèle, migrer les données, basculer les connection strings
- Activer backups barman vers S3 avant toute opération

## Open Questions

Aucune — toutes les décisions techniques sont prises.
