using Pulumi;
using Pulumi.Kubernetes.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Apps.V1;
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
    public int Replicas { get; set; } = 1;
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

        // ── Deployment ────────────────────────────────────────────────────────
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
                        Containers = new ContainerArgs
                        {
                            Name = "order-api",
                            Image = args.Image,
                            ImagePullPolicy = "IfNotPresent",
                            Ports = new ContainerPortArgs { ContainerPortValue = 8080 },
                            Env = BuildEnvVars(args),
                            ReadinessProbe = new ProbeArgs
                            {
                                HttpGet = new HTTPGetActionArgs { Path = "/health/ready", Port = 8080 },
                                InitialDelaySeconds = 30,
                                PeriodSeconds = 10,
                                FailureThreshold = 12
                            },
                            LivenessProbe = new ProbeArgs
                            {
                                HttpGet = new HTTPGetActionArgs { Path = "/health", Port = 8080 },
                                InitialDelaySeconds = 60,
                                PeriodSeconds = 15
                            }
                        }
                    }
                }
            }
        }, resourceOpts);

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
