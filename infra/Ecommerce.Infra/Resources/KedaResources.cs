using Pulumi;
using Pulumi.Command.Local;
// Command = resource avec lifecycle (Create/Update/Delete)
// Run     = invoke statique one-shot, sans lifecycle Pulumi — ne pas utiliser ici
using Pulumi.Kubernetes.Core.V1;
using Pulumi.Kubernetes.Helm.V3;
using Pulumi.Kubernetes.Types.Inputs.Core.V1;
using Pulumi.Kubernetes.Types.Inputs.Helm.V3;
using Pulumi.Kubernetes.Types.Inputs.Meta.V1;

namespace Ecommerce.Infra.Resources;

public class KedaResourcesArgs
{
    /// <summary>Namespace où résident inventory-api, le ScaledObject et la TriggerAuthentication.</summary>
    public Input<string> Namespace { get; set; } = "ecommerce";

    /// <summary>User RabbitMQ — utilisé pour construire l'URL AMQP du trigger.</summary>
    public string RabbitMqUser { get; set; } = "guest";

    /// <summary>Password RabbitMQ — utilisé pour construire l'URL AMQP du trigger.</summary>
    public string RabbitMqPassword { get; set; } = "guest";

    /// <summary>
    /// Nom de la queue RabbitMQ surveillée par KEDA.
    ///
    /// MassTransit + DefaultEndpointNameFormatter + ProductAddedToCartConsumer
    ///   → endpoint : "product-added-to-cart"
    ///
    /// Vérifier dans l'UI RabbitMQ : http://localhost:15672 (onglet Queues)
    /// après un premier démarrage de inventory-api.
    /// Configurable via keda:queueName dans Pulumi.*.yaml.
    /// </summary>
    public string QueueName { get; set; } = "product-added-to-cart";

    /// <summary>
    /// Nombre de messages par réplica actif qui déclenche un scale-out.
    /// Exemple : value=5, replicas=2 → scale-out si queue > 10 messages.
    /// Configurable via keda:queueLength.
    /// </summary>
    public int QueueLength { get; set; } = 5;

    /// <summary>Nombre minimum de réplicas (déjà résolu avec le mode presale si applicable).</summary>
    public int MinReplicas { get; set; } = 1;

    /// <summary>Nombre maximum de réplicas.</summary>
    public int MaxReplicas { get; set; } = 8;

    /// <summary>Fréquence de lecture de la profondeur de queue (secondes). Défaut : 5 s.</summary>
    public int PollingInterval { get; set; } = 5;

    /// <summary>
    /// Délai d'inactivité avant scale-in vers minReplicas (secondes).
    /// 60 s évite les oscillations (scale-in rapide après burst).
    /// </summary>
    public int CooldownPeriod { get; set; } = 60;

    /// <summary>
    /// Fenêtre de stabilisation du HPA interne KEDA pour le scale-in (secondes).
    /// Le HPA Kubernetes applique sa propre fenêtre (300 s par défaut) en plus du
    /// cooldownPeriod KEDA. Cette valeur surcharge ce défaut via
    /// spec.advanced.horizontalPodAutoscalerConfig.behavior.scaleDown.stabilizationWindowSeconds.
    ///
    /// Temps de scale-in effectif ≈ cooldownPeriod + scaleDownWindow.
    /// Valeur recommandée : 120–300 s. En dessous de 60 s, risque de thrashing
    /// si la queue oscille autour du seuil entre deux cycles de polling.
    /// </summary>
    public int ScaleDownWindow { get; set; } = 240;

    /// <summary>Version du chart Helm KEDA (kedacore/keda).</summary>
    public string KedaVersion { get; set; } = "2.17.0";
}

/// <summary>
/// Déploie KEDA (Kubernetes Event-Driven Autoscaling) et configure le scaling
/// réactif d'inventory-api sur la profondeur de la queue RabbitMQ.
///
/// Architecture :
///   RabbitMQ queue depth
///       │
///       ▼  (poll toutes les 5 s)
///     KEDA operator
///       │  ScaledObject → gère un HPA interne
///       ▼
///   inventory-api Deployment (spec.replicas géré par KEDA)
///
/// Avantages vs HPA CPU :
///   - Réaction ~5 s (vs ~75 s pour HPA CPU)
///   - Scale intent-based : réagit AVANT que le CPU sature
///   - Scale-to-zero possible (minReplicas=0 si non critique)
///   - Presale : minReplicas élevé → pods pré-chauffés avant le pic
///
/// Flux de provisionnement Pulumi :
///   1. Helm release KEDA (namespace keda, WaitForJobs=true)
///   2. Secret AMQP dans le namespace ecommerce
///   3. TriggerAuthentication + ScaledObject (CRDs KEDA, DependsOn Helm)
/// </summary>
public class KedaResources : ComponentResource
{
    public KedaResources(string name, KedaResourcesArgs args, ComponentResourceOptions? opts = null)
        : base("ecommerce:infra:KedaResources", name, opts)
    {
        var baseOpts = new CustomResourceOptions { Parent = this };

        // ── 1. KEDA Helm release (namespace : keda) ───────────────────────────
        // WaitForJobs = true : Pulumi attend que tous les pods KEDA soient Ready
        // avant de passer à l'étape suivante. Obligatoire : les CRDs (ScaledObject,
        // TriggerAuthentication) doivent être enregistrées dans l'API K8s avant
        // qu'on tente de les créer.
        // Timeout = 600 s (10 min) — les images KEDA viennent de ghcr.io et peuvent
        // être lentes à puller sur un réseau limité. Si les images sont pré-chargées
        // dans Kind via k8s_complete_launch.cmd (podman pull + kind load), ce timeout
        // ne sera pas atteint. Il sert de garde-fou pour les environnements sans pré-chargement.
        var kedaHelm = new Release("keda", new ReleaseArgs
        {
            Chart = "keda",
            Version = args.KedaVersion,
            Namespace = "keda",
            CreateNamespace = true,
            RepositoryOpts = new RepositoryOptsArgs { Repo = "https://kedacore.github.io/charts" },
            WaitForJobs = true,
            Timeout = 600,
            Values = new InputMap<object>
            {
                // Ressources conservatrices pour Kind (cluster local 1 nœud).
                // En production, ajuster en fonction du nombre de ScaledObjects.
                ["resources"] = new Dictionary<string, object>
                {
                    ["operator"] = new Dictionary<string, object>
                    {
                        ["requests"] = new Dictionary<string, object> { ["cpu"] = "10m", ["memory"] = "64Mi" },
                        ["limits"] = new Dictionary<string, object> { ["cpu"] = "200m", ["memory"] = "128Mi" }
                    },
                    ["metricServer"] = new Dictionary<string, object>
                    {
                        ["requests"] = new Dictionary<string, object> { ["cpu"] = "10m", ["memory"] = "32Mi" },
                        ["limits"] = new Dictionary<string, object> { ["cpu"] = "100m", ["memory"] = "64Mi" }
                    },
                    ["webhooks"] = new Dictionary<string, object>
                    {
                        ["requests"] = new Dictionary<string, object> { ["cpu"] = "5m", ["memory"] = "16Mi" },
                        ["limits"] = new Dictionary<string, object> { ["cpu"] = "50m", ["memory"] = "32Mi" }
                    }
                }
            }
        }, baseOpts);

        // ── 2. Secret AMQP (namespace : ecommerce) ────────────────────────────
        // KEDA se connecte directement à RabbitMQ pour lire la profondeur de la
        // queue. Le Secret contient l'URL AMQP complète (credentials inclus).
        //
        // DNS interne K8s : rabbitmq.ecommerce.svc.cluster.local
        //   → accessible depuis le namespace keda où tourne l'opérateur KEDA.
        //
        // ⚠️  En production : utiliser --secret pour chiffrer ce Secret dans Pulumi.
        var amqpUrl = $"amqp://{args.RabbitMqUser}:{args.RabbitMqPassword}" +
                      "@rabbitmq.ecommerce.svc.cluster.local:5672/";

        var kedaSecret = new Secret("keda-rabbitmq-secret", new SecretArgs
        {
            Metadata = new ObjectMetaArgs { Namespace = args.Namespace, Name = "keda-rabbitmq-secret" },
            Type = "Opaque",
            StringData = new InputMap<string> { ["amqp"] = amqpUrl }
        }, baseOpts);

        // ── 3. TriggerAuthentication + ScaledObject (CRDs KEDA) ──────────────
        // Problème : le provider Pulumi.Kubernetes met en cache la liste des GVK
        // (discovery API /apis) au démarrage. Même après que KEDA Helm installe
        // les CRDs, le cache n'est pas rafraîchi → "failed to determine if GVK
        // is namespaced: keda.sh/v1alpha1, Kind=TriggerAuthentication".
        //
        // Solution : kubectl apply (via Pulumi.Command) contourne ce cache.
        // kubectl interroge directement l'API server et connaît tous les CRDs
        // enregistrés, y compris ceux installés par KEDA pendant ce même pulumi up.
        //
        // DependsOn = { kedaHelm, kedaSecret } garantit que :
        //   1. KEDA Helm est terminé (CRDs enregistrées dans l'API server)
        //   2. Le Secret AMQP existe (référencé par TriggerAuthentication)
        var minReplicas = args.MinReplicas;
        var maxReplicas = args.MaxReplicas;
        var queueName = args.QueueName;
        var queueLength = args.QueueLength;
        var pollInterval = args.PollingInterval;
        var cooldown = args.CooldownPeriod;

        // args.Namespace est un Input<string> → .Apply() produit un Output<string>
        // compatible avec RunArgs.Stdin (Input<string>).
        var kedaYaml = args.Namespace.Apply(ns => $@"apiVersion: keda.sh/v1alpha1
kind: TriggerAuthentication
metadata:
  name: keda-rabbitmq-trigger-auth
  namespace: {ns}
spec:
  secretTargetRef:
  - parameter: host
    name: keda-rabbitmq-secret
    key: amqp
---
apiVersion: keda.sh/v1alpha1
kind: ScaledObject
metadata:
  name: inventory-api
  namespace: {ns}
spec:
  scaleTargetRef:
    apiVersion: apps/v1
    kind: Deployment
    name: inventory-api
  minReplicaCount: {minReplicas}
  maxReplicaCount: {maxReplicas}
  pollingInterval: {pollInterval}
  cooldownPeriod: {cooldown}
  advanced:
    horizontalPodAutoscalerConfig:
      behavior:
        scaleDown:
          stabilizationWindowSeconds: {args.ScaleDownWindow}
          policies:
          - type: Percent
            value: 100
            periodSeconds: 15
  triggers:
  - type: rabbitmq
    metadata:
      protocol: amqp
      queueName: {queueName}
      mode: QueueLength
      value: ""{queueLength}""
    authenticationRef:
      name: keda-rabbitmq-trigger-auth");

        // --server-side : évite les conflits de field manager (idempotent sur les re-runs).
        // Le Stdin contient les deux documents YAML séparés par --- (kubectl les traite nativement).
        // pulumi destroy → Delete : kubectl delete nettoie le ScaledObject et la TriggerAuthentication.
        //
        // Create : supprime d'abord tout HPA natif résiduel pour inventory-api avant de créer
        // le ScaledObject.  L'admission webhook KEDA (vscaledobject.kb.io) refuse le ScaledObject
        // si un HPA gère déjà le même Deployment — ce qui se produit quand on migre d'un HPA natif
        // vers KEDA dans le même pulumi up : Pulumi supprime l'HPA et crée le ScaledObject en
        // parallèle, le webhook peut alors intervenir avant que la suppression soit terminée.
        // --ignore-not-found = no-op si aucun HPA ne préexiste (ré-exécutions idempotentes).
        // Séparateur && (valide cmd.exe ET bash) :
        //   - kubectl delete exit 0 si l'HPA est absent (--ignore-not-found)
        //   - && enchaîne kubectl apply uniquement si delete réussit
        //   - kubectl delete ne lit pas stdin → le pipe reste disponible pour l'apply
        var createCmd = args.Namespace.Apply(ns =>
            $"kubectl delete hpa inventory-api -n {ns} --ignore-not-found && kubectl apply --server-side -f -");

        _ = new Command("keda-crds-apply", new CommandArgs
        {
            Create = createCmd,
            Update = "kubectl apply --server-side -f -",
            Delete = "kubectl delete --ignore-not-found -f -",
            Stdin = kedaYaml
        }, new CustomResourceOptions
        {
            Parent = this,
            DependsOn = new Resource[] { kedaHelm, kedaSecret }
        });

        RegisterOutputs();
    }
}
