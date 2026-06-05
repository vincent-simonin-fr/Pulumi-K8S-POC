## 1. RBAC least-privilege (prod)

- [ ] 1.1 Cartographier les lecteurs légitimes de Secrets (operators, exporters, pods, CI)
- [ ] 1.2 Ajouter un flag de config (`security:rbacStrict`, défaut false → dev inchangé)
- [ ] 1.3 Créer les ressources RBAC Pulumi (Role/ClusterRole + RoleBinding) least-privilege, gardées par le flag
- [ ] 1.4 Vérifier : `get secret` refusé pour un SA non autorisé, accordé pour le SA dédié

## 2. Chiffrement at-rest + audit (control-plane, hors Pulumi)

- [ ] 2.1 Documenter l'EncryptionConfiguration + provider KMS (production.md) selon le cloud cible
- [ ] 2.2 Documenter l'activation de l'audit logging des accès aux Secrets
- [ ] 2.3 Vérifier qu'un Secret est chiffré dans etcd/backup et lisible normalement par un pod autorisé

## 3. Sourcing Vault (KV v2) des secrets statiques restants

- [ ] 3.1 Activer le moteur KV v2 + policies + rôle k8s pour rabbitmq/grafana/app (script de config Vault)
- [ ] 3.2 Écrire les valeurs dans Vault KV (rabbitmq-credentials, grafana admin, app password)
- [ ] 3.3 Ajouter les CRDs `VaultStaticSecret` (VSO) → mêmes noms de Secrets K8s
- [ ] 3.4 Basculer `SecretsResources.cs` en mode "sourcé Vault" en prod (statique conservé en dev)
- [ ] 3.5 Vérifier : pods rabbitmq / order-api / inventory-api / grafana fonctionnent sans modification

## 4. Validation

- [ ] 4.1 Tester la rotation d'un KV Vault → VSO met à jour le Secret + redémarre les consommateurs
- [ ] 4.2 Vérifier le fallback statique si Vault indisponible
- [ ] 4.3 Ajouter des tests d'intégration (accès RBAC refusé/accordé ; sourcing Vault matérialisé)

## 5. Infra & documentation

- [ ] 5.1 Mettre à jour Pulumi : `Pulumi.prod.yaml` (rbacStrict + sourcing Vault) / `Pulumi.dev.yaml` (off)
- [ ] 5.2 Mettre à jour `docs/access.md` (récupération via kubectl = dev-only), `production.md`, `vault.md`
- [ ] 5.3 Préchargement Nuke / images si un composant supplémentaire est requis
