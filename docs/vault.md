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
| `VaultResources` | Serveur Vault (chart `hashicorp/vault`). Dev : standalone + storage fichier, exposé en **NodePort 30820**. Prod : HA Raft + auto-unseal KMS, exposé via Ingress. |
| `VaultSecretsOperatorResources` | Vault Secrets Operator (chart `vault-secrets-operator`). |
| `VaultDeclarativeConfigResources` | **Défaut (dev + prod)** : config Vault (DB engine, rôles dynamiques, auth k8s, policies) en ressources `pulumi-vault` déclaratives/diffables. Actif si `vault:configMode=provider`. |
| `VaultConfigResources` | **Filet de secours** : même config par un Job in-cluster (impératif). Actif si `vault:configMode=job`. |
| `VaultSecretsResources` | CRDs VSO (`VaultConnection`/`VaultAuth`/`VaultDynamicSecret`) → Secret `order-db-dynamic`. |

> **Aiguillage** `vault:configMode` : **`provider` par défaut (dev ET prod)** pour que le dev
> reflète la prod ; `job` reste un filet de secours. Les deux voies sont **mutuellement
> exclusives** et produisent la **même** config (mêmes rôles `order-app`/`inventory-app`, même
> SQL, mêmes TTL 1h/24h, mêmes policies). Détails : [Config déclarative](#config-déclarative-option-b--provider-pulumi-vault).

## Bootstrap dev (Kind)

Vault démarre **scellé** et **non configuré** : il faut un init/unseal manuel (pas de
KMS en local), puis fournir le token pour que **le provider `pulumi-vault` configure Vault**
(depuis l'hôte, via le NodePort `localhost:30820`). C'est un cycle en deux `pulumi up`.

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

### 3. Fournir le root token → le provider configure Vault

```bash
cd infra/Ecommerce.Infra
pulumi config set --secret vault:rootToken <ROOT_TOKEN>   # depuis vault-init.json
pulumi up --yes
```

Au second `up`, le **provider `pulumi-vault`** se connecte à `localhost:30820` (NodePort)
avec le token et déclare l'**auth Kubernetes**, le **database secrets engine** (order-db /
inventory-db), les **rôles dynamiques** + **policies** ; puis les **CRDs VSO** sont créées.

> Le token est requis **uniquement à la configuration** (pas à l'exécution des apps : elles
> s'authentifient ensuite par ServiceAccount). En filet de secours, `vault:configMode=job`
> reproduit la même config via un Job in-cluster (token dans un Secret K8s au lieu de l'hôte).

### 4. Vérifier

```bash
kubectl get vaultdynamicsecret order-db-dynamic -n ecommerce   # SYNCED/HEALTHY/READY = True
kubectl get secret order-db-dynamic -n ecommerce               # keys: username, password
kubectl get secret order-db-dynamic -n ecommerce \
  -o jsonpath='{.data.username}' | base64 -d                    # v-kubernet-order-ap-...
```

Le préfixe `v-kubernet-` confirme que VSO s'est authentifié via le ServiceAccount
(auth Kubernetes), pas avec un token statique.

## Creds dynamiques pour les apps (Phase 3e)

C'est **automatique** : dès que Vault est bootstrappé (rootToken posé → pipeline VSO
actif), **order-api** et **inventory-api** consomment des creds PostgreSQL **dynamiques**
(Secrets `order-db-dynamic` / `inventory-db-dynamic`, templés par VSO). Aucun flag —
c'est la méthode. Tant que Vault n'est pas bootstrappé, les apps utilisent les secrets
statiques (pas de blocage au 1er déploiement).

Les deux services suivent le **même schéma** : rôle dynamique dédié (`order-app` /
`inventory-app`), connexion directe `-rw`, et **restart rolling par VSO** à chaque
rotation (l'username change à chaque bail → relecture du Secret au boot).

Effet :
- order-api lit `ConnectionStrings__OrderDb` depuis le Secret **`order-db-dynamic`**
  (VSO le **template** : `Host=order-db-rw;…;Username={{dyn}};Password={{dyn}}`).
- À chaque rotation du bail, VSO **redémarre** order-api (`rolloutRestartTargets`) → le
  pod relit le Secret. C'est nécessaire car l'**username change à chaque bail** (on ne
  peut pas juste rafraîchir le password côté Npgsql) → **aucun changement de code .NET**.

⚠️ Points importants :
- **Opt-in** : quand `true`, order-api ne démarre **qu'après** le bootstrap Vault (le
  Secret dynamique doit exister). En dev, faire le bootstrap avant, ou laisser `false`.
- **Connexion directe à `order-db-rw`** (pas le Pooler PgBouncer) : plus fiable avec des
  users qui tournent (pas de cache d'auth PgBouncer). Pool Npgsql conservé (15/pod).
- **Migrations EF Core** : le rôle dynamique est `IN ROLE app` ; la révocation Vault fait
  `REASSIGN OWNED … TO app` avant `DROP ROLE` → les objets créés par un user éphémère
  sont transférés à `app`, donc la révocation n'échoue jamais.

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

- ⚠️ Le root token sert **uniquement à la configuration** (provider) ; en dev il vit dans
  l'état Pulumi (chiffré). En **prod**, l'auto-unseal KMS supprime les clés manuelles et on
  fournit un token **scellé/scopé** (AppRole court-vécu) plutôt que le root.
- Ne jamais committer `vault-init.json` (gitignoré) ni coller un token en clair.
- Prod : `vault:haEnabled=true` (HA Raft) + `vault:sealConfig` (KMS) — voir `Pulumi.prod.yaml`.

## Config déclarative (Option B — provider `pulumi-vault`) — **mode par défaut**

`VaultDeclarativeConfigResources` décrit la config interne de Vault en **ressources Pulumi**
(idempotentes, diffables au `pulumi up`) : mount `database`, connexions CNPG (`order-db`/
`inventory-db`), rôles dynamiques, auth Kubernetes, policies + rôles k8s. C'est le **chemin
par défaut en dev ET en prod** (pour que le dev reflète la prod).

**Contrainte clé** : le provider `pulumi-vault` s'exécute sur l'**hôte Pulumi** → Vault doit
être **joignable depuis l'hôte** (≠ DNS in-cluster). D'où :
- **dev** : Vault exposé en **NodePort** (`vault:nodePort=30820` → `http://localhost:30820`),
  configuré automatiquement, **sans port-forward** ;
- **prod** : Vault derrière **Ingress + TLS** (`https://vault.{domain}`).

Si Vault est scellé/injoignable, `pulumi up` échoue (token requis + serveur descellé).

```bash
# Déjà câblé dans Pulumi.dev.yaml / Pulumi.prod.yaml :
#   dev  : configMode=provider, nodePort=30820, providerAddress=http://localhost:30820
#   prod : configMode=provider, providerAddress=https://vault.{domain} (Ingress)

# Token d'admin Vault (SECRET, jamais committé ; AppRole court-vécu de préférence en prod)
pulumi config set --secret vault:rootToken <token>

# Prod uniquement : compte admin PostgreSQL du DB engine (pas de pg_hba trust en prod)
pulumi config set vault:dbAdminUser <user>
pulumi config set --secret vault:dbAdminPassword <pw>

pulumi up --yes
```

> **Dérive d'état au re-init** : après un re-init de Vault (nouvelle instance) sans reset
> complet du stack, lancer `pulumi up --refresh` pour que Pulumi recrée les objets Vault
> (l'état les croit présents). Un reset complet (`destroy`/`stack rm`) évite ce cas.
>
> Filet de secours : `pulumi config set vault:configMode job` (Job in-cluster, n'a pas besoin
> que Vault soit joignable depuis l'hôte). Config **identique** dans les deux modes.

### Auth du provider : AppRole (cible prod) vs root token (dev)

Le provider s'authentifie auprès de Vault de deux façons (l'**AppRole est prioritaire** si
les deux sont fournis) :

- **Dev** : `vault:rootToken` (root token de l'init) — simple, accepté localement.
- **Prod** : **AppRole** = token **court-vécu** et **scopé** (least-privilege), au lieu du root
  tout-puissant et éternel. Réduit le rayon d'explosion si la creds CI fuite.

**Bootstrap AppRole** (une seule fois, avec le root, puis on révoque le root) :

```bash
vault auth enable approle

# Policy de config : SEULEMENT les chemins que le provider gère (pas la lecture des secrets)
vault policy write vault-config-admin - <<'EOF'
path "sys/mounts/*"        { capabilities = ["create","read","update","delete"] }
path "database/*"          { capabilities = ["create","read","update","delete"] }
path "sys/auth/*"          { capabilities = ["create","read","update"] }
path "auth/kubernetes/*"   { capabilities = ["create","read","update"] }
path "sys/policies/acl/*"  { capabilities = ["create","read","update"] }
EOF

# AppRole lié à la policy, token court (20 min, max 1 h)
vault write auth/approle/role/pulumi token_policies=vault-config-admin token_ttl=20m token_max_ttl=1h
vault read  auth/approle/role/pulumi/role-id                 # → role_id
vault write -f auth/approle/role/pulumi/secret-id            # → secret_id (jetable)
```

```bash
# Fournir RoleId/SecretId au provider (au lieu du root), puis appliquer
pulumi config set --secret vault:approleRoleId   <role_id>
pulumi config set --secret vault:approleSecretId <secret_id>
pulumi config rm vault:rootToken          # le root n'est plus utilisé par Pulumi
pulumi up --yes

# Durcir : révoquer le root token de bootstrap
kubectl exec -n vault vault-0 -- vault token revoke <root_token>
```

> Le `secret_id` est idéalement **régénéré par run CI** (court-vécu / response-wrapping) plutôt
> que stocké durablement. Au quotidien, le provider se logge via AppRole → obtient un token qui
> **expire** automatiquement.

## Production

| Aspect | Dev (Kind) | Prod |
|---|---|---|
| Topologie | standalone + file | HA Raft 3 nœuds |
| Unseal | Shamir manuel | auto-unseal KMS |
| Config Vault | provider `pulumi-vault` (NodePort) | provider `pulumi-vault` (Ingress/TLS) |
| Exposition Vault | NodePort `localhost:30820` | Ingress `https://vault.{domain}` |
| Token provider | root (dev) | AppRole court-vécu |
| Stockage | local-path | réseau (gp3…) |
