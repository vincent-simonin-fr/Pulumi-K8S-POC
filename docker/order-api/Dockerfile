# ── Stage 1: Build ────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Fichiers de configuration MSBuild/NuGet (doivent précéder le restore)
COPY Directory.Build.props ./
COPY NuGet.config ./
COPY global.json ./

# Copy project files for layer caching
COPY src/Shared/Ecommerce.Contracts/Ecommerce.Contracts.csproj src/Shared/Ecommerce.Contracts/
COPY src/Services/Order/Order.Domain/Order.Domain.csproj src/Services/Order/Order.Domain/
COPY src/Services/Order/Order.Application/Order.Application.csproj src/Services/Order/Order.Application/
COPY src/Services/Order/Order.Infrastructure/Order.Infrastructure.csproj src/Services/Order/Order.Infrastructure/
COPY src/Services/Order/Order.Api/Order.Api.csproj src/Services/Order/Order.Api/

RUN dotnet restore src/Services/Order/Order.Api/Order.Api.csproj

# Copy all sources
COPY src/ src/

# Build and publish
RUN dotnet publish src/Services/Order/Order.Api/Order.Api.csproj \
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

ENTRYPOINT ["dotnet", "Order.Api.dll"]
