FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY apps/tb-consumer/ImagingPipeline.TbConsumer.csproj apps/tb-consumer/
COPY libs/rabbitmq-client/RabbitMqClient.csproj libs/rabbitmq-client/
COPY libs/common-dtos/ImagingPipeline.Common.Dtos.csproj libs/common-dtos/
COPY libs/projection-mapper-client/ProjectionMapperClient.csproj libs/projection-mapper-client/
COPY libs/observability/ImagingPipeline.Observability.csproj libs/observability/
RUN dotnet restore apps/tb-consumer/ImagingPipeline.TbConsumer.csproj

COPY . .
RUN dotnet publish apps/tb-consumer/ImagingPipeline.TbConsumer.csproj --configuration Release --no-restore --output /app/publish

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
USER app
ENTRYPOINT ["dotnet", "ImagingPipeline.TbConsumer.dll"]
