FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY apps/tb-publisher/ImagingPipeline.TbPublisher.csproj apps/tb-publisher/
COPY libs/rabbitmq-client/RabbitMqClient.csproj libs/rabbitmq-client/
COPY libs/projection-mapper-client/ProjectionMapperClient.csproj libs/projection-mapper-client/
COPY libs/common-dtos/ImagingPipeline.Common.Dtos.csproj libs/common-dtos/
COPY libs/geometry-utils/GeometryUtils.csproj libs/geometry-utils/
COPY libs/observability/ImagingPipeline.Observability.csproj libs/observability/
RUN dotnet restore apps/tb-publisher/ImagingPipeline.TbPublisher.csproj

COPY . .
RUN dotnet publish apps/tb-publisher/ImagingPipeline.TbPublisher.csproj --configuration Release --no-restore --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
EXPOSE 9464
COPY --from=build /app/publish .
USER app
ENTRYPOINT ["dotnet", "ImagingPipeline.TbPublisher.dll"]
