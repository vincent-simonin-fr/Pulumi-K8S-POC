# Accès — port-forwards & mots de passe

Référence centralisée pour accéder aux UIs et récupérer les identifiants des
composants. Tout est en **dev (Kind)** : accès via NodePort (localhost) ou
port-forward. En **prod**, ces UIs passent par l'Ingress (voir docs/production.md).

## Sommaire

- [Accès directs (NodePort)](#accès-directs-nodeport)
- [Port-forwards (services internes)](#port-forwards-services-internes)
- [Mots de passe & credentials](#mots-de-passe--credentials)
- [Décoder un secret Kubernetes](#décoder-un-secret-kubernetes)

---

## Accès directs (NodePort)

Ces services sont exposés sur `localhost` via les `extraPortMappings` de
`kind-config.yaml` — pas besoin de port-forward.

| Service | URL | Identifiants |
|---------|-----|--------------|
| **Gateway** (API) | http://localhost:30080 | — |
| **Grafana** | http://localhost:30030 | admin / voir [ci-dessous](#grafana) |
| **Jaeger** (tracing) | http://localhost:30686 | — |

> ⚠️ NodePort = compromis dev. En prod, ces accès passent par l'Ingress nginx
> (`grafana.{domain}`, etc.) — voir `Pulumi.prod.yaml`.

---

## Port-forwards (services internes)

Pour les composants en **ClusterIP** (pas exposés en NodePort), ouvrir un
port-forward dans un terminal dédié (à garder ouvert).

### Argo CD (UI GitOps)

```bash
kubectl port-forward -n argocd svc/argocd-server 8080:80
# → http://localhost:8080   (admin / voir ci-dessous)
```

### Prometheus (UI / targets)

```bash
kubectl port-forward -n monitoring svc/kube-prometheus-stack-prometheus 9090:9090
# → http://localhost:9090
# → http://localhost:9090/targets   (état des scrapes)
```

### Grafana (alternative au NodePort)

```bash
kubectl port-forward -n monitoring svc/kube-prometheus-stack-grafana 3000:80
# → http://localhost:3000
```

### RabbitMQ (management UI)

```bash
kubectl port-forward -n ecommerce svc/rabbitmq 15672:15672
# → http://localhost:15672   (voir credentials ci-dessous)
```

### PostgreSQL (psql direct via pooler)

```bash
# order-db
kubectl port-forward -n ecommerce svc/order-db-pooler 5432:5432
# inventory-db (autre port local pour éviter le conflit)
kubectl port-forward -n ecommerce svc/inventory-db-pooler 5433:5432
```

### Vault (UI / API)

En dev, Vault est exposé en **NodePort** (`vault:nodePort=30820`) → accès direct **sans
port-forward** (c'est aussi l'adresse qu'utilise le provider `pulumi-vault`) :

```bash
# → http://localhost:30820  (login : root token, cf. vault-init.json — voir docs/vault.md)
```

> En prod, Vault passe par l'Ingress (`vault:providerAddress=https://vault.{domain}`), pas le NodePort.

### MinIO (console — navigation des backups)

```bash
kubectl port-forward -n minio svc/minio-console 9001:9001
# → http://localhost:9001  (login : minio:rootUser / minio:rootPassword ; défaut dev minio / minio-dev-password)
# Bucket cnpg-backups → order-db/ & inventory-db/ (base/ + wals/). Voir docs/backups.md.
```

---

## Mots de passe & credentials

> ⚠️ **Windows** : `base64` n'existe pas dans CMD/PowerShell. Lancer ces commandes
> depuis **Git Bash** (installé avec Git for Windows), où `base64 -d` fonctionne.

### Grafana

Mot de passe **généré aléatoirement** par le chart kube-prometheus-stack
(sauf si `observability:grafanaAdminPassword` est défini).

```bash
# user (= admin)
kubectl get secret kube-prometheus-stack-grafana -n monitoring \
  -o jsonpath="{.data.admin-user}" | base64 -d

# password
kubectl get secret kube-prometheus-stack-grafana -n monitoring \
  -o jsonpath="{.data.admin-password}" | base64 -d
```

### Argo CD

Mot de passe admin **généré au premier démarrage** (secret temporaire) :

```bash
kubectl get secret argocd-initial-admin-secret -n argocd \
  -o jsonpath="{.data.password}" | base64 -d
```

> User : `admin`. Le secret `argocd-initial-admin-secret` peut être supprimé après
> avoir changé le mot de passe (voir docs/argocd.md).

### RabbitMQ

Credentials imposés via le secret ESO (mêmes valeurs en mode Deployment et cluster) :

```bash
# user
kubectl get secret rabbitmq-default-user -n ecommerce \
  -o jsonpath="{.data.username}" | base64 -d
# password
kubectl get secret rabbitmq-default-user -n ecommerce \
  -o jsonpath="{.data.password}" | base64 -d
```

> En mode Deployment (dev simple), le secret est `rabbitmq-credentials`
> (clés `RabbitMQ__Username` / `RabbitMQ__Password`).

### PostgreSQL (order-db / inventory-db)

Le user applicatif est **`app`** (owner CNPG). Connection string complète :

```bash
# order-db (user app)
kubectl get secret order-db-credentials -n ecommerce \
  -o jsonpath="{.data.ConnectionStrings__OrderDb}" | base64 -d

# superuser postgres (admin)
kubectl get secret order-db-superuser-config -n ecommerce \
  -o jsonpath="{.data.password}" | base64 -d
```

Connexion psql directe (via port-forward du pooler ci-dessus) :

```bash
# user app
psql "host=localhost port=5432 dbname=order_db user=app password=<voir secret>"
```

Ou directement dans le pod (sans port-forward) :

```bash
kubectl exec -it -n ecommerce order-db-1 -c postgres -- psql -U postgres -d order_db
```

---

## Décoder un secret Kubernetes

Méthode générique pour n'importe quel secret.

### Lister les clés d'un secret

```bash
kubectl get secret <nom> -n <namespace> -o jsonpath="{.data}" 
# → affiche {"cle1":"<base64>","cle2":"<base64>"}
```

### Décoder une clé précise

```bash
kubectl get secret <nom> -n <namespace> -o jsonpath="{.data.<cle>}" | base64 -d
```

### Tout décoder d'un coup

```bash
kubectl get secret <nom> -n <namespace> -o go-template='
{{range $k, $v := .data}}{{$k}}: {{$v | base64decode}}{{"\n"}}{{end}}'
```

> ⚠️ Les secrets contiennent des credentials en clair une fois décodés —
> ne pas les coller dans des logs, tickets ou commits.
