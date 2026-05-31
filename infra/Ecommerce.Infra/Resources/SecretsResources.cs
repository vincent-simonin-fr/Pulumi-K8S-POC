using Pulumi;
using Pulumi.Kubernetes.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Meta.V1;

namespace Ecommerce.Infra.Resources;

public class SecretsResourcesArgs
{
    public Input<string> Namespace { get; set; } = "ecommerce";

    // Valeurs lues depuis `pulumi config set --secret`
    public string OrderDbUser     { get; set; } = "postgres";
    public string OrderDbPassword { get; set; } = "postgres";
    public string OrderDbName     { get; set; } = "order_db";

    public string InventoryDbUser     { get; set; } = "postgres";
    public string InventoryDbPassword { get; set; } = "postgres";
    public string InventoryDbName     { get; set; } = "inventory_db";

    public string RabbitMqUser     { get; set; } = "guest";
    public string RabbitMqPassword { get; set; } = "guest";

    // DNS interne K8s — pointent vers les Poolers PgBouncer (et non directement vers CNPG -rw).
    // Les init containers utilisent {cluster}-rw pour leur health check (passé séparément
    // via ServiceResourcesArgs.OrderDbHost / InventoryDbHost dans EcommerceStack).
    public string OrderDbHost     { get; set; } = "order-db-pooler";
    public string InventoryDbHost { get; set; } = "inventory-db-pooler";
}

/// <summary>
/// Crée trois K8s Secrets natifs consommés par les pods.
///
/// Architecture dev local :
///   Les secrets sont créés directement (sans ESO) pour simplifier Kind/Podman.
///
/// Migration vers ESO en production :
///   1. Installer ESO via Helm  (chart : external-secrets)
///   2. Créer un ClusterSecretStore pointant vers AWS/Azure/GCP
///   3. Remplacer chaque Secret ci-dessous par un ExternalSecret
///      → Les noms (OrderDbSecretName, etc.) restent identiques,
///        les pods ne changent donc pas.
/// </summary>
public class SecretsResources : ComponentResource
{
    /// <summary>Nom du K8s Secret injecté dans les pods order-db / order-api.</summary>
    public const string OrderDbSecretName     = "order-db-credentials";

    /// <summary>Nom du K8s Secret injecté dans les pods inventory-db / inventory-api.</summary>
    public const string InventoryDbSecretName = "inventory-db-credentials";

    /// <summary>Nom du K8s Secret injecté dans les pods rabbitmq / order-api / inventory-api.</summary>
    public const string RabbitMqSecretName    = "rabbitmq-credentials";

    public SecretsResources(string name, SecretsResourcesArgs args, ComponentResourceOptions? opts = null)
        : base("ecommerce:infra:SecretsResources", name, opts)
    {
        var resourceOpts = new CustomResourceOptions { Parent = this };

        // ── Order DB ──────────────────────────────────────────────────────────
        _ = new Secret("order-db-secret", new SecretArgs
        {
            Metadata   = new ObjectMetaArgs { Namespace = args.Namespace, Name = OrderDbSecretName },
            StringData = new InputMap<string>
            {
                // User 'app' = owner de la base, créé par CNPG lors de l'initdb.
                // Son mot de passe est défini via postInitSQL : "ALTER USER app PASSWORD '<pwd>'".
                // ⚠️  Sur un cluster existant, synchroniser manuellement si le mot de passe change :
                //      kubectl exec -n ecommerce order-db-1 -- psql -U postgres -d order_db \
                //        -c "ALTER USER app PASSWORD '<nouveau-mot-de-passe>'"
                ["POSTGRES_USER"]     = args.OrderDbUser,     // "app" (CNPG owner)
                ["POSTGRES_PASSWORD"] = args.OrderDbPassword,
                ["POSTGRES_DB"]       = args.OrderDbName,
                // order-api — ASP.NET Core ConnectionStrings__OrderDb
                // Host = order-db-pooler : passe par PgBouncer (session mode).
                // Maximum Pool Size=15 (Npgsql, par pod) : en session mode chaque connexion
                // Npgsql = 1 connexion PG. Pic mesuré sous stress (8 pods KEDA) ≈ 8×15 = 120
                // connexions, bien sous max_connections=400 (marge confortable + réservé
                // réplication/superuser). Minimum Pool Size=0 : pas de connexions en veille.
                ["ConnectionStrings__OrderDb"] =
                    $"Host={args.OrderDbHost};Port=5432;Database={args.OrderDbName};" +
                    $"Username={args.OrderDbUser};Password={args.OrderDbPassword};" +
                    $"Maximum Pool Size=15;Minimum Pool Size=0"
            }
        }, resourceOpts);

        // ── Inventory DB ──────────────────────────────────────────────────────
        _ = new Secret("inventory-db-secret", new SecretArgs
        {
            Metadata   = new ObjectMetaArgs { Namespace = args.Namespace, Name = InventoryDbSecretName },
            StringData = new InputMap<string>
            {
                // User 'app' = owner de la base inventory-db (identique à order-db).
                // Voir commentaire order-db ci-dessus pour la procédure de synchronisation.
                ["POSTGRES_USER"]     = args.InventoryDbUser,  // "app" (CNPG owner)
                ["POSTGRES_PASSWORD"] = args.InventoryDbPassword,
                ["POSTGRES_DB"]       = args.InventoryDbName,
                // inventory-api — ASP.NET Core ConnectionStrings__InventoryDb
                // inventory-api scale jusqu'à keda:inventoryApiMax réplicas (8 en dev) via KEDA.
                // 8×15 = 120 connexions max, sous max_connections=400.
                ["ConnectionStrings__InventoryDb"]  =
                    $"Host={args.InventoryDbHost};Port=5432;Database={args.InventoryDbName};" +
                    $"Username={args.InventoryDbUser};Password={args.InventoryDbPassword};" +
                    $"Maximum Pool Size=15;Minimum Pool Size=0"
            }
        }, resourceOpts);

        // ── RabbitMQ ──────────────────────────────────────────────────────────
        _ = new Secret("rabbitmq-secret", new SecretArgs
        {
            Metadata   = new ObjectMetaArgs { Namespace = args.Namespace, Name = RabbitMqSecretName },
            StringData = new InputMap<string>
            {
                // Pod RabbitMQ (variables d'environnement officielles)
                ["RABBITMQ_DEFAULT_USER"] = args.RabbitMqUser,
                ["RABBITMQ_DEFAULT_PASS"] = args.RabbitMqPassword,
                // order-api / inventory-api (MassTransit / client RabbitMQ)
                ["RabbitMQ__Username"]    = args.RabbitMqUser,
                ["RabbitMQ__Password"]    = args.RabbitMqPassword
            }
        }, resourceOpts);

        RegisterOutputs(new Dictionary<string, object?>
        {
            ["orderDbSecretName"]     = OrderDbSecretName,
            ["inventoryDbSecretName"] = InventoryDbSecretName,
            ["rabbitMqSecretName"]    = RabbitMqSecretName
        });
    }
}
