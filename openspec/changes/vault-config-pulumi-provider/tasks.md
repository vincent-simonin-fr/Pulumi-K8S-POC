## 1. Pré-requis & provider

- [ ] 1.1 Ajouter le package `Pulumi.Vault` au projet `infra/Ecommerce.Infra`
- [ ] 1.2 Introduire la config `vault:configMode` (`job` par défaut, `provider` en prod) lue dans `EcommerceStack.cs`
- [ ] 1.3 Exposer Vault en prod (Ingress + TLS) ; documenter le port-forward pour un essai dev

## 2. Authentification du provider vers Vault

- [ ] 2.1 Définir la source du token d'admin Vault (AppRole ou identité de charge, court-vécu) — jamais committé
- [ ] 2.2 Configurer le provider `Pulumi.Vault` (adresse Vault + token) à partir de la config/secret
- [ ] 2.3 Gérer l'échec explicite si Vault est injoignable ou le token absent

## 3. Configuration déclarative de Vault

- [ ] 3.1 Créer `Resources/VaultDeclarativeConfigResources.cs` (gardé par `configMode=provider`)
- [ ] 3.2 Déclarer l'auth backend Kubernetes (`vault.AuthBackend` + rôles liés aux ServiceAccounts)
- [ ] 3.3 Déclarer le database secrets engine PostgreSQL pointant sur les services CNPG `{cluster}-rw`
- [ ] 3.4 Déclarer les rôles dynamiques (creation/revocation SQL, TTL bornés) pour `order-db` et `inventory-db`
- [ ] 3.5 Déclarer les policies de moindre privilège et les lier aux rôles d'auth k8s

## 4. Aiguillage & cohérence dev/prod

- [ ] 4.1 Dans `EcommerceStack.cs`, n'instancier QUE l'une des deux voies (Job vs provider) selon `configMode`
- [ ] 4.2 Vérifier la parité des rôles/policies entre le Job (dev) et le provider (prod)
- [ ] 4.3 `DependsOn` : la configuration provider s'exécute après un Vault initialisé/unsealed

## 5. Validation

- [ ] 5.1 Émettre des credentials dynamiques via le rôle et se connecter à PostgreSQL avec
- [ ] 5.2 Vérifier la révocation automatique de l'utilisateur PostgreSQL à l'expiration du bail
- [ ] 5.3 Vérifier qu'un ServiceAccount non autorisé est refusé par l'auth k8s
- [ ] 5.4 Ajouter des tests d'intégration (génération + révocation de creds dynamiques)

## 6. Infra & documentation

- [ ] 6.1 Mettre à jour Pulumi : `Pulumi.prod.yaml` (`vault:configMode=provider`, exposition Vault) et `Pulumi.dev.yaml` (`job`)
- [ ] 6.2 Documenter la procédure (exposition Vault, token provider, bascule de mode) dans `docs/`
- [ ] 6.3 Préchargement Nuke / images si un composant supplémentaire est requis
