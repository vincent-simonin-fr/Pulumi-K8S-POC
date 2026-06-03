using System.IO;
using Pulumi;
using Pulumi.Kubernetes.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Meta.V1;

namespace Ecommerce.Infra.Resources;

public class GrafanaDashboardsResourcesArgs
{
    public Input<string> Namespace { get; set; } = "monitoring";
}

/// <summary>
/// Dashboards Grafana pour le mode kube-prometheus-stack.
///
/// Le Grafana du chart utilise un SIDECAR qui charge automatiquement tout ConfigMap
/// portant le label "grafana_dashboard: 1" (configuré dans KubePrometheusStackResources :
/// sidecar.dashboards.label=grafana_dashboard, searchNamespace=ALL).
///
/// Ce composant crée un ConfigMap par dashboard avec ce label → le sidecar les détecte
/// et les injecte dans Grafana, sans provisioning manuel (provider.yaml).
///
/// Différence avec le mode manuel (ObservabilityResources) :
///   Manuel  : 1 gros ConfigMap "grafana-dashboards" monté en volume + provider.yaml.
///   Sidecar : 1 ConfigMap par dashboard, labellisé, découvert dynamiquement.
///
/// Les 6 mêmes fichiers JSON sont réutilisés (datasource uid="prometheus" en dur, déjà
/// fiabilisé). Le chart crée une datasource Prometheus avec uid par défaut — voir note.
/// </summary>
public class GrafanaDashboardsResources : ComponentResource
{
    static readonly string[] Dashboards =
    {
        "services", "database", "runtime", "kubernetes", "cnpg", "rabbitmq"
    };

    public GrafanaDashboardsResources(string name, GrafanaDashboardsResourcesArgs args, ComponentResourceOptions? opts = null)
        : base("ecommerce:infra:GrafanaDashboardsResources", name, opts)
    {
        var baseOpts = new CustomResourceOptions { Parent = this };

        foreach (var dash in Dashboards)
        {
            var json = File.ReadAllText($"../../docker/observability/dashboards/{dash}.json");

            _ = new ConfigMap($"grafana-dashboard-{dash}", new ConfigMapArgs
            {
                Metadata = new ObjectMetaArgs
                {
                    Namespace = args.Namespace,
                    Name      = $"grafana-dashboard-{dash}",
                    // Label détecté par le sidecar Grafana du chart → chargement auto.
                    Labels    = new InputMap<string> { ["grafana_dashboard"] = "1" }
                },
                Data = new InputMap<string> { [$"{dash}.json"] = json }
            }, baseOpts);
        }

        RegisterOutputs();
    }
}
