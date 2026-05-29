using Pulumi;
using Pulumi.Kubernetes.Autoscaling.V2;
using Pulumi.Kubernetes.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Apps.V1;
using Pulumi.Kubernetes.Types.Inputs.Autoscaling.V2;
using Pulumi.Kubernetes.Types.Inputs.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Meta.V1;
using Deployment = Pulumi.Kubernetes.Apps.V1.Deployment;

namespace Ecommerce.Infra.Resources;

public class InventoryServiceResourcesArgs
{
    public Input<string> Namespace { get; set; } = "ecommerce";
    public Input<string> Image { get; set; } = "localhost/ecommerce/inventory-api:dev";
    public Input<string> InventoryDbHost { get; set; } = "inventory-db";
    public Input<string> RabbitMqHost { get; set; } = "rabbitmq";
    public Input<string> OtelEndpoint { get; set; } = "http://localhost:4317";
    public Input<string> RedisConnectionString { get; set; } = "redis:6379";
    public int ReservationTtlMinutes { get; set; } = 10;
    public int CheckIntervalSeconds { get; set; } = 30;
    public int Replicas { get; set; } = 1;

    // Requests/limits — obligatoires pour que l'HPA puisse lire les métriques
    public string CpuRequest { get; set; } = "100m";
    public string CpuLimit { get; set; } = "500m";
    public string MemoryRequest { get; set; } = "128Mi";
    public string MemoryLimit { get; set; } = "256Mi";

    public HpaArgs Hpa { get; set; } = new();
}

public class InventoryServiceResources : ComponentResource
{
    public Output<string> ServiceName { get; }

    public InventoryServiceResources(string name, InventoryServiceResourcesArgs args, ComponentResourceOptions? opts = null)
        : base("ecommerce:infra:InventoryServiceResources", name, opts)
    {
        // Quand HPA actif : ignorer les changements sur spec.replicas pour ne pas écraser le scaling
        var deploymentOpts = new CustomResourceOptions
        {
            Parent = this,
            IgnoreChanges = args.Hpa.Enabled ? ["spec.replicas"] : []
        };

        var deployment = new Deployment("inventory-api-deploy", new DeploymentArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "inventory-api" },
            Spec = new DeploymentSpecArgs
            {
                Replicas = args.Replicas,
                Selector = new LabelSelectorArgs
                {
                    MatchLabels = new InputMap<string> { ["app"] = "inventory-api" }
                },
                Template = new PodTemplateSpecArgs
                {
                    Metadata = new ObjectMetaArgs
                    {
                        Labels = new InputMap<string> { ["app"] = "inventory-api" }
                    },
                    Spec = new PodSpecArgs
                    {
                        // Même logique que order-api : psql vérifie que la base existe
                        // et que les credentials fonctionnent avant de laisser démarrer l'API.
                        InitContainers = new ContainerArgs
                        {
                            Name  = "wait-for-dependencies",
                            Image = "postgres:16-alpine",
                            EnvFrom = new EnvFromSourceArgs
                            {
                                SecretRef = new SecretEnvSourceArgs { Name = SecretsResources.InventoryDbSecretName }
                            },
                            // bash est disponible dans postgres:16-alpine (utilisé par l'entrypoint officiel).
                            // /dev/tcp est un built-in bash — pas besoin de nc ou curl.
                            // 1) Attend que inventory-db existe ET que l'auth fonctionne (psql SELECT 1)
                            // 2) Attend que le port AMQP 5672 de RabbitMQ réponde (TCP)
                            Command = new[]
                            {
                                "/bin/bash", "-c",
                                "echo 'Waiting for inventory-db...' && " +
                                "until PGPASSWORD=$POSTGRES_PASSWORD psql -h inventory-db -U $POSTGRES_USER -d $POSTGRES_DB -c 'SELECT 1' >/dev/null 2>&1; do sleep 2; done && " +
                                "echo 'inventory-db ready. Waiting for RabbitMQ...' && " +
                                "until (echo > /dev/tcp/rabbitmq/5672) 2>/dev/null; do sleep 2; done && " +
                                "echo 'All dependencies ready.'"
                            }
                        },
                        Containers = new ContainerArgs
                        {
                            Name            = "inventory-api",
                            Image           = args.Image,
                            ImagePullPolicy = "IfNotPresent",
                            Ports           = new ContainerPortArgs { ContainerPortValue = 8080 },
                            // Variables non secrètes depuis le ConfigMap
                            EnvFrom = new List<EnvFromSourceArgs>
                            {
                                // ✅ ConnectionStrings__InventoryDb injecté depuis le secret ESO
                                new() { SecretRef = new SecretEnvSourceArgs { Name = SecretsResources.InventoryDbSecretName } },
                                // ✅ RabbitMQ__Username + RabbitMQ__Password injectés depuis le secret ESO
                                new() { SecretRef = new SecretEnvSourceArgs { Name = SecretsResources.RabbitMqSecretName } }
                            },
                            Env             = BuildEnvVars(args),
                            Resources = new ResourceRequirementsArgs
                            {
                                Requests = new InputMap<string>
                                {
                                    ["cpu"]    = args.CpuRequest,
                                    ["memory"] = args.MemoryRequest
                                },
                                Limits = new InputMap<string>
                                {
                                    ["cpu"]    = args.CpuLimit,
                                    ["memory"] = args.MemoryLimit
                                }
                            },
                            ReadinessProbe = new ProbeArgs
                            {
                                HttpGet             = new HTTPGetActionArgs { Path = "/health/ready", Port = 8080 },
                                InitialDelaySeconds = 30,
                                PeriodSeconds       = 10,
                                FailureThreshold    = 12
                            },
                            LivenessProbe = new ProbeArgs
                            {
                                HttpGet             = new HTTPGetActionArgs { Path = "/health", Port = 8080 },
                                InitialDelaySeconds = 60,
                                PeriodSeconds       = 15
                            }
                        }
                    }
                }
            }
        }, deploymentOpts);

        var service = new Service("inventory-api-svc", new ServiceArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "inventory-api" },
            Spec = new ServiceSpecArgs
            {
                Selector = new InputMap<string> { ["app"] = "inventory-api" },
                Ports    = new ServicePortArgs { Port = 8080, TargetPort = 8080 }
            }
        }, new CustomResourceOptions { Parent = this });

        if (args.Hpa.Enabled)
            CreateHpa(args, new CustomResourceOptions { Parent = this, DependsOn = deployment });

        ServiceName = Output.Create("inventory-api");
        RegisterOutputs(new Dictionary<string, object?> { ["serviceName"] = ServiceName });
    }

    private static void CreateHpa(InventoryServiceResourcesArgs args, CustomResourceOptions opts)
    {
        var metrics = new List<MetricSpecArgs>
        {
            new()
            {
                Type = "Resource",
                Resource = new ResourceMetricSourceArgs
                {
                    Name   = "cpu",
                    Target = new MetricTargetArgs
                    {
                        Type               = "Utilization",
                        AverageUtilization = args.Hpa.CpuPercent
                    }
                }
            }
        };

        if (args.Hpa.MemoryPercent.HasValue)
            metrics.Add(new MetricSpecArgs
            {
                Type = "Resource",
                Resource = new ResourceMetricSourceArgs
                {
                    Name   = "memory",
                    Target = new MetricTargetArgs
                    {
                        Type               = "Utilization",
                        AverageUtilization = args.Hpa.MemoryPercent.Value
                    }
                }
            });

        new HorizontalPodAutoscaler("inventory-api-hpa", new HorizontalPodAutoscalerArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "inventory-api" },
            Spec = new HorizontalPodAutoscalerSpecArgs
            {
                ScaleTargetRef = new CrossVersionObjectReferenceArgs
                {
                    ApiVersion = "apps/v1",
                    Kind       = "Deployment",
                    Name       = "inventory-api"
                },
                MinReplicas = args.Hpa.MinReplicas,
                MaxReplicas = args.Hpa.MaxReplicas,
                Metrics     = metrics
            }
        }, opts);
    }

    // Seules les variables non secrètes restent ici.
    // ConnectionStrings__InventoryDb, RabbitMQ__Username et RabbitMQ__Password
    // sont injectées via EnvFrom sur les secrets ESO ci-dessus.
    private static List<EnvVarArgs> BuildEnvVars(InventoryServiceResourcesArgs args) =>
    [
        new() { Name = "ASPNETCORE_ENVIRONMENT",            Value = "Production"                           },
        new() { Name = "RabbitMQ__Host",                    Value = args.RabbitMqHost                     },
        new() { Name = "RabbitMQ__VirtualHost",             Value = "/"                                    },
        new() { Name = "Reservation__TtlMinutes",           Value = args.ReservationTtlMinutes.ToString() },
        new() { Name = "Reservation__CheckIntervalSeconds", Value = args.CheckIntervalSeconds.ToString()  },
        new() { Name = "OpenTelemetry__Endpoint",           Value = args.OtelEndpoint                     },
        new() { Name = "ConnectionStrings__Redis",          Value = args.RedisConnectionString            }
    ];
}
