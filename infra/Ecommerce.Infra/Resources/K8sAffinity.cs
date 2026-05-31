using Pulumi.Kubernetes.Types.Inputs.Core.V1;

namespace Ecommerce.Infra.Resources;

/// <summary>
/// Helpers de placement des pods (affinité / anti-affinité) pour la HA multi-nœuds.
///
/// Anti-affinité SOFT (preferredDuringScheduling) volontairement :
///   - Dev (Kind mono-nœud) : la préférence ne peut pas être satisfaite → les pods
///     sont quand même schedulés sur l'unique nœud. Aucun pod ne reste Pending.
///   - Prod (multi-nœuds) : le scheduler répartit les réplicas d'un même service sur
///     des nœuds distincts → la perte d'un nœud ne supprime pas TOUS les pods du service.
///
/// Pourquoi pas HARD (requiredDuringScheduling) ?
///   En hard, si le nb de réplicas dépasse le nb de nœuds, les pods en trop restent
///   Pending. En soft, le scheduler "fait au mieux" : il répartit tant qu'il peut, puis
///   colocalise si nécessaire. C'est le bon compromis HA / disponibilité.
///   Pour forcer le hard en prod (clusters avec assez de nœuds), voir le paramètre weight.
/// </summary>
public static class K8sAffinity
{
    /// <summary>
    /// Construit une anti-affinité soft qui répartit les pods portant le label
    /// <c>app={appLabel}</c> sur des nœuds distincts (topologyKey kubernetes.io/hostname).
    /// </summary>
    public static AffinityArgs SpreadAcrossNodes(string appLabel) =>
        new()
        {
            PodAntiAffinity = new PodAntiAffinityArgs
            {
                PreferredDuringSchedulingIgnoredDuringExecution = new WeightedPodAffinityTermArgs
                {
                    // Weight 100 : préférence forte. Le scheduler maximise la dispersion.
                    Weight = 100,
                    PodAffinityTerm = new PodAffinityTermArgs
                    {
                        // 1 nœud = 1 "topologie" → 2 pods du même app évitent le même nœud.
                        TopologyKey = "kubernetes.io/hostname",
                        LabelSelector = new Pulumi.Kubernetes.Types.Inputs.Meta.V1.LabelSelectorArgs
                        {
                            MatchLabels = new Pulumi.InputMap<string> { ["app"] = appLabel }
                        }
                    }
                }
            }
        };
}
