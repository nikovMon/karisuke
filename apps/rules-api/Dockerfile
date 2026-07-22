FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY apps/rules-api/ImagingPipeline.Rules.Api.csproj apps/rules-api/
COPY libs/elasticsearch-client/ElasticsearchClient.csproj libs/elasticsearch-client/
COPY libs/common-dtos/ImagingPipeline.Common.Dtos.csproj libs/common-dtos/
COPY libs/observability/ImagingPipeline.Observability.csproj libs/observability/
RUN dotnet restore apps/rules-api/ImagingPipeline.Rules.Api.csproj

COPY . .
RUN dotnet publish apps/rules-api/ImagingPipeline.Rules.Api.csproj --configuration Release --no-restore --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
USER app
ENTRYPOINT ["dotnet", "ImagingPipeline.Rules.Api.dll"]
