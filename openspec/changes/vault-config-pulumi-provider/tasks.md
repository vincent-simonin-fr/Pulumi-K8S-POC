## 1. Pré-requis & provider

- [x] 1.1 Ajouter le package `Pulumi.Vault` au projet `infra/Ecommerce.Infra`
- [x] 1.2 `vault:configMode` lue dans `EcommerceStack.cs` — **défaut `provider`** (dev ET prod) ; `job` = filet de secours
- [x] 1.3 Exposer Vault : **dev = NodePort 30820** (`VaultResources` + `kind-config.yaml`) ; **prod = Ingress `vault.{domain}` + TLS cert-manager** (`IngressResources`, créé si `vault:enabled` & `ingress:enabled`, auth par token — pas de basic-auth)

## 2. Authentification du provider vers Vault

- [~] 2.1 Source du token d'admin : `vault:rootToken` (secret) câblé ; **AppRole/identité court-vécu = recommandation prod** (à brancher selon plateforme)
- [x] 2.2 Configurer le provider `Pulumi.Vault` (adresse + token) depuis la config/secret (`Vault.Provider`)
- [~] 2.3 Skip explicite si `providerAddress`/token absent (pas de config → apps sur secrets statiques) ; si Vault injoignable, `pulumi up` échoue (erreur provider)

## 3. Configuration déclarative de Vault

- [x] 3.1 Créer `Resources/VaultDeclarativeConfigResources.cs` (gardé par `configMode=provider`)
- [x] 3.2 Déclarer l'auth backend Kubernetes (`Vault.AuthBackend` + `AuthBackendConfig` + rôles k8s liés au SA `ecommerce/vault-auth`)
- [x] 3.3 Déclarer le database secrets engine PostgreSQL pointant sur `{cluster}-rw` (`Mount` + `SecretBackendConnection`)
- [x] 3.4 Déclarer les rôles dynamiques (creation/revocation SQL, TTL 1h/24h) pour `order-db` et `inventory-db`
- [x] 3.5 Déclarer les policies de moindre privilège (`Vault.Policy`) et les lier aux rôles d'auth k8s

## 4. Aiguillage & cohérence dev/prod

- [x] 4.1 Dans `EcommerceStack.cs`, n'instancier QUE l'une des deux voies (Job vs provider) selon `configMode` (marqueur `vaultConfigDone`)
- [x] 4.2 Parité Job (dev) ↔ provider (prod) : mêmes noms/SQL/TTL/policies (constantes partagées + doc)
- [x] 4.3 `DependsOn` : la config provider dépend du serveur Vault (unseal = pré-requis runtime, échec explicite si scellé)

## 5. Validation

- [ ] 5.1 Émettre des credentials dynamiques via le rôle et se connecter à PostgreSQL — *au `pulumi up` en mode provider (Vault exposé + token)*
- [ ] 5.2 Vérifier la révocation automatique de l'utilisateur PostgreSQL à l'expiration du bail — *idem*
- [ ] 5.3 Vérifier qu'un ServiceAccount non autorisé est refusé par l'auth k8s — *idem*
- [ ] 5.4 Tests d'intégration (génération + révocation de creds dynamiques)

## 6. Infra & documentation

- [x] 6.1 Pulumi : `Pulumi.prod.yaml` (`configMode=provider`, `providerAddress`, token/dbAdmin) et `Pulumi.dev.yaml` (`job`)
- [x] 6.2 Documenter la procédure (exposition Vault, token, bascule de mode) dans `docs/vault.md`
- [x] 6.3 Préchargement Nuke : N/A — le provider s'exécute sur l'hôte, aucun composant in-cluster supplémentaire
