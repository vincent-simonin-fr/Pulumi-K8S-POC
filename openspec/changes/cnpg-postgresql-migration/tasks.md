## 1. Infrastructure Pulumi — Opérateur CNPG

- [x] 1.1 Créer `infra/Ecommerce.Infra/Resources/CnpgResources.cs` — Helm release `cloudnative-pg` (namespace `cnpg-system`, `WaitForJobs=true`, `Timeout=300`, version configurable)
- [x] 1.2 Ajouter `cnpg:version: "1.24.0"` et autres clés config dans `Pulumi.dev.yaml`
- [x] 1.3 Ajouter `CnpgResources` dans `EcommerceStack.cs` avant `DatabaseResources` (avec `DependsOn`)

## 2. Infrastructure Pulumi — Clusters CNPG + Poolers

- [x] 2.1 Réécrire `DatabaseResources.cs` : supprimer les StatefulSets, Services headless/ClusterIP, PVC templates
- [x] 2.2 Ajouter dans `DatabaseResources.cs` : secrets bootstrap `{cluster}-pg-password` (clés `username` + `password`) pour order-db et inventory-db
- [x] 2.3 Ajouter dans `DatabaseResources.cs` : `Pulumi.Command.Local.Command` `order-db-cluster-apply` — YAML multi-document `Cluster` + `Pooler` order-db via `kubectl apply --server-side -f -`
- [x] 2.4 Ajouter dans `DatabaseResources.cs` : `Pulumi.Command.Local.Command` `inventory-db-cluster-apply` — YAML multi-document `Cluster` + `Pooler` inventory-db
- [x] 2.5 Exposer `OrderDbRwServiceName = "order-db-rw"` et `InventoryDbRwServiceName = "inventory-db-rw"` comme outputs de `DatabaseResources` (pour init containers et exporter)
- [x] 2.6 Exposer `OrderDbPoolerServiceName = "order-db-pooler"` et `InventoryDbPoolerServiceName = "inventory-db-pooler"` (pour connection strings)

## 3. Infrastructure Pulumi — Secrets et Connection Strings

- [x] 3.1 Mettre à jour `SecretsResources.cs` : `ConnectionStrings__OrderDb` → `Host=order-db-pooler;Port=5432;Database=order_db;Username=postgres;Password=<pwd>;Maximum Pool Size=20;Minimum Pool Size=0`
- [x] 3.2 Mettre à jour `SecretsResources.cs` : `ConnectionStrings__InventoryDb` → `Host=inventory-db-pooler;Port=5432;...`
- [x] 3.3 Conserver `POSTGRES_USER`, `POSTGRES_PASSWORD`, `POSTGRES_DB` dans les secrets existants (utilisés par les init containers)

## 4. Infrastructure Pulumi — Services applicatifs

- [x] 4.1 Mettre à jour `OrderServiceResources.cs` : init container `psql -h order-db` → `psql -h order-db-rw` (utiliser `args.OrderDbHost` passé depuis EcommerceStack)
- [x] 4.2 Mettre à jour `InventoryServiceResources.cs` : init container `psql -h inventory-db` → `psql -h inventory-db-rw`
- [x] 4.3 Mettre à jour `EcommerceStack.cs` : passer `OrderDbRwServiceName` / `InventoryDbRwServiceName` aux ServiceResources (init containers) et `PoolerServiceName` aux SecretsResources

## 5. Infrastructure Pulumi — postgres_exporter

- [x] 5.1 Mettre à jour `DatabaseResources.cs` : `DATA_SOURCE_URI` des postgres_exporters → `order-db-rw.ecommerce.svc.cluster.local:5432/order_db` et `inventory-db-rw.ecommerce.svc.cluster.local:5432/inventory_db`

## 6. Scripts et images

- [x] 6.1 Ajouter dans `scripts/k8s_complete_launch.cmd` : `podman pull ghcr.io/cloudnative-pg/cloudnative-pg:1.24.0` + `kind load`
- [x] 6.2 Ajouter dans `scripts/k8s_complete_launch.cmd` : `podman pull ghcr.io/cloudnative-pg/postgresql:16.6-bookworm` + `kind load`
- [x] 6.3 Ajouter dans `docs/kubernetes.md` : images CNPG dans la section "Images publiques"

## 7. Documentation

- [x] 7.1 Mettre à jour `docs/infrastructure.md` : remplacer section "Bases de données — StatefulSet" par section "Bases de données — CNPG Cluster + Pooler" (architecture, services créés, config, vérification)
- [x] 7.2 Ajouter dans `docs/infrastructure.md` : procédure de migration dev (delete StatefulSets, pulumi up)
- [x] 7.3 Mentionner les next steps prod (backup S3, 3 instances, Pooler ×2) dans `docs/infrastructure.md`

## 8. Vérification

- [ ] 8.1 `dotnet build infra/Ecommerce.Infra` — 0 erreurs, 0 warnings ✅ (vérifié)
- [ ] 8.2 Supprimer StatefulSets existants : `kubectl delete statefulset order-db inventory-db -n ecommerce && kubectl delete pvc data-order-db-0 data-inventory-db-0 -n ecommerce`
- [ ] 8.3 `pulumi up --yes` — vérifier que CNPG Helm s'installe, Clusters créés, Poolers créés
- [ ] 8.4 Vérifier `kubectl get cluster -n ecommerce` → `READY=True` pour order-db et inventory-db
- [ ] 8.5 Vérifier `kubectl get pooler -n ecommerce` → pods PgBouncer Running
- [ ] 8.6 Vérifier `kubectl get pods -n ecommerce` → tous les pods applicatifs `1/1 Running` (init containers passent sur `-rw` services)
- [ ] 8.7 Test smoke : `curl http://localhost:30080/inventory` → réponse HTTP 200
- [ ] 8.8 Test connexions PG : `kubectl exec -n ecommerce deploy/order-api -- psql -h order-db-rw -U postgres -d order_db -c "SELECT count(*) FROM pg_stat_activity"` — connexions < 50
