# Test de HA multi-nœuds (Kind)

Guide pour valider la haute disponibilité (anti-affinité, CNPG/RabbitMQ cluster,
failover) sur un cluster Kind **multi-nœuds**, ce que le mono-nœud ne permet pas.

## Sommaire

- [Ce que ce test valide (et ne valide pas)](#ce-que-ce-test-valide-et-ne-valide-pas)
- [1. Créer le cluster multi-nœuds](#1-créer-le-cluster-multi-nœuds)
- [2. Déployer en mode HA](#2-déployer-en-mode-ha)
- [3. Vérifier la répartition sur les nœuds](#3-vérifier-la-répartition-sur-les-nœuds)
- [4. Test failover — drain d'un nœud](#4-test-failover--drain-dun-nœud)
- [5. Le test du stockage (local-path montre ses limites)](#5-le-test-du-stockage)
- [Retour au dev mono-nœud](#retour-au-dev-mono-nœud)

---

## Ce que ce test valide (et ne valide pas)

| Validé ✅ | Non validé ❌ |
|-----------|----------------|
| Anti-affinité (pods répartis sur les nœuds) | Vraie panne matérielle (nœuds = conteneurs même laptop) |
| CNPG : 3 instances sur 3 nœuds, failover primary | Performance prod (CPU/RAM physique partagé) |
| RabbitMQ : quorum réparti, survie à la perte d'un nœud | Latence réseau inter-nœuds réelle |
| Limites de `local-path` en multi-nœuds | Stockage réseau (gp3/ceph — cloud uniquement) |

> Les nœuds Kind sont des conteneurs sur la même machine. On teste le **scheduling
> et le failover logique** (cordon/drain), pas la résilience physique. C'est
> largement suffisant pour valider la mécanique HA.

---

## 1. Activer le mode HA

```bash
cd infra/Ecommerce.Infra
pulumi config set cnpg:orderInstances 3
pulumi config set cnpg:inventoryInstances 3
pulumi config set rabbitmq:cluster true
cd ../..
```

## 2. Créer le cluster multi-nœuds + déployer

Une seule commande : Nuke recrée le cluster avec la config multi-nœuds, précharge
les images (sur tous les nœuds), build les apps, et déploie.

```bash
dotnet nuke Launch --kind-config kind-config-multinode.yaml
```

> Le paramètre `--kind-config` pointe `RecreateCluster` vers la topologie 4 nœuds.
> Sans lui, Nuke utiliserait `kind-config.yaml` (mono-nœud) par défaut.
> `kind load` charge les images sur **tous** les nœuds automatiquement.

> ⚠️ Cette commande **recrée le cluster `ecommerce`** : le cluster dev mono-nœud
> est détruit. Acceptable en dev (données jetables). Pour **préserver** le cluster
> dev, voir la variante ci-dessous.

### Variante : cluster HA parallèle (préserver le cluster dev)

Pour tester la HA **sans détruire** le cluster dev, on crée un second cluster
`ecommerce-ha` sur un stack Pulumi dédié `ha`. Kind ne sait pas ajouter de nœuds
à un cluster existant → c'est forcément un nouveau cluster ; et deux clusters ne
peuvent pas binder les mêmes NodePorts (30080/30030/30686) simultanément, donc on
**arrête** le mono (ses données restent sur le disque) le temps du test.

**Préparation du stack `ha` (une seule fois)** — clone la config de `dev` (aucun
secret chiffré ici → rien à ressaisir) puis applique les overrides HA :

```bash
cd infra/Ecommerce.Infra
export PULUMI_CONFIG_PASSPHRASE="<ta-passphrase>"   # Git Bash ; sous PowerShell : $env:PULUMI_CONFIG_PASSPHRASE="..."
pulumi stack init ha
pulumi stack select dev
pulumi config cp -d ha            # copie toute la config dev → ha
pulumi stack select ha
pulumi config set cnpg:orderInstances 3
pulumi config set cnpg:inventoryInstances 3
pulumi config set rabbitmq:cluster true
pulumi config set gitops:enabled false    # déploiement direct par Pulumi (pas d'ArgoCD/Git pour un test local)
cd ../..
```

**Déploiement du cluster parallèle** :

```bash
# 1. Libère les NodePorts en arrêtant le mono (cluster conservé, données intactes)
podman stop ecommerce-control-plane

# 2. Stack ha déjà sélectionné → Nuke déploie sur le nouveau cluster
dotnet nuke Launch \
  --kind-config kind-config-multinode.yaml \
  --cluster-name ecommerce-ha \
  --pulumi-passphrase "<ta-passphrase>"
```

> `--cluster-name ecommerce-ha` cible le nouveau cluster pour `kind create/delete`,
> `kind load` et le `kubectl config use-context`. Le `pulumi up` interne s'exécute
> sur le stack **actuellement sélectionné** (`ha`) → bien le sélectionner avant.

**Retour au dev mono-nœud** (sans rien reconstruire) :

```bash
kind delete cluster --name ecommerce-ha      # KIND_EXPERIMENTAL_PROVIDER=podman
pulumi stack select dev                       # (depuis infra/Ecommerce.Infra)
podman start ecommerce-control-plane          # remonte le mono avec ses données
kubectl config use-context kind-ecommerce
```

Vérifier les 4 nœuds :

```bash
kubectl get nodes
```

Résultat attendu :
```
NAME                       STATUS   ROLES           AGE
ecommerce-control-plane    Ready    control-plane   2m
ecommerce-worker           Ready    <none>          2m
ecommerce-worker2          Ready    <none>          2m
ecommerce-worker3          Ready    <none>          2m
```

---

## 3. Vérifier la répartition sur les nœuds

C'est ici que l'**anti-affinité** (K8sAffinity.cs) entre en jeu — invisible en
mono-nœud, active en multi-nœuds.

```bash
# Où tournent les pods order-api ? (doivent être sur des nœuds DIFFÉRENTS)
kubectl get pods -n ecommerce -l app=order-api -o wide

# Les 3 instances CNPG réparties ?
kubectl get pods -n ecommerce -l cnpg.io/cluster=order-db \
  -o custom-columns=POD:.metadata.name,NODE:.spec.nodeName

# Les 3 nœuds RabbitMQ répartis ?
kubectl get pods -n ecommerce -l app.kubernetes.io/name=rabbitmq \
  -o custom-columns=POD:.metadata.name,NODE:.spec.nodeName
```

✅ Succès = les réplicas d'un même service sont sur des nœuds distincts.

---

## 4. Test failover — drain d'un nœud

Le vrai test de HA : simuler la perte d'un nœud et vérifier que tout survit.

```bash
# Identifier le nœud qui porte le PRIMARY CNPG order-db
kubectl get pods -n ecommerce -l cnpg.io/cluster=order-db \
  -L cnpg.io/instanceRole -o wide
# → repérer le pod 'primary' et son NODE (ex: ecommerce-worker2)
```

### Drainer ce nœud (simule sa perte)

```bash
# cordon : empêche tout nouveau scheduling sur ce nœud
# drain : évince les pods (ils seront recréés ailleurs)
kubectl drain ecommerce-worker2 --ignore-daemonsets --delete-emptydir-data --force
```

### Observer la réaction (autre terminal)

```bash
# CNPG promeut un replica en primary (~10-30s)
kubectl get cluster order-db -n ecommerce -w
# → la colonne PRIMARY change vers order-db-1/2/3 survivant

# RabbitMQ garde le quorum (2/3 nœuds > 50%)
kubectl exec -n ecommerce rabbitmq-server-0 -c rabbitmq -- \
  rabbitmqctl cluster_status | grep -A4 "Running Nodes"

# Les apps continuent de répondre
curl http://localhost:30080/health
```

### Vérifier que l'app reste fonctionnelle pendant le drain

```bash
# Pendant le drain, lancer un petit test
k6 run tests/Ecommerce.LoadTests/scenarios/baseline.js
# → les requêtes passent (peut-être quelques erreurs transitoires pendant
#   la promotion CNPG / le reschedule, mais pas d'effondrement)
```

### Réintégrer le nœud

```bash
kubectl uncordon ecommerce-worker2
# Le scheduler peut de nouveau y placer des pods
```

---

## 5. Le test du stockage

C'est le point pédagogique clé : **pourquoi le stockage réseau est obligatoire
en prod**.

```bash
# Voir où sont les PVC CNPG
kubectl get pvc -n ecommerce -o wide
kubectl get pv -o custom-columns=PV:.metadata.name,NODE:.spec.nodeAffinity
```

Avec `local-path` (la StorageClass dev), chaque PVC est **lié au disque du nœud**
où le pod a démarré. Conséquence observable lors du drain :

- Le pod CNPG évincé ne peut **pas** récupérer son PVC sur un autre nœud
  (le volume est resté sur le nœud drainé).
- CNPG recrée le replica par `pg_basebackup` depuis le primary → ça marche, mais
  c'est une **reconstruction complète**, pas un simple rattachement.

→ En prod, une StorageClass **réseau** (`cnpg:storageClass=gp3/ceph/longhorn`)
permet au volume de suivre le pod sur n'importe quel nœud. C'est la config de
`Pulumi.prod.yaml`. Ce test montre concrètement pourquoi `local-path` ne suffit pas.

---

## Retour au dev mono-nœud

Une fois les tests HA terminés, revenir à la config légère (perfs optimales) :

```bash
# Config dev légère
cd infra/Ecommerce.Infra
pulumi config set cnpg:orderInstances 1
pulumi config set cnpg:inventoryInstances 1
pulumi config set rabbitmq:cluster false
cd ../..

# Recrée le cluster mono-nœud (config par défaut) + déploie
dotnet nuke Launch
```
