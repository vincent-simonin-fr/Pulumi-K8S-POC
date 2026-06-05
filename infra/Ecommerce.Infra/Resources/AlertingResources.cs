using System.Globalization;
using Pulumi;
using Pulumi.Command.Local;

namespace Ecommerce.Infra.Resources;

public class AlertingResourcesArgs
{
    /// <summary>Namespace où vit Prometheus (la PrometheusRule y est créée).</summary>
    public string MonitoringNamespace { get; set; } = "monitoring";

    /// <summary>Seuil de latence p95 applicative (ms) au-delà duquel on alerte.</summary>
    public int P95LatencyMs { get; set; } = 1000;

    /// <summary>Seuil d'alerte sur le pool PostgreSQL (% de max_connections).</summary>
    public int PgPoolWarnPct { get; set; } = 80;
}

/// <summary>
/// ════════════════════════════════════════════════════════════════════════════
///  PrometheusRule — les règles d'alerte découvertes par le Prometheus Operator.
///
///  Pendant : KubePrometheusStackResources active Alertmanager (récepteur Slack routé
///  par sévérité). Ce fichier fournit les RÈGLES qui passent en `firing`.
///
///  Découverte : le Prometheus du chart a `ruleSelectorNilUsesHelmValues=false`
///  → il sélectionne TOUTES les PrometheusRule. On ajoute quand même le label
///  `release: kube-prometheus-stack` par robustesse.
///
///  Appliqué via kubectl (Pulumi.Command) comme les ServiceMonitors : la CRD
///  PrometheusRule est installée par le chart pendant ce même pulumi up → absente
///  du cache GVK du provider Kubernetes.
///
///  PromQL CALIBRÉ sur les métriques/labels RÉELS du cluster (vérifiés via l'API
///  Prometheus) :
///    - kube_pod_container_status_waiting_reason{reason="CrashLoopBackOff"}  (kube-state-metrics)
///    - kube_pod_status_ready{condition="false"}                              (kube-state-metrics)
///    - cnpg_collector_up / cnpg_pg_replication_in_recovery / _lag           (CNPG, label `cluster`)
///    - cnpg_collector_last_{available,failed}_backup_timestamp              (CNPG backups)
///    - up{job="rabbitmq-metrics"}                                           (RabbitMQ exporter)
///    - ecommerce_http_server_request_duration_seconds_bucket{service_name,le} (OTel apps)
///    - pg_stat_activity_count / pg_settings_max_connections (label `server`) (postgres_exporter)
/// ════════════════════════════════════════════════════════════════════════════
/// </summary>
public class AlertingResources : ComponentResource
{
    public AlertingResources(string name, AlertingResourcesArgs args, ComponentResourceOptions? opts = null)
        : base("ecommerce:infra:AlertingResources", name, opts)
    {
        // Seuils interpolés dans le PromQL (placeholders pour garder le YAML lisible).
        var p95Seconds = (args.P95LatencyMs / 1000.0).ToString(CultureInfo.InvariantCulture);
        var poolFrac   = (args.PgPoolWarnPct / 100.0).ToString(CultureInfo.InvariantCulture);

        var yaml = @"
apiVersion: monitoring.coreos.com/v1
kind: PrometheusRule
metadata:
  name: ecommerce-alerts
  namespace: monitoring
  labels:
    release: kube-prometheus-stack
spec:
  groups:
    # ── Infrastructure ──────────────────────────────────────────────────────
    - name: ecommerce.infra
      rules:
        - alert: PodCrashLooping
          expr: max by (namespace, pod, container) (kube_pod_container_status_waiting_reason{reason=""CrashLoopBackOff"", namespace=""ecommerce""}) > 0
          for: 5m
          labels: { severity: critical }
          annotations:
            summary: ""Pod {{ $labels.pod }} en CrashLoopBackOff""
            description: ""Le conteneur {{ $labels.container }} du pod {{ $labels.namespace }}/{{ $labels.pod }} redémarre en boucle depuis 5 min.""
        - alert: PodNotReady
          expr: max by (namespace, pod) (kube_pod_status_ready{condition=""false"", namespace=""ecommerce""}) == 1
          for: 15m
          labels: { severity: warning }
          annotations:
            summary: ""Pod {{ $labels.pod }} non Ready""
            description: ""Le pod {{ $labels.namespace }}/{{ $labels.pod }} n'est pas Ready depuis 15 min.""
        - alert: CNPGInstanceUnreachable
          expr: cnpg_collector_up == 0
          for: 2m
          labels: { severity: critical }
          annotations:
            summary: ""Instance CNPG injoignable ({{ $labels.cluster }})""
            description: ""Le collector CNPG du cluster {{ $labels.cluster }} (pod {{ $labels.pod }}) ne répond plus depuis 2 min.""
        - alert: CNPGNoPrimary
          expr: min by (cluster) (cnpg_pg_replication_in_recovery) == 1
          for: 2m
          labels: { severity: critical }
          annotations:
            summary: ""Aucun primary CNPG ({{ $labels.cluster }})""
            description: ""Toutes les instances du cluster {{ $labels.cluster }} sont en recovery : pas de primary writable (échec de failover ?).""
        - alert: CNPGReplicationLag
          expr: max by (cluster) (cnpg_pg_replication_lag) > 30
          for: 5m
          labels: { severity: warning }
          annotations:
            summary: ""Réplication CNPG en retard ({{ $labels.cluster }})""
            description: ""Le lag de réplication du cluster {{ $labels.cluster }} dépasse 30s depuis 5 min.""
        - alert: CNPGBackupFailing
          expr: cnpg_collector_last_failed_backup_timestamp > cnpg_collector_last_available_backup_timestamp
          for: 15m
          labels: { severity: warning }
          annotations:
            summary: ""Backup CNPG en échec ({{ $labels.cluster }})""
            description: ""Le dernier backup du cluster {{ $labels.cluster }} a échoué (plus récent que le dernier backup disponible). PITR compromis — cf. docs/backups.md.""
        - alert: RabbitMQDown
          expr: absent(up{job=""rabbitmq-metrics""} == 1)
          for: 2m
          labels: { severity: critical }
          annotations:
            summary: ""RabbitMQ indisponible""
            description: ""Aucun nœud RabbitMQ sain n'est scrapé depuis 2 min (nœud down ou quorum perdu en mode cluster).""
    # ── Applicatif ──────────────────────────────────────────────────────────
    - name: ecommerce.app
      rules:
        - alert: HighRequestLatencyP95
          expr: histogram_quantile(0.95, sum by (le, service_name) (rate(ecommerce_http_server_request_duration_seconds_bucket[5m]))) > __P95SECONDS__
          for: 10m
          labels: { severity: warning }
          annotations:
            summary: ""Latence p95 élevée ({{ $labels.service_name }})""
            description: ""La latence p95 de {{ $labels.service_name }} dépasse __P95MS__ ms depuis 10 min (valeur: {{ $value | humanizeDuration }}).""
        - alert: PostgresConnectionPoolSaturation
          expr: (sum by (server) (pg_stat_activity_count)) / on(server) group_left() (max by (server) (pg_settings_max_connections)) > __POOLFRAC__
          for: 5m
          labels: { severity: warning }
          annotations:
            summary: ""Pool PostgreSQL proche de la saturation ({{ $labels.server }})""
            description: ""Les connexions de {{ $labels.server }} dépassent __POOLPCT__% de max_connections depuis 5 min (ratio: {{ $value | humanizePercentage }}).""
"
            .Replace("__P95SECONDS__", p95Seconds)
            .Replace("__P95MS__",      args.P95LatencyMs.ToString(CultureInfo.InvariantCulture))
            .Replace("__POOLFRAC__",   poolFrac)
            .Replace("__POOLPCT__",    args.PgPoolWarnPct.ToString(CultureInfo.InvariantCulture));

        _ = new Command("prometheus-rules-apply", new CommandArgs
        {
            Create = "kubectl apply --server-side -f -",
            Update = "kubectl apply --server-side -f -",
            Delete = "kubectl delete --ignore-not-found -f -",
            Stdin  = yaml
        }, new CustomResourceOptions { Parent = this });

        RegisterOutputs();
    }
}
