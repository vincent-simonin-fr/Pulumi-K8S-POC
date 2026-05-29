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
        var secretsCfg      = new Config("secrets");
        var obsCfg          = new Config("observability");
        var ingressCfg      = new Config("ingress");
        var presaleCfg      = new Config("presale");
        var kedaCfg         = new Config("keda");

        var nodePort        = gatewayCfg.GetInt32("nodePort")     ?? 30080;
        var grafanaNodePort = obsCfg.GetInt32("grafanaNodePort")  ?? 30030;
        var jaegerNodePort  = obsCfg.GetInt32("jaegerNodePort")   ?? 30686;
        var ingressEnabled  = ingressCfg.GetBoolean("enabled")    ?? false;
        var domain          = ingressCfg.Get("domain")            ?? "wizzz.com";

        // ── Mode presale ──────────────────────────────────────────────────────
        // Quand presale:enabled = true, les minReplicas des HPA/ScaledObjects sont
        // surchargés pour que les pods soient déjà en place avant le pic de trafic.
        // Activation : pulumi config set presale:enabled true && pulumi up --yes
        // Désactivation : pulumi config set presale:enabled false && pulumi up --yes
        var presaleEnabled = presaleCfg.GetBoolean("enabled") ?? false;

        // Retourne le minReplicas effectif : presale si activé, config HPA/KEDA sinon.
        // Pour inventory-api : la valeur est passée au ScaledObject KEDA (minReplicaCount).
        // Pour order-api/gateway : la valeur est passée à l'HPA natif (minReplicas).
        int PresaleMin(string hpaKey, string presaleKey, int fallback = 1) =>
            presaleEnabled
                ? (presaleCfg.GetInt32(presaleKey) ?? fallback)
                : (hpaCfg.GetInt32(hpaKey) ?? 1);

        // ── Observabilité (namespace monitoring — indépendant de ecommerce) ───
        var observability = new ObservabilityResources("observability", new ObservabilityResourcesArgs
        {
            OtelCollectorVersion = obsCfg.Get("otelVersion")               ?? "0.153.0",
            JaegerVersion        = obsCfg.Get("jaegerVersion")              ?? "1.76.0",
            PrometheusVersion    = obsCfg.Get("prometheusVersion")          ?? "v3.11.3",
            GrafanaVersion       = obsCfg.Get("grafanaVersion")             ?? "13.0.1-security-01",
            GrafanaNodePort      = grafanaNodePort,
            JaegerUiNodePort     = jaegerNodePort,
            IngressEnabled       = ingressEnabled,
            GrafanaAdminPassword = obsCfg.Get("grafanaAdminPassword")       ?? ""
        });

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

        // ── Infrastructure (PostgreSQL + RabbitMQ + Redis) ────────────────────
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

        var cacheResources = new CacheResources("cache", new CacheResourcesArgs
        {
            Namespace = namespaceName
        });

        // ── KEDA — Kubernetes Event-Driven Autoscaling (inventory-api) ────────
        // KEDA scale inventory-api en fonction de la profondeur de la queue
        // RabbitMQ (ProductAddedToCartEvent). Réaction ~5 s vs ~75 s pour HPA CPU.
        //
        // Workflow presale :
        //   pulumi config set presale:enabled true && pulumi up --yes
        //   → minReplicaCount du ScaledObject passe à presale:inventoryApiMin (3)
        //   → les pods sont pré-chauffés AVANT le pic, sans cold-start
        //
        // Urgence (sans pulumi up) :
        //   scripts\presale.cmd start / stop
        //   → patch direct du ScaledObject via kubectl
        _ = new KedaResources("keda", new KedaResourcesArgs
        {
            Namespace       = namespaceName,
            RabbitMqUser    = secretsCfg.Get("rabbitmqUser")     ?? "guest",
            RabbitMqPassword= secretsCfg.Get("rabbitmqPassword") ?? "guest",
            QueueName       = kedaCfg.Get("queueName")           ?? "product-added-to-cart",
            QueueLength     = kedaCfg.GetInt32("queueLength")    ?? 5,
            MinReplicas     = PresaleMin("inventoryApiMin", "inventoryApiMin", 3),
            MaxReplicas     = kedaCfg.GetInt32("inventoryApiMax") ?? 8,
            PollingInterval = kedaCfg.GetInt32("pollingInterval") ?? 5,
            CooldownPeriod  = kedaCfg.GetInt32("cooldownPeriod")  ?? 60,
            KedaVersion     = kedaCfg.Get("version")              ?? "2.17.0"
        });

        // ── Services applicatifs ──────────────────────────────────────────────
        var orderApi = new OrderServiceResources("order-service", new ServiceResourcesArgs
        {
            Namespace      = namespaceName,
            Image          = orderApiCfg.Get("image") ?? "localhost/ecommerce/order-api:dev",
            OrderDbHost    = dbResources.OrderDbServiceName,
            RabbitMqHost   = mqResources.RabbitMqServiceName,
            OtelEndpoint   = observability.OtelCollectorEndpoint,
            Replicas       = replicasCfg.GetInt32("orderApi") ?? 1,
            CpuRequest     = resourcesCfg.Get("orderApiCpuRequest")    ?? "100m",
            CpuLimit       = resourcesCfg.Get("orderApiCpuLimit")      ?? "500m",
            MemoryRequest  = resourcesCfg.Get("orderApiMemoryRequest") ?? "128Mi",
            MemoryLimit    = resourcesCfg.Get("orderApiMemoryLimit")   ?? "256Mi",
            Hpa = new HpaArgs
            {
                Enabled       = hpaCfg.GetBoolean("orderApiEnabled") ?? false,
                MinReplicas   = PresaleMin("orderApiMin", "orderApiMin", 3),
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
            OtelEndpoint          = observability.OtelCollectorEndpoint,
            RedisConnectionString = cacheResources.RedisConnectionString,
            ReservationTtlMinutes = reservationCfg.GetInt32("ttlMinutes") ?? 10,
            CheckIntervalSeconds  = reservationCfg.GetInt32("checkIntervalSeconds") ?? 30,
            Replicas              = replicasCfg.GetInt32("inventoryApi") ?? 1,
            CpuRequest            = resourcesCfg.Get("inventoryApiCpuRequest")    ?? "100m",
            CpuLimit              = resourcesCfg.Get("inventoryApiCpuLimit")      ?? "500m",
            MemoryRequest         = resourcesCfg.Get("inventoryApiMemoryRequest") ?? "128Mi",
            MemoryLimit           = resourcesCfg.Get("inventoryApiMemoryLimit")   ?? "256Mi",
            // Hpa : non passé — le scaling est géré par KEDA (ScaledObject ci-dessus).
        });

        var gateway = new GatewayResources("gateway", new GatewayResourcesArgs
        {
            Namespace        = namespaceName,
            Image            = gatewayCfg.Get("image") ?? "localhost/ecommerce/gateway:dev",
            NodePort         = nodePort,
            IngressEnabled   = ingressEnabled,
            OrderApiHost     = orderApi.ServiceName,
            InventoryApiHost = inventoryApi.ServiceName,
            OtelEndpoint     = observability.OtelCollectorEndpoint,
            Replicas         = replicasCfg.GetInt32("gateway") ?? 1,
            CpuRequest       = resourcesCfg.Get("gatewayCpuRequest")    ?? "50m",
            CpuLimit         = resourcesCfg.Get("gatewayCpuLimit")      ?? "250m",
            MemoryRequest    = resourcesCfg.Get("gatewayMemoryRequest") ?? "64Mi",
            MemoryLimit      = resourcesCfg.Get("gatewayMemoryLimit")   ?? "128Mi",
            Hpa = new HpaArgs
            {
                Enabled       = hpaCfg.GetBoolean("gatewayEnabled") ?? false,
                MinReplicas   = PresaleMin("gatewayMin", "gatewayMin", 2),
                MaxReplicas   = hpaCfg.GetInt32("gatewayMax") ?? 3,
                CpuPercent    = hpaCfg.GetInt32("gatewayCpu") ?? 70,
                MemoryPercent = hpaCfg.GetInt32("gatewayMemory")
            }
        });

        // ── Ingress (prod uniquement) ─────────────────────────────────────────
        if (ingressEnabled)
        {
            _ = new IngressResources("ingress", new IngressResourcesArgs
            {
                Domain                       = domain,
                AcmeEmail                    = ingressCfg.Get("acmeEmail")                    ?? "ops@wizzz.com",
                MonitoringBasicAuthHtpasswd  = ingressCfg.Get("monitoringBasicAuthHtpasswd") ?? "",
                CertManagerVersion           = ingressCfg.Get("certManagerVersion")           ?? "v1.16.2",
                NginxVersion                 = ingressCfg.Get("nginxVersion")                 ?? "4.11.3"
            });
        }

        // ── Outputs ───────────────────────────────────────────────────────────
        if (ingressEnabled)
        {
            GatewayUrl            = Output.Create($"https://{domain}");
            OrderApiHealthUrl     = Output.Create($"https://{domain}/health");
            InventoryApiHealthUrl = Output.Create($"https://{domain}/health");
            GrafanaUrl            = Output.Create($"https://grafana.{domain}");
            JaegerUrl             = Output.Create($"https://jaeger.{domain}");
        }
        else
        {
            GatewayUrl            = Output.Create($"http://localhost:{nodePort}");
            OrderApiHealthUrl     = Output.Create($"http://localhost:{nodePort}/health/orders");
            InventoryApiHealthUrl = Output.Create($"http://localhost:{nodePort}/health/inventory");
            GrafanaUrl            = Output.Create($"http://localhost:{grafanaNodePort}");
            JaegerUrl             = Output.Create($"http://localhost:{jaegerNodePort}");
        }
    }

    [Output] public Output<string> GatewayUrl            { get; set; }
    [Output] public Output<string> OrderApiHealthUrl     { get; set; }
    [Output] public Output<string> InventoryApiHealthUrl { get; set; }
    [Output] public Output<string> GrafanaUrl            { get; set; }
    [Output] public Output<string> JaegerUrl             { get; set; }
}
