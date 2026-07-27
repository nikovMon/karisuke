FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY apps/gateway/ImagingPipeline.Gateway.csproj apps/gateway/
COPY libs/common-dtos/ImagingPipeline.Common.Dtos.csproj libs/common-dtos/
COPY libs/elasticsearch-client/ElasticsearchClient.csproj libs/elasticsearch-client/
COPY libs/geometry-utils/GeometryUtils.csproj libs/geometry-utils/
COPY libs/observability/ImagingPipeline.Observability.csproj libs/observability/
COPY libs/rabbitmq-client/RabbitMqClient.csproj libs/rabbitmq-client/
RUN dotnet restore apps/gateway/ImagingPipeline.Gateway.csproj

COPY . .
RUN dotnet publish apps/gateway/ImagingPipeline.Gateway.csproj --configuration Release --no-restore --output /app/publish

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
USER app
ENTRYPOINT ["dotnet", "ImagingPipeline.Gateway.dll"]
