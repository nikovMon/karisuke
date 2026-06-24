FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY apps/tb-publisher/ImagingPipeline.TbPublisher.csproj apps/tb-publisher/
RUN dotnet restore apps/tb-publisher/ImagingPipeline.TbPublisher.csproj

COPY . .
RUN dotnet publish apps/tb-publisher/ImagingPipeline.TbPublisher.csproj --configuration Release --no-restore --output /app/publish

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
USER app
ENTRYPOINT ["dotnet", "ImagingPipeline.TbPublisher.dll"]
