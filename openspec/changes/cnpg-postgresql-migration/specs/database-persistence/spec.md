## MODIFIED Requirements

### Requirement: PostgreSQL data persisted across pod restarts
Les données PostgreSQL SHALL persister à travers les redémarrages de pods. Le mécanisme de persistance passe de PVC StatefulSet manuel à PVC géré par l'opérateur CNPG. Le volume de stockage est configuré à `1Gi` (dev) et géré par CNPG via `storageClass: standard` (Kind).

#### Scenario: Data survives pod restart
- **WHEN** le pod `order-db-1` est supprimé par Kubernetes
- **THEN** CNPG recrée le pod et le réattache au même PVC, les données sont intactes

#### Scenario: Data lost on cluster deletion
- **WHEN** le `Cluster` CNPG `order-db` est supprimé (`kubectl delete cluster order-db -n ecommerce`)
- **THEN** les PVCs associées sont également supprimées (CNPG gère le lifecycle), les données sont perdues — comportement attendu

## REMOVED Requirements

### Requirement: StatefulSet headless service for pod DNS
**Reason**: Le service headless (`order-db-headless`) n'est plus nécessaire. CNPG gère ses propres services (`-rw`, `-ro`, `-r`) et le DNS interne des pods de réplication via ses propres mécanismes.
**Migration**: Utiliser `order-db-rw` à la place de `order-db` ou `order-db-headless` pour accéder au primary PostgreSQL.
