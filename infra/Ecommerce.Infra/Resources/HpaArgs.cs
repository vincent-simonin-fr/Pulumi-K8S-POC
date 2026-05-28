namespace Ecommerce.Infra.Resources;

/// <summary>
/// Configuration d'un HorizontalPodAutoscaler.
/// Requiert que le Metrics Server soit installé dans le cluster.
/// </summary>
public class HpaArgs
{
    /// <summary>Active ou désactive l'HPA pour ce service.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Nombre minimum de réplicas (plancher).</summary>
    public int MinReplicas { get; set; } = 1;

    /// <summary>Nombre maximum de réplicas (plafond).</summary>
    public int MaxReplicas { get; set; } = 4;

    /// <summary>Seuil CPU (%) déclenchant le scale-out. Requiert requests.cpu sur le container.</summary>
    public int CpuPercent { get; set; } = 70;

    /// <summary>
    /// Seuil mémoire (%) déclenchant le scale-out. Optionnel.
    /// ⚠️ Attention : .NET ne libère pas toujours la mémoire après une charge — préférer le CPU.
    /// </summary>
    public int? MemoryPercent { get; set; } = null;
}
