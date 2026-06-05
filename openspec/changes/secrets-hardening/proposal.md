## Why

Les credentials PostgreSQL applicatifs sont désormais **dynamiques** (Vault/VSO),
donc leur rayon d'explosion est limité. Mais d'autres secrets restent **statiques et
lisibles en clair** par quiconque a le droit `get secrets` (`kubectl get secret … |
base64 -d` — le base64 n'est pas du chiffrement) :

- `rabbitmq-credentials` (user/password RabbitMQ),
- le mot de passe admin **Grafana**,
- le mot de passe statique de l'utilisateur **`app`** PostgreSQL (bootstrap CNPG +
  postgres-exporter).

En production, ce sont les prochaines cibles : un accès en lecture aux Secrets (ou un
vol de backup etcd) expose ces credentials durables. Il faut durcir l'accès et réduire
le blast radius, **sans alourdir le confort dev**.

## What Changes

- **RBAC strict (prod)** : retirer `get`/`list` sur `secrets` des rôles par défaut ;
  accès **least-privilege** par Role/ServiceAccount dédié (un dev ne lit pas les
  mots de passe de prod).
- **Chiffrement at-rest d'etcd** : `EncryptionConfiguration` + provider **KMS** (les
  Secrets ne sont plus stockés en base64 clair dans etcd / les backups).
- **Audit logging** des accès aux secrets (traçabilité « qui a lu quoi »).
- **Sourcing via Vault (cible)** : `rabbitmq-credentials`, Grafana et le mot de passe
  `app` sourcés depuis **Vault KV v2** et matérialisés par VSO (`VaultStaticSecret`),
  pour les aligner sur le modèle des creds DB dynamiques (rotation possible, secret
  maître hors etcd).
- **Dev inchangé** : secrets statiques + `kubectl get secret` restent la commodité locale.

## Capabilities

### New Capabilities
- `secrets-hardening`: contrôle d'accès et protection des Secrets K8s statiques restants
  (RBAC least-privilege, chiffrement at-rest KMS, audit, sourcing Vault optionnel).

### Modified Capabilities
<!-- Aucune spec métier modifiée : durcissement infra/sécurité transverse. -->

## Impact

- **Infra Pulumi** : `SecretsResources.cs` (option de sourcing Vault au lieu de
  StringData statique) ; ressources RBAC (`Role`/`ClusterRole`/`RoleBinding`) ;
  éventuels `VaultStaticSecret` (VSO) + entrées KV Vault.
- **Cluster / control-plane** : `EncryptionConfiguration` + accès KMS — **hors Pulumi**
  (niveau API server, géré à la création du cluster managé : EKS/GKE/AKS l'exposent).
- **Vault** : moteur KV v2 + policies + rôles k8s pour rabbitmq/grafana/app
  (s'appuie sur `VaultResources` + VSO existants).
- **Services applicatifs** : **aucun impact** — mêmes noms de Secrets K8s consommés
  (`rabbitmq-credentials`, etc.). Aucun nouveau contrat MassTransit.
- **Docs** : `access.md` (préciser que la récupération via kubectl est dev-only),
  `production.md` (RBAC/KMS/audit), `vault.md` (KV statiques).

## Non-goals

- Ne **pas** changer le confort **dev** (secrets statiques + `kubectl get secret`).
- Ne **pas** refaire la configuration Vault déclarative (couverte par la proposition
  `vault-config-pulumi-provider`).
- Ne **pas** toucher aux creds DB applicatifs (déjà dynamiques via Vault/VSO).
- Pas de changement de comportement métier (paniers, réservations, stock).
