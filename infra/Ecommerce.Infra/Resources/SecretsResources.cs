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

    // DNS interne K8s — utilisés pour construire les connection strings
    public string OrderDbHost     { get; set; } = "order-db";
    public string InventoryDbHost { get; set; } = "inventory-db";
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
                // Pod PostgreSQL
                ["POSTGRES_USER"]              = args.OrderDbUser,
                ["POSTGRES_PASSWORD"]          = args.OrderDbPassword,
                ["POSTGRES_DB"]                = args.OrderDbName,
                // order-api — ASP.NET Core ConnectionStrings__OrderDb
                ["ConnectionStrings__OrderDb"] =
                    $"Host={args.OrderDbHost};Port=5432;Database={args.OrderDbName};" +
                    $"Username={args.OrderDbUser};Password={args.OrderDbPassword}"
            }
        }, resourceOpts);

        // ── Inventory DB ──────────────────────────────────────────────────────
        _ = new Secret("inventory-db-secret", new SecretArgs
        {
            Metadata   = new ObjectMetaArgs { Namespace = args.Namespace, Name = InventoryDbSecretName },
            StringData = new InputMap<string>
            {
                // Pod PostgreSQL
                ["POSTGRES_USER"]                   = args.InventoryDbUser,
                ["POSTGRES_PASSWORD"]               = args.InventoryDbPassword,
                ["POSTGRES_DB"]                     = args.InventoryDbName,
                // inventory-api — ASP.NET Core ConnectionStrings__InventoryDb
                ["ConnectionStrings__InventoryDb"]  =
                    $"Host={args.InventoryDbHost};Port=5432;Database={args.InventoryDbName};" +
                    $"Username={args.InventoryDbUser};Password={args.InventoryDbPassword}"
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
