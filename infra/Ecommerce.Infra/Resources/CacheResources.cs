using Pulumi;
using Pulumi.Kubernetes.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Apps.V1;
using Pulumi.Kubernetes.Types.Inputs.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Meta.V1;
using Deployment = Pulumi.Kubernetes.Apps.V1.Deployment;

namespace Ecommerce.Infra.Resources;

public class CacheResourcesArgs
{
    public Input<string> Namespace { get; set; } = "ecommerce";
}

/// <summary>
/// Deploie Redis en tant que cache distribue pour inventory-api (GET /inventory).
/// Redis n ayant pas besoin de persistance (cache volatile par nature), on utilise
/// un simple Deployment (pas de StatefulSet ni PVC).
/// Service name : "redis" → connection string : "redis:6379"
/// </summary>
public class CacheResources : ComponentResource
{
    public Output<string> RedisServiceName       { get; }
    public Output<string> RedisConnectionString  { get; }

    public CacheResources(string name, CacheResourcesArgs args, ComponentResourceOptions? opts = null)
        : base("ecommerce:infra:CacheResources", name, opts)
    {
        var resourceOpts = new CustomResourceOptions { Parent = this };
        const string appLabel = "redis";

        _ = new Deployment("redis-deploy", new DeploymentArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = appLabel },
            Spec = new DeploymentSpecArgs
            {
                Replicas = 1,
                Selector = new LabelSelectorArgs
                {
                    MatchLabels = new InputMap<string> { ["app"] = appLabel }
                },
                Template = new PodTemplateSpecArgs
                {
                    Metadata = new ObjectMetaArgs
                    {
                        Labels = new InputMap<string> { ["app"] = appLabel }
                    },
                    Spec = new PodSpecArgs
                    {
                        Containers = new ContainerArgs
                        {
                            Name            = "redis",
                            Image           = "redis:7-alpine",
                            ImagePullPolicy = "IfNotPresent",
                            Ports           = new ContainerPortArgs { ContainerPortValue = 6379 },
                            // Pas de persistance : cache volatile
                            // --save "" desactive le RDB dump
                            Command = new[] { "redis-server", "--save", "", "--loglevel", "warning" },
                            Resources = new ResourceRequirementsArgs
                            {
                                Requests = new InputMap<string> { ["cpu"] = "50m",  ["memory"] = "64Mi"  },
                                Limits   = new InputMap<string> { ["cpu"] = "200m", ["memory"] = "128Mi" }
                            },
                            ReadinessProbe = new ProbeArgs
                            {
                                Exec = new ExecActionArgs
                                {
                                    Command = new[] { "redis-cli", "ping" }
                                },
                                InitialDelaySeconds = 5,
                                PeriodSeconds       = 5
                            },
                            LivenessProbe = new ProbeArgs
                            {
                                Exec = new ExecActionArgs
                                {
                                    Command = new[] { "redis-cli", "ping" }
                                },
                                InitialDelaySeconds = 15,
                                PeriodSeconds       = 10
                            }
                        }
                    }
                }
            }
        }, resourceOpts);

        _ = new Service("redis-svc", new ServiceArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = appLabel },
            Spec = new ServiceSpecArgs
            {
                Selector = new InputMap<string> { ["app"] = appLabel },
                Ports    = new ServicePortArgs { Port = 6379, TargetPort = 6379 }
            }
        }, resourceOpts);

        RedisServiceName      = Output.Create(appLabel);
        RedisConnectionString = Output.Create($"{appLabel}:6379");

        RegisterOutputs(new Dictionary<string, object?>
        {
            ["redisServiceName"]      = RedisServiceName,
            ["redisConnectionString"] = RedisConnectionString
        });
    }
}
