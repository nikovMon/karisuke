FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY apps/unified-gateway/ImagingPipeline.UnifiedGateway.csproj apps/unified-gateway/
COPY libs/pipeline-catalog/ImagingPipeline.PipelineCatalog.csproj libs/pipeline-catalog/
COPY libs/pipeline-contracts/ImagingPipeline.PipelineContracts.csproj libs/pipeline-contracts/
COPY libs/common-dtos/ImagingPipeline.Common.Dtos.csproj libs/common-dtos/
COPY libs/observability/ImagingPipeline.Observability.csproj libs/observability/
RUN dotnet restore apps/unified-gateway/ImagingPipeline.UnifiedGateway.csproj

COPY . .
RUN dotnet publish apps/unified-gateway/ImagingPipeline.UnifiedGateway.csproj --configuration Release --no-restore --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
EXPOSE 9464
COPY --from=build /app/publish .
USER app
ENTRYPOINT ["dotnet", "ImagingPipeline.UnifiedGateway.dll"]
