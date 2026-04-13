FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

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
EXPOSE 8080
ENTRYPOINT ["dotnet", "OhData.TestBench.AspNetCore.dll"]
