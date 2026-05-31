# Versioning des images & GitOps

Stratégie de versioning des 3 images applicatives (order-api, inventory-api,
gateway), conçue pour fonctionner avec le déploiement GitOps via Argo CD.

## Sommaire

- [Problème résolu](#problème-résolu)
- [Schéma de tag](#schéma-de-tag)
- [SHA par service](#sha-par-service)
- [Le script build-images.ps1](#le-script-build-imagesps1)
- [Workflow de développement](#workflow-de-développement)
- [Publier une release (bump SemVer)](#publier-une-release-bump-semver)
- [Pourquoi pas un tag `:dev` mutable](#pourquoi-pas-un-tag-dev-mutable)

---

## Problème résolu

GitOps (Argo CD) réagit aux **diffs de YAML**, pas au contenu des images Docker.

Avec un tag mutable (`:dev`), modifier le code d'un service ne change pas le YAML
rendu → `git diff` vide → Argo CD ne redéploie rien. Le tag doit donc être
**immuable** : changer à chaque évolution réelle du code.

À l'inverse, une version **partagée** par les 3 services causerait l'effet inverse :
modifier un seul service changerait le tag des trois → Argo CD redéploierait les
trois inutilement.

La solution combine les deux exigences.

---

## Schéma de tag

```
{SemVer}-{SHA-court-du-service}
```

Exemple :

| Service | Tag |
|---------|-----|
| order-api | `1.0.0-30e7e62` |
| inventory-api | `1.0.0-800a3a1` |
| gateway | `1.0.0-f2473ab` |

- **SemVer** (`1.0.0`) — lu depuis le fichier `VERSION` à la racine. Bumpé
  manuellement pour les releases. Donne une version lisible et cohérente pour les 3.
- **SHA court** — `git log -1` du dernier commit ayant touché les fichiers **de ce
  service uniquement**. Rend le tag immuable et **indépendant par service**.

---

## SHA par service

Le SHA est calculé à partir des chemins git propres à chaque service. Modifier un
service ne change que **son** SHA.

| Service | Chemins git suivis |
|---------|--------------------|
| order-api | `src/Services/Order` + `src/Shared/Ecommerce.Contracts` |
| inventory-api | `src/Services/Inventory` + `src/Shared/Ecommerce.Contracts` |
| gateway | `src/Gateway` |

`Ecommerce.Contracts` est inclus pour order-api **et** inventory-api : modifier un
contrat d'événement partagé rebumpe ses deux consommateurs (respect du graphe de
dépendances réel du monorepo).

### Effet sur le redéploiement

Modification de **inventory-api seul** :

| Service | Tag avant | Tag après | YAML change ? | Argo CD redéploie ? |
|---------|-----------|-----------|---------------|---------------------|
| order-api | `1.0.0-aaa111` | `1.0.0-aaa111` | non | ❌ non |
| inventory-api | `1.0.0-bbb222` | `1.0.0-ccc333` | **oui** | ✅ **oui** |
| gateway | `1.0.0-ddd444` | `1.0.0-ddd444` | non | ❌ non |

Seul le service modifié est redéployé.

---

## Le script build-images.ps1

`scripts/build-images.ps1` automatise tout le cycle :

1. Lit le SemVer dans `VERSION`.
2. Pour chaque service : calcule son SHA, build l'image taguée, `kind load`.
3. Pousse le tag dans Pulumi via `pulumi config set <svc>:image localhost/...:<tag>`.

Les `*ServiceResources` C# lisent déjà l'image depuis `pulumi config`
(`orderApi:image`, `inventoryApi:image`, `gateway:image`) — **aucun changement de
code C#** n'est nécessaire.

### Tag `-dirty`

Si le working tree a des modifications non commitées sur les paths d'un service,
son tag est suffixé `-dirty` (ex: `1.0.0-aaa111-dirty`). Cela évite d'écraser une
image « propre » avec un build local non commité, et signale clairement un état
de travail non reproductible.

### Options

```powershell
# Build + tag + load + pulumi config set (s'arrête là)
pwsh scripts/build-images.ps1

# Workflow GitOps complet : build → pulumi up (render) → commit → push → sync Argo CD
pwsh scripts/build-images.ps1 -Push
```

---

## Workflow de développement

### Itération rapide (test local, sans GitOps)

```powershell
# 1. modifier le code d'un service
# 2. rebuild + load + config (calcule le nouveau tag)
pwsh scripts/build-images.ps1
# 3. appliquer localement pour tester
cd infra/Ecommerce.Infra && pulumi up --yes
```

En mode `gitops:enabled=false`, Pulumi déploie directement — pas de commit requis.

### Publier en GitOps

```powershell
# tout enchaîner : build → render → commit → push → Argo CD sync
pwsh scripts/build-images.ps1 -Push
```

Ou manuellement :

```powershell
pwsh scripts/build-images.ps1
cd infra/Ecommerce.Infra && pulumi up --yes
cd ../..
git add gitops VERSION infra/Ecommerce.Infra/Pulumi.dev.yaml
git commit -m "build: inventory-api=1.0.0-ccc333"
git push
```

---

## Publier une release (bump SemVer)

Le SemVer dans `VERSION` est volontairement manuel — c'est une décision humaine
(une release, pas un build).

```bash
# Éditer le fichier VERSION : 1.0.0 → 1.1.0
echo 1.1.0 > VERSION

# Optionnel : taguer le commit de release dans Git
git tag v1.1.0
git push --tags
```

Au prochain build, les 3 services passent en `1.1.0-<sha>`. Règle SemVer usuelle :

| Incrément | Quand |
|-----------|-------|
| PATCH (`1.0.x`) | corrections de bugs rétrocompatibles |
| MINOR (`1.x.0`) | nouvelles fonctionnalités rétrocompatibles |
| MAJOR (`x.0.0`) | changements cassants (API, contrats d'événements) |

> Note : un bump de SemVer change le tag des **3** services (le préfixe change pour
> tous) → les 3 sont redéployés. C'est voulu : une release est un événement global.
> Les SHA par service ne pilotent les redéploiements ciblés qu'**entre** deux
> releases (à SemVer constant).

---

## Pourquoi pas un tag `:dev` mutable

| Approche | Immuable ? | Redéploiement ciblé ? | GitOps-compatible ? |
|----------|-----------|----------------------|---------------------|
| `:dev` (mutable) | ❌ | ❌ | ❌ (diff YAML toujours vide) |
| SemVer partagé | ✅ | ❌ (redéploie les 3) | ⚠️ imprécis |
| **SemVer + SHA par service** | ✅ | ✅ | ✅ |

Le tag `:dev` reste utilisé comme **fallback du tout premier build** (valeur par
défaut dans `Pulumi.dev.yaml`), avant le premier passage de `build-images.ps1`.
Ensuite, les `pulumi config set` du script prennent le relais.
