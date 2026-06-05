## Why

Deux protections manquent :

- **Pas de PodDisruptionBudget (PDB)** : lors d'un `drain` de nœud (maintenance,
  upgrade), Kubernetes peut évincer **tous les réplicas** d'un service d'un coup →
  coupure, alors que l'anti-affinité les avait répartis pour la HA.
- **Pas de NetworkPolicy** : par défaut, **tout pod parle à tout pod**. CNPG, RabbitMQ,
  Redis sont joignables latéralement par n'importe quel pod du cluster → surface
  d'attaque inutile.

## What Changes

- **PodDisruptionBudget** par déploiement applicatif (`order-api`, `inventory-api`,
  `gateway`) avec `minAvailable: 1` (ou %) → un drain ne peut pas tout évincer.
- **NetworkPolicies** :
  - **default-deny** (ingress) par namespace `ecommerce`,
  - autorisations **ciblées** : gateway→APIs, APIs→CNPG (pooler/-rw), APIs→RabbitMQ,
    inventory→Redis, scrape Prometheus (monitoring→exporters/metrics ports),
    DNS sortant.
- **Dev** : NetworkPolicies souvent **no-op** (le CNI Kind/kindnet ne les applique pas
  par défaut) → activable/portable ; PDB applicable partout (utile au test de drain HA).

## Capabilities

### New Capabilities
- `workload-resilience-isolation`: PDB pour préserver la disponibilité au drain +
  NetworkPolicies default-deny avec autorisations ciblées pour isoler les composants.

### Modified Capabilities
<!-- Aucune spec métier modifiée : durcissement résilience/réseau transverse. -->

## Impact

- **Infra Pulumi** : `PodDisruptionBudget` par service (Order/Inventory/Gateway
  ServiceResources) ; ressources `NetworkPolicy` (nouveau fichier
  `NetworkPolicyResources.cs` ou par composant).
- **Réseau** : nécessite un **CNI qui applique les NetworkPolicies** en prod (Calico,
  Cilium…) ; sur Kind/kindnet elles sont ignorées (no-op) → à valider en staging.
- **Services applicatifs** : aucun changement de code ; bien cartographier les flux
  autorisés (sinon une NetworkPolicy trop stricte casse une connexion).
- **Docs** : `production.md` (PDB + NetworkPolicy + CNI requis), `ha-testing.md` (PDB au
  drain), `architecture.md` (flux réseau).

## Non-goals

- Pas de NetworkPolicy **egress** stricte au-delà du nécessaire (DNS + flux ciblés) en
  première itération.
- Ne dépend pas d'un service mesh (mTLS) — hors périmètre.
- Pas de changement de comportement métier.
