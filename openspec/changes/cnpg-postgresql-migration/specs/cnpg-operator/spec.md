## ADDED Requirements

### Requirement: CNPG operator installed via Helm
L'opérateur CloudNativePG SHALL être installé via un Helm release Pulumi dans le namespace `cnpg-system`, en attente que tous les pods soient Ready avant de continuer (`WaitForJobs=true`, `Timeout=300s`). Le chart `cloudnative-pg` est récupéré depuis `https://cloudnative-pg.github.io/charts`.

#### Scenario: First pulumi up installs operator
- **WHEN** `pulumi up` est exécuté pour la première fois
- **THEN** le namespace `cnpg-system` est créé, l'opérateur CNPG est déployé, et les CRDs `Cluster`, `Pooler`, `Backup`, `ScheduledBackup` sont enregistrées dans l'API server K8s

#### Scenario: Operator is ready before databases
- **WHEN** l'opérateur CNPG est installé
- **THEN** `DatabaseResources` peut utiliser les CRDs CNPG (via `DependsOn` sur la Helm release)

### Requirement: CNPG version configurable
La version du chart Helm CNPG SHALL être configurable via `cnpg:version` dans `Pulumi.*.yaml` (défaut : `1.24.0`).

#### Scenario: Version override
- **WHEN** `cnpg:version: "1.25.0"` est défini dans `Pulumi.prod.yaml`
- **THEN** `pulumi up` installe le chart version 1.25.0 sans modification du code C#

### Requirement: CNPG images pre-loaded in Kind
Les images CNPG (opérateur + PostgreSQL 16 bookworm) SHALL être pré-chargées dans Kind via `k8s_complete_launch.cmd` pour éviter les timeouts lors du pull depuis `ghcr.io`.

#### Scenario: Launch script pre-loads images
- **WHEN** `scripts/k8s_complete_launch.cmd` est exécuté
- **THEN** `ghcr.io/cloudnative-pg/cloudnative-pg:{version}` et `ghcr.io/cloudnative-pg/postgresql:16.6-bookworm` sont disponibles localement dans Kind sans accès réseau
