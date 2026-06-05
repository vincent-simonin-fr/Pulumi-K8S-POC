## ADDED Requirements

### Requirement: RBAC de moindre privilège sur les Secrets (prod)
En production, le système SHALL retirer l'accès `get`/`list` sur les Secrets des rôles
par défaut et n'accorder cet accès qu'à des Role/ServiceAccount dédiés et restreints.

#### Scenario: Lecture refusée à un rôle non autorisé
- **WHEN** un utilisateur ou ServiceAccount sans permission explicite tente
  `kubectl get secret rabbitmq-credentials -n ecommerce`
- **THEN** l'API server refuse (Forbidden)

#### Scenario: Lecture autorisée à un rôle dédié
- **WHEN** un ServiceAccount explicitement autorisé sur ce Secret le lit
- **THEN** l'accès est accordé, limité aux secrets de son périmètre

### Requirement: Chiffrement des Secrets au repos
En production, le système SHALL chiffrer les Secrets dans etcd via une
EncryptionConfiguration adossée à un provider KMS (plus de stockage en base64 clair).

#### Scenario: Secret illisible dans un backup etcd
- **WHEN** un Secret est lu directement depuis le stockage etcd / un backup
- **THEN** sa valeur est chiffrée (non récupérable sans la clé KMS)

#### Scenario: Lecture applicative inchangée
- **WHEN** un pod autorisé consomme le Secret (env/volume) ou via l'API
- **THEN** la valeur déchiffrée est servie normalement (transparent pour l'app)

### Requirement: Audit des accès aux Secrets
En production, le système SHALL journaliser les accès en lecture aux Secrets
(qui, quoi, quand) pour la traçabilité.

#### Scenario: Accès tracé
- **WHEN** un appelant lit un Secret via l'API
- **THEN** une entrée d'audit est produite (sujet, ressource, horodatage)

### Requirement: Sourcing des secrets statiques restants depuis Vault
Le système SHALL permettre de sourcer `rabbitmq-credentials`, le mot de passe admin
Grafana et le mot de passe de l'utilisateur `app` depuis Vault (KV v2) via VSO, en
conservant les MÊMES noms de Secrets K8s consommés par les pods.

#### Scenario: Secret matérialisé depuis Vault sans impact applicatif
- **WHEN** le sourcing Vault est activé pour `rabbitmq-credentials`
- **THEN** VSO matérialise un Secret K8s de même nom et clés
- **AND** les pods consommateurs (rabbitmq, order-api, inventory-api) fonctionnent sans
  modification

#### Scenario: Rotation du secret maître dans Vault
- **WHEN** la valeur est modifiée dans Vault KV
- **THEN** VSO met à jour le Secret K8s et redémarre les consommateurs ciblés

### Requirement: Confort dev préservé
Le système SHALL conserver, en dev, les Secrets statiques et leur récupération via
`kubectl get secret`, sans imposer le durcissement prod.

#### Scenario: Dev inchangé
- **WHEN** la stack est déployée en dev
- **THEN** les secrets statiques restent lisibles via kubectl (commodité locale)
- **AND** aucune EncryptionConfiguration/RBAC restrictive n'est requise
