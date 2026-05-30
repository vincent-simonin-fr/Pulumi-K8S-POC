# Argo CD — GitOps Continuous Delivery

## Sommaire

- [Architecture](#architecture)
- [Installation](#installation)
- [Accès à l'interface](#accès-à-linterface)
- [CLI argocd](#cli-argocd)
- [Créer une Application (GitOps)](#créer-une-application-gitops)
- [RBAC](#rbac)
- [Mot de passe admin](#mot-de-passe-admin)
- [SSO (OIDC)](#sso-oidc)
- [Scaling HA (production)](#scaling-ha-production)
- [Métriques Prometheus](#métriques-prometheus)
- [Images à pré-charger (Kind)](#images-à-pré-charger-kind)

---

## Architecture

```
Git repo (manifestes K8s)
        │
        │  poll toutes les 3 min (ou webhook push)
        ▼
  Argo CD repo-server ──► render Helm / Kustomize / manifestes YAML
        │
        ▼
  Argo CD application-controller ──► compare état Git ↔ cluster
        │  diff détecté → sync
        ▼
  Kubernetes API ──► apply des ressources
        │
        ▼
  Namespace ecommerce (pods, services, configmaps...)
```

**Composants déployés** (namespace `argocd`) :

| Composant                          | Rôle                                      | Scalable              |
| ---------------------------------- | ----------------------------------------- | --------------------- |
| `argocd-server`                    | UI web + API REST + gRPC CLI              | Oui (stateless)       |
| `argocd-application-controller`    | Réconciliation Git ↔ cluster              | Non (leader election) |
| `argocd-repo-server`               | Clone Git, render Helm/Kustomize          | Oui                   |
| `argocd-applicationset-controller` | Génération d'Applications (multi-cluster) | Oui                   |
| `argocd-redis`                     | Cache interne Argo CD                     | Non (HA via redis-ha) |
| `argocd-notifications-controller`  | Envoi notifications (Slack, email…)       | Non                   |

---

## Installation

Argo CD est installé automatiquement par Pulumi lors du `pulumi up`.

```bash
# Depuis infra/Ecommerce.Infra/
pulumi up --yes
```

Vérifier que les pods sont Running :

```bash
kubectl get pods -n argocd
```

Résultat attendu :

```
argocd-application-controller-0             1/1     Running
argocd-applicationset-controller-xxx        1/1     Running
argocd-notifications-controller-xxx         1/1     Running
argocd-redis-xxx                            1/1     Running
argocd-repo-server-xxx                      1/1     Running
argocd-server-xxx                           1/1     Running
```

---

## Accès à l'interface

### Dev (Kind — sans ingress)

```bash
# Terminal dédié — à garder ouvert
kubectl port-forward -n argocd svc/argocd-server 8080:80
```

Ouvrir **http://localhost:8080**

### Production (avec ingress)

L'ingress est créé automatiquement quand `ingress:enabled: "true"` dans `Pulumi.dev.yaml`.  
URL : **https://argocd.wizzz.com**

---

## CLI argocd

### Installation

```bash
# Windows
winget install ArgoProj.ArgoCD

# macOS
brew install argocd

# Linux
curl -sSL -o argocd https://github.com/argoproj/argo-cd/releases/latest/download/argocd-linux-amd64
chmod +x argocd && mv argocd /usr/local/bin/
```

### Connexion

```bash
# Dev (port-forward actif)
argocd login localhost:8080 --username admin --insecure

# Production
argocd login argocd.wizzz.com --username admin
# → ou via SSO : argocd login argocd.wizzz.com --sso
```

### Commandes essentielles

```bash
# Lister les Applications
argocd app list

# Voir le statut d'une Application
argocd app get ecommerce

# Synchroniser manuellement (si auto-sync désactivé)
argocd app sync ecommerce

# Voir le diff Git ↔ cluster
argocd app diff ecommerce

# Historique des syncs
argocd app history ecommerce

# Rollback vers une révision précédente
argocd app rollback ecommerce <revision-id>

# Changer de contexte (multi-cluster)
argocd context
```

---

## Créer une Application (GitOps)

Une **Application** Argo CD lie un répertoire de manifestes Git à un namespace K8s.

### Via la CLI

```bash
argocd app create ecommerce \
  --repo https://github.com/votre-org/votre-repo.git \
  --path k8s/ecommerce \
  --dest-server https://kubernetes.default.svc \
  --dest-namespace ecommerce \
  --sync-policy automated \
  --auto-prune \
  --self-heal
```

### Via un manifeste YAML (recommandé en production)

```yaml
# argocd-apps/ecommerce.yaml
apiVersion: argoproj.io/v1alpha1
kind: Application
metadata:
    name: ecommerce
    namespace: argocd
    finalizers:
        - resources-finalizer.argocd.argoproj.io # supprime les ressources K8s à la suppression de l'App
spec:
    project: default
    source:
        repoURL: https://github.com/votre-org/votre-repo.git
        targetRevision: main
        path: k8s/ecommerce
    destination:
        server: https://kubernetes.default.svc
        namespace: ecommerce
    syncPolicy:
        automated:
            prune: true # supprime les ressources retirées du repo
            selfHeal: true # re-applique si quelqu'un modifie manuellement le cluster
        syncOptions:
            - CreateNamespace=true
            - PrunePropagationPolicy=foreground
            - RespectIgnoreDifferences=true
        retry:
            limit: 5
            backoff:
                duration: 5s
                factor: 2
                maxDuration: 3m
```

```bash
kubectl apply -f argocd-apps/ecommerce.yaml
```

### Coexistence Pulumi + Argo CD

Ce projet utilise **Pulumi** pour l'infrastructure (CNPG, KEDA, Redis, RabbitMQ, observabilité) et peut utiliser **Argo CD** pour les déploiements applicatifs (order-api, inventory-api, gateway).

Stratégie recommandée :

- **Pulumi** → infra stateful (bases de données, middleware, monitoring)
- **Argo CD** → applications stateless (images Docker, ConfigMaps, HPAs applicatifs)

---

## RBAC

Configuration actuelle (définie dans `ArgocdResources.cs`) :

| Rôle            | Permissions                               | Attribution                        |
| --------------- | ----------------------------------------- | ---------------------------------- |
| `role:readonly` | Lecture seule sur toutes les Applications | **Tous les utilisateurs** (défaut) |
| `role:admin`    | Droits complets                           | Groupe **admins**                  |

### Ajouter un utilisateur local

```bash
# Créer un compte "dev-user" avec droits lecture seule
argocd account create dev-user

# Générer un token (CI/CD)
argocd account generate-token --account dev-user

# Donner des droits à un projet spécifique (adapter dans argocd-rbac-cm)
```

### Avec SSO — mapper un groupe OIDC

```yaml
# ConfigMap argocd-rbac-cm
data:
    policy.csv: |
        g, admins, role:admin
        g, developers, role:readonly
        p, role:developer, applications, sync, ecommerce/*, allow
```

---

## Mot de passe admin

### Récupérer le mot de passe auto-généré

```bash
kubectl get secret argocd-initial-admin-secret -n argocd -o json | python -c "import sys,json,base64; d=json.load(sys.stdin);  print(base64.b64decode(d['data']['password']).decode())"
```

### Changer le mot de passe (via CLI)

```bash
argocd account update-password \
  --current-password <ancien> \
  --new-password <nouveau>
```

### Définir le mot de passe via Pulumi (production)

```bash
# 1. Générer le hash bcrypt
htpasswd -nbBC 10 "" monMotDePasse | tr -d ':\n'
# → $2y$10$xxxxx...

# 2. Stocker dans Pulumi (chiffré)
pulumi config set --secret argocd:adminPasswordHash '$2y$10$xxxxx...'

# 3. Appliquer
pulumi up --yes
```

---

## SSO (OIDC)

> **Non configuré** dans l'installation actuelle. Activer Dex + OIDC pour l'authentification via GitHub, GitLab, Azure AD, Google.

### Activer Dex + GitHub OAuth

```yaml
# Dans ArgocdResources.cs → Values["dex"]
["dex"] = new Dictionary<string, object>
{
    ["enabled"] = true
}
```

```yaml
# ConfigMap argocd-cm (à ajouter manuellement ou via Pulumi)
data:
    url: https://argocd.wizzz.com
    dex.config: |
        connectors:
        - type: github
          id: github
          name: GitHub
          config:
            clientID: <GITHUB_CLIENT_ID>
            clientSecret: $dex.github.clientSecret
            orgs:
            - name: votre-org
              teams:
              - admins
              - developers
```

---

## Scaling HA (production)

Pour un cluster multi-nœuds (≥ 3 nœuds), passer en mode HA :

```bash
# Dans Pulumi.dev.yaml (ou Pulumi.prod.yaml)
pulumi config set argocd:serverReplicas         2
pulumi config set argocd:repoServerReplicas     2
pulumi config set argocd:applicationSetReplicas 2
pulumi up --yes
```

Pour Redis HA (tolérance à la panne d'un nœud Redis) — nécessite une modification dans `ArgocdResources.cs` :

```csharp
// Remplacer :
["redis"] = new Dictionary<string, object> { ["enabled"] = true, ... }

// Par :
["redis"]    = new Dictionary<string, object> { ["enabled"] = false },
["redis-ha"] = new Dictionary<string, object>
{
    ["enabled"] = true,
    ["redis"] = new Dictionary<string, object>
    {
        ["resources"] = Resources("50m", "200m", "64Mi", "256Mi")
    }
}
```

---

## Métriques Prometheus

Prometheus scrape automatiquement les 4 composants Argo CD (configuré dans `ObservabilityResources.cs`).

**Métriques clés** disponibles dans Grafana :

| Métrique                              | Description                                                     |
| ------------------------------------- | --------------------------------------------------------------- |
| `argocd_app_info`                     | État de chaque Application (Synced/OutOfSync, Healthy/Degraded) |
| `argocd_app_sync_total`               | Nombre de syncs par Application et résultat                     |
| `argocd_git_request_duration_seconds` | Latence des requêtes Git (fetch, ls-remote)                     |
| `argocd_app_k8s_request_total`        | Requêtes K8s générées par la réconciliation                     |
| `argocd_repo_pending_request_total`   | Requêtes en attente côté repo-server                            |

Dashboard Grafana communautaire : **ID 14584** (importer depuis grafana.com/dashboards).

```bash
# Vérifier que les targets sont UP dans Prometheus
kubectl port-forward -n monitoring svc/prometheus 9090:9090
# http://localhost:9090/targets → jobs argocd-*
```

---

## Images à pré-charger (Kind)

Pour accélérer le démarrage (éviter de puller depuis quay.io à chaque `pulumi up`) :

```bash
# Version 2.14.x (chart 7.8.3)
ARGOCD_VERSION=v2.14.3

podman pull quay.io/argoproj/argocd:${ARGOCD_VERSION}
kind load docker-image quay.io/argoproj/argocd:${ARGOCD_VERSION} --name ecommerce

# Redis intégré Argo CD
podman pull redis:7.0.15-alpine
kind load docker-image redis:7.0.15-alpine --name ecommerce
```
