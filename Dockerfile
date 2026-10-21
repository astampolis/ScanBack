# Alternative WEB-ONLY image. Production needs a separately hosted ScanBack.Worker,
# explicit deployment CLI migrations, external keys/photos/config and verified TLS.
# The supported Windows IIS package is built by scripts/publish-iis.ps1.
ARG DOTNET_SDK_VERSION=10.0.402
ARG DOTNET_RUNTIME_VERSION=10.0.13
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_VERSION} AS build
WORKDIR /source
COPY . .
RUN dotnet restore src/ScanBack.Web/ScanBack.Web.csproj --locked-mode
RUN dotnet publish src/ScanBack.Web/ScanBack.Web.csproj -c Release --no-restore -p:UseAppHost=false -p:ContinuousIntegrationBuild=true -o /app

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_RUNTIME_VERSION} AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 ASPNETCORE_ENVIRONMENT=Production DOTNET_ENVIRONMENT=Production
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "ScanBack.Web.dll"]
