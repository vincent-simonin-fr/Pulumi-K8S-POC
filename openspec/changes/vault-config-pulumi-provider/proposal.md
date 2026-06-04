## Why

La configuration interne de Vault (auth Kubernetes, database secrets engine, rôles
dynamiques, policies) est faite en dev par un **Job de bootstrap in-cluster** qui
exécute le CLI `vault` via un script (Option A). C'est robuste sur Kind mais
impératif, scripté en bash, peu lisible et non versionné comme du code d'infra.
Pour la **production**, on veut une configuration **déclarative, idempotente et
intégrée au flux `pulumi up`** : le provider `pulumi-vault` permet de décrire ces
objets Vault comme des ressources Pulumi, au même titre que le reste de la stack.

## What Changes

- Introduire une configuration Vault **déclarative** via le provider `pulumi-vault`
  (Pulumi.Vault), activée en prod, en remplacement du Job de bootstrap (Option A) :
  - Auth backend **Kubernetes** (les pods s'authentifient par ServiceAccount).
  - **Database secrets engine** connecté au cluster CNPG (génération de creds
    PostgreSQL éphémères).
  - **Rôles dynamiques** (SQL de création/révocation) + **policies** associées.
- Le Job in-cluster (Option A) reste la voie **dev** ; le choix dev/prod est piloté
  par configuration (ex. `vault:configMode = job | provider`).
- Pré-requis : Vault doit être **joignable depuis l'hôte Pulumi** (Ingress/port-forward)
  et un **token Vault** doit être fourni au provider (via secret/identité).

## Capabilities

### New Capabilities
- `vault-declarative-config`: configuration de Vault (auth k8s, DB secrets engine,
  rôles dynamiques, policies) déclarée en ressources `pulumi-vault`, comme cible prod.

### Modified Capabilities
<!-- Aucune modification de spec existante : ajout d'une voie de configuration
     alternative. Le serveur Vault (VaultResources) et VSO sont inchangés. -->

## Impact

- **Infra Pulumi** : nouveau provider `Pulumi.Vault` (package NuGet) ; nouvelle
  ressource (ex. `VaultDeclarativeConfigResources.cs`) ; aiguillage dans
  `EcommerceStack.cs` selon `vault:configMode`.
- **Réseau** : exposition de Vault hors cluster en prod (Ingress + TLS) ; en dev,
  l'approche provider resterait optionnelle (port-forward).
- **Secrets** : gestion d'un token Vault pour Pulumi (idéalement court-vécu / via
  identité de charge), à ne jamais committer.
- **Articulation** : s'appuie sur `VaultResources.cs` (serveur Vault) et VSO
  (livraison des secrets), et sur les clusters CNPG (`order-db`, `inventory-db`)
  comme cible du database secrets engine.
- **Services applicatifs** : aucun impact direct (OrderApi / InventoryApi / Gateway /
  Contracts) — c'est de l'infra. Aucun nouveau contrat MassTransit.

## Non-goals

- Ne **remplace pas** l'Option A (Job in-cluster) en dev : les deux coexistent,
  pilotées par configuration.
- Ne traite **pas** la consommation applicative des secrets rotatés (gestion de la
  rotation côté Npgsql) — cela relève de l'intégration applicative, hors de ce change.
- Ne couvre **pas** l'auto-unseal KMS ni la topologie HA Raft du serveur Vault
  (déjà gérés par `VaultResources` / `Pulumi.prod.yaml`).
- Pas de changement de comportement métier (paniers, réservations, stock).
