<#
.SYNOPSIS
    Build + tag + load des 3 images applicatives avec versioning SemVer + SHA par service.

.DESCRIPTION
    Schéma de tag : {SemVer}-{SHA-court-du-service}
      - SemVer  : lu depuis le fichier VERSION à la racine (bumpé manuellement pour les releases).
      - SHA     : git log -1 du DERNIER commit ayant touché les fichiers du service.
                  Calculé indépendamment pour chaque service → seul le service réellement
                  modifié change de tag → seul lui sera redéployé par ArgoCD (diff YAML).

    Mapping service → paths suivis (le SHA dépend de ces chemins) :
      order-api      src/Services/Order  + src/Shared/Ecommerce.Contracts
      inventory-api  src/Services/Inventory + src/Shared/Ecommerce.Contracts
      gateway        src/Gateway

    Ecommerce.Contracts est inclus pour order-api et inventory-api : modifier un contrat
    d'événement partagé rebumpe les deux consommateurs (respect du graphe de dépendances).

    Le tag calculé est :
      1. utilisé pour podman build + kind load (image locale immuable)
      2. poussé dans Pulumi via `pulumi config set <svc>:image localhost/...:<tag>`
         → le prochain `pulumi up` rend le YAML avec ce tag → git diff → ArgoCD sync.

.PARAMETER Push
    Si présent : après le build, lance `pulumi up`, commit et push automatiquement
    les manifests rendus (workflow GitOps complet). Sinon, s'arrête après le build+config.

.PARAMETER DirtySuffix
    Si le working tree a des modifications non commitées sur les paths d'un service,
    suffixe son tag avec "-dirty" (ex: 1.0.0-aaa111-dirty). Évite d'écraser une image
    "propre" avec un build local non commité. Activé par défaut.

.EXAMPLE
    pwsh scripts/build-images.ps1
    # Build + tag + load + pulumi config set (pas de commit)

.EXAMPLE
    pwsh scripts/build-images.ps1 -Push
    # Workflow GitOps complet : build → pulumi up → commit → push → ArgoCD sync
#>

[CmdletBinding()]
param(
    [switch]$Push,
    [bool]$DirtySuffix = $true
)

$ErrorActionPreference = 'Stop'

# Racine du repo (le script est dans scripts/)
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

# ── SemVer de base ────────────────────────────────────────────────────────────
$versionFile = Join-Path $repoRoot 'VERSION'
if (-not (Test-Path $versionFile)) {
    throw "Fichier VERSION introuvable à la racine. Créez-le avec un SemVer (ex: 1.0.0)."
}
$semver = (Get-Content $versionFile -Raw).Trim()
if ($semver -notmatch '^\d+\.\d+\.\d+$') {
    throw "VERSION invalide : '$semver'. Attendu : MAJOR.MINOR.PATCH (ex: 1.0.0)."
}

# ── Définition des services ───────────────────────────────────────────────────
# image      : nom de l'image locale (sans tag)
# dockerfile : chemin du Dockerfile
# config     : clé Pulumi config (namespace:key) où injecter l'image taguée
# paths      : chemins git suivis pour calculer le SHA du service
$services = @(
    @{
        name       = 'order-api'
        image      = 'localhost/ecommerce/order-api'
        dockerfile = 'docker/order-api/Dockerfile'
        config     = 'orderApi:image'
        paths      = @('src/Services/Order', 'src/Shared/Ecommerce.Contracts')
    },
    @{
        name       = 'inventory-api'
        image      = 'localhost/ecommerce/inventory-api'
        dockerfile = 'docker/inventory-api/Dockerfile'
        config     = 'inventoryApi:image'
        paths      = @('src/Services/Inventory', 'src/Shared/Ecommerce.Contracts')
    },
    @{
        name       = 'gateway'
        image      = 'localhost/ecommerce/gateway'
        dockerfile = 'docker/gateway/Dockerfile'
        config     = 'gateway:image'
        paths      = @('src/Gateway')
    }
)

# ── Helpers ───────────────────────────────────────────────────────────────────

# SHA court du dernier commit ayant touché l'un des paths du service.
function Get-ServiceSha {
    param([string[]]$Paths)
    $sha = (git log -1 --format=%h -- $Paths 2>$null)
    if ([string]::IsNullOrWhiteSpace($sha)) {
        # Aucun commit sur ces paths (nouveau service) → fallback HEAD global
        $sha = (git rev-parse --short HEAD)
    }
    return $sha.Trim()
}

# True si des modifications non commitées existent sur les paths du service.
function Test-ServiceDirty {
    param([string[]]$Paths)
    $changes = (git status --porcelain -- $Paths 2>$null)
    return -not [string]::IsNullOrWhiteSpace($changes)
}

# ── Build de chaque service ───────────────────────────────────────────────────
Write-Host "SemVer de base : $semver" -ForegroundColor Cyan
Write-Host ""

$pulumiDir = Join-Path $repoRoot 'infra/Ecommerce.Infra'
$built = @()

foreach ($svc in $services) {
    $sha = Get-ServiceSha -Paths $svc.paths
    $tag = "$semver-$sha"

    if ($DirtySuffix -and (Test-ServiceDirty -Paths $svc.paths)) {
        $tag = "$tag-dirty"
        Write-Host "[$($svc.name)] modifications non commitées détectées → tag -dirty" -ForegroundColor Yellow
    }

    $fullImage = "$($svc.image):$tag"

    Write-Host "[$($svc.name)] build $fullImage" -ForegroundColor Green
    podman build -f $svc.dockerfile -t $fullImage .
    if ($LASTEXITCODE -ne 0) { throw "Échec du build de $($svc.name)" }

    Write-Host "[$($svc.name)] kind load $fullImage"
    kind load docker-image $fullImage --name ecommerce
    if ($LASTEXITCODE -ne 0) { throw "Échec du kind load de $($svc.name)" }

    # Injection du tag dans Pulumi config (consommé par les *ServiceResources via xxxApi:image)
    Write-Host "[$($svc.name)] pulumi config set $($svc.config) = $fullImage"
    Push-Location $pulumiDir
    pulumi config set $svc.config $fullImage
    if ($LASTEXITCODE -ne 0) { Pop-Location; throw "Échec du pulumi config set de $($svc.name)" }
    Pop-Location

    $built += [pscustomobject]@{ Service = $svc.name; Tag = $tag; Image = $fullImage }
    Write-Host ""
}

# ── Récapitulatif ─────────────────────────────────────────────────────────────
Write-Host "Images construites :" -ForegroundColor Cyan
$built | Format-Table -AutoSize

# ── Workflow GitOps complet (optionnel) ───────────────────────────────────────
if ($Push) {
    Write-Host "── pulumi up (render des manifests) ──" -ForegroundColor Cyan
    Push-Location $pulumiDir
    pulumi up --yes
    if ($LASTEXITCODE -ne 0) { Pop-Location; throw "Échec du pulumi up" }
    Pop-Location

    Write-Host "── commit + push des manifests ──" -ForegroundColor Cyan
    git add gitops VERSION infra/Ecommerce.Infra/Pulumi.dev.yaml
    $tagsSummary = ($built | ForEach-Object { "$($_.Service)=$($_.Tag)" }) -join ', '
    git commit -m "build: $tagsSummary"
    git push
    Write-Host ""
    Write-Host "ArgoCD va synchroniser les services modifiés depuis Git." -ForegroundColor Green
}
else {
    Write-Host "Build terminé. Pour publier en GitOps :" -ForegroundColor Cyan
    Write-Host "  cd infra/Ecommerce.Infra && pulumi up --yes" -ForegroundColor Gray
    Write-Host "  git add gitops VERSION infra/Ecommerce.Infra/Pulumi.dev.yaml" -ForegroundColor Gray
    Write-Host "  git commit -m 'build: <services>' && git push" -ForegroundColor Gray
    Write-Host ""
    Write-Host "Ou relancez avec -Push pour tout enchaîner automatiquement." -ForegroundColor Gray
}
