# k9s

Interface terminal pour Kubernetes. Remplace l'essentiel des usages du Kubernetes Dashboard
(logs, describe, exec, events) sans rien déployer dans le cluster.

## Sommaire

- [Pourquoi k9s](#pourquoi-k9s)
- [Installation](#installation)
- [Lancement](#lancement)
- [Navigation](#navigation)
- [Actions sur un pod](#actions-sur-un-pod)
- [Vue logs](#vue-logs)
- [Utilisation avec la stack ecommerce](#utilisation-avec-la-stack-ecommerce)

---

## Pourquoi k9s

| | k9s | Kubernetes Dashboard |
|---|---|---|
| Surface réseau | Aucune | Port local expose |
| Credentials | kubeconfig existant | Token cluster-admin dedie |
| Installation cluster | Aucune | Namespace + RBAC + pods |
| Logs pod precedent | Touche `p` | Non disponible |

k9s utilise directement le kubeconfig actif — memes droits RBAC, meme canal TLS que `kubectl`.
Pas de token a generer, pas de service a exposer.

---

## Installation

**Windows**
```bash
# Via winget
winget install k9s

# Via Scoop
scoop install k9s
```

**macOS**
```bash
brew install k9s
```

**Linux**
```bash
# Via snap
snap install k9s

# Via binaire (https://github.com/derailed/k9s/releases)
curl -sL https://github.com/derailed/k9s/releases/latest/download/k9s_Linux_amd64.tar.gz | tar xz
sudo mv k9s /usr/local/bin/
```

---

## Lancement

```bash
# Cluster et namespace par defaut (kubeconfig actif)
k9s

# Cibler un namespace directement
k9s -n ecommerce

# Cibler un contexte specifique
k9s --context kind-ecommerce

# Cibler un contexte et un namespace
k9s --context kind-ecommerce -n monitoring
```

---

## Navigation

| Touche | Action |
|---|---|
| `:pods` + Entree | Afficher les pods |
| `:deploy` + Entree | Afficher les deployments |
| `:svc` + Entree | Afficher les services |
| `:ns` + Entree | Afficher et changer de namespace |
| `:cm` + Entree | Afficher les ConfigMaps |
| `:secrets` + Entree | Afficher les Secrets |
| `:events` + Entree | Afficher les events du cluster |
| `↑ ↓` | Naviguer dans la liste |
| `/` + texte | Filtrer la liste (ex : `/order` pour les pods order-api) |
| `Echap` | Revenir en arriere |
| `q` | Quitter k9s |

---

## Actions sur un pod

Selectionner un pod avec `↑ ↓`, puis :

| Touche | Action |
|---|---|
| `l` | Afficher les logs |
| `s` | Ouvrir un shell dans le conteneur |
| `d` | Describe (equivalent `kubectl describe pod`) |
| `e` | Editer le manifest YAML |
| `ctrl+d` | Supprimer le pod |

---

## Vue logs

Appuyer sur `l` sur un pod pour entrer dans la vue logs, puis :

| Touche | Action |
|---|---|
| `f` | Activer / desactiver le follow (equivalent `kubectl logs -f`) |
| `w` | Activer / desactiver le wrap des lignes |
| `/` + texte | Rechercher dans les logs |
| `0` | Afficher tous les logs depuis le debut du pod |
| `p` | Logs du pod **precedent** (indispensable apres un CrashLoopBackOff) |
| `Echap` | Revenir a la liste des pods |

> **Astuce** : la touche `p` (previous) est la plus utile en debug — elle affiche les logs du
> conteneur avant le dernier restart, ce que `kubectl logs` ne fait qu'avec le flag `--previous`.

---

## Utilisation avec la stack ecommerce

```bash
# Ouvrir k9s sur le namespace applicatif
k9s -n ecommerce
```

Pods disponibles : `order-api`, `inventory-api`, `gateway`, `rabbitmq`,
`order-db`, `inventory-db`, `postgres-exporter-order`, `postgres-exporter-inventory`.

```bash
# Ouvrir k9s sur le namespace monitoring
k9s -n monitoring
```

Pods disponibles : `otel-collector`, `jaeger`, `prometheus`, `grafana`,
`kube-state-metrics`, `node-exporter`.

**Workflow de debug type :**

1. `k9s -n ecommerce`
2. Filtrer avec `/order-api`
3. Selectionner le pod → `l` pour les logs en direct
4. Si le pod a redemarré → `p` pour les logs avant le crash
5. Si besoin d'inspecter l'environnement → `s` pour un shell