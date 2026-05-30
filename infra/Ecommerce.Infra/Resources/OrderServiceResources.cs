using Pulumi;
using Pulumi.Kubernetes.Autoscaling.V2;
using Pulumi.Kubernetes.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Apps.V1;
using Pulumi.Kubernetes.Types.Inputs.Autoscaling.V2;
using Pulumi.Kubernetes.Types.Inputs.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Meta.V1;
using Deployment = Pulumi.Kubernetes.Apps.V1.Deployment;

namespace Ecommerce.Infra.Resources;

public class ServiceResourcesArgs
{
    public Input<string> Namespace { get; set; } = "ecommerce";
    public Input<string> Image { get; set; } = "localhost/ecommerce/order-api:dev";
    public Input<string> OrderDbHost { get; set; } = "order-db";
    public Input<string> RabbitMqHost { get; set; } = "rabbitmq";
    public Input<string> OtelEndpoint { get; set; } = "http://localhost:4317";
    public int Replicas { get; set; } = 1;

    // Requests/limits — obligatoires pour que l'HPA puisse lire les métriques
    public string CpuRequest { get; set; } = "100m";
    public string CpuLimit { get; set; } = "500m";
    public string MemoryRequest { get; set; } = "128Mi";
    public string MemoryLimit { get; set; } = "256Mi";

    public HpaArgs Hpa { get; set; } = new();
}

public class OrderServiceResources : ComponentResource
{
    public Output<string> ServiceName { get; }

    public OrderServiceResources(string name, ServiceResourcesArgs args, ComponentResourceOptions? opts = null)
        : base("ecommerce:infra:OrderServiceResources", name, opts)
    {
        // Quand HPA actif : ignorer les changements sur spec.replicas pour ne pas écraser le scaling
        var deploymentOpts = new CustomResourceOptions
        {
            Parent = this,
            IgnoreChanges = args.Hpa.Enabled ? ["spec.replicas"] : []
        };

        // ConfigMap : uniquement les variables non secrètes
        // Les credentials (ConnectionStrings__OrderDb, RabbitMQ__Username/Password)
        // sont injectés via les K8s Secrets créés par ESO.
        var configMap = new ConfigMap("order-api-config", new ConfigMapArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "order-api-config" },
            Data = new InputMap<string>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Production",
                ["RabbitMQ__Host"]         = args.RabbitMqHost.Apply(h => h),
                ["RabbitMQ__VirtualHost"]  = "/"
            }
        }, new CustomResourceOptions { Parent = this });

        var deployment = new Deployment("order-api-deploy", new DeploymentArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "order-api" },
            Spec = new DeploymentSpecArgs
            {
                Replicas = args.Replicas,
                Selector = new LabelSelectorArgs
                {
                    MatchLabels = new InputMap<string> { ["app"] = "order-api" }
                },
                Template = new PodTemplateSpecArgs
                {
                    Metadata = new ObjectMetaArgs
                    {
                        Labels = new InputMap<string> { ["app"] = "order-api" }
                    },
                    Spec = new PodSpecArgs
                    {
                        // Attend que la base order_db soit réellement accessible avant de démarrer.
                        // pg_isready retourne true dès que postgres accepte les connexions TCP,
                        // AVANT que l'initialisation du catalogue soit terminée.
                        // On utilise psql pour vérifier que la base existe ET que les credentials fonctionnent.
                        InitContainers = new ContainerArgs
                        {
                            Name  = "wait-for-dependencies",
                            Image = "postgres:16-alpine",
                            // Pas besoin du secret DB : on fait uniquement un check TCP (pas d'auth).
                            // /dev/tcp est un built-in bash disponible dans postgres:16-alpine.
                            //
                            // Pourquoi TCP plutôt que psql SELECT 1 ?
                            //   psql = DNS + TCP + handshake TLS + authentification PostgreSQL (~5-8 s)
                            //   TCP  = DNS + TCP uniquement (~0.2 s par tentative, 1-2 s au total)
                            // Gain estimé : -5 à -8 s sur le cold-start des pods lors d'un scale-out.
                            //
                            // Tradeoff : on ne valide plus que les credentials sont corrects à ce stade.
                            // L'authentification est vérifiée au premier accès EF Core (startup de l'app).
                            // Avec CNPG, quand -rw répond sur 5432, la base est opérationnelle.
                            Command = args.OrderDbHost.Apply(dbHost => (IEnumerable<string>) new[]
                            {
                                "/bin/bash", "-c",
                                $"echo 'Waiting for {dbHost}:5432 (TCP)...' && " +
                                $"until (echo > /dev/tcp/{dbHost}/5432) 2>/dev/null; do sleep 1; done && " +
                                $"echo '{dbHost} TCP ready. Waiting for RabbitMQ:5672 (TCP)...' && " +
                                "until (echo > /dev/tcp/rabbitmq/5672) 2>/dev/null; do sleep 1; done && " +
                                "echo 'All dependencies TCP-ready.'"
                            })
                        },
                        Containers = new ContainerArgs
                        {
                            Name            = "order-api",
                            Image           = args.Image,
                            ImagePullPolicy = "IfNotPresent",
                            Ports           = new ContainerPortArgs { ContainerPortValue = 8080 },
                            // Variables non secrètes depuis le ConfigMap
                            EnvFrom = new List<EnvFromSourceArgs>
                            {
                                new() { ConfigMapRef = new ConfigMapEnvSourceArgs { Name = "order-api-config" } },
                                // ✅ ConnectionStrings__OrderDb injecté depuis le secret ESO
                                new() { SecretRef = new SecretEnvSourceArgs { Name = SecretsResources.OrderDbSecretName } },
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
                            // InitialDelaySeconds réduit à 10 : l'init container TCP (~1-2 s) libère le
                            // main container bien avant que .NET ait fini son startup (~15 s).
                            // PeriodSeconds=5 + FailureThreshold=24 → budget total : 10 + 24×5 = 130 s
                            // (couvre EF Core MigrateAsync même sur une base fraîche).
                            ReadinessProbe = new ProbeArgs
                            {
                                HttpGet             = new HTTPGetActionArgs { Path = "/health/ready", Port = 8080 },
                                InitialDelaySeconds = 10,
                                PeriodSeconds       = 5,
                                FailureThreshold    = 24
                            },
                            LivenessProbe = new ProbeArgs
                            {
                                HttpGet             = new HTTPGetActionArgs { Path = "/health", Port = 8080 },
                                InitialDelaySeconds = 40,
                                PeriodSeconds       = 15,
                                TimeoutSeconds      = 5,
                                FailureThreshold    = 5
                            }
                        }
                    }
                }
            }
        }, deploymentOpts);

        var service = new Service("order-api-svc", new ServiceArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "order-api" },
            Spec = new ServiceSpecArgs
            {
                Selector = new InputMap<string> { ["app"] = "order-api" },
                Ports    = new ServicePortArgs { Port = 8080, TargetPort = 8080 }
            }
        }, new CustomResourceOptions { Parent = this });

        if (args.Hpa.Enabled)
            CreateHpa(args, new CustomResourceOptions { Parent = this, DependsOn = deployment });

        ServiceName = Output.Create("order-api");
        RegisterOutputs(new Dictionary<string, object?> { ["serviceName"] = ServiceName });
    }

    private static void CreateHpa(ServiceResourcesArgs args, CustomResourceOptions opts)
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

        new HorizontalPodAutoscaler("order-api-hpa", new HorizontalPodAutoscalerArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "order-api" },
            Spec = new HorizontalPodAutoscalerSpecArgs
            {
                ScaleTargetRef = new CrossVersionObjectReferenceArgs
                {
                    ApiVersion = "apps/v1",
                    Kind       = "Deployment",
                    Name       = "order-api"
                },
                MinReplicas = args.Hpa.MinReplicas,
                MaxReplicas = args.Hpa.MaxReplicas,
                Metrics     = metrics
            }
        }, opts);
    }

    // Seules les variables non secrètes restent ici.
    // ConnectionStrings__OrderDb, RabbitMQ__Username et RabbitMQ__Password
    // sont injectées via EnvFrom sur les secrets ESO ci-dessus.
    private static List<EnvVarArgs> BuildEnvVars(ServiceResourcesArgs args) =>
    [
        new() { Name = "RabbitMQ__Host",           Value = args.RabbitMqHost  },
        new() { Name = "OpenTelemetry__Endpoint",  Value = args.OtelEndpoint  }
    ];
}
