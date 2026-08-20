FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ENV HUSKY=0

# Copy project files and restore
COPY src/OhData.AspNetCore/OhData.AspNetCore.csproj OhData.AspNetCore/
COPY src/OhData.TestBench.AspNetCore/OhData.TestBench.AspNetCore.csproj OhData.TestBench.AspNetCore/
COPY src/Directory.Build.props ./
RUN dotnet restore OhData.TestBench.AspNetCore/OhData.TestBench.AspNetCore.csproj

# Copy source and publish (skip GitVersion in Docker — no git history)
COPY src/ ./
RUN dotnet publish OhData.TestBench.AspNetCore/OhData.TestBench.AspNetCore.csproj \
    -c Release -o /app \
    /p:DisableGitVersionTask=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
# Disable config reload-on-change. WebApplication.CreateBuilder registers appsettings.json
# with reloadOnChange:true, and FileConfigurationProvider's ctor then creates a
# FileSystemWatcher -> one inotify instance per config source. Containers get a small
# per-user inotify budget (Render hit the 128-instance limit), and StartRaisingEvents()
# throws IOException out of CreateBuilder before the app can start - a crash loop, not a
# degraded start. The watcher also buys nothing here: the image is immutable, so
# appsettings.json cannot change at runtime.
ENV DOTNET_hostBuilder__reloadConfigOnChange=false
EXPOSE 8080
ENTRYPOINT ["dotnet", "OhData.TestBench.AspNetCore.dll"]
