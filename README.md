# Ecommerce — .NET 10 · Pulumi · Kubernetes

Stack microservices e-commerce de démonstration en **Clean Architecture**, communication event-driven via **RabbitMQ / MassTransit**, déployée localement sur **Kind** avec **Podman** et provisionnée via **Pulumi C#**.

---

## Sommaire

| Documentation                                           | Contenu                                                                                        |
| ------------------------------------------------------- | ---------------------------------------------------------------------------------------------- |
| **Ce fichier**                                          | Prérequis, démarrage rapide                                                                    |
| [Architecture](docs/architecture.md)                    | Diagramme, flux métier, structure projet, endpoints, configuration                             |
| [Kubernetes & Déploiement](docs/kubernetes.md)          | Cluster Kind, build images, `pulumi up`, reset, arrêt/redémarrage, Metrics Server              |
| [Infrastructure Pulumi](docs/infrastructure.md)         | Code Pulumi, secrets, HPA, KEDA, Redis, StatefulSet PostgreSQL, presale, ressources CPU/RAM    |
| [Déploiement en production](docs/production.md)         | Stack prod, secrets, Ingress, DNS, Let's Encrypt, opérations                                   |
| [Observabilité](docs/observability.md)                  | OTel Collector, Jaeger, Prometheus, Grafana, 4 dashboards                                      |
| [Debugging](docs/debugging.md)                          | VS 2022 + Pulumi, `kubectl` diagnostics, problèmes courants                                    |
| [Dev local & Tests](docs/dev-local.md)                  | `podman-compose`, tests d'intégration, migrations EF Core, OpenAPI, observabilité              |
| [k9s](docs/k9s.md)                                      | Interface terminal Kubernetes — logs, shell, describe sans Dashboard                           |
| [Tests de charge](docs/load-testing.md)                 | k6 — scénarios baseline/load/stress/spike, intégration Prometheus                              |
| [Accès (mots de passe & port-forwards)](docs/access.md) | NodePorts, port-forwards, récupération des credentials (Grafana, ArgoCD, RabbitMQ, PostgreSQL) |
| [Argo CD / GitOps](docs/argocd.md)                      | Déploiement GitOps des apps, credential dépôt privé, RBAC, SSO                                 |
| [Versioning des images](docs/versioning.md)             | Tags SemVer + SHA par service, `dotnet nuke BuildImages`, redéploiement ciblé                  |
| [Test HA multi-nœuds](docs/ha-testing.md)               | Kind multi-nœuds, anti-affinité, failover CNPG/RabbitMQ, drain de nœud                         |
| [Vault — secrets dynamiques](docs/vault.md)             | HashiCorp Vault + VSO, creds PostgreSQL dynamiques, bootstrap init/unseal, re-init             |
| [Sauvegardes & MinIO](docs/backups.md)                  | Backups CNPG (Barman) + WAL, MinIO (S3) + console, ScheduledBackup, PITR                       |
| [Ingress en local](docs/ingress-local.md)              | Monter nginx Ingress + TLS self-signed sur Kind (hostPort 80/443, `/etc/hosts`)               |

---

## Vue d'ensemble

```
Client HTTP
    │
    ▼
Gateway (YARP) ─── NodePort 30080 ─── localhost:30080
    ├── /orders/**    → Order API    → order-db   (PostgreSQL)
    └── /inventory/** → Inventory API → inventory-db (PostgreSQL)
                              │
                              ├── Redis (cache GET /inventory, TTL 30s)
                              └── RabbitMQ :5672 (ProductAddedToCartEvent)
                                      │
                                   KEDA ──► scale inventory-api
                                   (queue depth, ~5s reaction)
```

Le flux principal : ajout panier → réservation stock → expiration automatique → libération stock.  
Voir [Architecture](docs/architecture.md) pour les détails.

### Scaling

| Service       | Mécanisme | Signal                              |
| ------------- | --------- | ----------------------------------- |
| order-api     | HPA natif | CPU > 70%                           |
| inventory-api | **KEDA**  | Queue RabbitMQ depth (réaction ~5s) |
| gateway       | HPA natif | CPU > 70%                           |

---

## Prérequis

| Outil                                              | Version | Rôle                                |
| -------------------------------------------------- | ------- | ----------------------------------- |
| [Podman Desktop](https://podman-desktop.io)        | 1.10+   | Moteur de containers                |
| [Kind](https://kind.sigs.k8s.io)                   | 0.23+   | Kubernetes local                    |
| [kubectl](https://kubernetes.io/docs/tasks/tools/) | 1.29+   | CLI Kubernetes                      |
| [Helm](https://helm.sh/docs/intro/install/)        | 3.x     | Gestionnaire de packages K8s (KEDA) |
| [Pulumi CLI](https://www.pulumi.com/docs/install/) | 3.x     | Infrastructure as Code              |
| [.NET SDK](https://dotnet.microsoft.com)           | 10.0    | Build applicatif et Pulumi C#       |
| [k6](https://k6.io/docs/get-started/installation/) | 0.50+   | Tests de charge (optionnel)         |

> **Windows** : définir `KIND_EXPERIMENTAL_PROVIDER=podman` (variable d'environnement permanente) avant toute commande `kind`.

---

## Démarrage rapide — automatisé (Nuke)

```bash
# Depuis la racine du projet — fait tout : cluster, images, build, pulumi up
dotnet nuke Launch
```

> Le secrets provider Pulumi requiert une passphrase (exécution non interactive) :
> exporter `PULUMI_CONFIG_PASSPHRASE` dans le shell, ou passer `--pulumi-passphrase`.
>
> L'ancien script batch `scripts\k8s_complete_launch.cmd` est conservé pour mémoire
> (équivalent autonome, voir [docs/versioning.md](docs/versioning.md)).

### Secrets en dev (mode statique par défaut)

En dev, tout tourne sur des **secrets statiques** définis dans `Pulumi.dev.yaml`
(mots de passe par défaut) — **rien à configurer**, c'est le mode simple.

HashiCorp **Vault** (serveur + VSO) est déployé, mais order-api / inventory-api ne
passent aux **creds PostgreSQL dynamiques** qu'une fois Vault **bootstrappé**
(init/unseal + `pulumi config set --secret vault:rootToken …`). Tant que ce n'est pas
fait, les apps **restent sur les secrets statiques** → aucun blocage au démarrage.

- Alléger le dev (pas de Vault du tout) : `vault:enabled=false` dans `Pulumi.dev.yaml`.

#### Bootstrap Vault (init + unseal) — pour activer les creds dynamiques

> **Configuration de Vault = provider déclaratif `pulumi-vault`** (`vault:configMode=provider`,
> défaut dev **et** prod) : Pulumi configure Vault (auth k8s + DB engine + rôles + policies)
> **depuis l'hôte**. En dev, Vault est donc exposé en **NodePort** (`localhost:30820`) →
> pas de port-forward. (`configMode=job` reste un filet de secours.) Voir [docs/vault.md](docs/vault.md).

Le setup se fait en **deux `pulumi up`** : le 1ᵉʳ déploie le serveur Vault (scellé), puis on
l'initialise/desselle et on fournit le token, et le 2ᵉ le configure. Séquence (PowerShell,
à refaire après chaque reset) :

```powershell
# (Le 1er `pulumi up` / `dotnet nuke Launch` a déjà déployé le serveur Vault, scellé.)

# 1. Init → écrit les clés dans vault-init.json (gitignoré, lié à CETTE instance Vault)
kubectl exec -n vault vault-0 -- vault operator init -key-shares=1 -key-threshold=1 -format=json > vault-init.json

# 2. Unseal avec la clé du fichier
$k = (Get-Content vault-init.json -Raw | ConvertFrom-Json).unseal_keys_b64[0]
kubectl exec -n vault vault-0 -- vault operator unseal $k
kubectl exec -n vault vault-0 -- vault status      # Sealed: false

# 3. Poser le root token → le provider pulumi-vault configure Vault (via localhost:30820) + VSO
$t = (Get-Content vault-init.json -Raw | ConvertFrom-Json).root_token
cd infra/Ecommerce.Infra
pulumi config set --secret vault:rootToken $t
pulumi up --yes
```

> ⚠️ `vault-init.json` + `vault:rootToken` sont liés à une instance Vault donnée → **caducs à chaque reset de cluster** : `rm vault-init.json` + `pulumi config rm vault:rootToken` avant de recommencer. Détails, re-init et prod : [docs/vault.md](docs/vault.md).

> **Après un arrêt/redémarrage complet du cluster** (`podman stop`, reboot PC…) : Vault
> redémarre **scellé** (pas d'auto-unseal en dev) → il faut le **redesceller** (étape 2
> ci-dessus, SANS refaire l'init), sinon order-api/inventory-api crashent en `28P01`.
>
> **Après un re-init de Vault OU si les creds restent caducs après unseal** : les Secrets
> dynamiques gardent d'anciens _leases_ → forcer VSO à régénérer un lease frais, puis
> redémarrer les apps :
>
> ```bash
> kubectl delete secret order-db-dynamic inventory-db-dynamic -n ecommerce
> kubectl rollout restart deploy/order-api deploy/inventory-api deploy/gateway -n ecommerce
> ```
>
> **Mode provider — dérive d'état** : après un **re-init** de Vault (nouvelle instance) sans
> reset complet du stack, l'état Pulumi croit encore les objets Vault présents alors que le
> nouveau Vault est vide. Lancer `pulumi up --refresh` (Pulumi détecte l'absence et recrée
> auth/DB engine/rôles/policies). Un reset **complet** (`pulumi destroy`/`stack rm`) évite ce cas.

---

## Démarrage rapide — étapes manuelles

### 1. Créer le cluster

```bash
set KIND_EXPERIMENTAL_PROVIDER=podman
kind create cluster --name ecommerce --config kind-config.yaml
kubectl config use-context kind-ecommerce
```

### 2. Pré-charger les images

```bash
# Toutes les images infra/observabilité (kube-prometheus-stack)/CNPG/KEDA/
# metrics-server/Vault+VSO — liste épinglée UNIQUE dans build/Build.cs (PreloadImageList) :
dotnet nuke PreloadImages

# Images applicatives
podman build -t localhost/ecommerce/order-api:dev     -f docker/order-api/Dockerfile .
podman build -t localhost/ecommerce/inventory-api:dev -f docker/inventory-api/Dockerfile .
podman build -t localhost/ecommerce/gateway:dev       -f docker/gateway/Dockerfile .
kind load docker-image localhost/ecommerce/order-api:dev     --name ecommerce
kind load docker-image localhost/ecommerce/inventory-api:dev --name ecommerce
kind load docker-image localhost/ecommerce/gateway:dev       --name ecommerce
```

### 3. Déployer

```bash
cd infra/Ecommerce.Infra
pulumi login --local
pulumi stack init dev
pulumi up --yes
```

### 4. Vérifier

```bash
kubectl get pods -n ecommerce -w          # attendre que tous soient Running
kubectl get pods -n keda                  # KEDA operator
kubectl get scaledobject -n ecommerce     # ScaledObject inventory-api

curl http://localhost:30080/health         # gateway
curl http://localhost:30080/health/orders      # order-api
curl http://localhost:30080/health/inventory   # inventory-api
```

**Accès** :

- Gateway : `http://localhost:30080`
- Grafana : `http://localhost:30030`
- Jaeger : `http://localhost:30686`

---

## GitOps avec Argo CD (optionnel)

Les applications (order-api, inventory-api, gateway) peuvent être déployées en
GitOps : Pulumi rend leurs manifestes en YAML dans `gitops/apps/`, et Argo CD les
synchronise depuis Git. L'infrastructure (CNPG, KEDA, secrets, observabilité,
Argo CD) reste gérée par Pulumi en direct.

```bash
pulumi config set gitops:enabled true
pulumi config set gitops:repoUrl https://github.com/<user>/<repo>.git
pulumi up --yes                                   # rend les YAML + crée l'Application
git add gitops && git commit -m "gitops: apps" && git push
```

Les images sont versionnées en `{SemVer}-{SHA-par-service}` via
`dotnet nuke BuildImages`, de sorte qu'un changement sur un seul service ne
redéploie que celui-ci. Détails : [docs/versioning.md](docs/versioning.md).

### Accès à un dépôt privé

Argo CD clone le dépôt distant : pour un dépôt **privé**, il faut lui fournir un
credential, sinon l'Application reste en `ComparisonError: authentication required`.

```bash
kubectl create secret generic repo-pulumi-k8s-poc -n argocd \
  --from-literal=type=git \
  --from-literal=url=https://github.com/vincent-simonin-fr/Pulumi-K8S-POC.git \
  --from-literal=username=vincent-simonin-fr \
  --from-literal=password=ghp_xxxxxxxxxxxx

kubectl label secret repo-pulumi-k8s-poc -n argocd \
  argocd.argoproj.io/secret-type=repository
```

Le label `argocd.argoproj.io/secret-type=repository` et la correspondance exacte
de l'`url` avec le `repoURL` de l'Application sont obligatoires. Procédure complète
(création du PAT, diagnostic, workflow) : voir [docs/argocd.md](docs/argocd.md).

Revenir en mode Pulumi direct : `pulumi config set gitops:enabled false && pulumi up --yes`.

---

## Arrêter / relancer sans perdre les données

```bash
# Stopper (cluster conservé, données PostgreSQL préservées)
podman stop ecommerce-control-plane

# Relancer
podman start ecommerce-control-plane
```

---

## Réinitialisation complète

Voir [Kubernetes & Déploiement → Reset complet](docs/kubernetes.md#reset-complet).
