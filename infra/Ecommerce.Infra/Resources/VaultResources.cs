using Pulumi;
using Pulumi.Kubernetes.Helm.V3;
using Pulumi.Kubernetes.Types.Inputs.Helm.V3;

namespace Ecommerce.Infra.Resources;

public class VaultResourcesArgs
{
    /// <summary>Version du chart Helm hashicorp/vault. Configurable via vault:version.</summary>
    public string Version { get; set; } = "0.32.0"; // chart 0.32.0 = Vault 1.21.2

    /// <summary>
    /// HA Raft (3 nœuds, quorum) si true ; standalone (1 pod, storage fichier) si false.
    /// Dev/Kind : false (standalone, simple, pas de KMS pour l'auto-unseal).
    /// Prod : true (HA Raft + auto-unseal KMS via SealConfig). Configurable via vault:haEnabled.
    /// </summary>
    public bool HaEnabled { get; set; } = false;

    /// <summary>Nombre de nœuds Raft en HA (impair pour le quorum). Configurable via vault:haReplicas.</summary>
    public int HaReplicas { get; set; } = 3;

    /// <summary>StorageClass des volumes Vault. Dev : standard (local-path). Prod : stockage RÉSEAU.</summary>
    public string StorageClass { get; set; } = "standard";

    /// <summary>Taille du volume de données Vault. Configurable via vault:storageSize.</summary>
    public string StorageSize { get; set; } = "1Gi";

    /// <summary>
    /// Stanza HCL d'auto-unseal (prod). Ex. (AWS) :
    ///   seal "awskms" { region = "eu-west-1" kms_key_id = "arn:aws:kms:..." }
    /// Vide (dev) → Shamir : Vault démarre scellé, descellé par le Job d'init (Phase 2).
    /// Configurable via vault:sealConfig (poser en --secret si la clé est sensible).
    /// </summary>
    public string SealConfig { get; set; } = "";
}

/// <summary>
/// ════════════════════════════════════════════════════════════════════════════
///  HashiCorp Vault — coffre central des secrets (chart officiel hashicorp/vault).
///
///  Topologie (vault:haEnabled) :
///    - DEV  (false) : STANDALONE, storage "file" sur un PVC. 1 pod. Pas de KMS
///                     local → unseal Shamir, automatisé par le Job d'init (Phase 2).
///    - PROD (true)  : HA RAFT (integrated storage), {HaReplicas} nœuds + quorum,
///                     auto-unseal KMS (SealConfig). retry_join → auto-formation du
///                     cluster Raft. service_registration kubernetes (services -active/-standby).
///
///  ⚠️ SkipAwait = true : un Vault fraîchement déployé est SCELLÉ donc NON Ready
///     (la readiness probe échoue tant qu'il est sealed). Sans SkipAwait, pulumi up
///     bloquerait indéfiniment sur ce pod. L'init/unseal (Phase 2) le rend Ready.
///
///  injector.enabled = false : on livre les secrets via le Vault Secrets Operator
///     (VSO, CRD → Secret K8s), pas via le sidecar Agent Injector. CNPG/RabbitMQ
///     exigent un Secret K8s natif → VSO est le bon mécanisme (voir Phase 3).
///
///  Flux de provisionnement :
///    1. VaultResources (ce composant) — Helm install du serveur (SkipAwait).
///    2. [Phase 2] Job d'init/unseal → Vault Ready, clés stockées (dev: Secret K8s).
///    3. [Phase 3] VSO + DB secrets engine dynamique + auth Kubernetes.
/// ════════════════════════════════════════════════════════════════════════════
/// </summary>
public class VaultResources : ComponentResource
{
    /// <summary>Namespace où Vault est déployé (réutilisé par le Job d'init et VSO).</summary>
    public const string VaultNamespace = "vault";

    public VaultResources(string name, VaultResourcesArgs args, ComponentResourceOptions? opts = null)
        : base("ecommerce:infra:VaultResources", name, opts)
    {
        var baseOpts = new CustomResourceOptions { Parent = this };

        // Listener commun (TLS désactivé : le trafic intra-cluster passe par le mesh/CNI ;
        // en prod réelle, activer TLS ou un service mesh mTLS devant Vault).
        const string listener = @"
    ui = true
    listener ""tcp"" {
      tls_disable = 1
      address = ""[::]:8200""
      cluster_address = ""[::]:8201""
    }";

        // ── Config HCL selon la topologie ─────────────────────────────────────
        Dictionary<string, object> serverValues;

        if (args.HaEnabled)
        {
            // PROD : HA Raft. retry_join liste les pods du StatefulSet (vault-0..N) via
            // le Service headless {release}-internal → auto-formation du quorum.
            var retryJoins = string.Concat(Enumerable.Range(0, args.HaReplicas).Select(i => $@"
        retry_join {{
          leader_api_addr = ""http://vault-{i}.vault-internal:8200""
        }}"));

            var raftConfig = $@"{listener}
    storage ""raft"" {{
      path = ""/vault/data""{retryJoins}
    }}
    service_registration ""kubernetes"" {{}}
{args.SealConfig}";

            serverValues = new Dictionary<string, object>
            {
                ["ha"] = new Dictionary<string, object>
                {
                    ["enabled"]  = true,
                    ["replicas"] = args.HaReplicas,
                    ["raft"] = new Dictionary<string, object>
                    {
                        ["enabled"]   = true,
                        ["setNodeId"] = true,
                        ["config"]    = raftConfig
                    }
                }
            };
        }
        else
        {
            // DEV : standalone, storage fichier sur PVC (persiste entre redémarrages,
            // contrairement au mode -dev en mémoire).
            var fileConfig = $@"{listener}
    storage ""file"" {{
      path = ""/vault/data""
    }}";

            serverValues = new Dictionary<string, object>
            {
                ["standalone"] = new Dictionary<string, object>
                {
                    ["enabled"] = true,
                    ["config"]  = fileConfig
                },
                ["ha"] = new Dictionary<string, object> { ["enabled"] = false }
            };
        }

        // Stockage persistant + ressources (commun aux deux topologies).
        serverValues["dataStorage"] = new Dictionary<string, object>
        {
            ["enabled"]      = true,
            ["size"]         = args.StorageSize,
            ["storageClass"] = args.StorageClass
        };
        serverValues["resources"] = new Dictionary<string, object>
        {
            ["requests"] = new Dictionary<string, object> { ["cpu"] = "50m",  ["memory"] = "128Mi" },
            ["limits"]   = new Dictionary<string, object> { ["cpu"] = "250m", ["memory"] = "256Mi" }
        };

        _ = new Release("vault", new ReleaseArgs
        {
            Name            = "vault",
            Chart           = "vault",
            Version         = args.Version,
            Namespace       = VaultNamespace,
            CreateNamespace = true,
            RepositoryOpts  = new RepositoryOptsArgs
            {
                Repo = "https://helm.releases.hashicorp.com"
            },
            // Vault démarre SCELLÉ → jamais Ready avant init/unseal → ne pas attendre.
            SkipAwait = true,
            Values = new InputMap<object>
            {
                ["server"]   = serverValues,
                // VSO (Phase 3) livre les secrets, pas l'Agent Injector.
                ["injector"] = new Dictionary<string, object> { ["enabled"] = false },
                // UI activée, accès via port-forward en dev (pas de NodePort/Ingress ici).
                ["ui"] = new Dictionary<string, object>
                {
                    ["enabled"]     = true,
                    ["serviceType"] = "ClusterIP"
                }
            }
        }, baseOpts);

        RegisterOutputs();
    }
}
