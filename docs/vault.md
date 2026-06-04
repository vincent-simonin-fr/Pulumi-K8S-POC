# HashiCorp Vault — secrets dynamiques

Vault fournit des **credentials PostgreSQL dynamiques** (un utilisateur éphémère par
bail, TTL court, révoqué automatiquement) au lieu de mots de passe statiques. Le
**Vault Secrets Operator (VSO)** synchronise ces credentials dans un Secret K8s natif.

```
SA vault-auth ─(auth Kubernetes)─► Vault ─(database engine)─► user PG éphémère (CNPG)
                                        └─► VSO ─► Secret K8s order-db-dynamic (rotaté)
```

## Composants (Pulumi)

| Composant | Rôle |
|---|---|
| `VaultResources` | Serveur Vault (chart `hashicorp/vault`). Dev : standalone + storage fichier. Prod : HA Raft + auto-unseal KMS. |
| `VaultSecretsOperatorResources` | Vault Secrets Operator (chart `vault-secrets-operator`). |
| `VaultConfigResources` | **Option A** : Job in-cluster qui configure Vault (DB engine, rôle dynamique, auth k8s, policy). Gardé par `vault:rootToken`. |
| `VaultSecretsResources` | CRDs VSO (`VaultConnection`/`VaultAuth`/`VaultDynamicSecret`) → Secret `order-db-dynamic`. |

> **Prod** : la configuration de Vault par le provider déclaratif `pulumi-vault`
> (Option B) est décrite dans la proposition OpenSpec `vault-config-pulumi-provider`.

## Bootstrap dev (Kind)

Vault démarre **scellé** et **non configuré** : il faut un init/unseal manuel (pas de
KMS en local), puis fournir le token pour activer le Job de config. C'est un cycle en
deux `pulumi up`.

### 1. Déployer le serveur + VSO

```bash
# images préchargées par Nuke (vault, vault-secrets-operator)
cd infra/Ecommerce.Infra && pulumi up --yes
kubectl get pods -n vault                              # vault-0 : 0/1 (scellé = ATTENDU)
kubectl get pods -n vault-secrets-operator-system     # VSO Running
```

### 2. Initialiser + desceller (one-shot)

```bash
kubectl exec -n vault vault-0 -- vault operator init \
  -key-shares=1 -key-threshold=1 -format=json > vault-init.json   # gitignoré

# clé d'unseal = unseal_keys_b64[0] ; root token = root_token (voir le fichier)
kubectl exec -n vault vault-0 -- vault operator unseal <UNSEAL_KEY>
kubectl exec -n vault vault-0 -- vault status          # Sealed: false → vault-0 1/1
```

### 3. Fournir le root token → activer le Job de config

```bash
cd infra/Ecommerce.Infra
pulumi config set --secret vault:rootToken <ROOT_TOKEN>   # depuis vault-init.json
pulumi up --yes
```

Au second `up`, Pulumi crée le ClusterRoleBinding `vault-auth-delegator`, le Secret
`vault-root-token`, la ConfigMap du script, le **Job de config** (configure Vault) et
les **CRDs VSO**.

### 4. Vérifier

```bash
kubectl get vaultdynamicsecret order-db-dynamic -n ecommerce   # SYNCED/HEALTHY/READY = True
kubectl get secret order-db-dynamic -n ecommerce               # keys: username, password
kubectl get secret order-db-dynamic -n ecommerce \
  -o jsonpath='{.data.username}' | base64 -d                    # v-kubernet-order-ap-...
```

Le préfixe `v-kubernet-` confirme que VSO s'est authentifié via le ServiceAccount
(auth Kubernetes), pas avec un token statique.

## Accès UI / CLI

```bash
kubectl port-forward -n vault svc/vault 8200:8200   # http://localhost:8200 — login = root token
```

## Re-init (régénérer les clés / le root token)

⚠️ Efface **toute** la config Vault → le Job de config la rejouera au prochain `up`.

```bash
kubectl exec -n vault vault-0 -- sh -c 'rm -rf /vault/data/*'
kubectl delete pod vault-0 -n vault
kubectl rollout status statefulset/vault -n vault
kubectl exec -n vault vault-0 -- vault operator init -key-shares=1 -key-threshold=1 -format=json > vault-init.json
kubectl exec -n vault vault-0 -- vault operator unseal <NOUVELLE_CLE>
cd infra/Ecommerce.Infra
pulumi config set --secret vault:rootToken <NOUVEAU_ROOT_TOKEN>
pulumi up --yes        # reconfigure Vault + re-sync VSO
```

## Sécurité

- ⚠️ En **dev**, le root token vit dans un Secret K8s + l'état Pulumi (chiffré). C'est
  l'anti-pattern accepté localement ; en **prod**, l'auto-unseal KMS supprime les clés
  manuelles et la config passe par `pulumi-vault` (Option B) avec un token scellé/scopé.
- Ne jamais committer `vault-init.json` (gitignoré) ni coller un token en clair.
- Prod : `vault:haEnabled=true` (HA Raft) + `vault:sealConfig` (KMS) — voir `Pulumi.prod.yaml`.

## Production

| Aspect | Dev (Kind) | Prod |
|---|---|---|
| Topologie | standalone + file | HA Raft 3 nœuds |
| Unseal | Shamir manuel | auto-unseal KMS |
| Config Vault | Job in-cluster (Option A) | provider `pulumi-vault` (Option B) |
| Stockage | local-path | réseau (gp3…) |
