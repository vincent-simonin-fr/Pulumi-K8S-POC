## Context

Le serveur Vault est déployé par `VaultResources.cs` (standalone+file en dev, HA Raft+
auto-unseal KMS en prod). La **configuration interne** de Vault (auth Kubernetes,
database secrets engine, rôles dynamiques, policies) est, en dev, réalisée par un
**Job de bootstrap in-cluster** exécutant le CLI `vault` (Option A). Ce change ajoute
une **voie déclarative** via le provider `pulumi-vault`, visée pour la **prod**, sans
supprimer l'Option A (qui reste le défaut dev).

Contrainte clé : en dev Vault est en `ClusterIP` → injoignable depuis l'hôte Pulumi.
Le provider `pulumi-vault` suppose donc un Vault **exposé** (Ingress/TLS en prod, ou
port-forward) et un **token d'administration** transmis au provider.

## Goals / Non-Goals

**Goals:**
- Décrire auth k8s + DB secrets engine + rôles + policies en ressources `pulumi-vault`.
- Aiguillage `vault:configMode` (`job` dev par défaut / `provider` prod).
- Idempotence et intégration au flux `pulumi up`.

**Non-Goals:**
- Supprimer l'Option A (Job in-cluster) — coexistence pilotée par config.
- Gérer la rotation côté application (Npgsql) — hors périmètre.
- Auto-unseal KMS / HA Raft du serveur (déjà couverts par `VaultResources`).

## Decisions

- **Provider `Pulumi.Vault` plutôt que CLI scripté** : déclaratif, idempotent,
  diffable, versionné. Alternative écartée : étendre le Job — rejeté pour la prod
  (impératif, peu auditable).
- **Aiguillage par `vault:configMode`** plutôt que deux stacks séparés : conserve un
  seul pipeline, bascule dev↔prod par configuration. Alternative : projet Pulumi
  distinct — rejeté (duplication, désync).
- **Token d'admin Vault fourni au provider via secret/identité court-vécu**, jamais
  committé. En prod : idéalement un token AppRole/identité de charge à TTL court
  plutôt que le root token.
- **Exposition de Vault en prod via Ingress+TLS** (cohérent avec le reste de la
  stack) ; en dev, le mode provider reste optionnel via port-forward.
- **Cible du DB engine = services CNPG** (`{cluster}-rw`) avec SQL de
  création/révocation de rôles éphémères ; le compte admin utilisé par Vault est
  distinct des credentials applicatifs.

## Risks / Trade-offs

- [Vault injoignable depuis l'hôte Pulumi en dev] → garder `job` par défaut en dev ;
  n'activer `provider` que là où Vault est exposé.
- [Token d'admin Vault dans Pulumi = secret sensible] → token court-vécu / identité
  de charge ; jamais en clair dans Git ; rotation.
- [Dépendance d'ordre : Vault doit être initialisé/unsealed avant configuration] →
  `DependsOn` sur le serveur + healthcheck ; échec explicite si Vault scellé.
- [Divergence dev (job) ↔ prod (provider)] → spécifications partagées (mêmes
  rôles/policies attendus) pour limiter l'écart de comportement.
- [Couplage Vault ↔ CNPG (compte admin)] → restreindre les droits du compte admin au
  strict nécessaire (CREATEROLE) et le faire tourner.

## Migration Plan

1. Ajouter le package `Pulumi.Vault` et la ressource `VaultDeclarativeConfigResources`.
2. Introduire `vault:configMode` (défaut `job`).
3. En prod : exposer Vault (Ingress/TLS), fournir le token au provider, basculer
   `configMode=provider`, `pulumi up`, valider l'émission de creds dynamiques.
4. Rollback : repasser `configMode=job` (ou désactiver la config provider) sans
   toucher au serveur Vault ni aux données.

## Open Questions

- Méthode d'auth du provider Pulumi vers Vault en prod : AppRole vs token d'identité
  de charge (CI) — à trancher selon la plateforme cible.
- Faut-il gérer le database engine par cluster CNPG (2 connexions) ou un engine
  unique multi-rôles ?
- Convergence à terme : viser `provider` aussi en dev (Vault exposé localement) pour
  supprimer l'Option A, ou conserver les deux durablement ?
