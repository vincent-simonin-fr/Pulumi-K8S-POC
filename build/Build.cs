using System;
using System.Collections.Generic;
using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.Tooling.ProcessTasks;

/// <summary>
/// Automatisation des orchestrations multi-étapes du projet.
///
/// Périmètre volontairement restreint aux enchaînements fastidieux à faire à la
/// main. Les commandes pulumi (up, preview, destroy, config) et kubectl restent
/// lancées manuellement — l'API Pulumi/kubectl est conservée au quotidien.
///
/// Lancement :
///   dotnet nuke <Target>            (ex: dotnet nuke Launch)
///   dotnet nuke --help              (liste des targets + paramètres)
///
/// Targets exposées :
///   Launch          Bootstrap complet : RecreateCluster + PreloadImages + BuildImages + pulumi up
///   RecreateCluster Supprime et recrée le cluster Kind
///   PreloadImages   Pull (podman) + load (kind) des images infra/observabilité/KEDA/metrics-server
///   BuildImages     Build/tag/load des 3 apps (SemVer + SHA par service) + pulumi config set
///   Publish         BuildImages + pulumi up (render) + commit + push (workflow GitOps complet)
///   GitopsOn/Off    Bascule gitops:enabled
///   PresaleStart/Stop  Pré-scaling avant flash sale (KEDA + HPA)
///
/// Le cluster Kind tourne sous Podman : KIND_EXPERIMENTAL_PROVIDER=podman est
/// positionné automatiquement pour toutes les commandes kind.
/// </summary>
partial class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.Launch);

    // ── Constantes projet ─────────────────────────────────────────────────────
    const string ClusterName = "ecommerce";
    const string Namespace   = "ecommerce";

    // Répertoire d'exécution Pulumi (relatif à la racine du repo).
    AbsolutePath PulumiDir   => RootDirectory / "infra" / "Ecommerce.Infra";
    AbsolutePath VersionFile => RootDirectory / "VERSION";
    AbsolutePath KindConfig  => RootDirectory / "kind-config.yaml";

    // ── Helpers d'exécution ───────────────────────────────────────────────────
    // Toutes les commandes kind reçoivent KIND_EXPERIMENTAL_PROVIDER=podman.
    static readonly IReadOnlyDictionary<string, string> KindEnv =
        new Dictionary<string, string>(EnvironmentInfo.Variables) { ["KIND_EXPERIMENTAL_PROVIDER"] = "podman" };

    // Exécute une commande externe et échoue le build si exit code != 0.
    static void Run(string tool, string args, AbsolutePath? workingDir = null,
                    IReadOnlyDictionary<string, string>? env = null)
    {
        var process = StartProcess(tool, args,
            workingDirectory: workingDir,
            environmentVariables: env);
        process.AssertZeroExitCode();
    }

    // Variante kind (injecte l'env Podman).
    static void Kind(string args) => Run("kind", args, env: KindEnv);

    // ── Pulumi (gestion de la passphrase) ─────────────────────────────────────
    // Le stack utilise un secrets provider par passphrase → pulumi réclame
    // PULUMI_CONFIG_PASSPHRASE. Lancé en sous-process, le prompt interactif ne
    // fonctionne pas : on injecte la passphrase via l'environnement pour rendre
    // tous les appels pulumi NON interactifs.
    //
    // Source de la passphrase (par priorité) :
    //   1. paramètre Nuke --pulumi-passphrase (marqué [Secret])
    //   2. variable PULUMI_CONFIG_PASSPHRASE déjà présente dans le shell
    // Si aucune des deux : pulumi échoue avec un message clair ("passphrase must be set").
    [Parameter("Passphrase du secrets provider Pulumi. À défaut, lue depuis PULUMI_CONFIG_PASSPHRASE.")]
    [Secret]
    readonly string? PulumiPassphrase;

    // Environnement enrichi pour les commandes pulumi : hérite du shell + force
    // PULUMI_CONFIG_PASSPHRASE si le paramètre Nuke est fourni.
    IReadOnlyDictionary<string, string> PulumiEnv()
    {
        var env = new Dictionary<string, string>(EnvironmentInfo.Variables);
        if (!string.IsNullOrEmpty(PulumiPassphrase))
            env["PULUMI_CONFIG_PASSPHRASE"] = PulumiPassphrase;
        return env;
    }

    // Helper centralisé pour TOUTE commande pulumi (up, config set...).
    // Garantit que la passphrase est transmise → exécution non interactive.
    void Pulumi(string args) => Run("pulumi", args, PulumiDir, PulumiEnv());

    // pulumi up interne — appelé par Launch et Publish (pas exposé comme target :
    // le `pulumi up` du quotidien se lance manuellement, on conserve la compétence).
    void PulumiUp() => Pulumi("up --yes");

    // ── Bootstrap complet (ex k8s_complete_launch.cmd) ────────────────────────
    Target Launch => _ => _
        .Description("Bootstrap complet : recrée le cluster Kind, précharge les images, build les apps, pulumi up.")
        .DependsOn(RecreateCluster, PreloadImages, BuildImages)
        .Executes(() =>
        {
            PulumiUp();
            Serilog.Log.Information("══════════════════════════════════════════════");
            Serilog.Log.Information(" Déploiement terminé !");
            Serilog.Log.Information(" Gateway    -> http://localhost:30080");
            Serilog.Log.Information(" Grafana    -> http://localhost:30030");
            Serilog.Log.Information(" Jaeger     -> http://localhost:30686");
            Serilog.Log.Information(" Argo CD    -> kubectl port-forward -n argocd svc/argocd-server 8080:80");
            Serilog.Log.Information("══════════════════════════════════════════════");
        });

    Target RecreateCluster => _ => _
        .Description("Supprime et recrée le cluster Kind ecommerce.")
        .Executes(() =>
        {
            Assert.FileExists(KindConfig, $"kind-config.yaml introuvable : {KindConfig}");
            // kind delete ne doit pas faire échouer si le cluster n'existe pas.
            StartProcess("kind", $"delete cluster --name {ClusterName}", environmentVariables: KindEnv)
                .WaitForExit();
            Kind($"create cluster --name {ClusterName} --config \"{KindConfig}\"");
            Run("kubectl", $"config use-context kind-{ClusterName}");
        });

    // ── Préchargement des images infra/observabilité/KEDA/metrics-server ──────
    // Évite les timeouts de pull pendant les Helm install (CNPG, KEDA) sur connexion lente.
    static readonly string[] PreloadImageList =
    {
        // Infra
        "postgres:16-alpine",
        "rabbitmq:4.3.1-management-alpine",
        "redis:7-alpine",
        // CNPG (chart 0.23.2 = opérateur 1.25.1) + PostgreSQL bookworm + PgBouncer
        "ghcr.io/cloudnative-pg/cloudnative-pg:1.25.1",
        "ghcr.io/cloudnative-pg/postgresql:16.6-bookworm",
        "ghcr.io/cloudnative-pg/pgbouncer:1.23.0",
        // Observabilité
        "otel/opentelemetry-collector-contrib:0.153.0",
        "jaegertracing/all-in-one:1.76.0",
        "prom/prometheus:v3.11.3",
        "grafana/grafana:13.0.1-security-01",
        "prometheuscommunity/postgres-exporter:v0.16.0",
        "registry.k8s.io/kube-state-metrics/kube-state-metrics:v2.13.0",
        "quay.io/prometheus/node-exporter:v1.9.1",
        // KEDA 2.17.0 (3 composants du chart)
        "ghcr.io/kedacore/keda:2.17.0",
        "ghcr.io/kedacore/keda-metrics-apiserver:2.17.0",
        "ghcr.io/kedacore/keda-admission-webhooks:2.17.0",
        // Metrics Server (chart 3.12.2 = app v0.7.2)
        "registry.k8s.io/metrics-server/metrics-server:v0.7.2",
    };

    // Images du mode RabbitMQ cluster (rabbitmq:cluster=true uniquement).
    // ⚠️ NON préchargées par défaut : le mode cluster est prod (multi-nœuds), pas
    // dev Kind. L'opérateur officiel (rabbitmqoperator/cluster-operator:latest) est
    // appliqué via manifeste depuis github.com/rabbitmq/cluster-operator.
    // Pour précharger en vue d'un test cluster local, ajouter à PreloadImageList :
    //   "rabbitmqoperator/cluster-operator:latest"
    //   "rabbitmq:4.3.1-management-alpine"   (déjà préchargée ci-dessus)
    // Note : "latest" n'est pas reproductible — épingler une version pour un test stable.

    Target PreloadImages => _ => _
        .Description("Pull (podman) + load (kind) des images infra/observabilité/KEDA/metrics-server.")
        .Executes(() =>
        {
            foreach (var image in PreloadImageList)
            {
                Serilog.Log.Information("Préchargement {Image}", image);
                Run("podman", $"pull {image}");
                Kind($"load docker-image {image} --name {ClusterName}");
            }
        });
}
