using Pulumi;
using Pulumi.Kubernetes.Helm.V3;
using Pulumi.Kubernetes.Types.Inputs.Helm.V3;

namespace Ecommerce.Infra.Resources;

public class MinioResourcesArgs
{
    /// <summary>Version du chart Helm officiel MinIO. Configurable via minio:version.</summary>
    public string Version { get; set; } = "5.4.0"; // chart 5.4.0 = RELEASE.2024-12-18

    /// <summary>Access key (root user) MinIO = ACCESS_KEY_ID pour CNPG/Barman.</summary>
    public Input<string> RootUser { get; set; } = "minio";

    /// <summary>Secret key (root password) MinIO = ACCESS_SECRET_KEY. ≥ 8 caractères.</summary>
    public Input<string> RootPassword { get; set; } = "minio-dev-password";

    /// <summary>Bucket créé au déploiement (cible des backups CNPG).</summary>
    public string Bucket { get; set; } = "cnpg-backups";

    /// <summary>StorageClass du volume MinIO. Dev : standard (local-path).</summary>
    public string StorageClass { get; set; } = "standard";

    /// <summary>Taille du volume MinIO.</summary>
    public string StorageSize { get; set; } = "5Gi";
}

/// <summary>
/// ════════════════════════════════════════════════════════════════════════════
///  MinIO — object storage S3-compatible (chart officiel minio/minio).
///
///  Sert de cible aux backups CNPG/Barman SANS dépendre d'un cloud (S3/GCS) :
///  même API S3, donc le jour où un vrai bucket cloud existe, seul l'endpoint +
///  les credentials changent.
///
///  Déployé en mode standalone (1 réplica, storage local) — c'est une cible de DEV
///  pour valider le mécanisme de sauvegarde + le PITR. En prod, viser un object
///  storage managé ou un MinIO HA distinct.
///
///  ⚠️ Le chart MinIO demande 16Gi de RAM par défaut → override indispensable pour Kind.
///  Le chart crée aussi le bucket via un Job post-install (image minio/mc).
/// ════════════════════════════════════════════════════════════════════════════
/// </summary>
public class MinioResources : ComponentResource
{
    public const string MinioNamespace = "minio";

    /// <summary>Endpoint S3 interne (pour la config backup CNPG).</summary>
    public Output<string> EndpointUrl { get; }

    public MinioResources(string name, MinioResourcesArgs args, ComponentResourceOptions? opts = null)
        : base("ecommerce:infra:MinioResources", name, opts)
    {
        var baseOpts = new CustomResourceOptions { Parent = this };

        _ = new Release("minio", new ReleaseArgs
        {
            Name            = "minio",
            Chart           = "minio",
            Version         = args.Version,
            Namespace       = MinioNamespace,
            CreateNamespace = true,
            RepositoryOpts  = new RepositoryOptsArgs { Repo = "https://charts.min.io/" },
            Values = new InputMap<object>
            {
                ["mode"]         = "standalone",
                ["replicas"]     = 1,
                ["rootUser"]     = args.RootUser,
                ["rootPassword"] = args.RootPassword,
                // Image épinglée (cohérente avec le préchargement Nuke).
                ["image"] = new Dictionary<string, object>
                {
                    ["repository"] = "quay.io/minio/minio",
                    ["tag"]        = "RELEASE.2024-12-18T13-15-44Z"
                },
                ["persistence"] = new Dictionary<string, object>
                {
                    ["enabled"]      = true,
                    ["size"]         = args.StorageSize,
                    ["storageClass"] = args.StorageClass
                },
                // ⚠️ Override OBLIGATOIRE : le défaut du chart est requests.memory=16Gi.
                ["resources"] = new Dictionary<string, object>
                {
                    ["requests"] = new Dictionary<string, object> { ["cpu"] = "50m", ["memory"] = "256Mi" },
                    ["limits"]   = new Dictionary<string, object> { ["cpu"] = "500m", ["memory"] = "512Mi" }
                },
                // Création du bucket cible au déploiement (Job mc post-install).
                ["buckets"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["name"]   = args.Bucket,
                        ["policy"] = "none",
                        ["purge"]  = false
                    }
                }
            }
        }, baseOpts);

        EndpointUrl = Output.Create($"http://minio.{MinioNamespace}.svc.cluster.local:9000");
        RegisterOutputs();
    }
}
