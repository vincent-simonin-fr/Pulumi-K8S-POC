using Pulumi;
using Pulumi.Kubernetes.Helm.V3;
using Pulumi.Kubernetes.Types.Inputs.Helm.V3;

namespace Ecommerce.Infra.Resources;

public class VaultSecretsOperatorResourcesArgs
{
    /// <summary>Version du chart Helm hashicorp/vault-secrets-operator. Configurable via vault:vsoVersion.</summary>
    public string Version { get; set; } = "1.4.0";
}

/// <summary>
/// ════════════════════════════════════════════════════════════════════════════
///  Vault Secrets Operator (VSO) — chart officiel hashicorp/vault-secrets-operator.
///
///  Rôle : synchroniser Vault → Secret K8s natif, via des CRDs déclaratives
///    (VaultConnection, VaultAuth, VaultStaticSecret, VaultDynamicSecret…).
///  C'est le mécanisme de LIVRAISON choisi (vs Agent Injector / CSI) car CNPG et
///  RabbitMQ exigent un Secret K8s natif (superuserSecret, app-bootstrap…).
///
///  Ce composant installe UNIQUEMENT l'opérateur (les CRDs VaultConnection/VaultAuth/
///  VaultDynamicSecret seront appliquées ensuite — Phase 3d — une fois Vault configuré).
///
///  Flux de provisionnement Vault :
///    1. VaultResources               — serveur Vault (Helm), scellé puis unseal (init).
///    2. VaultSecretsOperatorResources (ce composant) — opérateur VSO.
///    3. [Job] config de Vault         — auth k8s + DB secrets engine + rôles + policies.
///    4. [CRDs VSO]                    — VaultConnection/VaultAuth/VaultDynamicSecret
///                                       → Secret K8s peuplé et rotaté.
/// ════════════════════════════════════════════════════════════════════════════
/// </summary>
public class VaultSecretsOperatorResources : ComponentResource
{
    public VaultSecretsOperatorResources(string name, VaultSecretsOperatorResourcesArgs args, ComponentResourceOptions? opts = null)
        : base("ecommerce:infra:VaultSecretsOperatorResources", name, opts)
    {
        var baseOpts = new CustomResourceOptions { Parent = this };

        // VSO dans son propre namespace. WaitForJobs : l'opérateur doit être Ready
        // (CRDs enregistrées) avant qu'on applique les VaultConnection/VaultAuth/etc.
        _ = new Release("vault-secrets-operator", new ReleaseArgs
        {
            Name            = "vault-secrets-operator",
            Chart           = "vault-secrets-operator",
            Version         = args.Version,
            Namespace       = "vault-secrets-operator-system",
            CreateNamespace = true,
            RepositoryOpts  = new RepositoryOptsArgs
            {
                Repo = "https://helm.releases.hashicorp.com"
            },
            WaitForJobs = true,
            Timeout     = 300,
            Values = new InputMap<object>
            {
                // Ressources conservatrices pour Kind.
                ["controller"] = new Dictionary<string, object>
                {
                    ["manager"] = new Dictionary<string, object>
                    {
                        ["resources"] = new Dictionary<string, object>
                        {
                            ["requests"] = new Dictionary<string, object> { ["cpu"] = "25m",  ["memory"] = "64Mi" },
                            ["limits"]   = new Dictionary<string, object> { ["cpu"] = "200m", ["memory"] = "256Mi" }
                        }
                    }
                }
            }
        }, baseOpts);

        RegisterOutputs();
    }
}
