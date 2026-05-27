using Pulumi;
using Pulumi.Kubernetes.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Apps.V1;
using Pulumi.Kubernetes.Types.Inputs.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Meta.V1;
using Deployment = Pulumi.Kubernetes.Apps.V1.Deployment;

namespace Ecommerce.Infra.Resources;

public class GatewayResourcesArgs
{
    public Input<string> Namespace { get; set; } = "ecommerce";
    public Input<string> Image { get; set; } = "ecommerce/gateway:dev";
    public int NodePort { get; set; } = 30080;
    public Input<string> OrderApiHost { get; set; } = "order-api";
    public Input<string> InventoryApiHost { get; set; } = "inventory-api";
}

public class GatewayResources : ComponentResource
{
    public GatewayResources(string name, GatewayResourcesArgs args, ComponentResourceOptions? opts = null)
        : base("ecommerce:infra:GatewayResources", name, opts)
    {
        var resourceOpts = new CustomResourceOptions { Parent = this };

        // ConfigMap with YARP config injected as env overrides
        var configMap = new ConfigMap("gateway-config", new ConfigMapArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "gateway-config" },
            Data = new InputMap<string>
            {
                ["ReverseProxy__Clusters__order-cluster__Destinations__order-api__Address"] =
                    Output.Format($"http://{args.OrderApiHost}:8080"),
                ["ReverseProxy__Clusters__inventory-cluster__Destinations__inventory-api__Address"] =
                    Output.Format($"http://{args.InventoryApiHost}:8080")
            }
        }, resourceOpts);

        var deployment = new Deployment("gateway-deploy", new DeploymentArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "gateway" },
            Spec = new DeploymentSpecArgs
            {
                Replicas = 1,
                Selector = new LabelSelectorArgs
                {
                    MatchLabels = new InputMap<string> { ["app"] = "gateway" }
                },
                Template = new PodTemplateSpecArgs
                {
                    Metadata = new ObjectMetaArgs
                    {
                        Labels = new InputMap<string> { ["app"] = "gateway" }
                    },
                    Spec = new PodSpecArgs
                    {
                        Containers = new ContainerArgs
                        {
                            Name = "gateway",
                            Image = args.Image,
                            Ports = new ContainerPortArgs { ContainerPortValue = 8080 },
                            EnvFrom = new EnvFromSourceArgs
                            {
                                ConfigMapRef = new ConfigMapEnvSourceArgs { Name = "gateway-config" }
                            },
                            ReadinessProbe = new ProbeArgs
                            {
                                HttpGet = new HTTPGetActionArgs { Path = "/health", Port = 8080 },
                                InitialDelaySeconds = 5,
                                PeriodSeconds = 5
                            }
                        }
                    }
                }
            }
        }, resourceOpts);

        // NodePort pour accès local depuis l'hôte Podman
        var service = new Service("gateway-svc", new ServiceArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "gateway" },
            Spec = new ServiceSpecArgs
            {
                Type = "NodePort",
                Selector = new InputMap<string> { ["app"] = "gateway" },
                Ports = new ServicePortArgs
                {
                    Port = 8080,
                    TargetPort = 8080,
                    NodePort = args.NodePort
                }
            }
        }, resourceOpts);

        RegisterOutputs(new Dictionary<string, object?> { ["nodePort"] = args.NodePort });
    }
}
