using System.IO;
using System.Security.Cryptography;
using System.Text;
using Pulumi;
using Pulumi.Kubernetes.Batch.V1;
using Pulumi.Kubernetes.Core.V1;
using Pulumi.Kubernetes.Rbac.V1;
using Pulumi.Kubernetes.Types.Inputs.Batch.V1;
using Pulumi.Kubernetes.Types.Inputs.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Meta.V1;
using Pulumi.Kubernetes.Types.Inputs.Rbac.V1;

namespace Ecommerce.Infra.Resources;

public class VaultConfigResourcesArgs
{
    /// <summary>Token d'admin Vault (root en dev) injecté dans le Job de config. Via vault:rootToken (--secret).</summary>
    public Input<string> RootToken { get; set; } = "";

    /// <summary>Tag de l'image hashicorp/vault utilisée par le Job (doit matcher le serveur).</summary>
    public string VaultImageTag { get; set; } = "1.21.2";
}

/// <summary>
/// ════════════════════════════════════════════════════════════════════════════
///  Configuration Vault — Option A (Job de bootstrap in-cluster).
///
///  Rejoue de façon idempotente, DANS le cluster, la config validée manuellement :
///    - database secrets engine -> order-db (CNPG), rôle dynamique order-app
///    - auth Kubernetes + policy + rôle k8s order-app (SA ecommerce/vault-auth)
///  (script : scripts/vault-config.sh, lu dans une ConfigMap).
///
///  Ressources créées :
///    1. ClusterRoleBinding vault-auth-delegator : donne au SA vault:vault le droit
///       de TokenReview (requis par l'auth Kubernetes de Vault).
///    2. Secret vault-root-token : le token d'admin injecté au Job (VAULT_TOKEN).
///    3. ConfigMap vault-config-script : le script de config.
///    4. Job vault-config-<hash> : exécute le script (re-créé si le script change).
///
///  Pré-requis : Vault initialisé + unsealed, et vault:rootToken renseigné. Gardé en
///  amont (EcommerceStack) par la présence du token → le Job n'est créé qu'une fois
///  Vault prêt (bootstrap : up → init/unseal → config set rootToken → up).
///
///  ⚠️ Le root token en Secret K8s + état Pulumi est l'anti-pattern accepté EN DEV.
///     En prod : voir la proposition OpenSpec 'vault-config-pulumi-provider' (Option B).
/// ════════════════════════════════════════════════════════════════════════════
/// </summary>
public class VaultConfigResources : ComponentResource
{
    private const string Ns = VaultResources.VaultNamespace; // "vault"

    public VaultConfigResources(string name, VaultConfigResourcesArgs args, ComponentResourceOptions? opts = null)
        : base("ecommerce:infra:VaultConfigResources", name, opts)
    {
        var o = new CustomResourceOptions { Parent = this };

        // 1. RBAC : le SA de Vault doit pouvoir faire des TokenReview (auth k8s).
        var binding = new ClusterRoleBinding("vault-auth-delegator", new ClusterRoleBindingArgs
        {
            Metadata = new ObjectMetaArgs { Name = "vault-auth-delegator" },
            RoleRef = new RoleRefArgs
            {
                ApiGroup = "rbac.authorization.k8s.io",
                Kind     = "ClusterRole",
                Name     = "system:auth-delegator"
            },
            Subjects = new[]
            {
                new SubjectArgs { Kind = "ServiceAccount", Name = "vault", Namespace = Ns }
            }
        }, o);

        // 2. Secret avec le token d'admin (root en dev). Pulumi chiffre la valeur en état.
        var tokenSecret = new Secret("vault-root-token", new SecretArgs
        {
            Metadata   = new ObjectMetaArgs { Name = "vault-root-token", Namespace = Ns },
            StringData = new InputMap<string> { ["token"] = args.RootToken }
        }, o);

        // 3. ConfigMap : le script de config (source de vérité = fichier versionné).
        var scriptText = File.ReadAllText("scripts/vault-config.sh");
        var scriptCm = new ConfigMap("vault-config-script", new ConfigMapArgs
        {
            Metadata = new ObjectMetaArgs { Name = "vault-config-script", Namespace = Ns },
            Data     = new InputMap<string> { ["vault-config.sh"] = scriptText }
        }, o);

        // Hash court du script → suffixe du nom du Job. Si le script change, un nouveau
        // Job est créé (les Jobs sont immuables) et l'ancien remplacé.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scriptText)))[..8].ToLowerInvariant();
        var jobName = $"vault-config-{hash}";

        // 4. Job : exécute le script avec VAULT_ADDR/VAULT_TOKEN (le CLI les lit dans l'env).
        _ = new Job(jobName, new JobArgs
        {
            Metadata = new ObjectMetaArgs { Name = jobName, Namespace = Ns },
            Spec = new JobSpecArgs
            {
                BackoffLimit = 5, // Vault peut mettre quelques secondes à être prêt
                Template = new PodTemplateSpecArgs
                {
                    Spec = new PodSpecArgs
                    {
                        RestartPolicy = "OnFailure",
                        Containers = new[]
                        {
                            new ContainerArgs
                            {
                                Name    = "vault-config",
                                Image   = $"hashicorp/vault:{args.VaultImageTag}",
                                Command = new[] { "sh", "/scripts/vault-config.sh" },
                                Env = new[]
                                {
                                    new EnvVarArgs
                                    {
                                        Name  = "VAULT_ADDR",
                                        Value = "http://vault.vault.svc.cluster.local:8200"
                                    },
                                    new EnvVarArgs
                                    {
                                        Name = "VAULT_TOKEN",
                                        ValueFrom = new EnvVarSourceArgs
                                        {
                                            SecretKeyRef = new SecretKeySelectorArgs
                                            {
                                                Name = "vault-root-token",
                                                Key  = "token"
                                            }
                                        }
                                    }
                                },
                                VolumeMounts = new[]
                                {
                                    new VolumeMountArgs { Name = "script", MountPath = "/scripts" }
                                }
                            }
                        },
                        Volumes = new[]
                        {
                            new VolumeArgs
                            {
                                Name      = "script",
                                ConfigMap = new ConfigMapVolumeSourceArgs { Name = "vault-config-script" }
                            }
                        }
                    }
                }
            }
        }, new CustomResourceOptions { Parent = this, DependsOn = { binding, tokenSecret, scriptCm } });

        RegisterOutputs();
    }
}
