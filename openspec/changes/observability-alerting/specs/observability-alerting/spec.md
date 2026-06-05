## ADDED Requirements

### Requirement: Alertmanager actif et routé
Le système SHALL déployer Alertmanager (kube-prometheus-stack) et router les alertes
vers un récepteur configuré, avec routage par sévérité.

#### Scenario: Alerte délivrée au récepteur
- **WHEN** une PrometheusRule passe en état `firing`
- **THEN** Alertmanager envoie une notification au récepteur configuré
- **AND** le routage respecte la sévérité (ex. critical → PagerDuty, warning → Slack)

### Requirement: Règles d'alerte infrastructure
Le système SHALL définir des PrometheusRule couvrant la santé des composants critiques.

#### Scenario: Pod en CrashLoopBackOff
- **WHEN** un pod redémarre en boucle au-delà d'un seuil
- **THEN** une alerte `firing` est émise avec le pod/namespace concerné

#### Scenario: Primary CNPG indisponible
- **WHEN** un cluster CNPG n'a plus de primary sain
- **THEN** une alerte critique est émise

#### Scenario: Quorum RabbitMQ perdu
- **WHEN** le nombre de nœuds RabbitMQ disponibles passe sous le quorum
- **THEN** une alerte critique est émise

### Requirement: Règles d'alerte applicatives
Le système SHALL alerter sur la dégradation applicative (latence, saturation DB).

#### Scenario: Latence p95 élevée
- **WHEN** la latence p95 d'un service dépasse le seuil configuré pendant la fenêtre définie
- **THEN** une alerte est émise

#### Scenario: Saturation du pool PostgreSQL
- **WHEN** le nombre de connexions approche `max_connections`
- **THEN** une alerte est émise (avant l'épuisement)

### Requirement: Secret de récepteur non committé
Le système SHALL fournir l'URL/token du récepteur via secret chiffré (`--secret`),
jamais en clair dans le dépôt.

#### Scenario: Récepteur configuré sans fuite
- **WHEN** la config Alertmanager est rendue
- **THEN** le credential du récepteur provient d'un secret, pas du YAML versionné

### Requirement: Dev sans récepteur réel
Le système SHALL ne pas exiger de récepteur externe en dev.

#### Scenario: Dev
- **WHEN** la stack dev est déployée
- **THEN** l'alerting est minimal/désactivé (pas d'envoi vers un service externe)
