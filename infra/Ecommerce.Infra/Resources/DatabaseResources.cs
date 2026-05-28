using Pulumi;
using Pulumi.Kubernetes.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Apps.V1;
using Pulumi.Kubernetes.Types.Inputs.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Meta.V1;
using StatefulSet = Pulumi.Kubernetes.Apps.V1.StatefulSet;

namespace Ecommerce.Infra.Resources;

public class DatabaseResourcesArgs
{
    public Input<string> Namespace { get; set; } = "ecommerce";
    /// <remarks>⚠️ Garder à 1 en dev — scaler PostgreSQL sans réplication active corrompt les données.</remarks>
    public int Replicas { get; set; } = 1;
}

public class DatabaseResources : ComponentResource
{
    public Output<string> OrderDbServiceName     { get; }
    public Output<string> InventoryDbServiceName { get; }

    public DatabaseResources(string name, DatabaseResourcesArgs args, ComponentResourceOptions? opts = null)
        : base("ecommerce:infra:DatabaseResources", name, opts)
    {
        var resourceOpts = new CustomResourceOptions { Parent = this };

        // ── Order DB ──────────────────────────────────────────────────────────
        CreatePostgresStatefulSet("order-db",     SecretsResources.OrderDbSecretName,     args, resourceOpts);

        // ── Inventory DB ──────────────────────────────────────────────────────
        CreatePostgresStatefulSet("inventory-db", SecretsResources.InventoryDbSecretName, args, resourceOpts);

        OrderDbServiceName     = Output.Create("order-db");
        InventoryDbServiceName = Output.Create("inventory-db");

        RegisterOutputs(new Dictionary<string, object?>
        {
            ["orderDbServiceName"]     = OrderDbServiceName,
            ["inventoryDbServiceName"] = InventoryDbServiceName
        });
    }

    /// <summary>
    /// Crée pour une base de données :
    ///   1. Un Service headless (requis par StatefulSet pour les DNS pods)
    ///   2. Un Service ClusterIP (utilisé par les pods applicatifs)
    ///   3. Un StatefulSet avec PVC géré et preStop gracieux
    /// </summary>
    private static void CreatePostgresStatefulSet(
        string appLabel,
        string secretName,
        DatabaseResourcesArgs args,
        CustomResourceOptions opts)
    {
        // ── 1. Service headless ───────────────────────────────────────────────
        //  Requis par le StatefulSet.
        //  Permet aussi le DNS pod-level : <appLabel>-0.<appLabel>-headless.<ns>.svc.cluster.local
        _ = new Service($"{appLabel}-headless-svc", new ServiceArgs
        {
            Metadata = new ObjectMetaArgs
            {
                Namespace = args.Namespace,
                Name      = $"{appLabel}-headless"
            },
            Spec = new ServiceSpecArgs
            {
                ClusterIP = "None",  // headless
                Selector  = new InputMap<string> { ["app"] = appLabel },
                Ports     = new ServicePortArgs { Port = 5432, TargetPort = 5432, Name = "postgres" }
            }
        }, opts);

        // ── 2. Service ClusterIP ──────────────────────────────────────────────
        //  Point d'entrée stable pour order-api / inventory-api.
        //  Adresse fixe même si le pod redémarre et change d'IP.
        _ = new Service($"{appLabel}-svc", new ServiceArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = appLabel },
            Spec = new ServiceSpecArgs
            {
                Selector = new InputMap<string> { ["app"] = appLabel },
                Ports    = new ServicePortArgs { Port = 5432, TargetPort = 5432 }
            }
        }, opts);

        // ── 3. StatefulSet ────────────────────────────────────────────────────
        //  Avantages vs Deployment :
        //   • Pod stable (order-db-0) — un seul pod actif à la fois
        //   • PVC lié au pod (data-order-db-0) — données persistées entre redémarrages
        //   • Arrêt ordonné avant redémarrage (évite la corruption WAL)
        //
        //  ⚠️  VolumeClaimTemplates est IMMUABLE après création.
        //      Pour changer la taille du PVC :
        //        kubectl delete sts <appLabel> -n ecommerce --cascade=orphan
        //        pulumi up --yes   (les PVCs existants sont réutilisés automatiquement)
        _ = new StatefulSet($"{appLabel}-sts", new StatefulSetArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = appLabel },
            Spec = new StatefulSetSpecArgs
            {
                ServiceName = $"{appLabel}-headless",  // référence le service headless ci-dessus
                Replicas    = args.Replicas,
                Selector    = new LabelSelectorArgs
                {
                    MatchLabels = new InputMap<string> { ["app"] = appLabel }
                },
                // PVC créé et géré par le StatefulSet.
                // Nom final : data-<appLabel>-0  (ex : data-order-db-0)
                VolumeClaimTemplates = new List<PersistentVolumeClaimArgs>
                {
                    new()
                    {
                        Metadata = new ObjectMetaArgs { Name = "data" },
                        Spec     = new PersistentVolumeClaimSpecArgs
                        {
                            AccessModes = new[] { "ReadWriteOnce" },
                            Resources   = new VolumeResourceRequirementsArgs
                            {
                                Requests = new InputMap<string> { ["storage"] = "1Gi" }
                            }
                        }
                    }
                },
                Template = new PodTemplateSpecArgs
                {
                    Metadata = new ObjectMetaArgs
                    {
                        Labels = new InputMap<string> { ["app"] = appLabel }
                    },
                    Spec = new PodSpecArgs
                    {
                        // Doit être > durée du preStop + checkpoint PostgreSQL
                        TerminationGracePeriodSeconds = 60,
                        Containers = new ContainerArgs
                        {
                            Name            = "postgres",
                            Image           = "postgres:16-alpine",
                            ImagePullPolicy = "IfNotPresent",
                            Ports           = new ContainerPortArgs { ContainerPortValue = 5432 },
                            EnvFrom = new EnvFromSourceArgs
                            {
                                SecretRef = new SecretEnvSourceArgs { Name = secretName }
                            },
                            // Arrêt gracieux : force un checkpoint propre AVANT que
                            // Kubernetes envoie SIGTERM au processus principal.
                            // Sans ça, PostgreSQL peut être tué en pleine écriture WAL
                            // → corruption au prochain démarrage.
                            // "|| true" : exit 0 si postgres est déjà arrêté (pod en CrashLoop)
                            Lifecycle = new LifecycleArgs
                            {
                                PreStop = new LifecycleHandlerArgs
                                {
                                    Exec = new ExecActionArgs
                                    {
                                        Command = new[]
                                        {
                                            "/bin/sh", "-c",
                                            "pg_ctl stop -D \"$PGDATA\" -m fast || true"
                                        }
                                    }
                                }
                            },
                            VolumeMounts = new VolumeMountArgs
                            {
                                Name      = "data",
                                MountPath = "/var/lib/postgresql/data"
                            },
                            ReadinessProbe = new ProbeArgs
                            {
                                Exec = new ExecActionArgs
                                {
                                    Command = new[] { "pg_isready", "-U", "postgres" }
                                },
                                InitialDelaySeconds = 5,
                                PeriodSeconds       = 5
                            }
                        }
                    }
                }
            }
        }, opts);
    }
}
