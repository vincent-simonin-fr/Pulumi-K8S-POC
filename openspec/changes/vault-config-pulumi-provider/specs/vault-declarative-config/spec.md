## ADDED Requirements

### Requirement: Aiguillage du mode de configuration Vault
Le système SHALL permettre de choisir, par configuration, entre la configuration
de Vault par Job in-cluster (dev) et par le provider déclaratif `pulumi-vault` (prod),
via la clé `vault:configMode` (valeurs `job` | `provider`).

#### Scenario: Mode provider en production
- **WHEN** `vault:configMode = provider` et `pulumi up` est exécuté
- **THEN** la configuration de Vault est appliquée par des ressources `pulumi-vault`
  et le Job de bootstrap in-cluster N'EST PAS déployé

#### Scenario: Mode job en dev (défaut)
- **WHEN** `vault:configMode` est absent ou vaut `job`
- **THEN** le comportement dev actuel (Job in-cluster) est conservé et aucune
  ressource `pulumi-vault` n'est créée

### Requirement: Auth backend Kubernetes déclaré
Le système SHALL déclarer l'auth method Kubernetes de Vault via `pulumi-vault`, de
sorte que les pods s'authentifient auprès de Vault par leur ServiceAccount.

#### Scenario: Authentification d'un pod par ServiceAccount
- **WHEN** un pod portant un rôle Vault autorisé présente le JWT de son ServiceAccount
- **THEN** Vault émet un token applicatif portant les policies du rôle
- **AND** un ServiceAccount non autorisé est refusé

### Requirement: Database secrets engine connecté à CNPG
Le système SHALL déclarer un database secrets engine PostgreSQL pointant vers les
clusters CNPG (`order-db`, `inventory-db`), avec le SQL de création et de révocation
des rôles éphémères.

#### Scenario: Génération de credentials dynamiques
- **WHEN** un consommateur autorisé demande des credentials sur le rôle dynamique
- **THEN** Vault crée un utilisateur PostgreSQL temporaire avec un TTL borné
- **AND** l'utilisateur est automatiquement révoqué à l'expiration du bail

#### Scenario: Révocation à l'expiration du bail
- **WHEN** le bail d'un credential dynamique expire ou est révoqué
- **THEN** l'utilisateur PostgreSQL correspondant est supprimé de la base

### Requirement: Policies de moindre privilège
Le système SHALL associer à chaque rôle une policy Vault limitant l'accès aux seuls
chemins nécessaires (database role concerné), sans privilège superflu.

#### Scenario: Accès restreint au chemin du rôle
- **WHEN** un token applicatif tente de lire un chemin Vault hors de sa policy
- **THEN** Vault refuse l'accès (denied)

### Requirement: Pré-requis de joignabilité et d'authentification du provider
Le système SHALL documenter et exiger, en mode `provider`, que Vault soit joignable
depuis l'hôte Pulumi (Ingress/port-forward) et qu'un token Vault d'administration
soit fourni au provider sans être committé.

#### Scenario: Vault injoignable en mode provider
- **WHEN** `vault:configMode = provider` mais Vault n'est pas joignable depuis l'hôte
- **THEN** `pulumi up` échoue avec un message explicite invitant à exposer Vault
  ou à utiliser le mode `job`

#### Scenario: Token d'administration absent
- **WHEN** le mode `provider` est actif mais aucun token Vault n'est fourni
- **THEN** `pulumi up` échoue avant toute tentative de configuration
