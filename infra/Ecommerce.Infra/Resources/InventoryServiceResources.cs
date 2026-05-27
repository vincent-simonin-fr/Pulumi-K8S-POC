using Pulumi;
using Pulumi.Kubernetes.Batch.V1;
using Pulumi.Kubernetes.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Apps.V1;
using Pulumi.Kubernetes.Types.Inputs.Batch.V1;
using Pulumi.Kubernetes.Types.Inputs.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Meta.V1;
using Deployment = Pulumi.Kubernetes.Apps.V1.Deployment;

namespace Ecommerce.Infra.Resources;

public class InventoryServiceResourcesArgs
{
    public Input<string> Namespace { get; set; } = "ecommerce";
    public Input<string> Image { get; set; } = "ecommerce/inventory-api:dev";
    public Input<string> InventoryDbHost { get; set; } = "inventory-db";
    public Input<string> RabbitMqHost { get; set; } = "rabbitmq";
    public int ReservationTtlMinutes { get; set; } = 10;
    public int CheckIntervalSeconds { get; set; } = 30;
}

public class InventoryServiceResources : ComponentResource
{
    public Output<string> ServiceName { get; }

    public InventoryServiceResources(string name, InventoryServiceResourcesArgs args, ComponentResourceOptions? opts = null)
        : base("ecommerce:infra:InventoryServiceResources", name, opts)
    {
        var resourceOpts = new CustomResourceOptions { Parent = this };

        // ── Init container Job pour EF Core migrations ────────────────────────
        var migrationJob = new Job("inventory-api-migration", new JobArgs
        {
            Metadata = new ObjectMetaArgs
            {
                Namespace = args.Namespace,
                Name = "inventory-api-migration"
            },
            Spec = new JobSpecArgs
            {
                BackoffLimit = 3,
                Template = new PodTemplateSpecArgs
                {
                    Spec = new PodSpecArgs
                    {
                        RestartPolicy = "OnFailure",
                        Containers = new ContainerArgs
                        {
                            Name = "migration",
                            Image = args.Image,
                            Command = new[] { "dotnet", "ef", "database", "update" },
                            Env = BuildEnvVars(args)
                        }
                    }
                }
            }
        }, resourceOpts);

        // ── Deployment ────────────────────────────────────────────────────────
        var deployment = new Deployment("inventory-api-deploy", new DeploymentArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "inventory-api" },
            Spec = new DeploymentSpecArgs
            {
                Replicas = 1,
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
                        Containers = new ContainerArgs
                        {
                            Name = "inventory-api",
                            Image = args.Image,
                            Ports = new ContainerPortArgs { ContainerPortValue = 8080 },
                            Env = BuildEnvVars(args),
                            ReadinessProbe = new ProbeArgs
                            {
                                HttpGet = new HTTPGetActionArgs { Path = "/health", Port = 8080 },
                                InitialDelaySeconds = 10,
                                PeriodSeconds = 5
                            },
                            LivenessProbe = new ProbeArgs
                            {
                                HttpGet = new HTTPGetActionArgs { Path = "/health", Port = 8080 },
                                InitialDelaySeconds = 20,
                                PeriodSeconds = 10
                            }
                        }
                    }
                }
            }
        }, new CustomResourceOptions { Parent = this, DependsOn = migrationJob });

        var service = new Service("inventory-api-svc", new ServiceArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "inventory-api" },
            Spec = new ServiceSpecArgs
            {
                Selector = new InputMap<string> { ["app"] = "inventory-api" },
                Ports = new ServicePortArgs { Port = 8080, TargetPort = 8080 }
            }
        }, resourceOpts);

        ServiceName = Output.Create("inventory-api");

        RegisterOutputs(new Dictionary<string, object?> { ["serviceName"] = ServiceName });
    }

    private List<EnvVarArgs> BuildEnvVars(InventoryServiceResourcesArgs args) =>
    [
        new() { Name = "ASPNETCORE_ENVIRONMENT", Value = "Production" },
        new()
        {
            Name  = "ConnectionStrings__InventoryDb",
            Value = Output.Format($"Host={args.InventoryDbHost};Port=5432;Database=inventory_db;Username=postgres;Password=postgres")
        },
        new() { Name = "RabbitMQ__Host",                    Value = args.RabbitMqHost },
        new() { Name = "RabbitMQ__VirtualHost",              Value = "/" },
        new() { Name = "RabbitMQ__Username",                 Value = "guest" },
        new() { Name = "RabbitMQ__Password",                 Value = "guest" },
        new() { Name = "Reservation__TtlMinutes",            Value = args.ReservationTtlMinutes.ToString() },
        new() { Name = "Reservation__CheckIntervalSeconds",  Value = args.CheckIntervalSeconds.ToString() }
    ];
}
