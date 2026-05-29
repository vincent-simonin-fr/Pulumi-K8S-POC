@echo off
setlocal

:: ─────────────────────────────────────────────────────────────────────────────
:: presale.cmd — Pre-scaling avant un flash sale / evenement de trafic
::
:: Usage :
::   scripts\presale.cmd start   — augmente le minReplicas (pods pre-chauffes)
::   scripts\presale.cmd stop    — retablit les minReplicas nominaux
::
:: Architecture de scaling par service :
::   inventory-api : KEDA ScaledObject (minReplicaCount)
::   order-api     : HPA natif         (minReplicas)
::   gateway       : HPA natif         (minReplicas)
::
:: Valeurs par defaut (modifier selon les besoins) :
::   INVENTORY_MIN  : replicas pre-scale inventory-api (ScaledObject KEDA)
::   ORDER_MIN      : replicas pre-scale order-api     (HPA)
::   GATEWAY_MIN    : replicas pre-scale gateway       (HPA)
::
:: Quand utiliser ce script vs pulumi up ?
::   - Ce script : urgence, event imprevu — effet en secondes
::   - pulumi up  : event planifie — coherence IaC preservee
::   ⚠️  Le prochain pulumi up avec presale:enabled=false ecrasera ces changements
::
:: Lancer depuis la RACINE du projet :
::   scripts\presale.cmd start
:: ─────────────────────────────────────────────────────────────────────────────

set NAMESPACE=ecommerce

:: Replicas presale (ajuster selon la capacite du cluster)
set INVENTORY_PRESALE=3
set ORDER_PRESALE=3
set GATEWAY_PRESALE=2

:: Replicas nominaux (synchronises avec hpa:*Min dans Pulumi.dev.yaml)
set INVENTORY_NOMINAL=1
set ORDER_NOMINAL=1
set GATEWAY_NOMINAL=1

if "%1"=="start" goto :presale_start
if "%1"=="stop"  goto :presale_stop

echo Usage : scripts\presale.cmd [start^|stop]
echo.
echo   start  — pre-scale les HPA avant un flash sale
echo   stop   — retablit les minReplicas nominaux
exit /b 1

:: ─────────────────────────────────────────────────────────────────────────────
:presale_start
echo [PRESALE] Activation du pre-scaling...
echo.

:: inventory-api : KEDA ScaledObject — patch minReplicaCount (pas minReplicas comme HPA)
:: KEDA reagit immediatement : le Deployment est scale-out en quelques secondes.
echo [1/3] inventory-api : %INVENTORY_NOMINAL% -> %INVENTORY_PRESALE% replicas (ScaledObject KEDA)
kubectl patch scaledobject inventory-api -n %NAMESPACE% ^
  --type=merge ^
  -p "{\"spec\":{\"minReplicaCount\":%INVENTORY_PRESALE%}}"

:: order-api / gateway : HPA natif (inchange)
echo [2/3] order-api : %ORDER_NOMINAL% -> %ORDER_PRESALE% replicas (HPA)
kubectl patch hpa order-api -n %NAMESPACE% ^
  --type=merge ^
  -p "{\"spec\":{\"minReplicas\":%ORDER_PRESALE%}}"

echo [3/3] gateway : %GATEWAY_NOMINAL% -> %GATEWAY_PRESALE% replicas (HPA)
kubectl patch hpa gateway -n %NAMESPACE% ^
  --type=merge ^
  -p "{\"spec\":{\"minReplicas\":%GATEWAY_PRESALE%}}"

echo.
echo [PRESALE] ScaledObject et HPA patches. Attente que les pods soient Ready...
kubectl rollout status deployment/inventory-api -n %NAMESPACE% --timeout=120s
kubectl rollout status deployment/order-api     -n %NAMESPACE% --timeout=120s
kubectl rollout status deployment/gateway       -n %NAMESPACE% --timeout=120s

echo.
echo [PRESALE] Etat actuel :
kubectl get pods -n %NAMESPACE% -l "app in (inventory-api,order-api,gateway)"
echo.
echo [PRESALE] ScaledObject KEDA :
kubectl get scaledobject -n %NAMESPACE%
echo.
echo [PRESALE] Pret pour le flash sale.
echo [PRESALE] Apres l'event : scripts\presale.cmd stop
goto :end

:: ─────────────────────────────────────────────────────────────────────────────
:presale_stop
echo [PRESALE] Retour au dimensionnement nominal...
echo.

:: inventory-api : revert du ScaledObject KEDA
echo [1/3] inventory-api : %INVENTORY_PRESALE% -> %INVENTORY_NOMINAL% replicas (ScaledObject KEDA)
kubectl patch scaledobject inventory-api -n %NAMESPACE% ^
  --type=merge ^
  -p "{\"spec\":{\"minReplicaCount\":%INVENTORY_NOMINAL%}}"

:: KEDA scale-in automatiquement une fois la charge reduite (cooldownPeriod=60s)

echo [2/3] order-api : %ORDER_PRESALE% -> %ORDER_NOMINAL% replicas (HPA)
kubectl patch hpa order-api -n %NAMESPACE% ^
  --type=merge ^
  -p "{\"spec\":{\"minReplicas\":%ORDER_NOMINAL%}}"

echo [3/3] gateway : %GATEWAY_PRESALE% -> %GATEWAY_NOMINAL% replicas (HPA)
kubectl patch hpa gateway -n %NAMESPACE% ^
  --type=merge ^
  -p "{\"spec\":{\"minReplicas\":%GATEWAY_NOMINAL%}}"

echo.
echo [PRESALE] Retabli. KEDA et HPA reduiront les pods selon la charge reelle.
echo.
kubectl get scaledobject -n %NAMESPACE%
kubectl get hpa -n %NAMESPACE%

:end
endlocal
