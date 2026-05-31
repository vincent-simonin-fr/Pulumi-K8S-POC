using Nuke.Common;

/// <summary>
/// Opérations multi-étapes : bascule GitOps, pré-scaling (presale).
/// Les diagnostics (get pods/hpa, top, logs) se font directement en kubectl.
/// </summary>
partial class Build
{
    // ── Bascule GitOps ────────────────────────────────────────────────────────
    Target GitopsOn => _ => _
        .Description("Active le mode GitOps (apps rendues en YAML, déployées par ArgoCD).")
        .Executes(() => Pulumi("config set gitops:enabled true"));

    Target GitopsOff => _ => _
        .Description("Désactive GitOps (Pulumi déploie les apps directement).")
        .Executes(() => Pulumi("config set gitops:enabled false"));

    // ── Presale (pré-scaling avant flash sale) ─────────────────────────────────
    // inventory-api : KEDA ScaledObject (minReplicaCount)
    // order-api / gateway : HPA natif (minReplicas)
    const int InventoryPresale = 3, OrderPresale = 3, GatewayPresale = 2;
    const int InventoryNominal = 1, OrderNominal = 1, GatewayNominal = 1;

    Target PresaleStart => _ => _
        .Description("Pré-scale les pods avant un flash sale (KEDA + HPA).")
        .Executes(() =>
        {
            PatchScaledObject("inventory-api", InventoryPresale);
            PatchHpa("order-api", OrderPresale);
            PatchHpa("gateway", GatewayPresale);

            Run("kubectl", $"rollout status deployment/inventory-api -n {Namespace} --timeout=120s");
            Run("kubectl", $"rollout status deployment/order-api -n {Namespace} --timeout=120s");
            Run("kubectl", $"rollout status deployment/gateway -n {Namespace} --timeout=120s");

            Serilog.Log.Information("Prêt pour le flash sale. Après l'event : dotnet nuke PresaleStop");
        });

    Target PresaleStop => _ => _
        .Description("Rétablit les minReplicas nominaux après un flash sale.")
        .Executes(() =>
        {
            PatchScaledObject("inventory-api", InventoryNominal);
            PatchHpa("order-api", OrderNominal);
            PatchHpa("gateway", GatewayNominal);
            Serilog.Log.Information("Rétabli. KEDA/HPA réduiront les pods selon la charge réelle.");
        });

    void PatchScaledObject(string name, int min) =>
        Run("kubectl",
            $"patch scaledobject {name} -n {Namespace} --type=merge " +
            $"-p \"{{\\\"spec\\\":{{\\\"minReplicaCount\\\":{min}}}}}\"");

    void PatchHpa(string name, int min) =>
        Run("kubectl",
            $"patch hpa {name} -n {Namespace} --type=merge " +
            $"-p \"{{\\\"spec\\\":{{\\\"minReplicas\\\":{min}}}}}\"");
}
