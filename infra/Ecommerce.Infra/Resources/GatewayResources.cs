using Pulumi;
using Pulumi.Kubernetes.Autoscaling.V2;
using Pulumi.Kubernetes.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Apps.V1;
using Pulumi.Kubernetes.Types.Inputs.Autoscaling.V2;
using Pulumi.Kubernetes.Types.Inputs.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Meta.V1;
using Deployment = Pulumi.Kubernetes.Apps.V1.Deployment;

namespace Ecommerce.Infra.Resources;

public class GatewayResourcesArgs
{
    public Input<string> Namespace { get; set; } = "ecommerce";
    public Input<string> Image { get; set; } = "localhost/ecommerce/gateway:dev";
    public int NodePort { get; set; } = 30080;
    public Input<string> OrderApiHost { get; set; } = "order-api";
    public Input<string> InventoryApiHost { get; set; } = "inventory-api";
    public int Replicas { get; set; } = 1;

    // Requests/limits — obligatoires pour que l'HPA puisse lire les métriques
    public string CpuRequest { get; set; } = "50m";
    public string CpuLimit { get; set; } = "250m";
    public string MemoryRequest { get; set; } = "64Mi";
    public string MemoryLimit { get; set; } = "128Mi";

    public HpaArgs Hpa { get; set; } = new();
}

public class GatewayResources : ComponentResource
{
    public GatewayResources(string name, GatewayResourcesArgs args, ComponentResourceOptions? opts = null)
        : base("ecommerce:infra:GatewayResources", name, opts)
    {
        // Quand HPA actif : ignorer les changements sur spec.replicas pour ne pas écraser le scaling
        var deploymentOpts = new CustomResourceOptions
        {
            Parent = this,
            IgnoreChanges = args.Hpa.Enabled ? ["spec.replicas"] : []
        };

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
        }, new CustomResourceOptions { Parent = this });

        var deployment = new Deployment("gateway-deploy", new DeploymentArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "gateway" },
            Spec = new DeploymentSpecArgs
            {
                Replicas = args.Replicas,
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
                            Name            = "gateway",
                            Image           = args.Image,
                            ImagePullPolicy = "IfNotPresent",
                            Ports           = new ContainerPortArgs { ContainerPortValue = 8080 },
                            EnvFrom = new EnvFromSourceArgs
                            {
                                ConfigMapRef = new ConfigMapEnvSourceArgs { Name = "gateway-config" }
                            },
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
                                HttpGet             = new HTTPGetActionArgs { Path = "/health", Port = 8080 },
                                InitialDelaySeconds = 5,
                                PeriodSeconds       = 5
                            }
                        }
                    }
                }
            }
        }, deploymentOpts);

        var service = new Service("gateway-svc", new ServiceArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "gateway" },
            Spec = new ServiceSpecArgs
            {
                Type     = "NodePort",
                Selector = new InputMap<string> { ["app"] = "gateway" },
                Ports    = new ServicePortArgs
                {
                    Port       = 8080,
                    TargetPort = 8080,
                    NodePort   = args.NodePort
                }
            }
        }, new CustomResourceOptions { Parent = this });

        if (args.Hpa.Enabled)
            CreateHpa(args, new CustomResourceOptions { Parent = this, DependsOn = deployment });

        RegisterOutputs(new Dictionary<string, object?> { ["nodePort"] = args.NodePort });
    }

    private static void CreateHpa(GatewayResourcesArgs args, CustomResourceOptions opts)
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

        new HorizontalPodAutoscaler("gateway-hpa", new HorizontalPodAutoscalerArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "gateway" },
            Spec = new HorizontalPodAutoscalerSpecArgs
            {
                ScaleTargetRef = new CrossVersionObjectReferenceArgs
                {
                    ApiVersion = "apps/v1",
                    Kind       = "Deployment",
                    Name       = "gateway"
                },
                MinReplicas = args.Hpa.MinReplicas,
                MaxReplicas = args.Hpa.MaxReplicas,
                Metrics     = metrics
            }
        }, opts);
    }
}
