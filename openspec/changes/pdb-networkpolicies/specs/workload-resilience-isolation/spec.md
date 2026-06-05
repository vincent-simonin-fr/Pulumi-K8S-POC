## ADDED Requirements

### Requirement: PodDisruptionBudget par service applicatif
Le système SHALL définir un PodDisruptionBudget pour `order-api`, `inventory-api` et
`gateway`, garantissant au moins une réplica disponible lors des éviction volontaires.

#### Scenario: Drain de nœud sans coupure totale
- **WHEN** un nœud portant des réplicas est drainé
- **THEN** le PDB empêche l'éviction simultanée de toutes les réplicas du service
- **AND** au moins `minAvailable` reste disponible pendant le drain

### Requirement: Isolation réseau par défaut (default-deny)
Le système SHALL appliquer une politique d'ingress par défaut refusant tout trafic non
explicitement autorisé dans le namespace `ecommerce`.

#### Scenario: Trafic non autorisé bloqué
- **WHEN** un pod arbitraire tente d'ouvrir une connexion vers CNPG/RabbitMQ/Redis sans
  autorisation
- **THEN** la connexion est refusée (sur un CNI appliquant les NetworkPolicies)

### Requirement: Autorisations réseau ciblées
Le système SHALL autoriser uniquement les flux légitimes : gateway→APIs, APIs→pooler/CNPG,
APIs→RabbitMQ, inventory→Redis, monitoring→ports métriques, et le DNS sortant.

#### Scenario: Flux légitime autorisé
- **WHEN** order-api se connecte au pooler `order-db-pooler` (ou `order-db-rw`)
- **THEN** la connexion est autorisée par la NetworkPolicy correspondante

#### Scenario: Scrape Prometheus autorisé
- **WHEN** Prometheus (namespace monitoring) scrape les ports métriques des exporters
- **THEN** le trafic est autorisé

### Requirement: Portabilité dev (CNI sans enforcement)
Le système SHALL rester fonctionnel en dev même si le CNI n'applique pas les
NetworkPolicies (Kind/kindnet) — les PDB restent applicables.

#### Scenario: Dev sur Kind
- **WHEN** la stack est déployée sur Kind (kindnet)
- **THEN** les NetworkPolicies sont présentes mais non bloquantes (no-op), et les PDB
  fonctionnent
