FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/OhData.TestBench.AspNetCore/OhData.TestBench.AspNetCore.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# Install curl for the Docker Compose healthcheck; not present in the slim base image
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "OhData.TestBench.AspNetCore.dll"]
