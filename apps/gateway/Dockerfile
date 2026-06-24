FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY global.json Directory.Build.props Directory.Packages.props ./
COPY apps/gateway/ImagingPipeline.Gateway.csproj apps/gateway/
RUN dotnet restore apps/gateway/ImagingPipeline.Gateway.csproj

COPY . .
RUN dotnet publish apps/gateway/ImagingPipeline.Gateway.csproj --configuration Release --no-restore --output /app/publish

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
USER app
ENTRYPOINT ["dotnet", "ImagingPipeline.Gateway.dll"]
