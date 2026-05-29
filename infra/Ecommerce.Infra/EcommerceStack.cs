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
        var cnpgCfg         = new Config("cnpg");

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

        // Retourne le minReplicas effectif pour order-api et gateway (HPA natif).
        // Quand presale est actif, force la valeur presale pour pré-chauffer les pods.
        int HpaMin(string key, int fallback = 1) =>
            presaleEnabled
                ? (presaleCfg.GetInt32(key) ?? fallback)
                : (hpaCfg.GetInt32(key) ?? 1);

        // Retourne le minReplicaCount effectif pour inventory-api (ScaledObject KEDA).
        // La valeur nominale vient de keda:inventoryApiMin (section KEDA, pas HPA).
        int KedaMin(int fallback = 1) =>
            presaleEnabled
                ? (presaleCfg.GetInt32("inventoryApiMin") ?? fallback)
                : (kedaCfg.GetInt32("inventoryApiMin") ?? 1);

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
            RabbitMqPassword    = secretsCfg.Get("rabbitmqPassword")    ?? "guest",
            // Connection strings pointent vers les Poolers PgBouncer (pas directement vers CNPG -rw).
            // Les init containers utilisent -rw séparément (voir OrderServiceResources + InventoryServiceResources).
            OrderDbHost         = "order-db-pooler",
            InventoryDbHost     = "inventory-db-pooler"
        });

        var secretsDep = new ComponentResourceOptions { DependsOn = { secretsResources } };

        // ── CNPG Operator (avant les bases de données) ────────────────────────
        // Installe cloudnative-pg via Helm (namespace cnpg-system, WaitForJobs=true).
        // DatabaseResources dépend de CnpgResources pour que les CRDs (Cluster, Pooler)
        // soient enregistrées dans l'API K8s avant que kubectl apply ne les utilise.
        // Voir CnpgResources.cs pour le détail du workaround GVK cache.
        var cnpgResources = new CnpgResources("cnpg", new CnpgResourcesArgs
        {
            Version = cnpgCfg.Get("version") ?? "0.22.0"
        });

        // DependsOn combiné : secrets (pour postgres_exporter) + CNPG (pour CRDs).
        var cnpgSecretsDep = new ComponentResourceOptions
        {
            DependsOn = { secretsResources, cnpgResources }
        };

        // ── Infrastructure (PostgreSQL CNPG + RabbitMQ + Redis) ──────────────
        var dbResources = new DatabaseResources("databases", new DatabaseResourcesArgs
        {
            Namespace           = namespaceName,
            OrderDbPassword     = secretsCfg.Get("orderDbPassword")     ?? "postgres",
            InventoryDbPassword = secretsCfg.Get("inventoryDbPassword") ?? "postgres",
            OrderInstances      = cnpgCfg.GetInt32("orderInstances")     ?? 1,
            InventoryInstances  = cnpgCfg.GetInt32("inventoryInstances") ?? 1,
            PoolerInstances     = cnpgCfg.GetInt32("poolerInstances")    ?? 1,
        }, cnpgSecretsDep);

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
            MinReplicas     = KedaMin(fallback: 3),
            MaxReplicas     = kedaCfg.GetInt32("inventoryApiMax") ?? 8,
            PollingInterval = kedaCfg.GetInt32("pollingInterval") ?? 5,
            CooldownPeriod  = kedaCfg.GetInt32("cooldownPeriod")  ?? 60,
            ScaleDownWindow = kedaCfg.GetInt32("scaleDownWindow") ?? 240,
            KedaVersion     = kedaCfg.Get("version")              ?? "2.17.0"
        });

        // ── Services applicatifs ──────────────────────────────────────────────
        var orderApi = new OrderServiceResources("order-service", new ServiceResourcesArgs
        {
            Namespace      = namespaceName,
            Image          = orderApiCfg.Get("image") ?? "localhost/ecommerce/order-api:dev",
            // Init container : attend que le primary CNPG soit Ready (service -rw créé par CNPG).
            // La connection string ASP.NET Core passe par le Pooler (secrets → order-db-pooler).
            OrderDbHost    = dbResources.OrderDbRwServiceName,
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
                MinReplicas   = HpaMin("orderApiMin", fallback: 3),
                MaxReplicas   = hpaCfg.GetInt32("orderApiMax") ?? 4,
                CpuPercent    = hpaCfg.GetInt32("orderApiCpu") ?? 70,
                MemoryPercent = hpaCfg.GetInt32("orderApiMemory")
            }
        });

        var inventoryApi = new InventoryServiceResources("inventory-service", new InventoryServiceResourcesArgs
        {
            Namespace             = namespaceName,
            Image                 = inventoryApiCfg.Get("image") ?? "localhost/ecommerce/inventory-api:dev",
            // Init container : attend que le primary CNPG soit Ready (service -rw créé par CNPG).
            InventoryDbHost       = dbResources.InventoryDbRwServiceName,
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
                MinReplicas   = HpaMin("gatewayMin", fallback: 2),
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
