using Pulumi;
using Pulumi.Kubernetes.Batch.V1;
using Pulumi.Kubernetes.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Apps.V1;
using Pulumi.Kubernetes.Types.Inputs.Batch.V1;
using Pulumi.Kubernetes.Types.Inputs.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Meta.V1;
using Deployment = Pulumi.Kubernetes.Apps.V1.Deployment;

namespace Ecommerce.Infra.Resources;

public class ServiceResourcesArgs
{
    public Input<string> Namespace { get; set; } = "ecommerce";
    public Input<string> Image { get; set; } = "ecommerce/order-api:dev";
    public Input<string> OrderDbHost { get; set; } = "order-db";
    public Input<string> RabbitMqHost { get; set; } = "rabbitmq";
}

public class OrderServiceResources : ComponentResource
{
    public Output<string> ServiceName { get; }

    public OrderServiceResources(string name, ServiceResourcesArgs args, ComponentResourceOptions? opts = null)
        : base("ecommerce:infra:OrderServiceResources", name, opts)
    {
        var resourceOpts = new CustomResourceOptions { Parent = this };

        // ConfigMap
        var configMap = new ConfigMap("order-api-config", new ConfigMapArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "order-api-config" },
            Data = new InputMap<string>
            {
                ["ASPNETCORE_ENVIRONMENT"] = "Production",
                ["RabbitMQ__VirtualHost"] = "/",
                ["RabbitMQ__Username"] = "guest",
                ["RabbitMQ__Password"] = "guest"
            }
        }, resourceOpts);

        // ── Init container Job pour EF Core migrations ────────────────────────
        var migrationJob = new Job("order-api-migration", new JobArgs
        {
            Metadata = new ObjectMetaArgs
            {
                Namespace = args.Namespace,
                Name = "order-api-migration",
                Annotations = new InputMap<string>
                {
                    // Force la recréation du Job à chaque pulumi up si l'image change
                    ["pulumi.com/replaceOnChanges"] = "spec.template.spec.containers[0].image"
                }
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
        var deployment = new Deployment("order-api-deploy", new DeploymentArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "order-api" },
            Spec = new DeploymentSpecArgs
            {
                Replicas = 1,
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
                        Containers = new ContainerArgs
                        {
                            Name = "order-api",
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

        var service = new Service("order-api-svc", new ServiceArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "order-api" },
            Spec = new ServiceSpecArgs
            {
                Selector = new InputMap<string> { ["app"] = "order-api" },
                Ports = new ServicePortArgs { Port = 8080, TargetPort = 8080 }
            }
        }, resourceOpts);

        ServiceName = Output.Create("order-api");

        RegisterOutputs(new Dictionary<string, object?> { ["serviceName"] = ServiceName });
    }

    private static List<EnvVarArgs> BuildEnvVars(ServiceResourcesArgs args) =>
    [
        new() { Name = "ASPNETCORE_ENVIRONMENT", Value = "Production" },
        new()
        {
            Name  = "ConnectionStrings__OrderDb",
            Value = Output.Format($"Host={args.OrderDbHost};Port=5432;Database=order_db;Username=postgres;Password=postgres")
        },
        new() { Name = "RabbitMQ__Host",        Value = args.RabbitMqHost },
        new() { Name = "RabbitMQ__VirtualHost",  Value = "/" },
        new() { Name = "RabbitMQ__Username",     Value = "guest" },
        new() { Name = "RabbitMQ__Password",     Value = "guest" }
    ];
}
