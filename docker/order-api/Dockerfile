# ── Stage 1: Build ────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy solution + project files for layer caching
COPY Ecommerce.sln ./
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
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Non-root user for security
RUN addgroup --system appgroup && adduser --system --ingroup appgroup appuser
USER appuser

COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

HEALTHCHECK --interval=10s --timeout=5s --retries=3 \
  CMD wget -qO- http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "Order.Api.dll"]
