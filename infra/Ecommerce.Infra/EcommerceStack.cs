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

        var nodePort = gatewayCfg.GetInt32("nodePort") ?? 30080;

        // ── Namespace ─────────────────────────────────────────────────────────
        var ns = new Namespace("ecommerce-ns", new NamespaceArgs
        {
            Metadata = new ObjectMetaArgs { Name = "ecommerce" }
        });

        var namespaceName = ns.Metadata.Apply(m => m.Name);

        // ── Infrastructure (PostgreSQL + RabbitMQ) ────────────────────────────
        var dbResources = new DatabaseResources("databases", new DatabaseResourcesArgs
        {
            Namespace = namespaceName,
            Replicas  = replicasCfg.GetInt32("db") ?? 1
        });

        var mqResources = new MessagingResources("messaging", new MessagingResourcesArgs
        {
            Namespace = namespaceName,
            Replicas  = replicasCfg.GetInt32("rabbitmq") ?? 1
        });

        // ── Services applicatifs ──────────────────────────────────────────────
        var orderApi = new OrderServiceResources("order-service", new ServiceResourcesArgs
        {
            Namespace    = namespaceName,
            Image        = orderApiCfg.Get("image") ?? "localhost/ecommerce/order-api:dev",
            OrderDbHost  = dbResources.OrderDbServiceName,
            RabbitMqHost = mqResources.RabbitMqServiceName,
            Replicas     = replicasCfg.GetInt32("orderApi") ?? 1
        });

        var inventoryApi = new InventoryServiceResources("inventory-service", new InventoryServiceResourcesArgs
        {
            Namespace             = namespaceName,
            Image                 = inventoryApiCfg.Get("image") ?? "localhost/ecommerce/inventory-api:dev",
            InventoryDbHost       = dbResources.InventoryDbServiceName,
            RabbitMqHost          = mqResources.RabbitMqServiceName,
            ReservationTtlMinutes = reservationCfg.GetInt32("ttlMinutes") ?? 10,
            CheckIntervalSeconds  = reservationCfg.GetInt32("checkIntervalSeconds") ?? 30,
            Replicas              = replicasCfg.GetInt32("inventoryApi") ?? 1
        });

        var gateway = new GatewayResources("gateway", new GatewayResourcesArgs
        {
            Namespace        = namespaceName,
            Image            = gatewayCfg.Get("image") ?? "localhost/ecommerce/gateway:dev",
            NodePort         = nodePort,
            OrderApiHost     = orderApi.ServiceName,
            InventoryApiHost = inventoryApi.ServiceName,
            Replicas         = replicasCfg.GetInt32("gateway") ?? 1
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
