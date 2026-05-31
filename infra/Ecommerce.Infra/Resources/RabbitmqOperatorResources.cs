using Pulumi;
using Pulumi.Command.Local;

namespace Ecommerce.Infra.Resources;

public class RabbitmqOperatorResourcesArgs
{
    /// <summary>
    /// Override OPTIONNEL de l'image opérateur. Configurable via rabbitmq:operatorImage.
    ///
    /// Vide (défaut) = utiliser l'image embarquée dans le manifeste officiel
    /// (ghcr.io/rabbitmq/cluster-operator:&lt;version du manifeste&gt;). C'est le cas normal.
    ///
    /// ⚠️ N'override QUE par une image OFFICIELLE compatible (ghcr.io/rabbitmq/...).
    /// L'image legacy Docker Hub (rabbitmqoperator/cluster-operator) a des flags
    /// incompatibles avec le manifeste actuel (--health-probe-bind-address) et crashe.
    /// Pour épingler une version : rabbitmq:operatorImage=ghcr.io/rabbitmq/cluster-operator:2.21.0
    /// </summary>
    public string OperatorImage { get; set; } = "";

    /// <summary>
    /// URL du manifeste d'installation officiel. Configurable via rabbitmq:operatorManifest.
    /// Défaut : latest release du repo github.com/rabbitmq/cluster-operator (épingle déjà
    /// une image ghcr.io/rabbitmq/cluster-operator compatible).
    /// </summary>
    public string ManifestUrl { get; set; } =
        "https://github.com/rabbitmq/cluster-operator/releases/latest/download/cluster-operator.yml";
}

/// <summary>
/// Installe le RabbitMQ Cluster Operator OFFICIEL via son manifeste d'installation.
///
/// Contrairement à CNPG (chart Helm), l'opérateur officiel RabbitMQ se distribue
/// comme un manifeste YAML unique (cluster-operator.yml) qui crée :
///   - le namespace rabbitmq-system
///   - la CRD RabbitmqCluster
///   - l'opérateur (Deployment, image ghcr.io/rabbitmq/cluster-operator) + RBAC
///
/// On l'applique via kubectl (Pulumi.Command) — pas via le provider Pulumi.Kubernetes —
/// pour la même raison que CNPG/KEDA : la CRD RabbitmqCluster n'est pas dans le cache
/// GVK du provider au démarrage du programme. kubectl interroge directement l'API server.
///
/// Image : le manifeste embarque DÉJÀ la bonne image officielle. On ne l'override que
/// si OperatorImage est renseigné (épinglage de version). Par défaut, on garde celle du
/// manifeste — c'est le comportement sûr.
///
/// Flux de provisionnement :
///   1. RabbitmqOperatorResources (ce composant) — kubectl apply du manifeste officiel
///   2. MessagingResources (DependsOn cet opérateur) en mode cluster
///      → kubectl apply RabbitmqCluster YAML via Pulumi.Command
/// </summary>
public class RabbitmqOperatorResources : ComponentResource
{
    public RabbitmqOperatorResources(string name, RabbitmqOperatorResourcesArgs args, ComponentResourceOptions? opts = null)
        : base("ecommerce:infra:RabbitmqOperatorResources", name, opts)
    {
        var baseOpts = new CustomResourceOptions { Parent = this };

        // ── Apply du manifeste officiel + attente du rollout ──────────────────
        // Create/Update : applique (idempotent). Delete : retire l'opérateur + CRD.
        // --server-side : évite les conflits de field manager sur les re-runs.
        //
        // Override d'image OPTIONNEL : seulement si OperatorImage est renseigné.
        // Sinon on garde l'image du manifeste (cas normal, sûr).
        var setImage = string.IsNullOrWhiteSpace(args.OperatorImage)
            ? ""
            : $"kubectl set image deployment/rabbitmq-cluster-operator " +
              $"operator={args.OperatorImage} -n rabbitmq-system && ";

        var applyCmd =
            $"kubectl apply --server-side -f {args.ManifestUrl} && " +
            setImage +
            "kubectl rollout status deployment/rabbitmq-cluster-operator -n rabbitmq-system --timeout=180s";

        _ = new Command("rabbitmq-operator-apply", new CommandArgs
        {
            Create = applyCmd,
            Update = applyCmd,
            Delete = $"kubectl delete --ignore-not-found -f {args.ManifestUrl}"
        }, baseOpts);

        RegisterOutputs();
    }
}
