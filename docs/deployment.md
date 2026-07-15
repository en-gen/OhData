# Deployment

The repo root ships a `Dockerfile` and a `render.yaml`. Both target `OhData.TestBench.AspNetCore` - the runnable demo app (EF Core InMemory, Swagger UI + Scalar, versioned `v1`/`v2` registrations) - as a deployable container. Neither is required to consume OhData as a library; they exist so the demo app (and, by extension, the framework's behavior) can be exercised as a live, deployed service rather than only `dotnet run` locally.

## Dockerfile

Multi-stage build:

1. **Build stage** (`mcr.microsoft.com/dotnet/sdk:10.0`) - restores and publishes `OhData.TestBench.AspNetCore.csproj` in `Release` configuration to `/app`. `HUSKY=0` disables the Husky.NET git-hook installer (irrelevant inside a container image), and `/p:DisableGitVersionTask=true` skips GitVersion's MSBuild task since the build context has no `.git` history to compute a version from.
2. **Runtime stage** (`mcr.microsoft.com/dotnet/aspnet:10.0`) - copies the published output and runs `dotnet OhData.TestBench.AspNetCore.dll`. Listens on `http://+:8080` (`ASPNETCORE_URLS`), and `EXPOSE 8080` documents that port for container tooling.

### Build and run locally

From the repo root:

```bash
docker build -t ohdata-testbench -f Dockerfile .
docker run --rm -p 8080:8080 ohdata-testbench
```

Then browse to `http://localhost:8080/` (redirects to `/scalar/v1`) for the interactive Scalar API reference, or `http://localhost:8080/v1` for the v1 OData service document (`/v2` for the v2 registration).

## render.yaml

A [Render](https://render.com) Blueprint that deploys the same Dockerfile as a single web service:

```yaml
services:
  - type: web
    name: ohdata-testbench
    runtime: docker
    dockerfilePath: ./Dockerfile
    plan: free
    branch: develop
    envVars:
      - key: ASPNETCORE_ENVIRONMENT
        value: Production
```

- `runtime: docker` + `dockerfilePath: ./Dockerfile` - Render builds and runs the same image described above rather than using a buildpack.
- `plan: free` - Render's free web service tier.
- `branch: develop` - the service tracks the `develop` branch; a push to `develop` triggers a new deploy.
- `ASPNETCORE_ENVIRONMENT=Production` - suppresses developer-only behavior (e.g. detailed exception pages) in the deployed instance.

To use it: connect the repo to Render as a Blueprint (New → Blueprint, point it at this repo), and Render provisions the `ohdata-testbench` service from `render.yaml` directly - no manual service configuration needed. This is a demo/reference deployment target, not a publishing mechanism for the NuGet packages themselves.
