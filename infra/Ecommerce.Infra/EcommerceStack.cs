using Pulumi;
using Pulumi.Kubernetes.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Meta.V1;

namespace Ecommerce.Infra.Resources;

public class EcommerceStack : Stack
{
    public EcommerceStack()
    {
        // ── Config par namespace (format YAML : "namespace:key") ─────────────
        // new Config("orderApi").Get("image")  lit  "orderApi:image"  dans Pulumi.dev.yaml
        var orderApiCfg     = new Config("orderApi");
        var inventoryApiCfg = new Config("inventoryApi");
        var gatewayCfg      = new Config("gateway");
        var reservationCfg  = new Config("reservation");
        var replicasCfg     = new Config("replicas");
        var hpaCfg          = new Config("hpa");
        var resourcesCfg    = new Config("resources");
        var secretsCfg      = new Config("secrets");  // valeurs gérées par ESO

        var nodePort = gatewayCfg.GetInt32("nodePort") ?? 30080;

        // ── Namespace ─────────────────────────────────────────────────────────
        var ns = new Namespace("ecommerce-ns", new NamespaceArgs
        {
            Metadata = new ObjectMetaArgs { Name = "ecommerce" }
        });

        var namespaceName = ns.Metadata.Apply(m => m.Name);

        // ── Secrets (ESO + ClusterSecretStore + ExternalSecrets) ─────────────
        //  Doit être créé AVANT les pods qui consomment les secrets.
        //  Les valeurs sont lues depuis `pulumi config set --secret secrets:xxx`
        //  (voir commentaires dans Pulumi.dev.yaml).
        var secretsResources = new SecretsResources("secrets", new SecretsResourcesArgs
        {
            Namespace           = namespaceName,
            OrderDbUser         = secretsCfg.Get("orderDbUser")         ?? "postgres",
            OrderDbPassword     = secretsCfg.Get("orderDbPassword")     ?? "postgres",
            OrderDbName         = secretsCfg.Get("orderDbName")         ?? "order_db",
            InventoryDbUser     = secretsCfg.Get("inventoryDbUser")     ?? "postgres",
            InventoryDbPassword = secretsCfg.Get("inventoryDbPassword") ?? "postgres",
            InventoryDbName     = secretsCfg.Get("inventoryDbName")     ?? "inventory_db",
            RabbitMqUser        = secretsCfg.Get("rabbitmqUser")        ?? "guest",
            RabbitMqPassword    = secretsCfg.Get("rabbitmqPassword")    ?? "guest"
        });

        var secretsDep = new ComponentResourceOptions { DependsOn = { secretsResources } };

        // ── Infrastructure (PostgreSQL + RabbitMQ) ────────────────────────────
        var dbResources = new DatabaseResources("databases", new DatabaseResourcesArgs
        {
            Namespace = namespaceName,
            Replicas  = replicasCfg.GetInt32("db") ?? 1
        }, secretsDep);

        var mqResources = new MessagingResources("messaging", new MessagingResourcesArgs
        {
            Namespace = namespaceName,
            Replicas  = replicasCfg.GetInt32("rabbitmq") ?? 1
        }, secretsDep);

        // ── Services applicatifs ──────────────────────────────────────────────
        var orderApi = new OrderServiceResources("order-service", new ServiceResourcesArgs
        {
            Namespace      = namespaceName,
            Image          = orderApiCfg.Get("image") ?? "localhost/ecommerce/order-api:dev",
            OrderDbHost    = dbResources.OrderDbServiceName,
            RabbitMqHost   = mqResources.RabbitMqServiceName,
            Replicas       = replicasCfg.GetInt32("orderApi") ?? 1,
            CpuRequest     = resourcesCfg.Get("orderApiCpuRequest")    ?? "100m",
            CpuLimit       = resourcesCfg.Get("orderApiCpuLimit")      ?? "500m",
            MemoryRequest  = resourcesCfg.Get("orderApiMemoryRequest") ?? "128Mi",
            MemoryLimit    = resourcesCfg.Get("orderApiMemoryLimit")   ?? "256Mi",
            Hpa = new HpaArgs
            {
                Enabled       = hpaCfg.GetBoolean("orderApiEnabled") ?? false,
                MinReplicas   = hpaCfg.GetInt32("orderApiMin") ?? 1,
                MaxReplicas   = hpaCfg.GetInt32("orderApiMax") ?? 4,
                CpuPercent    = hpaCfg.GetInt32("orderApiCpu") ?? 70,
                MemoryPercent = hpaCfg.GetInt32("orderApiMemory")
            }
        });

        var inventoryApi = new InventoryServiceResources("inventory-service", new InventoryServiceResourcesArgs
        {
            Namespace             = namespaceName,
            Image                 = inventoryApiCfg.Get("image") ?? "localhost/ecommerce/inventory-api:dev",
            InventoryDbHost       = dbResources.InventoryDbServiceName,
            RabbitMqHost          = mqResources.RabbitMqServiceName,
            ReservationTtlMinutes = reservationCfg.GetInt32("ttlMinutes") ?? 10,
            CheckIntervalSeconds  = reservationCfg.GetInt32("checkIntervalSeconds") ?? 30,
            Replicas              = replicasCfg.GetInt32("inventoryApi") ?? 1,
            CpuRequest            = resourcesCfg.Get("inventoryApiCpuRequest")    ?? "100m",
            CpuLimit              = resourcesCfg.Get("inventoryApiCpuLimit")      ?? "500m",
            MemoryRequest         = resourcesCfg.Get("inventoryApiMemoryRequest") ?? "128Mi",
            MemoryLimit           = resourcesCfg.Get("inventoryApiMemoryLimit")   ?? "256Mi",
            Hpa = new HpaArgs
            {
                Enabled       = hpaCfg.GetBoolean("inventoryApiEnabled") ?? false,
                MinReplicas   = hpaCfg.GetInt32("inventoryApiMin") ?? 1,
                MaxReplicas   = hpaCfg.GetInt32("inventoryApiMax") ?? 4,
                CpuPercent    = hpaCfg.GetInt32("inventoryApiCpu") ?? 70,
                MemoryPercent = hpaCfg.GetInt32("inventoryApiMemory")
            }
        });

        var gateway = new GatewayResources("gateway", new GatewayResourcesArgs
        {
            Namespace        = namespaceName,
            Image            = gatewayCfg.Get("image") ?? "localhost/ecommerce/gateway:dev",
            NodePort         = nodePort,
            OrderApiHost     = orderApi.ServiceName,
            InventoryApiHost = inventoryApi.ServiceName,
            Replicas         = replicasCfg.GetInt32("gateway") ?? 1,
            CpuRequest       = resourcesCfg.Get("gatewayCpuRequest")    ?? "50m",
            CpuLimit         = resourcesCfg.Get("gatewayCpuLimit")      ?? "250m",
            MemoryRequest    = resourcesCfg.Get("gatewayMemoryRequest") ?? "64Mi",
            MemoryLimit      = resourcesCfg.Get("gatewayMemoryLimit")   ?? "128Mi",
            Hpa = new HpaArgs
            {
                Enabled       = hpaCfg.GetBoolean("gatewayEnabled") ?? false,
                MinReplicas   = hpaCfg.GetInt32("gatewayMin") ?? 1,
                MaxReplicas   = hpaCfg.GetInt32("gatewayMax") ?? 3,
                CpuPercent    = hpaCfg.GetInt32("gatewayCpu") ?? 70,
                MemoryPercent = hpaCfg.GetInt32("gatewayMemory")
            }
        });

        // ── Outputs ───────────────────────────────────────────────────────────
        GatewayUrl            = Output.Create($"http://localhost:{nodePort}");
        OrderApiHealthUrl     = Output.Create($"http://localhost:{nodePort}/health/orders");
        InventoryApiHealthUrl = Output.Create($"http://localhost:{nodePort}/health/inventory");
    }

    [Output] public Output<string> GatewayUrl { get; set; }
    [Output] public Output<string> OrderApiHealthUrl { get; set; }
    [Output] public Output<string> InventoryApiHealthUrl { get; set; }
}
