using Pulumi;
using Pulumi.Command.Local;
using Pulumi.Kubernetes.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Apps.V1;
using Pulumi.Kubernetes.Types.Inputs.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Meta.V1;
using Deployment = Pulumi.Kubernetes.Apps.V1.Deployment;

namespace Ecommerce.Infra.Resources;

public class DatabaseResourcesArgs
{
    public Input<string> Namespace { get; set; } = "ecommerce";

    /// <summary>Mot de passe du superuser postgres pour order-db. Doit correspondre à secrets:orderDbPassword.</summary>
    public string OrderDbPassword { get; set; } = "postgres";

    /// <summary>Mot de passe du superuser postgres pour inventory-db. Doit correspondre à secrets:inventoryDbPassword.</summary>
    public string InventoryDbPassword { get; set; } = "postgres";

    /// <summary>Nombre d'instances PostgreSQL pour le cluster order-db (1 = dev, 3 = prod HA). Configurable via cnpg:orderInstances.</summary>
    public int OrderInstances { get; set; } = 1;

    /// <summary>Nombre d'instances PostgreSQL pour le cluster inventory-db. Configurable via cnpg:inventoryInstances.</summary>
    public int InventoryInstances { get; set; } = 1;

    /// <summary>Nombre de pods PgBouncer par Pooler (1 = dev, 2 = prod recommandé). Configurable via cnpg:poolerInstances.</summary>
    public int PoolerInstances { get; set; } = 1;
}

/// <summary>
/// Déploie les clusters PostgreSQL CNPG et les Poolers PgBouncer.
///
/// Architecture :
///       ▼  kubectl apply --server-side
///   order-db (Cluster CNPG)
///       │  CNPG crée automatiquement :
///       ├── order-db-rw:5432       (Service → pod primary)
///       ├── order-db-ro:5432       (Service → pods replicas)
///       ├── order-db-r:5432        (Service → tout pod)
///       ├── order-db-superuser     (Secret : credentials postgres — utilisé par Pooler authQuery)
///       └── order-db-app           (Secret : credentials user 'app' — géré par CNPG, non utilisé ici)
///
///   order-db-pooler (Pooler CNPG / PgBouncer)
///       │  PgBouncer écoute sur :
///       └── order-db-pooler:5432  (Service → pods PgBouncer)
///
/// User PostgreSQL :
///   Le user 'app' (owner de la base) est utilisé pour toutes les connexions applicatives.
///   Son mot de passe est défini au initdb via postInitSQL et corresponde à OrderDbPassword.
///   CNPG gère le superuser 'postgres' de façon autonome (Secret order-db-superuser auto-créé).
///   Le Pooler utilise order-db-superuser pour l'authQuery (SELECT depuis pg_shadow).
///
///   ⚠️ CNPG 1.24.0 bug : quand superuserSecret est fourni, la réconciliation CNPG
///   efface périodiquement le mot de passe postgres (ALTER ROLE avec valeur nulle).
///   Workaround : ne pas fournir superuserSecret — laisser CNPG gérer autonomement.
///
/// Workaround GVK cache Pulumi :
///   Identique à KedaResources : les CRDs CNPG (Cluster, Pooler) installées par le
///   Helm CNPG pendant ce même pulumi up ne sont pas dans le cache GVK du provider.
///   → kubectl apply --server-side (via Pulumi.Command) contourne ce cache.
///   → DependsOn = CnpgResources dans EcommerceStack garantit que l'opérateur est Ready.
///
/// Outputs :
///   OrderDbRwServiceName         = "order-db-rw"        → init containers
///   InventoryDbRwServiceName     = "inventory-db-rw"    → init containers
///   OrderDbPoolerServiceName     = "order-db-pooler"    → connection strings app
///   InventoryDbPoolerServiceName = "inventory-db-pooler" → connection strings app
/// </summary>
public class DatabaseResources : ComponentResource
{
    /// <summary>Service primary order-db créé automatiquement par CNPG. Utilisé par les init containers.</summary>
    public Output<string> OrderDbRwServiceName { get; }

    /// <summary>Service primary inventory-db créé automatiquement par CNPG. Utilisé par les init containers.</summary>
    public Output<string> InventoryDbRwServiceName { get; }

    /// <summary>Service PgBouncer order-db. Utilisé dans ConnectionStrings__OrderDb.</summary>
    public Output<string> OrderDbPoolerServiceName { get; }

    /// <summary>Service PgBouncer inventory-db. Utilisé dans ConnectionStrings__InventoryDb.</summary>
    public Output<string> InventoryDbPoolerServiceName { get; }

    public DatabaseResources(string name, DatabaseResourcesArgs args, ComponentResourceOptions? opts = null)
        : base("ecommerce:infra:DatabaseResources", name, opts)
    {
        var resourceOpts = new CustomResourceOptions { Parent = this };

        // ── Order DB ──────────────────────────────────────────────────────────
        // Pas de CreateBootstrapSecret : CNPG gère le superuser postgres de façon autonome
        // (order-db-superuser créé automatiquement). Le user 'app' reçoit son mot de passe
        // via postInitSQL lors de l'initdb et est utilisé pour toutes les connexions applicatives.
        CreateCnpgClusterAndPooler(
            clusterName: "order-db",
            dbName:      "order_db",
            appPassword: args.OrderDbPassword,
            instances:   args.OrderInstances,
            poolerInstances: args.PoolerInstances,
            args, resourceOpts);
        // Service dédié aux métriques CNPG built-in (port 9187 sur les pods).
        // Le service -rw créé par CNPG n'expose que le port 5432.
        CreateCnpgMetricsService("order-db", args, resourceOpts);
        CreatePostgresExporter(
            "order",
            host:       "order-db-rw.ecommerce.svc.cluster.local",
            dbName:     "order_db",
            secretName: SecretsResources.OrderDbSecretName,
            args, resourceOpts);

        // ── Inventory DB ──────────────────────────────────────────────────────
        CreateCnpgClusterAndPooler(
            clusterName: "inventory-db",
            dbName:      "inventory_db",
            appPassword: args.InventoryDbPassword,
            instances:   args.InventoryInstances,
            poolerInstances: args.PoolerInstances,
            args, resourceOpts);
        CreateCnpgMetricsService("inventory-db", args, resourceOpts);
        CreatePostgresExporter(
            "inventory",
            host:       "inventory-db-rw.ecommerce.svc.cluster.local",
            dbName:     "inventory_db",
            secretName: SecretsResources.InventoryDbSecretName,
            args, resourceOpts);

        // Services créés automatiquement par l'opérateur CNPG (pas gérés par Pulumi).
        // Ces constantes sont exposées comme outputs pour que EcommerceStack puisse
        // les passer aux init containers (rw) et aux connection strings (pooler).
        OrderDbRwServiceName       = Output.Create("order-db-rw");
        InventoryDbRwServiceName   = Output.Create("inventory-db-rw");
        OrderDbPoolerServiceName   = Output.Create("order-db-pooler");
        InventoryDbPoolerServiceName = Output.Create("inventory-db-pooler");

        RegisterOutputs(new Dictionary<string, object?>
        {
            ["orderDbRwServiceName"]         = OrderDbRwServiceName,
            ["inventoryDbRwServiceName"]     = InventoryDbRwServiceName,
            ["orderDbPoolerServiceName"]     = OrderDbPoolerServiceName,
            ["inventoryDbPoolerServiceName"] = InventoryDbPoolerServiceName
        });
    }

    /// <summary>
    /// Crée un Service ClusterIP qui sélectionne les pods CNPG d'un cluster par le label
    /// <c>cnpg.io/cluster: {clusterName}</c> et expose le port 9187 (métriques built-in CNPG).
    ///
    /// Pourquoi un Service dédié ?
    ///   CNPG crée automatiquement {cluster}-rw / -ro / -r, mais ces Services n'exposent
    ///   que le port 5432 (PostgreSQL). Les métriques CNPG (cnpg_pg_stat_database_*,
    ///   cnpg_backends_*, cnpg_pg_replication_*, ...) sont exposées directement par le
    ///   process postgres sur le port 9187 des pods. Ce Service permet à Prometheus de les
    ///   atteindre via une cible statique sans Kubernetes SD.
    ///
    /// Dev (1 instance) : le Service pointe vers l'unique pod primary.
    /// Prod (N instances) : le Service load-balance entre tous les pods du cluster.
    ///   Pour prod, préférer Kubernetes SD (role=pod) pour obtenir les métriques de
    ///   chaque instance séparément.
    /// </summary>
    private static void CreateCnpgMetricsService(
        string clusterName,
        DatabaseResourcesArgs args,
        CustomResourceOptions opts)
    {
        _ = new Service($"{clusterName}-metrics-svc", new ServiceArgs
        {
            Metadata = new ObjectMetaArgs
            {
                Namespace = args.Namespace,
                Name      = $"{clusterName}-metrics"
            },
            Spec = new ServiceSpecArgs
            {
                // Sélectionne tous les pods CNPG du cluster (primary + replicas).
                // Le label cnpg.io/cluster est positionné par l'opérateur CNPG sur chaque pod.
                Selector = new InputMap<string> { ["cnpg.io/cluster"] = clusterName },
                Ports    = new ServicePortArgs { Name = "metrics", Port = 9187, TargetPort = 9187 }
            }
        }, opts);
    }

    /// <summary>
    /// Applique via kubectl (Pulumi.Command) le manifeste CNPG multi-document :
    ///   1. Cluster CNPG — remplace le StatefulSet PostgreSQL
    ///   2. Pooler CNPG  — PgBouncer devant le cluster (session mode)
    ///
    /// Pourquoi kubectl apply et non le provider Pulumi.Kubernetes ?
    ///   Le provider met en cache les GVK au démarrage du programme. Les CRDs CNPG
    ///   (postgresql.cnpg.io/v1) étant installées par le Helm chart pendant ce même
    ///   pulumi up, elles ne sont pas dans le cache → erreur "failed to determine if
    ///   GVK is namespaced". kubectl interroge directement l'API server et connaît
    ///   toutes les CRDs, y compris celles installées dans le même run.
    ///
    /// --server-side : évite les conflits de field manager — idempotent sur les re-runs.
    ///
    /// Gestion des mots de passe :
    ///   superuserSecret n'est PAS fourni → CNPG crée {cluster}-superuser automatiquement
    ///   avec un mot de passe aléatoire qu'il maintient lui-même. Le Pooler référence ce
    ///   secret auto-créé pour l'authQuery (accès pg_shadow superuser).
    ///
    ///   Le user 'app' (owner de la base) reçoit appPassword via postInitSQL lors de l'initdb.
    ///   C'est ce user que les applications utilisent pour se connecter (via SecretsResources).
    ///
    ///   ⚠️ Sur un cluster déjà initialisé, postInitSQL ne tourne PAS à nouveau.
    ///   Si le mot de passe du user 'app' doit être synchronisé manuellement :
    ///     kubectl exec -n ecommerce {cluster}-1 -- psql -U postgres -d {db} \
    ///       -c "ALTER USER app PASSWORD '<password>'"
    /// </summary>
    private static void CreateCnpgClusterAndPooler(
        string clusterName,
        string dbName,
        string appPassword,
        int instances,
        int poolerInstances,
        DatabaseResourcesArgs args,
        CustomResourceOptions opts)
    {
        var poolerName = $"{clusterName}-pooler";
        // CNPG crée {cluster}-superuser automatiquement (pas de superuserSecret).
        // Ce secret a les droits superuser pour exécuter l'authQuery dans pg_shadow.
        var poolerAuthSecret = $"{clusterName}-superuser";

        var yaml = args.Namespace.Apply(ns => $@"apiVersion: postgresql.cnpg.io/v1
kind: Cluster
metadata:
  name: {clusterName}
  namespace: {ns}
spec:
  instances: {instances}
  imageName: ghcr.io/cloudnative-pg/postgresql:16.6-bookworm
  storage:
    size: 1Gi
    storageClass: standard
  postgresql:
    parameters:
      max_connections: ""200""
  bootstrap:
    initdb:
      database: {dbName}
      owner: app
      postInitSQL:
        - ""ALTER USER app PASSWORD '{appPassword}'""
---
apiVersion: postgresql.cnpg.io/v1
kind: Pooler
metadata:
  name: {poolerName}
  namespace: {ns}
spec:
  cluster:
    name: {clusterName}
  instances: {poolerInstances}
  type: rw
  pgbouncer:
    poolMode: session
    parameters:
      max_client_conn: ""1000""
      default_pool_size: ""200""
    authQuery: ""SELECT usename, passwd FROM pg_shadow WHERE usename=$1""
    authQuerySecret:
      name: {poolerAuthSecret}");

        // Create = Update = server-side apply (idempotent).
        // Delete = delete le Cluster et le Pooler (CNPG supprime aussi les PVCs).
        _ = new Command($"{clusterName}-cluster-apply", new CommandArgs
        {
            Create = "kubectl apply --server-side -f -",
            Update = "kubectl apply --server-side -f -",
            Delete = "kubectl delete --ignore-not-found -f -",
            Stdin  = yaml
        }, new CustomResourceOptions
        {
            Parent = opts.Parent
        });
    }

    /// <summary>
    /// Déploie un postgres_exporter dédié à une base de données.
    ///
    /// Connexion directe via le service -rw (primary) pour éviter les interférences
    /// avec le pool PgBouncer. Le Prometheus scrape les métriques pg_stat_activity,
    /// pg_stat_database_*, pg_database_size_bytes sur le port 9187.
    ///
    /// DATA_SOURCE_URI : utilise le FQDN complet (namespace inclus) pour être
    /// accessible depuis le namespace ecommerce sans ambiguïté DNS.
    /// </summary>
    private static void CreatePostgresExporter(
        string dbAlias,
        string host,
        string dbName,
        string secretName,
        DatabaseResourcesArgs args,
        CustomResourceOptions opts)
    {
        const string image = "prometheuscommunity/postgres-exporter:v0.16.0";
        var appLabel = $"postgres-exporter-{dbAlias}";

        _ = new Deployment($"{appLabel}-deploy", new DeploymentArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = appLabel },
            Spec = new DeploymentSpecArgs
            {
                Replicas = 1,
                Selector = new LabelSelectorArgs
                {
                    MatchLabels = new InputMap<string> { ["app"] = appLabel }
                },
                Template = new PodTemplateSpecArgs
                {
                    Metadata = new ObjectMetaArgs
                    {
                        Labels = new InputMap<string> { ["app"] = appLabel }
                    },
                    Spec = new PodSpecArgs
                    {
                        Containers = new ContainerArgs
                        {
                            Name            = "postgres-exporter",
                            Image           = image,
                            ImagePullPolicy = "IfNotPresent",
                            Ports           = new ContainerPortArgs { Name = "metrics", ContainerPortValue = 9187 },
                            Env = new List<EnvVarArgs>
                            {
                                new()
                                {
                                    // Connexion directe via -rw (primary) — pas via le Pooler PgBouncer.
                                    // Évite les interférences avec le pool de connexions applicatives.
                                    Name  = "DATA_SOURCE_URI",
                                    Value = $"{host}:5432/{dbName}?sslmode=disable"
                                },
                                new()
                                {
                                    Name = "DATA_SOURCE_USER",
                                    ValueFrom = new EnvVarSourceArgs
                                    {
                                        SecretKeyRef = new SecretKeySelectorArgs
                                        {
                                            Name = secretName,
                                            Key  = "POSTGRES_USER"
                                        }
                                    }
                                },
                                new()
                                {
                                    Name = "DATA_SOURCE_PASS",
                                    ValueFrom = new EnvVarSourceArgs
                                    {
                                        SecretKeyRef = new SecretKeySelectorArgs
                                        {
                                            Name = secretName,
                                            Key  = "POSTGRES_PASSWORD"
                                        }
                                    }
                                }
                            },
                            Resources = new ResourceRequirementsArgs
                            {
                                Requests = new InputMap<string> { ["cpu"] = "10m", ["memory"] = "32Mi" },
                                Limits   = new InputMap<string> { ["cpu"] = "50m", ["memory"] = "64Mi"  }
                            },
                            ReadinessProbe = new ProbeArgs
                            {
                                HttpGet             = new HTTPGetActionArgs { Path = "/", Port = 9187 },
                                InitialDelaySeconds = 5,
                                PeriodSeconds       = 5
                            }
                        }
                    }
                }
            }
        }, opts);

        _ = new Service($"{appLabel}-svc", new ServiceArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = appLabel },
            Spec = new ServiceSpecArgs
            {
                Selector = new InputMap<string> { ["app"] = appLabel },
                Ports    = new ServicePortArgs { Name = "metrics", Port = 9187, TargetPort = 9187 }
            }
        }, opts);
    }
}
