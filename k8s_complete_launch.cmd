@echo off
:: ─────────────────────────────────────────────────────────────────────────────
:: Recrée le cluster Kind + déploie via Pulumi.
:: À lancer depuis la RACINE du projet :
::   scripts\k8s_complete_launch.cmd
::
:: Préférer le script PowerShell équivalent (meilleure gestion d'erreurs) :
::   powershell -ExecutionPolicy Bypass -File scripts\k8s_complete_launch.ps1
:: ─────────────────────────────────────────────────────────────────────────────
setlocal

:: Vérification du répertoire courant
if not exist "kind-config.yaml" (
    echo ERREUR : lancer ce script depuis la racine du projet.
    exit /b 1
)

:: Variable obligatoire pour Kind + Podman
set KIND_EXPERIMENTAL_PROVIDER=podman

:: ── 1. Cluster Kind ───────────────────────────────────────────────────────────
echo.
echo [1/5] Recreation du cluster Kind...
kind delete cluster --name ecommerce
kind create cluster --name ecommerce --config kind-config.yaml
if errorlevel 1 ( echo ERREUR kind create & exit /b 1 )
kubectl config use-context kind-ecommerce

:: ── 2. Images infra ───────────────────────────────────────────────────────────
echo.
echo [2/5] Images infra (postgres, rabbitmq)...
podman pull postgres:16-alpine
kind load docker-image postgres:16-alpine --name ecommerce
podman pull rabbitmq:4.3.1-management-alpine
kind load docker-image rabbitmq:4.3.1-management-alpine --name ecommerce

:: ── 3. Images observabilite ───────────────────────────────────────────────────
echo.
echo [3/5] Images observabilite...
podman pull otel/opentelemetry-collector-contrib:0.153.0
kind load docker-image otel/opentelemetry-collector-contrib:0.153.0 --name ecommerce
podman pull jaegertracing/all-in-one:1.76.0
kind load docker-image jaegertracing/all-in-one:1.76.0 --name ecommerce
podman pull prom/prometheus:v3.11.3
kind load docker-image prom/prometheus:v3.11.3 --name ecommerce
podman pull grafana/grafana:13.0.1-security-01
kind load docker-image grafana/grafana:13.0.1-security-01 --name ecommerce

:: ── 4. Build + chargement images applicatives ─────────────────────────────────
echo.
echo [4/5] Build et chargement des images applicatives...
podman build -f docker/order-api/Dockerfile     -t localhost/ecommerce/order-api:dev .
if errorlevel 1 ( echo ERREUR build order-api & exit /b 1 )
kind load docker-image localhost/ecommerce/order-api:dev --name ecommerce

podman build -f docker/inventory-api/Dockerfile -t localhost/ecommerce/inventory-api:dev .
if errorlevel 1 ( echo ERREUR build inventory-api & exit /b 1 )
kind load docker-image localhost/ecommerce/inventory-api:dev --name ecommerce

podman build -f docker/gateway/Dockerfile       -t localhost/ecommerce/gateway:dev .
if errorlevel 1 ( echo ERREUR build gateway & exit /b 1 )
kind load docker-image localhost/ecommerce/gateway:dev --name ecommerce

:: ── 5. Pulumi ─────────────────────────────────────────────────────────────────
echo.
echo [5/5] Deploiement Pulumi...
pushd infra\Ecommerce.Infra
pulumi login --local
pulumi stack init dev
pulumi up --yes
if errorlevel 1 ( popd & echo ERREUR pulumi up & exit /b 1 )
popd

echo.
echo  Deploiement termine !
echo  Gateway   -^> http://localhost:30080
echo  Grafana   -^> http://localhost:30030
echo  Jaeger    -^> http://localhost:30686
endlocal
