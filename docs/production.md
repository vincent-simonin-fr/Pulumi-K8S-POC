# Déploiement en production

## Sommaire

- [Prérequis](#prérequis)
- [Avant le premier déploiement](#avant-le-premier-déploiement)
- [Configurer les secrets](#configurer-les-secrets)
- [Déployer](#déployer)
- [Configurer le DNS](#configurer-le-dns)
- [Vérifier le déploiement](#vérifier-le-déploiement)
- [Opérations courantes](#opérations-courantes)
- [Changer de domaine](#changer-de-domaine)

---

## Prérequis

| Prérequis | Détail |
|---|---|
| Cluster Kubernetes | GKE / EKS / AKS — avec support `LoadBalancer` (provisionne une IP publique) |
| `kubectl` configuré | `kubectl config use-context <prod-context>` |
| `pulumi` installé | v3+ |
| `htpasswd` disponible | Apache utils — `sudo apt install apache2-utils` ou `brew install httpd` |
| Domaine `wizzz.com` | Accès à la gestion DNS du registrar |
| Container registry | ghcr.io, Docker Hub, ECR, GCR... avec les images buildées |

> **Port 80 accessible depuis Internet** : requis pour la validation HTTP-01 de Let's Encrypt.
> Si votre cluster est derrière un pare-feu, ouvrez le port 80 sur le LoadBalancer nginx-ingress.

---

## Avant le premier déploiement

### 1. Créer le stack prod

```bash
cd infra/Ecommerce.Infra
pulumi stack init prod
pulumi stack select prod
```

### 2. Vérifier le domaine dans `Pulumi.prod.yaml`

```yaml
ingress:domain: "wizzz.com"      # ← à modifier si le domaine change
ingress:acmeEmail: "ops@wizzz.com"
```

Le domaine est le seul paramètre à changer pour adapter la stack à un autre site.
Toutes les URLs en découlent automatiquement :
- `wizzz.com` → gateway
- `grafana.wizzz.com` → Grafana
- `jaeger.wizzz.com` → Jaeger

### 3. Builder et pousser les images

```bash
# Exemple avec GitHub Container Registry
docker build -f docker/order-api/Dockerfile     -t ghcr.io/wizzz/order-api:latest .
docker build -f docker/inventory-api/Dockerfile -t ghcr.io/wizzz/inventory-api:latest .
docker build -f docker/gateway/Dockerfile       -t ghcr.io/wizzz/gateway:latest .

docker push ghcr.io/wizzz/order-api:latest
docker push ghcr.io/wizzz/inventory-api:latest
docker push ghcr.io/wizzz/gateway:latest
```

Mettre à jour les images dans `Pulumi.prod.yaml` si vous utilisez un registry différent :
```yaml
orderApi:image: ghcr.io/wizzz/order-api:latest
inventoryApi:image: ghcr.io/wizzz/inventory-api:latest
gateway:image: ghcr.io/wizzz/gateway:latest
```

---

## Configurer les secrets

Tous les secrets sont chiffrés par Pulumi dans `Pulumi.prod.yaml` (jamais en clair).

```bash
cd infra/Ecommerce.Infra
pulumi stack select prod

# ── Bases de données ─────────────────────────────────────────────────────────
pulumi config set --secret secrets:orderDbPassword     "<password>"
pulumi config set --secret secrets:inventoryDbPassword "<password>"
pulumi config set --secret secrets:orderDbUser         postgres      # si différent du défaut
pulumi config set --secret secrets:inventoryDbUser     postgres

# ── RabbitMQ ─────────────────────────────────────────────────────────────────
pulumi config set --secret secrets:rabbitmqPassword "<password>"
pulumi config set --secret secrets:rabbitmqUser     admin

# ── Monitoring (basic-auth nginx pour Grafana + Jaeger) ──────────────────────
# Générer le hash htpasswd (format Apache) :
htpasswd -nb admin <password>
# Exemple de sortie : admin:$apr1$xyz...$hash...
# Copier la valeur complète et la passer à Pulumi :
pulumi config set --secret ingress:monitoringBasicAuthHtpasswd "admin:\$apr1\$xyz...\$hash..."

# ── Grafana (login natif, auth anonyme désactivée en prod) ───────────────────
pulumi config set --secret observability:grafanaAdminPassword "<password>"
```

> **Vérification** : après ces commandes, `Pulumi.prod.yaml` doit contenir des valeurs
> chiffrées (format `secure: <base64>`), jamais de mots de passe en clair.

---

## Déployer

```bash
cd infra/Ecommerce.Infra
pulumi stack select prod

# Aperçu sans appliquer (recommandé avant le premier déploiement)
pulumi preview

# Déploiement complet
pulumi up --stack prod
```

**Durée estimée** : 5-10 minutes (cert-manager et nginx-ingress ont des hooks Helm qui attendent
que les pods soient Ready avant de continuer).

**Ordre de déploiement automatique** :
1. Namespace `monitoring` + `cert-manager` + `ingress`
2. cert-manager (Helm) + nginx-ingress (Helm) — en parallèle
3. `ClusterIssuer` letsencrypt-prod — après cert-manager
4. Infra applicative (PostgreSQL, RabbitMQ, APIs)
5. Ingress resources (gateway, grafana, jaeger) — après ClusterIssuer + nginx

---

## Configurer le DNS

Après `pulumi up`, récupérer l'IP publique du LoadBalancer nginx-ingress :

```bash
kubectl get svc -n ingress
# NAME                                 TYPE           EXTERNAL-IP
# ingress-nginx-controller             LoadBalancer   <IP-PUBLIQUE>
```

Créer les enregistrements DNS chez votre registrar :

| Type | Nom | Valeur |
|---|---|---|
| A | `wizzz.com` | `<IP-PUBLIQUE>` |
| A | `grafana.wizzz.com` | `<IP-PUBLIQUE>` |
| A | `jaeger.wizzz.com` | `<IP-PUBLIQUE>` |

> **TTL recommandé** : 300s (5 min) pour le premier déploiement, augmenter à 3600s ensuite.

Une fois le DNS propagé, cert-manager déclenche automatiquement les challenges Let's Encrypt
et génère les certificats (délai : 1-5 minutes).

Vérifier la progression :
```bash
kubectl get certificate -n ecommerce
kubectl get certificate -n monitoring
# READY = True → certificat émis et stocké dans le Secret K8s
```

---

## Vérifier le déploiement

```bash
# 1. Pods applicatifs
kubectl get pods -n ecommerce
kubectl get pods -n monitoring
kubectl get pods -n ingress
kubectl get pods -n cert-manager
# Tous → Running

# 2. Certificats TLS (Let's Encrypt)
kubectl get certificate -A
# READY = True pour tls-gateway, tls-grafana, tls-jaeger

# 3. Ingress
kubectl get ingress -A
# ADDRESS = <IP-PUBLIQUE> pour les 3 Ingress

# 4. Endpoints HTTPS
curl https://wizzz.com/health
# 200 OK → gateway opérationnel, TLS valide

curl -u admin:<password> https://grafana.wizzz.com/api/health
# {"commit":"...","database":"ok",...} → Grafana opérationnel

# 5. Outputs Pulumi
pulumi stack output --stack prod
# GatewayUrl  = https://wizzz.com
# GrafanaUrl  = https://grafana.wizzz.com
# JaegerUrl   = https://jaeger.wizzz.com
```

---

## Opérations courantes

### Mettre à jour une image

```bash
# Builder + pousser la nouvelle image
docker build -f docker/order-api/Dockerfile -t ghcr.io/wizzz/order-api:v1.2.0 .
docker push ghcr.io/wizzz/order-api:v1.2.0

# Modifier Pulumi.prod.yaml
# orderApi:image: ghcr.io/wizzz/order-api:v1.2.0

# Déployer (rolling update automatique)
pulumi up --stack prod
```

### Changer un mot de passe

```bash
pulumi config set --secret secrets:orderDbPassword "<nouveau-password>" --stack prod
pulumi up --stack prod
```

### Scaler manuellement (si HPA désactivé)

```bash
# Modifier Pulumi.prod.yaml
# replicas:orderApi: "3"
pulumi up --stack prod
```

### Voir les logs en production

```bash
# Via k9s (recommandé)
k9s --context <prod-context> -n ecommerce

# Via kubectl
kubectl logs -n ecommerce deploy/order-api -f
kubectl logs -n ecommerce deploy/order-api --previous  # après un crash
```

### Renouvellement des certificats

cert-manager renouvelle automatiquement les certificats 30 jours avant expiration.
Aucune action manuelle requise. Pour forcer un renouvellement :

```bash
kubectl delete secret tls-gateway -n ecommerce
# cert-manager recrée le certificat automatiquement
```

---

## Changer de domaine

Le domaine est centralisé dans `Pulumi.prod.yaml`. Pour migrer de `wizzz.com` vers un autre domaine :

```yaml
# Pulumi.prod.yaml
ingress:domain: "nouveau-domaine.com"
ingress:acmeEmail: "ops@nouveau-domaine.com"
```

```bash
pulumi up --stack prod
```

Pulumi mettra à jour les 3 Ingress et cert-manager émettra de nouveaux certificats pour le nouveau domaine. Les anciens certificats seront supprimés.

> **⚠️ DNS** : pointer les enregistrements A du nouveau domaine vers le même LoadBalancer IP
> avant de lancer `pulumi up`, sinon Let's Encrypt ne pourra pas valider les nouveaux certificats.
