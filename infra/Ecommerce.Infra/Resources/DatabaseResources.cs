using Pulumi;
using Pulumi.Kubernetes.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Apps.V1;
using Pulumi.Kubernetes.Types.Inputs.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Meta.V1;
using Deployment = Pulumi.Kubernetes.Apps.V1.Deployment;

namespace Ecommerce.Infra.Resources;

public class DatabaseResourcesArgs
{
    public Input<string> Namespace { get; set; } = "ecommerce";
}

public class DatabaseResources : ComponentResource
{
    public Output<string> OrderDbServiceName { get; }
    public Output<string> InventoryDbServiceName { get; }

    public DatabaseResources(string name, DatabaseResourcesArgs args, ComponentResourceOptions? opts = null)
        : base("ecommerce:infra:DatabaseResources", name, opts)
    {
        var resourceOpts = new CustomResourceOptions { Parent = this };

        // ── Order DB ──────────────────────────────────────────────────────────
        var orderDbSecret = new Secret("order-db-secret", new SecretArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "order-db-secret" },
            StringData = new InputMap<string>
            {
                ["POSTGRES_DB"] = "order_db",
                ["POSTGRES_USER"] = "postgres",
                ["POSTGRES_PASSWORD"] = "postgres"
            }
        }, resourceOpts);

        var orderDbPvc = new PersistentVolumeClaim("order-db-pvc", new PersistentVolumeClaimArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "order-db-pvc" },
            Spec = new PersistentVolumeClaimSpecArgs
            {
                AccessModes = new[] { "ReadWriteOnce" },
                Resources = new VolumeResourceRequirementsArgs
                {
                    Requests = new InputMap<string> { ["storage"] = "1Gi" }
                }
            }
        }, resourceOpts);

        var orderDb = new Deployment("order-db", new DeploymentArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "order-db" },
            Spec = new DeploymentSpecArgs
            {
                Replicas = 1,
                Selector = new LabelSelectorArgs
                {
                    MatchLabels = new InputMap<string> { ["app"] = "order-db" }
                },
                Template = new PodTemplateSpecArgs
                {
                    Metadata = new ObjectMetaArgs
                    {
                        Labels = new InputMap<string> { ["app"] = "order-db" }
                    },
                    Spec = new PodSpecArgs
                    {
                        Containers = new ContainerArgs
                        {
                            Name = "postgres",
                            Image = "postgres:16-alpine",
                            Ports = new ContainerPortArgs { ContainerPortValue = 5432 },
                            EnvFrom = new EnvFromSourceArgs
                            {
                                SecretRef = new SecretEnvSourceArgs { Name = "order-db-secret" }
                            },
                            VolumeMounts = new VolumeMountArgs
                            {
                                Name = "data",
                                MountPath = "/var/lib/postgresql/data"
                            },
                            ReadinessProbe = new ProbeArgs
                            {
                                Exec = new ExecActionArgs
                                {
                                    Command = new[] { "pg_isready", "-U", "postgres" }
                                },
                                InitialDelaySeconds = 5,
                                PeriodSeconds = 5
                            }
                        },
                        Volumes = new VolumeArgs
                        {
                            Name = "data",
                            PersistentVolumeClaim = new PersistentVolumeClaimVolumeSourceArgs
                            {
                                ClaimName = "order-db-pvc"
                            }
                        }
                    }
                }
            }
        }, resourceOpts);

        var orderDbService = new Service("order-db-svc", new ServiceArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "order-db" },
            Spec = new ServiceSpecArgs
            {
                Selector = new InputMap<string> { ["app"] = "order-db" },
                Ports = new ServicePortArgs { Port = 5432, TargetPort = 5432 }
            }
        }, resourceOpts);

        // ── Inventory DB ──────────────────────────────────────────────────────
        var inventoryDbSecret = new Secret("inventory-db-secret", new SecretArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "inventory-db-secret" },
            StringData = new InputMap<string>
            {
                ["POSTGRES_DB"] = "inventory_db",
                ["POSTGRES_USER"] = "postgres",
                ["POSTGRES_PASSWORD"] = "postgres"
            }
        }, resourceOpts);

        var inventoryDbPvc = new PersistentVolumeClaim("inventory-db-pvc", new PersistentVolumeClaimArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "inventory-db-pvc" },
            Spec = new PersistentVolumeClaimSpecArgs
            {
                AccessModes = new[] { "ReadWriteOnce" },
                Resources = new VolumeResourceRequirementsArgs
                {
                    Requests = new InputMap<string> { ["storage"] = "1Gi" }
                }
            }
        }, resourceOpts);

        var inventoryDb = new Deployment("inventory-db", new DeploymentArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "inventory-db" },
            Spec = new DeploymentSpecArgs
            {
                Replicas = 1,
                Selector = new LabelSelectorArgs
                {
                    MatchLabels = new InputMap<string> { ["app"] = "inventory-db" }
                },
                Template = new PodTemplateSpecArgs
                {
                    Metadata = new ObjectMetaArgs
                    {
                        Labels = new InputMap<string> { ["app"] = "inventory-db" }
                    },
                    Spec = new PodSpecArgs
                    {
                        Containers = new ContainerArgs
                        {
                            Name = "postgres",
                            Image = "postgres:16-alpine",
                            Ports = new ContainerPortArgs { ContainerPortValue = 5432 },
                            EnvFrom = new EnvFromSourceArgs
                            {
                                SecretRef = new SecretEnvSourceArgs { Name = "inventory-db-secret" }
                            },
                            VolumeMounts = new VolumeMountArgs
                            {
                                Name = "data",
                                MountPath = "/var/lib/postgresql/data"
                            },
                            ReadinessProbe = new ProbeArgs
                            {
                                Exec = new ExecActionArgs
                                {
                                    Command = new[] { "pg_isready", "-U", "postgres" }
                                },
                                InitialDelaySeconds = 5,
                                PeriodSeconds = 5
                            }
                        },
                        Volumes = new VolumeArgs
                        {
                            Name = "data",
                            PersistentVolumeClaim = new PersistentVolumeClaimVolumeSourceArgs
                            {
                                ClaimName = "inventory-db-pvc"
                            }
                        }
                    }
                }
            }
        }, resourceOpts);

        var inventoryDbService = new Service("inventory-db-svc", new ServiceArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "inventory-db" },
            Spec = new ServiceSpecArgs
            {
                Selector = new InputMap<string> { ["app"] = "inventory-db" },
                Ports = new ServicePortArgs { Port = 5432, TargetPort = 5432 }
            }
        }, resourceOpts);

        OrderDbServiceName = Output.Create("order-db");
        InventoryDbServiceName = Output.Create("inventory-db");

        RegisterOutputs(new Dictionary<string, object?>
        {
            ["orderDbServiceName"] = OrderDbServiceName,
            ["inventoryDbServiceName"] = InventoryDbServiceName
        });
    }
}
