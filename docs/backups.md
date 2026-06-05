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

### Restauration Point-In-Time (PITR)

La restauration CNPG crée un **nouveau Cluster** en mode `bootstrap.recovery` ciblant un
backup + un instant (`recoveryTarget.targetTime`). Procédure : créer un Cluster
`recovery` pointant l'`externalCluster` (même `barmanObjectStore` + `serverName`), à un
timestamp **avant** l'incident. Voir la proposition `openspec/changes/cnpg-backups/`
(tasks « Restauration ») pour la séquence détaillée + le test sur cluster jetable.

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
