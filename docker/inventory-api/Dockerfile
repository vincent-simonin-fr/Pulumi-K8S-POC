# ── Stage 1: Build ────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Fichiers de configuration MSBuild/NuGet (doivent précéder le restore)
COPY Directory.Build.props ./
COPY NuGet.config ./
COPY global.json ./

COPY src/Shared/Ecommerce.Contracts/Ecommerce.Contracts.csproj src/Shared/Ecommerce.Contracts/
COPY src/Services/Inventory/Inventory.Domain/Inventory.Domain.csproj src/Services/Inventory/Inventory.Domain/
COPY src/Services/Inventory/Inventory.Application/Inventory.Application.csproj src/Services/Inventory/Inventory.Application/
COPY src/Services/Inventory/Inventory.Infrastructure/Inventory.Infrastructure.csproj src/Services/Inventory/Inventory.Infrastructure/
COPY src/Services/Inventory/Inventory.Api/Inventory.Api.csproj src/Services/Inventory/Inventory.Api/

RUN dotnet restore src/Services/Inventory/Inventory.Api/Inventory.Api.csproj

COPY src/ src/

RUN dotnet publish src/Services/Inventory/Inventory.Api/Inventory.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# ── Stage 2: Runtime ───────────────────────────────────────────────────────────
# noble = Ubuntu 24.04 complet (addgroup/adduser disponibles)
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
WORKDIR /app

COPY --from=build --chown=app:app /app/publish .

USER app

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

HEALTHCHECK --interval=10s --timeout=5s --retries=3 \
  CMD wget -qO- http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "Inventory.Api.dll"]
