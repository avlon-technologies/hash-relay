# HashRelay server image. Built remotely by `az acr build` from deploy.ps1 (no local Docker needed).
# Context = repository root; see .dockerignore. The Windows installer + CLI zip under
# src/HashRelay.Server/wwwroot/downloads (produced by build.ps1) are baked in so /downloads serves them.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/HashRelay.Contracts/HashRelay.Contracts.csproj src/HashRelay.Contracts/
COPY src/HashRelay.Server/HashRelay.Server.csproj src/HashRelay.Server/
RUN dotnet restore src/HashRelay.Server/HashRelay.Server.csproj
COPY src/HashRelay.Contracts/ src/HashRelay.Contracts/
COPY src/HashRelay.Server/ src/HashRelay.Server/
RUN dotnet publish src/HashRelay.Server/HashRelay.Server.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# Reported by /healthz as "build" so a deploy can prove which image is serving.
ARG BUILD_LABEL=""
ENV HASHRELAY_BUILD=$BUILD_LABEL
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "HashRelay.Server.dll"]
