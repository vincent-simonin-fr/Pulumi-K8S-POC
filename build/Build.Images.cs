using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using static Nuke.Common.Tooling.ProcessTasks;

/// <summary>
/// Versioning des 3 images applicatives : tag {SemVer}-{SHA-par-service}.
///
///   SemVer : fichier VERSION à la racine (bumpé manuellement pour les releases).
///   SHA    : dernier commit ayant touché les fichiers DU service uniquement
///            → modifier un service ne change que son tag → ArgoCD ne redéploie que lui.
///
/// Le tag est poussé dans Pulumi config (xxxApi:image), consommé par les
/// *ServiceResources C#. Aucun changement de code applicatif nécessaire.
/// </summary>
partial class Build
{
    /// <summary>Définition d'un service applicatif versionné.</summary>
    sealed record ServiceSpec(
        string Name,         // libellé humain
        string Image,        // image locale sans tag (localhost/ecommerce/xxx)
        string Dockerfile,   // chemin du Dockerfile (relatif racine)
        string ConfigKey,    // clé Pulumi config (namespace:key)
        string[] Paths);     // chemins git suivis pour le SHA du service

    // Ecommerce.Contracts est inclus pour order + inventory : un changement de
    // contrat d'événement rebumpe ses deux consommateurs (graphe de dépendances).
    static readonly ServiceSpec[] Services =
    {
        new("order-api",
            "localhost/ecommerce/order-api",
            "docker/order-api/Dockerfile",
            "orderApi:image",
            new[] { "src/Services/Order", "src/Shared/Ecommerce.Contracts" }),
        new("inventory-api",
            "localhost/ecommerce/inventory-api",
            "docker/inventory-api/Dockerfile",
            "inventoryApi:image",
            new[] { "src/Services/Inventory", "src/Shared/Ecommerce.Contracts" }),
        new("gateway",
            "localhost/ecommerce/gateway",
            "docker/gateway/Dockerfile",
            "gateway:image",
            new[] { "src/Gateway" }),
    };

    /// <summary>
    /// Si défini, désactive le suffixe -dirty (par défaut activé).
    /// Usage : dotnet nuke BuildImages --no-dirty-suffix
    /// </summary>
    [Parameter("Désactive le suffixe -dirty sur les builds avec modifications non commitées.")]
    readonly bool NoDirtySuffix;

    // Tags calculés pendant BuildImages, réutilisés par Publish (commit message).
    readonly Dictionary<string, string> _builtTags = new();

    Target BuildImages => _ => _
        .Description("Build + tag + load des 3 apps (SemVer + SHA par service) + pulumi config set.")
        // Ordre dans Launch : le cluster doit exister avant 'kind load' (sinon
        // "no nodes found"). .After() = contrainte d'ordre SOUPLE (ne déclenche pas
        // ces cibles ; n'impose l'ordre que si elles sont déjà dans le plan).
        .After(RecreateCluster, PreloadImages)
        .Executes(() =>
        {
            Assert.FileExists(VersionFile, $"Fichier VERSION introuvable : {VersionFile}");
            var semver = ReadSemVer();
            Serilog.Log.Information("SemVer de base : {SemVer}", semver);

            foreach (var svc in Services)
            {
                var sha = ServiceSha(svc.Paths);
                var tag = $"{semver}-{sha}";

                if (!NoDirtySuffix && ServiceDirty(svc.Paths))
                {
                    tag += "-dirty";
                    Serilog.Log.Warning("[{Svc}] modifications non commitées → tag -dirty", svc.Name);
                }

                var fullImage = $"{svc.Image}:{tag}";

                Serilog.Log.Information("[{Svc}] build {Image}", svc.Name, fullImage);
                Run("podman", $"build -f {svc.Dockerfile} -t {fullImage} .", RootDirectory);

                Serilog.Log.Information("[{Svc}] kind load {Image}", svc.Name, fullImage);
                Kind($"load docker-image {fullImage} --name {ClusterName}");

                Serilog.Log.Information("[{Svc}] pulumi config set {Key} = {Image}", svc.Name, svc.ConfigKey, fullImage);
                Pulumi($"config set {svc.ConfigKey} {fullImage}");

                _builtTags[svc.Name] = tag;
            }

            Serilog.Log.Information("Images construites : {Tags}",
                string.Join(", ", _builtTags.Select(kv => $"{kv.Key}={kv.Value}")));
        });

    Target Publish => _ => _
        .Description("Workflow GitOps complet : BuildImages + pulumi up (render) + commit + push.")
        .DependsOn(BuildImages)
        .Executes(() =>
        {
            // pulumi up rend les manifests (gitops/apps) avec les nouveaux tags d'image,
            // AVANT de committer — c'est ce diff YAML qu'ArgoCD synchronise.
            PulumiUp();

            var summary = string.Join(", ", _builtTags.Select(kv => $"{kv.Key}={kv.Value}"));
            Run("git", "add gitops VERSION infra/Ecommerce.Infra/Pulumi.dev.yaml", RootDirectory);
            Run("git", $"commit -m \"build: {summary}\"", RootDirectory);
            Run("git", "push", RootDirectory);
            Serilog.Log.Information("Push effectué. ArgoCD va synchroniser les services modifiés.");
        });

    // ── Helpers versioning ────────────────────────────────────────────────────

    string ReadSemVer()
    {
        var raw = VersionFile.ReadAllText().Trim();
        if (!Regex.IsMatch(raw, @"^\d+\.\d+\.\d+$"))
            throw new Exception($"VERSION invalide : '{raw}'. Attendu MAJOR.MINOR.PATCH (ex: 1.0.0).");
        return raw;
    }

    // SHA court du dernier commit touchant l'un des paths du service.
    string ServiceSha(string[] paths)
    {
        var pathArgs = string.Join(" ", paths.Select(p => $"\"{p}\""));
        var output = StartProcess("git", $"log -1 --format=%h -- {pathArgs}",
                workingDirectory: RootDirectory, logOutput: false)
            .AssertZeroExitCode()
            .Output.Select(o => o.Text).FirstOrDefault()?.Trim();

        if (string.IsNullOrWhiteSpace(output))
        {
            // Nouveau service sans historique sur ces paths → fallback HEAD global.
            output = StartProcess("git", "rev-parse --short HEAD",
                    workingDirectory: RootDirectory, logOutput: false)
                .AssertZeroExitCode()
                .Output.Select(o => o.Text).First().Trim();
        }
        return output;
    }

    // True si modifications non commitées sur les paths du service.
    bool ServiceDirty(string[] paths)
    {
        var pathArgs = string.Join(" ", paths.Select(p => $"\"{p}\""));
        var output = StartProcess("git", $"status --porcelain -- {pathArgs}",
                workingDirectory: RootDirectory, logOutput: false)
            .AssertZeroExitCode()
            .Output.Select(o => o.Text);
        return output.Any(line => !string.IsNullOrWhiteSpace(line));
    }
}
