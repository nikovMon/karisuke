# Offline / Closed-Network Setup Guide

This guide explains how to clone, build, and run the **Karisuke** monorepo on a machine that has **no internet access** (air-gapped / closed network).

The process has two phases:

1. **Prep phase** – run on a machine **with** internet to collect all dependencies.
2. **Deploy phase** – transfer everything to the closed network and build locally.

---

## Phase 1 – Preparation (Internet-Connected Machine)

### 1.1 Clone the Repository

```powershell
git clone <repo-url> karisuke
cd karisuke
```

### 1.2 Install Required Tools (to copy later)

Download the offline installers and keep them in a folder (e.g. `C:\offline-tools\`):

| Tool | Version | Download |
|------|---------|----------|
| .NET SDK | **10.0.301** | https://dotnet.microsoft.com/download/dotnet/10.0 |
| Node.js | **24.18.0+** (LTS) | https://nodejs.org/en/download/ |
| Docker Desktop | Latest | https://www.docker.com/products/docker-desktop/ |
| Git | Latest | https://git-scm.com/downloads |

> [!IMPORTANT]
> Download the **offline installers** (`.exe` / `.msi`), not the web installers.

### 1.3 Cache NuGet Packages

Restore all NuGet packages and create a local package folder:

```powershell
dotnet restore ImagingPipeline.sln

# Copy the global NuGet cache into a portable folder
mkdir offline-packages\nuget
dotnet nuget locals global-packages --list
# Copy everything from the listed path into offline-packages\nuget
```

Or generate a single folder with only the packages this solution needs:

```powershell
mkdir offline-packages\nuget
dotnet restore ImagingPipeline.sln --packages offline-packages\nuget
```

### 1.4 Cache npm Packages

```powershell
npm install
npm cache ls

# Create a portable tarball of the npm cache
npm cache --cache ./offline-packages/npm-cache verify
npm pack --pack-destination ./offline-packages/npm-tarballs nx@23.0.1
npm pack --pack-destination ./offline-packages/npm-tarballs @nx/dotnet@23.0.1
```

Or simply copy the entire `node_modules` folder:

```powershell
xcopy /E /I node_modules offline-packages\node_modules
```

### 1.5 Save Docker Base Images

The Dockerfiles pull two base images from Microsoft Container Registry. Save them for offline use:

```powershell
docker pull mcr.microsoft.com/dotnet/sdk:10.0
docker pull mcr.microsoft.com/dotnet/runtime:10.0
docker pull mcr.microsoft.com/dotnet/aspnet:10.0

docker save mcr.microsoft.com/dotnet/sdk:10.0 -o offline-packages\dotnet-sdk-10.0.tar
docker save mcr.microsoft.com/dotnet/runtime:10.0 -o offline-packages\dotnet-runtime-10.0.tar
docker save mcr.microsoft.com/dotnet/aspnet:10.0 -o offline-packages\dotnet-aspnet-10.0.tar
```

### 1.6 Bundle Everything

Copy the following to a portable drive or transfer medium:

```text
portable-drive/
├── karisuke/                  # The full git repo
├── offline-tools/             # .NET SDK, Node.js, Docker, Git installers
└── offline-packages/
    ├── nuget/                 # Cached NuGet packages
    ├── node_modules/          # (or npm-tarballs/)
    ├── dotnet-sdk-10.0.tar    # Docker base image
    ├── dotnet-runtime-10.0.tar
    └── dotnet-aspnet-10.0.tar
```

---

## Phase 2 – Deployment (Closed Network Machine)

### 2.1 Install Tools

Install from the offline installers you downloaded:

1. **Git** – from `offline-tools\Git-*.exe`
2. **.NET SDK 10.0.301** – from `offline-tools\dotnet-sdk-*.exe`
3. **Node.js 24.x** – from `offline-tools\node-*.msi`
4. **Docker Desktop** – from `offline-tools\Docker-Desktop-*.exe`

Verify installations:

```powershell
git --version
dotnet --version          # Should output 10.0.301
node --version            # Should output v24.x.x
npm --version             # Should output 11.x.x
docker --version
```

### 2.2 Copy the Repository

Copy the `karisuke` folder to the target machine:

```powershell
xcopy /E /I D:\portable-drive\karisuke C:\source\karisuke
cd C:\source\karisuke
```

### 2.3 Configure NuGet Offline Source

Point NuGet to your local package cache instead of nuget.org:

```powershell
# Disable the default online source
dotnet nuget disable source nuget.org

# Add the offline folder as a source
dotnet nuget add source D:\portable-drive\offline-packages\nuget --name offline-local
```

Then restore:

```powershell
dotnet restore ImagingPipeline.sln
```

### 2.4 Restore npm / Nx

If you copied `node_modules` directly:

```powershell
# Nothing to do – node_modules is already in place
```

If you packed tarballs instead:

```powershell
npm install --offline --cache D:\portable-drive\offline-packages\npm-cache
```

### 2.5 Build the Solution

```powershell
dotnet build ImagingPipeline.sln
dotnet test ImagingPipeline.sln
```

Or via Nx:

```powershell
npm run build
npm run test
```

### 2.6 Load Docker Base Images

```powershell
docker load -i D:\portable-drive\offline-packages\dotnet-sdk-10.0.tar
docker load -i D:\portable-drive\offline-packages\dotnet-runtime-10.0.tar
docker load -i D:\portable-drive\offline-packages\dotnet-aspnet-10.0.tar
```

### 2.7 Build & Run Docker Containers

Since the base images are now loaded locally and NuGet packages are cached, the Docker build will work fully offline:

```powershell
# Build all four service images
docker compose build

# Run
docker compose up -d
```

> [!TIP]
> If the Docker build still tries to reach nuget.org during `dotnet restore` inside the container, add a `nuget.config` at the repo root that points to a volume-mounted local source, or use a multi-stage approach where you copy the pre-built publish output directly.

---

## Quick Reference

| Action | Command |
|--------|---------|
| Restore .NET packages | `dotnet restore ImagingPipeline.sln` |
| Build all projects | `dotnet build ImagingPipeline.sln` |
| Run all tests | `dotnet test ImagingPipeline.sln` |
| Build Docker images | `docker compose build` |
| Start services | `docker compose up -d` |
| Stop services | `docker compose down` |

---

## Troubleshooting

| Problem | Fix |
|---------|-----|
| `global.json` SDK mismatch | Ensure installed SDK matches `global.json` → currently `10.0.301` with `latestPatch` roll-forward |
| NuGet restore fails offline | Verify `dotnet nuget list source` shows only the local offline source |
| Docker build fails on `dotnet restore` | Mount local NuGet cache into the Docker build or use pre-published binaries |
| npm install fails offline | Ensure `node_modules` was copied or `--offline --cache` points to the correct cache |
