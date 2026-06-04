using Pulumi;
using Pulumi.Command.Local;
using Pulumi.Kubernetes.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Apps.V1;
using Pulumi.Kubernetes.Types.Inputs.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Meta.V1;
using Deployment = Pulumi.Kubernetes.Apps.V1.Deployment;

namespace Ecommerce.Infra.Resources;

public class MessagingResourcesArgs
{
    public Input<string> Namespace { get; set; } = "ecommerce";

    /// <summary>
    /// Mode HA : false = Deployment simple (dev Kind, 1 nœud),
    /// true = RabbitmqCluster via l'opérateur (prod, cluster quorum N nœuds).
    /// Configurable via rabbitmq:cluster dans Pulumi.*.yaml.
    /// </summary>
    public bool UseCluster { get; set; } = false;

    /// <summary>
    /// Nombre de nœuds RabbitMQ en mode cluster (impair : 3 ou 5 pour le quorum Raft).
    /// Ignoré en mode Deployment. Configurable via rabbitmq:replicas.
    /// </summary>
    public int Replicas { get; set; } = 3;

    /// <summary>User RabbitMQ — imposé au cluster via le secret default-user pré-créé.</summary>
    public string RabbitMqUser { get; set; } = "guest";

    /// <summary>Password RabbitMQ — imposé au cluster via le secret default-user pré-créé.</summary>
    public string RabbitMqPassword { get; set; } = "guest";

    /// <summary>StorageClass des volumes RabbitMQ (mode cluster). Prod : stockage réseau.</summary>
    public string StorageClass { get; set; } = "standard";

    /// <summary>Taille des volumes RabbitMQ par nœud (mode cluster).</summary>
    public string StorageSize { get; set; } = "5Gi";

    /// <summary>Image RabbitMQ (cohérente entre Deployment dev et cluster prod).</summary>
    public string Image { get; set; } = "rabbitmq:4.3.1-management-alpine";
}

/// <summary>
/// Déploie RabbitMQ selon deux modes :
///
///   Dev (UseCluster=false)  → Deployment simple 1 réplica (Kind mono-nœud).
///   Prod (UseCluster=true)  → RabbitmqCluster via l'opérateur (cluster quorum N nœuds).
///
/// Dans les DEUX cas, le Service exposé s'appelle "rabbitmq" et les apps s'y
/// connectent de façon identique (RabbitMQ__Host=rabbitmq) → aucun changement côté
/// order-api / inventory-api selon le mode.
///
/// Mode cluster — credentials imposés (pas générés par l'opérateur) :
///   L'opérateur génère normalement {cluster}-default-user avec des credentials
///   aléatoires. On pré-crée ce secret avec NOS credentials (cohérents avec ESO) :
///   l'opérateur l'adopte au lieu d'en générer. Les apps continuent de lire
///   rabbitmq-credentials comme en dev → flux inchangé. Même logique que le
///   bootstrap secret CNPG (DatabaseResources).
///
/// Workaround GVK cache Pulumi (mode cluster) :
///   La CRD RabbitmqCluster est appliquée via kubectl (Pulumi.Command), comme les
///   Cluster/Pooler CNPG, car elle n'est pas dans le cache GVK du provider.
/// </summary>
public class MessagingResources : ComponentResource
{
    public Output<string> RabbitMqServiceName { get; }

    public MessagingResources(string name, MessagingResourcesArgs args, ComponentResourceOptions? opts = null)
        : base("ecommerce:infra:MessagingResources", name, opts)
    {
        var resourceOpts = new CustomResourceOptions { Parent = this };

        if (args.UseCluster)
            CreateRabbitmqCluster(args, resourceOpts);
        else
            CreateRabbitmqDeployment(args, resourceOpts);

        // Nom de Service identique dans les deux modes → apps agnostiques au mode.
        RabbitMqServiceName = Output.Create("rabbitmq");
        RegisterOutputs(new Dictionary<string, object?> { ["rabbitMqServiceName"] = RabbitMqServiceName });
    }

    // ── Mode DEV : Deployment simple 1 réplica ────────────────────────────────
    private void CreateRabbitmqDeployment(MessagingResourcesArgs args, CustomResourceOptions resourceOpts)
    {
        _ = new Deployment("rabbitmq", new DeploymentArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "rabbitmq" },
            Spec = new DeploymentSpecArgs
            {
                Replicas = 1,
                Selector = new LabelSelectorArgs
                {
                    MatchLabels = new InputMap<string> { ["app"] = "rabbitmq" }
                },
                Template = new PodTemplateSpecArgs
                {
                    Metadata = new ObjectMetaArgs
                    {
                        Labels = new InputMap<string> { ["app"] = "rabbitmq" }
                    },
                    Spec = new PodSpecArgs
                    {
                        Containers = new ContainerArgs
                        {
                            Name = "rabbitmq",
                            Image = args.Image,
                            ImagePullPolicy = "IfNotPresent",
                            Ports = new List<ContainerPortArgs>
                            {
                                new() { ContainerPortValue = 5672, Name = "amqp" },
                                new() { ContainerPortValue = 15672, Name = "management" },
                                // Métriques Prometheus (plugin rabbitmq_prometheus, activé par défaut).
                                new() { ContainerPortValue = 15692, Name = "prometheus" }
                            },
                            // RABBITMQ_DEFAULT_USER / RABBITMQ_DEFAULT_PASS injectés depuis le secret ESO.
                            EnvFrom = new EnvFromSourceArgs
                            {
                                SecretRef = new SecretEnvSourceArgs { Name = SecretsResources.RabbitMqSecretName }
                            },
                            Env = new List<EnvVarArgs>
                            {
                                new() { Name = "RABBITMQ_ERLANG_COOKIE", Value = "ecommerce-secret-cookie" },
                                new() { Name = "ERL_FLAGS",              Value = "-setcookie ecommerce-secret-cookie" }
                            },
                            // Test TCP du port AMQP : rabbitmq-diagnostics ping échoue souvent en K8s
                            // (Erlang distribution ne résout pas le hostname du pod).
                            ReadinessProbe = new ProbeArgs
                            {
                                TcpSocket           = new TCPSocketActionArgs { Port = 5672 },
                                InitialDelaySeconds = 15,
                                PeriodSeconds       = 5,
                                FailureThreshold    = 6
                            },
                            LivenessProbe = new ProbeArgs
                            {
                                TcpSocket           = new TCPSocketActionArgs { Port = 5672 },
                                InitialDelaySeconds = 60,
                                PeriodSeconds       = 30,
                                FailureThreshold    = 3
                            }
                        }
                    }
                }
            }
        }, resourceOpts);

        _ = new Service("rabbitmq-svc", new ServiceArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "rabbitmq" },
            Spec = new ServiceSpecArgs
            {
                Selector = new InputMap<string> { ["app"] = "rabbitmq" },
                Ports = new List<ServicePortArgs>
                {
                    new() { Name = "amqp",       Port = 5672,  TargetPort = 5672  },
                    new() { Name = "management", Port = 15672, TargetPort = 15672 }
                }
            }
        }, resourceOpts);

        // Métriques Prometheus : pods étiquetés app=rabbitmq (Deployment), 1 pod → ClusterIP normal.
        CreateRabbitmqMetricsService(args, resourceOpts, "app", "rabbitmq", headless: false);
    }

    // ── Mode PROD : RabbitmqCluster via l'opérateur ───────────────────────────
    private void CreateRabbitmqCluster(MessagingResourcesArgs args, CustomResourceOptions resourceOpts)
    {
        // Secret default-user imposé : l'opérateur l'adopte au lieu de générer des
        // credentials aléatoires. Format attendu par le RabbitMQ Cluster Operator :
        //   - username / password : lus par les clients et le management plugin
        //   - default_user.conf   : fichier de conf injecté dans RabbitMQ au boot,
        //     définit l'utilisateur par défaut (sinon RabbitMQ démarre avec guest).
        // Nom OBLIGATOIRE : {cluster}-default-user (l'opérateur cherche ce nom exact).
        var defaultUserConf = $"default_user = {args.RabbitMqUser}\ndefault_pass = {args.RabbitMqPassword}\n";

        var defaultUserSecret = new Secret("rabbitmq-default-user", new SecretArgs
        {
            Metadata = new ObjectMetaArgs
            {
                Namespace = args.Namespace,
                Name      = "rabbitmq-default-user"
            },
            StringData = new InputMap<string>
            {
                ["username"]          = args.RabbitMqUser,
                ["password"]          = args.RabbitMqPassword,
                ["default_user.conf"] = defaultUserConf
            }
        }, resourceOpts);

        // CRD RabbitmqCluster — appliquée via kubectl (cache GVK, comme CNPG).
        //
        // - replicas N (impair) : quorum Raft. Perte de (N-1)/2 nœuds tolérée.
        // - persistence : volumes par nœud (stockage réseau en prod).
        // - additionalConfig : quorum queues par défaut → les queues MassTransit
        //   (product-added-to-cart...) sont répliquées entre les nœuds.
        // - override.statefulSet : anti-affinité pour répartir les nœuds sur des
        //   hôtes distincts (sinon le quorum ne protège de rien si tout est colocalisé).
        var yaml = args.Namespace.Apply(ns => $@"apiVersion: rabbitmq.com/v1beta1
kind: RabbitmqCluster
metadata:
  name: rabbitmq
  namespace: {ns}
spec:
  replicas: {args.Replicas}
  image: {args.Image}
  persistence:
    storageClassName: {args.StorageClass}
    storage: {args.StorageSize}
  rabbitmq:
    additionalConfig: |
      # Toutes les queues classiques deviennent quorum (répliquées par Raft).
      default_queue_type = quorum
      # Le management plugin écoute sur 15672 (exposé par le Service de l'opérateur).
  override:
    statefulSet:
      spec:
        template:
          spec:
            # containers est REQUIS par la validation du schéma StatefulSet dès qu'on
            # override template.spec. On redéclare le conteneur 'rabbitmq' par son nom
            # (l'opérateur fusionne avec sa définition complète : image, ports, probes).
            containers:
              - name: rabbitmq
            affinity:
              podAntiAffinity:
                preferredDuringSchedulingIgnoredDuringExecution:
                  - weight: 100
                    podAffinityTerm:
                      topologyKey: kubernetes.io/hostname
                      labelSelector:
                        matchLabels:
                          app.kubernetes.io/name: rabbitmq");

        // DependsOn defaultUserSecret : le secret doit exister avant que l'opérateur
        // réconcilie le cluster, sinon il génère des credentials aléatoires.
        _ = new Command("rabbitmq-cluster-apply", new CommandArgs
        {
            Create = "kubectl apply --server-side -f -",
            Update = "kubectl apply --server-side -f -",
            Delete = "kubectl delete --ignore-not-found -f -",
            Stdin  = yaml
        }, new CustomResourceOptions
        {
            Parent    = this,
            DependsOn = { defaultUserSecret }
        });

        // L'opérateur crée automatiquement un Service "rabbitmq" (amqp:5672 + management:15672),
        // identique au mode Deployment.
        // Métriques : pods étiquetés app.kubernetes.io/name=rabbitmq (opérateur), N nœuds
        // → headless pour scraper chaque nœud individuellement.
        CreateRabbitmqMetricsService(args, resourceOpts, "app.kubernetes.io/name", "rabbitmq", headless: true);
    }

    // ── Service métriques Prometheus (partagé Deployment / cluster) ────────────
    // Ciblé par le ServiceMonitor 'rabbitmq' : label monitoring=ecommerce + port nommé
    // 'prometheus' (15692, plugin rabbitmq_prometheus).
    //   selectorKey/selectorVal : label des pods RabbitMQ — diffère selon le créateur
    //     (nous en Deployment : app=rabbitmq ; l'opérateur en cluster :
    //      app.kubernetes.io/name=rabbitmq).
    //   headless : true en cluster — N nœuds exposent CHACUN leurs métriques sur 15692,
    //     un ClusterIP load-balancerait sur un nœud au hasard ; le headless (clusterIP:None)
    //     résout vers toutes les IP de pods → Prometheus scrape chaque nœud. false en dev (1 pod).
    private void CreateRabbitmqMetricsService(
        MessagingResourcesArgs args, CustomResourceOptions opts,
        string selectorKey, string selectorVal, bool headless)
    {
        var spec = new ServiceSpecArgs
        {
            Selector = new InputMap<string> { [selectorKey] = selectorVal },
            Ports    = new ServicePortArgs { Name = "prometheus", Port = 15692, TargetPort = 15692 }
        };
        if (headless) spec.ClusterIP = "None";   // scrape par nœud (N pods) ; sinon ClusterIP par défaut

        _ = new Service("rabbitmq-metrics", new ServiceArgs
        {
            Metadata = new ObjectMetaArgs
            {
                Namespace = args.Namespace,
                Name      = "rabbitmq-metrics",
                Labels    = new InputMap<string> { ["monitoring"] = "ecommerce" }
            },
            Spec = spec
        }, opts);
    }
}
