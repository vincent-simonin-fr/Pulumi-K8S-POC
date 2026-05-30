using Pulumi;
using Pulumi.Kubernetes.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Apps.V1;
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

    /// <summary>
    /// HpaArgs conservé pour compatibilité ascendante mais ignoré :
    /// le scaling d'inventory-api est désormais géré par KEDA (KedaResources).
    /// KEDA crée son propre HPA interne à partir du ScaledObject.
    /// </summary>
    [Obsolete("Remplacé par KedaResources — KEDA gère le scaling d'inventory-api via ScaledObject.")]
    public HpaArgs Hpa { get; set; } = new();
}

public class InventoryServiceResources : ComponentResource
{
    public Output<string> ServiceName { get; }

    public InventoryServiceResources(string name, InventoryServiceResourcesArgs args, ComponentResourceOptions? opts = null)
        : base("ecommerce:infra:InventoryServiceResources", name, opts)
    {
        // KEDA gère spec.replicas via son HPA interne → toujours ignorer ce champ.
        // Sans IgnoreChanges, Pulumi réinitialiserait le nombre de réplicas à chaque
        // `pulumi up`, annulant le scaling décidé par KEDA.
        var deploymentOpts = new CustomResourceOptions
        {
            Parent        = this,
            IgnoreChanges = ["spec.replicas"]
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
                            // Pas besoin du secret DB : uniquement un check TCP (pas d'auth).
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
                            Command = args.InventoryDbHost.Apply(dbHost => (IEnumerable<string>) new[]
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

        // Le scaling d'inventory-api est géré par KEDA (KedaResources.cs).
        // KEDA crée automatiquement un HPA interne à partir du ScaledObject.
        // Ne pas créer d'HPA natif ici : conflit avec l'HPA KEDA si les deux existent.

        ServiceName = Output.Create("inventory-api");
        RegisterOutputs(new Dictionary<string, object?> { ["serviceName"] = ServiceName });
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
