using System.IO;
using Pulumi;
using Pulumi.Kubernetes.Core.V1;
using Pulumi.Kubernetes.Rbac.V1;
using Pulumi.Kubernetes.Types.Inputs.Apps.V1;
using Pulumi.Kubernetes.Types.Inputs.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Meta.V1;
using Pulumi.Kubernetes.Types.Inputs.Rbac.V1;
using DaemonSet = Pulumi.Kubernetes.Apps.V1.DaemonSet;
using Deployment = Pulumi.Kubernetes.Apps.V1.Deployment;

namespace Ecommerce.Infra.Resources;

public class ObservabilityResourcesArgs
{
    public string Namespace { get; set; } = "monitoring";

    // Versions des images — configurables via Pulumi.dev.yaml.
    // Prometheus/Grafana sont désormais fournis par kube-prometheus-stack
    // (KubePrometheusStackResources) — ce composant ne gère plus que OTel + Jaeger.
    public string OtelCollectorVersion { get; set; } = "0.153.0";
    public string JaegerVersion        { get; set; } = "1.76.0";

    // NodePort Jaeger UI exposé sur l'hôte via Kind extraPortMappings (ignoré si IngressEnabled).
    public int JaegerUiNodePort { get; set; } = 30686;

    /// <summary>
    /// Quand true : service Jaeger en ClusterIP (nginx-ingress gère l'accès externe).
    /// Quand false : NodePort — accès direct via localhost (dev Kind).
    /// </summary>
    public bool IngressEnabled { get; set; } = false;
}

public class ObservabilityResources : ComponentResource
{
    /// <summary>Endpoint OTLP gRPC du collecteur — à passer aux APIs via env var.</summary>
    public Output<string> OtelCollectorEndpoint { get; }

    public ObservabilityResources(string name, ObservabilityResourcesArgs args, ComponentResourceOptions? opts = null)
        : base("ecommerce:infra:ObservabilityResources", name, opts)
    {
        var resourceOpts = new CustomResourceOptions { Parent = this };

        // ── Namespace monitoring ─────────────────────────────────────────────────
        var ns = new Namespace("monitoring-ns", new NamespaceArgs
        {
            Metadata = new ObjectMetaArgs { Name = args.Namespace }
        }, resourceOpts);

        // Toutes les ressources ci-dessous dépendent du namespace
        var nsDep = new CustomResourceOptions { Parent = this, DependsOn = ns };

        // ── OpenTelemetry Collector ──────────────────────────────────────────────
        // Reçoit OTLP (gRPC :4317, HTTP :4318) depuis les APIs.
        // Route : traces → Jaeger (OTLP gRPC), métriques → endpoint Prometheus (:8889).
        var otelConfigMap = new ConfigMap("otel-collector-config", new ConfigMapArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "otel-collector-config" },
            Data = new InputMap<string>
            {
                ["config.yaml"] =
@"extensions:
  health_check:
    endpoint: 0.0.0.0:13133

receivers:
  otlp:
    protocols:
      grpc:
        endpoint: 0.0.0.0:4317
      http:
        endpoint: 0.0.0.0:4318

processors:
  # memory_limiter : protège le collector de l'OOM sous afflux de traces (tests de
  # charge). Quand la RAM dépasse le seuil, il rejette/ralentit les nouvelles données
  # plutôt que de gonfler le buffer jusqu'au kill. DOIT être le 1er processor.
  # limit_mib=400 < limite conteneur 512Mi (marge pour heap Go + GC).
  memory_limiter:
    check_interval: 1s
    limit_mib: 400
    spike_limit_mib: 100
  # batch borné : send_batch_max_size évite des batchs géants en mémoire.
  batch:
    timeout: 5s
    send_batch_size: 1024
    send_batch_max_size: 2048

exporters:
  otlp/jaeger:
    endpoint: jaeger.monitoring.svc.cluster.local:4317
    tls:
      insecure: true
  prometheus:
    endpoint: 0.0.0.0:8889
    namespace: ecommerce
    resource_to_telemetry_conversion:
      enabled: true

service:
  extensions: [health_check]
  pipelines:
    traces:
      receivers: [otlp]
      processors: [memory_limiter, batch]
      exporters: [otlp/jaeger]
    metrics:
      receivers: [otlp]
      processors: [memory_limiter, batch]
      exporters: [prometheus]
"
            }
        }, nsDep);

        _ = new Deployment("otel-collector-deploy", new DeploymentArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "otel-collector" },
            Spec = new DeploymentSpecArgs
            {
                Replicas = 1,
                Selector = new LabelSelectorArgs
                {
                    MatchLabels = new InputMap<string> { ["app"] = "otel-collector" }
                },
                Template = new PodTemplateSpecArgs
                {
                    Metadata = new ObjectMetaArgs
                    {
                        Labels = new InputMap<string> { ["app"] = "otel-collector" }
                    },
                    Spec = new PodSpecArgs
                    {
                        Containers = new ContainerArgs
                        {
                            Name            = "otel-collector",
                            Image           = $"otel/opentelemetry-collector-contrib:{args.OtelCollectorVersion}",
                            ImagePullPolicy = "IfNotPresent",
                            Args            = new[] { "--config=/etc/otel/config.yaml" },
                            Ports = new List<ContainerPortArgs>
                            {
                                new() { Name = "otlp-grpc",    ContainerPortValue = 4317  },
                                new() { Name = "otlp-http",    ContainerPortValue = 4318  },
                                new() { Name = "prom-metrics", ContainerPortValue = 8889  },
                                new() { Name = "health",       ContainerPortValue = 13133 }
                            },
                            VolumeMounts = new VolumeMountArgs
                            {
                                Name      = "config",
                                MountPath = "/etc/otel"
                            },
                            // RAM 512Mi : le memory_limiter (limit_mib=400) doit rester sous
                            // la limite conteneur, avec marge pour heap Go + GC. 256Mi
                            // provoquait des OOMKill sous tests de charge (buffer de spans).
                            Resources = new ResourceRequirementsArgs
                            {
                                Requests = new InputMap<string> { ["cpu"] = "50m",  ["memory"] = "128Mi" },
                                Limits   = new InputMap<string> { ["cpu"] = "500m", ["memory"] = "512Mi" }
                            },
                            ReadinessProbe = new ProbeArgs
                            {
                                HttpGet             = new HTTPGetActionArgs { Path = "/", Port = 13133 },
                                InitialDelaySeconds = 5,
                                PeriodSeconds       = 5
                            }
                        },
                        Volumes = new VolumeArgs
                        {
                            Name      = "config",
                            ConfigMap = new ConfigMapVolumeSourceArgs { Name = "otel-collector-config" }
                        }
                    }
                }
            }
        }, new CustomResourceOptions { Parent = this, DependsOn = otelConfigMap });

        _ = new Service("otel-collector-svc", new ServiceArgs
        {
            // Label monitoring=ecommerce : cible des ServiceMonitor (Prometheus Operator).
            Metadata = new ObjectMetaArgs
            {
                Namespace = args.Namespace,
                Name      = "otel-collector",
                Labels    = new InputMap<string> { ["monitoring"] = "ecommerce" }
            },
            Spec = new ServiceSpecArgs
            {
                Selector = new InputMap<string> { ["app"] = "otel-collector" },
                Ports = new List<ServicePortArgs>
                {
                    new() { Name = "otlp-grpc",    Port = 4317,  TargetPort = 4317  },
                    new() { Name = "otlp-http",    Port = 4318,  TargetPort = 4318  },
                    new() { Name = "prom-metrics", Port = 8889,  TargetPort = 8889  }
                }
            }
        }, nsDep);

        // ── Jaeger all-in-one ────────────────────────────────────────────────────
        // Stockage en mémoire (adapté au dev local).
        // Port 4317 : reçoit les traces OTLP depuis le collecteur.
        // Port 16686 : UI web (NodePort 30686 → accessible sur http://localhost:30686).
        _ = new Deployment("jaeger-deploy", new DeploymentArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "jaeger" },
            Spec = new DeploymentSpecArgs
            {
                Replicas = 1,
                Selector = new LabelSelectorArgs
                {
                    MatchLabels = new InputMap<string> { ["app"] = "jaeger" }
                },
                Template = new PodTemplateSpecArgs
                {
                    Metadata = new ObjectMetaArgs
                    {
                        Labels = new InputMap<string> { ["app"] = "jaeger" }
                    },
                    Spec = new PodSpecArgs
                    {
                        Containers = new ContainerArgs
                        {
                            Name            = "jaeger",
                            Image           = $"jaegertracing/all-in-one:{args.JaegerVersion}",
                            ImagePullPolicy = "IfNotPresent",
                            Env = new List<EnvVarArgs>
                            {
                                // Active le récepteur OTLP natif de Jaeger
                                new() { Name = "COLLECTOR_OTLP_ENABLED", Value = "true" },
                                // Limite les traces conservées en mémoire (badger in-memory).
                                // 10000 : tient dans 512Mi même sous tests de charge 1000 VU.
                                // 50000 provoquait des OOMKill (afflux massif de traces OTLP).
                                new() { Name = "MEMORY_MAX_TRACES",      Value = "10000" }
                            },
                            Ports = new List<ContainerPortArgs>
                            {
                                new() { Name = "otlp-grpc", ContainerPortValue = 4317  },
                                new() { Name = "ui",        ContainerPortValue = 16686 }
                            },
                            Resources = new ResourceRequirementsArgs
                            {
                                Requests = new InputMap<string> { ["cpu"] = "50m",  ["memory"] = "128Mi" },
                                Limits   = new InputMap<string> { ["cpu"] = "500m", ["memory"] = "512Mi" }
                            },
                            ReadinessProbe = new ProbeArgs
                            {
                                HttpGet             = new HTTPGetActionArgs { Path = "/", Port = 16686 },
                                InitialDelaySeconds = 5,
                                PeriodSeconds       = 5
                            }
                        }
                    }
                }
            }
        }, nsDep);

        // Service Jaeger : port 4317 (OTLP interne, toujours ClusterIP).
        // En dev : UI exposée en NodePort. En prod : UI exposée via nginx-ingress (ClusterIP).
        _ = new Service("jaeger-svc", new ServiceArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "jaeger" },
            Spec = args.IngressEnabled
                ? new ServiceSpecArgs
                  {
                      Type     = "ClusterIP",
                      Selector = new InputMap<string> { ["app"] = "jaeger" },
                      Ports = new List<ServicePortArgs>
                      {
                          new() { Name = "otlp-grpc", Port = 4317,  TargetPort = 4317  },
                          new() { Name = "ui",        Port = 16686, TargetPort = 16686 }
                      }
                  }
                : new ServiceSpecArgs
                  {
                      Type     = "NodePort",
                      Selector = new InputMap<string> { ["app"] = "jaeger" },
                      Ports = new List<ServicePortArgs>
                      {
                          new() { Name = "otlp-grpc", Port = 4317,  TargetPort = 4317  },
                          new() { Name = "ui",        Port = 16686, TargetPort = 16686, NodePort = args.JaegerUiNodePort }
                      }
                  }
        }, nsDep);


        // ── Métriques (Prometheus + Grafana + node-exporter + kube-state-metrics) ──
        // Fournies par le chart kube-prometheus-stack (KubePrometheusStackResources)
        // + ServiceMonitors. Ce composant ne gère plus que OTel Collector et Jaeger.


        OtelCollectorEndpoint = Output.Create(
            $"http://otel-collector.{args.Namespace}.svc.cluster.local:4317");

        RegisterOutputs(new Dictionary<string, object?>
        {
            ["otelCollectorEndpoint"] = OtelCollectorEndpoint
        });
    }
}
