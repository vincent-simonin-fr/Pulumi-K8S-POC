using Ecommerce.Infra.Resources;
using Pulumi;
using Pulumi.Kubernetes.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Meta.V1;

namespace Ecommerce.Infra.Resources;

public class EcommerceStack : Stack
{
    public EcommerceStack()
    {
        var config = new Config();

        // ── Namespace ─────────────────────────────────────────────────────────
        var ns = new Namespace("ecommerce-ns", new NamespaceArgs
        {
            Metadata = new ObjectMetaArgs { Name = "ecommerce" }
        });

        var namespaceName = ns.Metadata.Apply(m => m.Name);

        // ── Infrastructure (PostgreSQL + RabbitMQ) ────────────────────────────
        var dbResources = new DatabaseResources("databases", new DatabaseResourcesArgs
        {
            Namespace = namespaceName
        });

        var mqResources = new MessagingResources("messaging", new MessagingResourcesArgs
        {
            Namespace = namespaceName
        });

        // ── Services applicatifs ──────────────────────────────────────────────
        var orderApi = new OrderServiceResources("order-service", new ServiceResourcesArgs
        {
            Namespace     = namespaceName,
            Image         = config.Get("orderApi:image") ?? "ecommerce/order-api:dev",
            OrderDbHost   = dbResources.OrderDbServiceName,
            RabbitMqHost  = mqResources.RabbitMqServiceName
        });

        var inventoryApi = new InventoryServiceResources("inventory-service", new InventoryServiceResourcesArgs
        {
            Namespace            = namespaceName,
            Image                = config.Get("inventoryApi:image") ?? "ecommerce/inventory-api:dev",
            InventoryDbHost      = dbResources.InventoryDbServiceName,
            RabbitMqHost         = mqResources.RabbitMqServiceName,
            ReservationTtlMinutes = config.GetInt32("reservation:ttlMinutes") ?? 10,
            CheckIntervalSeconds  = config.GetInt32("reservation:checkIntervalSeconds") ?? 30
        });

        var gateway = new GatewayResources("gateway", new GatewayResourcesArgs
        {
            Namespace      = namespaceName,
            Image          = config.Get("gateway:image") ?? "ecommerce/gateway:dev",
            NodePort       = config.GetInt32("gateway:nodePort") ?? 30080,
            OrderApiHost   = orderApi.ServiceName,
            InventoryApiHost = inventoryApi.ServiceName
        });

        // ── Outputs ───────────────────────────────────────────────────────────
        GatewayUrl = Output.Create($"http://localhost:{config.GetInt32("gateway:nodePort") ?? 30080}");
        OrderApiHealthUrl    = Output.Format($"http://localhost:{config.GetInt32("gateway:nodePort") ?? 30080}/health/orders");
        InventoryApiHealthUrl = Output.Format($"http://localhost:{config.GetInt32("gateway:nodePort") ?? 30080}/health/inventory");
    }

    [Output] public Output<string> GatewayUrl { get; set; }
    [Output] public Output<string> OrderApiHealthUrl { get; set; }
    [Output] public Output<string> InventoryApiHealthUrl { get; set; }
}
