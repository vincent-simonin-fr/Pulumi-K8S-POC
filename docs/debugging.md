# Debugging

## Sommaire

- [Déboguer le projet Pulumi dans Visual Studio](#déboguer-le-projet-pulumi-dans-visual-studio)
- [Diagnostics kubectl](#diagnostics-kubectl)
- [Problèmes courants](#problèmes-courants)

---

## Déboguer le projet Pulumi dans Visual Studio

Le projet `Ecommerce.Infra` est un exécutable C# lancé par le CLI Pulumi. Il ne peut pas être démarré directement via F5 (le runtime Pulumi doit l'orchestrer). La procédure d'attachement est :

### Étape 1 — Préparer le code

Optionnel : insérer un point d'arrêt manuel au début du constructeur pour attendre l'attachement :

```csharp
// EcommerceStack.cs — temporaire, à retirer ensuite
public EcommerceStack()
{
    System.Diagnostics.Debugger.Launch(); // ← ouvre la fenêtre d'attachement
    // ... reste du code
}
```

### Étape 2 — Lancer Pulumi avec l'option debugger

```bash
cd infra/Ecommerce.Infra
pulumi up --attach-debugger --yes
```

Pulumi compile le projet, le lance, puis attend que Visual Studio s'attache.

### Étape 3 — Attacher Visual Studio

Dans Visual Studio :  
`Déboguer` → `Attacher au processus` (`Ctrl+Alt+P`)  
→ Sélectionner le processus `dotnet` qui exécute `Ecommerce.Infra`  
→ Cliquer **Attacher**

Les breakpoints dans `EcommerceStack.cs` et les `ComponentResource` sont maintenant actifs.

> **Remarque** : `Debugger.Launch()` est une alternative — il ouvre directement la boîte de dialogue d'attachement au moment de l'exécution.

---

## Diagnostics kubectl

### Voir l'état général

```bash
kubectl get all -n ecommerce
```

### Détails d'un pod (events, raison d'un crash)

```bash
kubectl describe pod -n ecommerce <nom-du-pod>
# ou par label
kubectl describe pod -n ecommerce -l app=order-api
```

### Logs d'un pod

```bash
# Logs en cours
kubectl logs -n ecommerce deploy/order-api -f

# Logs du container précédent (après un CrashLoopBackOff)
kubectl logs -n ecommerce deploy/order-api --previous

# Logs filtrés
kubectl logs -n ecommerce deploy/order-api | findstr -i "error\|fatal\|warn"
```

### Exécuter une commande dans un pod

```bash
# Vérifier les variables d'environnement injectées
kubectl exec -n ecommerce deploy/order-api -- env | findstr "ConnectionStrings\|RabbitMQ"

# Shell interactif
kubectl exec -it -n ecommerce deploy/order-api -- /bin/sh
```

### Vérifier les secrets

```bash
kubectl get secrets -n ecommerce
kubectl get secret order-db-credentials -n ecommerce -o jsonpath='{.data.POSTGRES_USER}' | base64 -d
kubectl get secret order-db-credentials -n ecommerce -o jsonpath='{.data.ConnectionStrings__OrderDb}' | base64 -d
```

### Vérifier les StatefulSets et PVCs

```bash
kubectl get statefulsets -n ecommerce
kubectl get pvc -n ecommerce
# Attendu : data-order-db-0, data-inventory-db-0  (STATUS=Bound)
```

---

## Problèmes courants

### ImagePullBackOff

**Symptôme** : pod bloqué sur `ImagePullBackOff` ou `ErrImagePull`.

**Causes et fixes** :

```bash
# 1. Vérifier que l'image est dans Kind
podman exec ecommerce-control-plane crictl images | findstr order-api

# 2. Si absente, charger l'image
kind load docker-image localhost/ecommerce/order-api:dev --name ecommerce

# 3. Vérifier que le nom dans Pulumi.dev.yaml correspond exactement
#    (Podman : préfixe localhost/ obligatoire)
```

---

### CrashLoopBackOff — application

**Symptôme** : pod de l'API redémarre en boucle.

```bash
# 1. Lire les logs
kubectl logs -n ecommerce deploy/order-api --previous

# 2. Causes fréquentes :
#    - DB pas encore prête → attendre, les init containers gèrent le wait
#    - Secret manquant     → kubectl get secrets -n ecommerce
#    - Erreur de migration EF Core → voir les logs (niveau Fatal)
```

---

### CrashLoopBackOff — PostgreSQL (corruption WAL)

**Symptôme** : `order-db-0` ou `inventory-db-0` en CrashLoopBackOff.

```bash
kubectl logs -n ecommerce -l app=order-db --previous
```

**Log caractéristique** :
```
PANIC: could not locate a valid checkpoint record
invalid magic number 0000 in WAL segment
```

**Cause** : PostgreSQL tué brutalement pendant une écriture WAL (pod supprimé lors d'un `pulumi up`).

**Fix** :
```bash
# Les StatefulSets ont un hook preStop pour éviter ça désormais.
# Si ça arrive malgré tout, supprimer le PVC corrompu :
kubectl delete pvc data-order-db-0 data-inventory-db-0 -n ecommerce
# Puis supprimer le StatefulSet (sans cascade) et relancer Pulumi
kubectl delete sts order-db inventory-db -n ecommerce --cascade=orphan
pulumi up --yes
```

> ⚠️ Supprimer le PVC efface toutes les données. Les migrations EF Core se relanceront au démarrage.

---

### HPA affiche `<unknown>/70%`

**Symptôme** : `kubectl get hpa -n ecommerce` → `TARGETS: <unknown>/70%`

**Cause** : Metrics Server non installé ou pas encore prêt.

```bash
kubectl get deployment metrics-server -n kube-system
# Si READY = 0/1 :
kubectl logs -n kube-system deploy/metrics-server
```

**Fix** : voir [Metrics Server](kubernetes.md#metrics-server-hpa).

---

### Connection refused vers order-db (order-api)

**Symptôme** : `Npgsql.NpgsqlException: Connection refused` dans les logs order-api.

**Cause** : order-api a démarré avant que order-db soit prête (race condition au premier démarrage).

**Comportement attendu** : le init container `wait-for-db` attend que `pg_isready` réponde avant de lancer l'API. Si order-db est en CrashLoopBackOff, l'init container attend indéfiniment.

**Diagnostic** :
```bash
kubectl get pods -n ecommerce
# Si order-db-0 est en CrashLoopBackOff → résoudre d'abord le problème DB
kubectl logs -n ecommerce -l app=order-db --previous
```

---

### Gateway en état Running mais non Ready (0/1)

**Symptôme** : `gateway-xxx` est `0/1 Running` depuis longtemps.

**Cause** : la readiness probe (`/health`) retourne une erreur — généralement parce que les services upstream (order-api ou inventory-api) ne sont pas encore prêts.

```bash
kubectl describe pod -n ecommerce -l app=gateway
# Chercher les derniers events : "Readiness probe failed"

kubectl logs -n ecommerce deploy/gateway
```

---

### `pulumi up` échoue avec "409 Conflict" ou "resource already exists"

**Cause** : ressources K8s existantes en dehors de l'état Pulumi (créées manuellement ou après un reset partiel).

```bash
# Option 1 : importer la ressource existante dans l'état Pulumi
pulumi import kubernetes:core/v1:Namespace ecommerce-ns ecommerce

# Option 2 : supprimer la ressource manuellement et relancer
kubectl delete namespace ecommerce
pulumi up --yes
```

---

### Vérification rapide complète

```bash
# Script de diagnostic en une passe
kubectl get pods,svc,secrets,hpa,pvc -n ecommerce
kubectl top pods -n ecommerce    # nécessite Metrics Server
curl -s http://localhost:30080/health | python -m json.tool
```
