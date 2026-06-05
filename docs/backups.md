# Sauvegardes CNPG & MinIO

Les bases PostgreSQL (CNPG) sont sauvegardées en continu via **Barman** vers un
**object storage S3-compatible**. En dev, cet object storage est **MinIO** déployé
localement (pas besoin de cloud) ; en prod, on pointe un bucket cloud (S3/GCS/Azure) —
même API, seuls l'endpoint et les credentials changent.

> Rappel : **HA ≠ DR**. Les 3 instances CNPG protègent d'une panne de nœud, pas d'un
> `DROP TABLE` ni d'une corruption. Les backups + le PITR couvrent ce risque.

## MinIO — object storage S3-compatible

Déployé par `MinioResources.cs` (chart officiel `minio/minio`, mode standalone), **gardé
par `cnpg:backupEnabled`** (et `minio:enabled` qui le suit par défaut).

| Aspect | Valeur |
|---|---|
| Namespace | `minio` |
| API S3 (CNPG/Barman) | `http://minio.minio.svc.cluster.local:9000` |
| Console web | port `9001` |
| Bucket | `cnpg-backups` (créé au déploiement) |
| Login | `minio:rootUser` / `minio:rootPassword` (défaut dev : `minio` / `minio-dev-password`) |

> ⚠️ Le chart MinIO demande **16 Gi de RAM par défaut** → overridé à 256 Mi pour Kind
> (`MinioResources.cs`). MinIO ajoute ~256–512 Mi : désactivable via `cnpg:backupEnabled=false`.

### Accéder à la console MinIO

```bash
kubectl port-forward -n minio svc/minio-console 9001:9001
# → http://localhost:9001  (login : minio / minio-dev-password)
```
On y voit le bucket `cnpg-backups` → `order-db/` et `inventory-db/` (CNPG sépare par
`serverName`), avec `base/` (base backups) et `wals/` (WAL archivés).

> Mot de passe MinIO : `pulumi config set --secret minio:rootPassword <pw>` (≥ 8 caractères).

## Sauvegardes CNPG (Barman)

Configurées sur les deux Clusters (`DatabaseResources.cs`) quand `cnpg:backupEnabled=true` :

- **WAL archiving** continu (`spec.backup.barmanObjectStore`) → base du PITR.
- **`ScheduledBackup`** par cluster : base backup quotidien (02:00, cron 6 champs CNPG).
- **Rétention** : `cnpg:backupRetention` (dev `7d`, prod `30d`).
- **Credentials S3** : secret `cnpg-backup-creds` (ACCESS_KEY_ID / ACCESS_SECRET_KEY).

### Vérifier

```bash
kubectl get scheduledbackup -n ecommerce         # order-db-daily / inventory-db-daily
kubectl get backups -n ecommerce                 # historique des backups

# Backup à la demande (sans attendre 02:00) :
kubectl apply -f - <<'EOF'
apiVersion: postgresql.cnpg.io/v1
kind: Backup
metadata: { name: order-db-manual, namespace: ecommerce }
spec: { cluster: { name: order-db } }
EOF
kubectl get backup order-db-manual -n ecommerce -w   # → phase: completed
```
Puis vérifier visuellement dans la console MinIO que les objets arrivent.

### Restauration Point-In-Time (PITR) — runbook testé

La restauration CNPG crée un **nouveau Cluster** en mode `bootstrap.recovery` ciblant un
base backup + un instant (`recoveryTarget.targetTime`), en rejouant les WAL archivés.
On ne restaure **jamais in-place** : on monte un cluster de restauration, on vérifie,
puis on bascule (ou on copie les données).

**Scénario validé** : récupérer une table supprimée par erreur (`DROP TABLE`), en
restaurant à l'instant **juste avant** l'incident.

```bash
# 1. (Pré-requis) un base backup existe + WAL archiving actif
kubectl get backups -n ecommerce                                   # un backup 'completed'
kubectl exec -n ecommerce order-db-1 -- psql -U postgres -tAc \
  "SELECT archived_count, failed_count FROM pg_stat_archiver;"     # archived>0, failed=0

# 2. Donnée témoin + capture de l'instant T (AVANT l'incident)
kubectl exec -n ecommerce order-db-1 -- psql -U postgres -d order_db -tAc \
  "CREATE TABLE pitr_test(id serial, note text); INSERT INTO pitr_test(note) VALUES('avant le DROP');"
kubectl exec -n ecommerce order-db-1 -- psql -U postgres -tAc "SELECT now();"   # → noter T
# Forcer l'archivage du segment WAL contenant T :
kubectl exec -n ecommerce order-db-1 -- psql -U postgres -tAc "SELECT pg_switch_wal();"

# 3. L'incident (APRÈS T)
kubectl exec -n ecommerce order-db-1 -- psql -U postgres -d order_db -c "DROP TABLE pitr_test;"

# 4. Cluster de restauration ciblant T (targetTime = T capturé à l'étape 2)
kubectl apply -f - <<'YAML'
apiVersion: postgresql.cnpg.io/v1
kind: Cluster
metadata: { name: order-db-restore, namespace: ecommerce }
spec:
  instances: 1
  imageName: ghcr.io/cloudnative-pg/postgresql:16.6-bookworm
  storage: { size: 1Gi, storageClass: standard }
  bootstrap:
    recovery:
      source: order-db
      recoveryTarget:
        targetTime: "2026-06-05 08:13:34+00"   # ← T
  externalClusters:
    - name: order-db
      barmanObjectStore:
        destinationPath: s3://cnpg-backups/
        endpointURL: http://minio.minio.svc.cluster.local:9000
        serverName: order-db
        s3Credentials:
          accessKeyId:     { name: cnpg-backup-creds, key: ACCESS_KEY_ID }
          secretAccessKey: { name: cnpg-backup-creds, key: ACCESS_SECRET_KEY }
YAML

# 5. Attendre la fin de la restauration + vérifier
kubectl wait --for=condition=Ready cluster/order-db-restore -n ecommerce --timeout=240s
kubectl exec -n ecommerce order-db-restore-1 -- psql -U postgres -d order_db -tAc \
  "SELECT * FROM pitr_test;"        # → la table est de retour (état à T, avant le DROP)

# 6. Nettoyer (ou, en vrai DR : promouvoir / recopier les données vers le cluster cible)
kubectl delete cluster order-db-restore -n ecommerce
```

> Points clés : `serverName` = nom du cluster d'origine (CNPG range les backups sous
> `s3://cnpg-backups/<serverName>/`) ; `targetTime` doit être **couvert par les WAL
> archivés** (d'où le `pg_switch_wal` à l'étape 2 pour forcer l'archivage du segment).

## RPO & RTO

Deux métriques qui définissent une stratégie de sauvegarde :

| Métrique | Définition | Ce qui la détermine ici |
|---|---|---|
| **RPO** (Recovery Point Objective) | quantité de données qu'on accepte de **perdre** (fenêtre avant l'incident) | l'**archivage WAL continu** → RPO ≈ **quelques secondes** (au pire, le dernier segment WAL non encore archivé). Le base backup quotidien ne fixe **pas** le RPO : c'est le WAL qui permet de viser n'importe quel instant. |
| **RTO** (Recovery Time Objective) | **temps** nécessaire pour restaurer le service | temps de restauration du base backup (depuis l'object storage) **+** rejeu des WAL jusqu'à `targetTime` **+** bascule applicative. Croît avec la **taille de la base** et le **volume de WAL** à rejouer depuis le dernier base backup. |

**Leviers :**
- **Réduire le RTO** : base backups **plus fréquents** (moins de WAL à rejouer), réseau
  object storage rapide, base backup via **volume snapshots** (CNPG les supporte) pour
  les grosses bases.
- **Améliorer le RPO** : il est déjà quasi-temps-réel grâce au WAL ; en prod, surveiller
  l'échec d'archivage (alerte `pg_stat_archiver.failed_count` — cf. `observability-alerting`).

> ⚠️ Un backup **non testé** = fausse sécurité. Ce runbook PITR doit être **rejoué
> périodiquement** (et le RTO mesuré) — pas seulement supposé fonctionner.

## Production

| | Dev (Kind) | Prod |
|---|---|---|
| Object storage | **MinIO local** (`minio:enabled=true`) | **bucket cloud** (`minio:enabled=false`) |
| Endpoint | `http://minio.minio…:9000` | `cnpg:backupEndpoint` = endpoint S3/GCS/Azure |
| Credentials | rootUser/rootPassword | clés cloud (`--secret`) ou **identité de charge** (IRSA/WI) |
| Rétention | 7d | 30d (`cnpg:backupRetention`) |

En prod : ne **pas** déployer MinIO, pointer un bucket managé + accès par identité de
charge (pas de clé statique). Cf. `docs/production.md` et la proposition OpenSpec.

## Clés de configuration

| Clé | Défaut dev | Rôle |
|---|---|---|
| `cnpg:backupEnabled` | `true` | active backups + (par défaut) MinIO |
| `minio:enabled` | = backupEnabled | déployer MinIO (false en prod → bucket cloud) |
| `cnpg:backupBucket` | `cnpg-backups` | bucket cible |
| `cnpg:backupEndpoint` | endpoint MinIO interne | endpoint S3 (prod : cloud) |
| `cnpg:backupRetention` | `7d` | rétention Barman |
| `minio:rootUser` / `minio:rootPassword` | `minio` / `minio-dev-password` | clés S3 (= access/secret key) |
| `minio:storageSize` | `5Gi` | volume MinIO |
