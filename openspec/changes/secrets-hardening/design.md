## Context

`SecretsResources.cs` crée des Secrets K8s natifs en clair (StringData). Les creds DB
applicatifs sont déjà couverts par Vault/VSO (dynamiques), mais `rabbitmq-credentials`,
le mot de passe admin Grafana et le mot de passe statique `app` restent **statiques et
lisibles** par tout détenteur de `get secrets` (base64 ≠ chiffrement), et stockés en
clair dans etcd par défaut. Ce change durcit l'accès et réduit le blast radius en prod,
sans changer le confort dev.

## Goals / Non-Goals

**Goals:**
- RBAC least-privilege sur les Secrets (prod).
- Chiffrement at-rest etcd via KMS.
- Audit des accès aux Secrets.
- Sourcing optionnel de ces 3 secrets depuis Vault KV (VSO), mêmes noms K8s.

**Non-Goals:**
- Toucher au confort dev (statique + kubectl get).
- Config Vault déclarative (→ `vault-config-pulumi-provider`).
- Creds DB applicatifs (déjà dynamiques).

## Decisions

- **RBAC en ressources Pulumi** (Role/ClusterRole/RoleBinding), activé par config
  (`security:rbacStrict` ou via stack prod) ; en dev, non appliqué. Alternative écartée :
  laisser le RBAC cluster par défaut — rejeté pour la prod.
- **EncryptionConfiguration hors Pulumi** : c'est une config **du control-plane**
  (API server), pas un objet K8s applicable. Sur cluster managé (EKS/GKE/AKS) elle se
  configure au provisioning ; on la **documente** (production.md) plutôt que de la coder.
  Alternative écartée : tenter de la gérer via Pulumi — impossible sur control-plane managé.
- **Sourcing Vault via `VaultStaticSecret` (KV v2)** plutôt que dynamique : RabbitMQ et
  Grafana n'ont pas de moteur de secrets dynamique natif simple ; KV statique + rotation
  manuelle/scriptée suffit, en gardant le secret maître hors etcd applicatif et le même
  nom de Secret K8s (zéro impact app). Le mot de passe `app` reste un KV (CNPG l'attend
  comme secret stable).
- **Aiguillage par stack** : prod active RBAC + sourcing Vault ; dev reste statique.
  Mêmes noms de Secrets → bascule transparente côté pods.

## Risks / Trade-offs

- [RBAC trop strict casse un composant qui lit un Secret] → cartographier les lecteurs
  (operators, exporters) avant de restreindre ; tester en staging.
- [EncryptionConfiguration mal configurée = perte d'accès aux Secrets] → procédure KMS
  validée + sauvegarde de la clé ; rollover de clé documenté.
- [Un détenteur de `create pod` peut monter un Secret et le lire malgré le RBAC] →
  limiter aussi la création de pods dans les namespaces sensibles ; le sourcing Vault +
  rotation réduit la valeur d'un secret exfiltré.
- [Sourcing Vault ajoute une dépendance au bootstrap Vault] → fallback statique si Vault
  indisponible ; activer le sourcing seulement une fois Vault opérationnel.

## Migration Plan

1. Ajouter les ressources RBAC (gardées par config prod) + tests d'accès.
2. Documenter + appliquer l'EncryptionConfiguration KMS sur le cluster prod, activer l'audit.
3. Créer les entrées Vault KV + `VaultStaticSecret` pour rabbitmq/grafana/app ; basculer
   `SecretsResources` en mode "sourcé Vault" en prod (mêmes noms K8s).
4. Rollback : repasser au sourcing statique (les noms de Secrets ne changent pas).

## Open Questions

- Faut-il un namespace/Role distinct par service pour le RBAC, ou un Role "secrets-reader"
  partagé minimal ?
- Rotation des KV (rabbitmq/grafana) : manuelle, ScheduledJob, ou via un moteur Vault ?
- Le mot de passe `app` doit-il rester statique (CNPG bootstrap) ou peut-on le faire
  gérer entièrement par Vault à terme ?
