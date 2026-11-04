FROM mcr.microsoft.com/dotnet/sdk:10.0.300 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY apps/dlq-recovery/ImagingPipeline.DlqRecovery.csproj apps/dlq-recovery/
COPY libs/rabbitmq-client/RabbitMqClient.csproj libs/rabbitmq-client/
COPY libs/rabbitmq-configuration/ImagingPipeline.RabbitMqConfiguration.csproj libs/rabbitmq-configuration/
COPY libs/observability/ImagingPipeline.Observability.csproj libs/observability/
RUN dotnet restore apps/dlq-recovery/ImagingPipeline.DlqRecovery.csproj
COPY . .
RUN dotnet publish apps/dlq-recovery/ImagingPipeline.DlqRecovery.csproj -c Release --no-restore -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
USER app
ENTRYPOINT ["dotnet", "ImagingPipeline.DlqRecovery.dll"]
