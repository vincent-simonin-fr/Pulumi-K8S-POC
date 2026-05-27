using Pulumi;
using Pulumi.Kubernetes.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Apps.V1;
using Pulumi.Kubernetes.Types.Inputs.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Meta.V1;
using Deployment = Pulumi.Kubernetes.Apps.V1.Deployment;

namespace Ecommerce.Infra.Resources;

public class MessagingResourcesArgs
{
    public Input<string> Namespace { get; set; } = "ecommerce";
}

public class MessagingResources : ComponentResource
{
    public Output<string> RabbitMqServiceName { get; }

    public MessagingResources(string name, MessagingResourcesArgs args, ComponentResourceOptions? opts = null)
        : base("ecommerce:infra:MessagingResources", name, opts)
    {
        var resourceOpts = new CustomResourceOptions { Parent = this };

        var rabbitmq = new Deployment("rabbitmq", new DeploymentArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "rabbitmq" },
            Spec = new DeploymentSpecArgs
            {
                Replicas = 1,
                Selector = new LabelSelectorArgs
                {
                    MatchLabels = new InputMap<string> { ["app"] = "rabbitmq" }
                },
                Template = new PodTemplateSpecArgs
                {
                    Metadata = new ObjectMetaArgs
                    {
                        Labels = new InputMap<string> { ["app"] = "rabbitmq" }
                    },
                    Spec = new PodSpecArgs
                    {
                        Containers = new ContainerArgs
                        {
                            Name = "rabbitmq",
                            Image = "rabbitmq:3.13-management-alpine",
                            Ports = new List<ContainerPortArgs>
                            {
                                new() { ContainerPortValue = 5672, Name = "amqp" },
                                new() { ContainerPortValue = 15672, Name = "management" }
                            },
                            Env = new List<EnvVarArgs>
                            {
                                new() { Name = "RABBITMQ_DEFAULT_USER", Value = "guest" },
                                new() { Name = "RABBITMQ_DEFAULT_PASS", Value = "guest" }
                            },
                            ReadinessProbe = new ProbeArgs
                            {
                                Exec = new ExecActionArgs
                                {
                                    Command = new[] { "rabbitmq-diagnostics", "-q", "ping" }
                                },
                                InitialDelaySeconds = 15,
                                PeriodSeconds = 10
                            }
                        }
                    }
                }
            }
        }, resourceOpts);

        var rabbitmqService = new Service("rabbitmq-svc", new ServiceArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "rabbitmq" },
            Spec = new ServiceSpecArgs
            {
                Selector = new InputMap<string> { ["app"] = "rabbitmq" },
                Ports = new List<ServicePortArgs>
                {
                    new() { Name = "amqp",       Port = 5672,  TargetPort = 5672  },
                    new() { Name = "management", Port = 15672, TargetPort = 15672 }
                }
            }
        }, resourceOpts);

        RabbitMqServiceName = Output.Create("rabbitmq");

        RegisterOutputs(new Dictionary<string, object?> { ["rabbitMqServiceName"] = RabbitMqServiceName });
    }
}
