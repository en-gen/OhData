#!/usr/bin/env bash
# Comprehensive mutation sweep: one Stryker run per shipping project.
#
# Stryker mutates ONE project per invocation, so "the whole product" is six runs, not one. They are
# sequenced rather than parallelised because every run builds the same solution and they contend for
# the test assemblies' bin/ (a concurrent run fails with "the process cannot access the file ...
# OhData.AspNetCore.dll").
#
# Each run gets its own config FILE rather than CLI flags: coverage-analysis is config-only in
# Stryker 5, and it is the setting that makes this affordable -- 84% of the main suite boots a
# TestServer, so running every test per mutant is the difference between hours and weeks.
#
# Small projects first (minutes each, early signal). OhData.AspNetCore is last and unscoped:
# ~4,850 testable mutants, measured in hours.
set -u
mkdir -p sweep-logs sweep-configs

run () {
  local name="$1" proj="$2"; shift 2
  local tests_json; tests_json=$(printf '"%s",' "$@"); tests_json="${tests_json%,}"
  local cfg="sweep-configs/${name}.json"
  cat > "${cfg}" <<JSON
{
  "stryker-config": {
    "project": "${proj}",
    "solution": "src/OhData.sln",
    "target-framework": "net10.0",
    "test-projects": [${tests_json}],
    "coverage-analysis": "perTest",
    "concurrency": 16,
    "thresholds": { "high": 80, "low": 60, "break": 0 },
    "reporters": ["json", "cleartext", "progress"],
    "report-file-name": "sweep-${name}",
    "mutation-level": "Standard"
  }
}
JSON
  echo "=== ${name}  start $(date +%H:%M:%S)"
  dotnet stryker --config-file "${cfg}" > "sweep-logs/${name}.log" 2>&1
  local rc=$?
  echo "    ${name}: exit=${rc} $(grep -o 'final mutation score is [0-9.]*' "sweep-logs/${name}.log" | tail -1) end $(date +%H:%M:%S)"
}

run swashbuckle OhData.AspNetCore.Swashbuckle.csproj src/OhData.AspNetCore.Swashbuckle.Tests/OhData.AspNetCore.Swashbuckle.Tests.csproj
run nswag       OhData.AspNetCore.NSwag.csproj       src/OhData.AspNetCore.NSwag.Tests/OhData.AspNetCore.NSwag.Tests.csproj
run openapi     OhData.AspNetCore.OpenApi.csproj     src/OhData.AspNetCore.OpenApi.Tests/OhData.AspNetCore.OpenApi.Tests.csproj
run mapper      OhData.AspNetCore.Mapper.csproj      src/OhData.AspNetCore.Mapper.Tests/OhData.AspNetCore.Mapper.Tests.csproj src/OhData.AspNetCore.Tests/OhData.AspNetCore.Tests.csproj
run client      OhData.Client.csproj                 src/OhData.Client.Tests/OhData.Client.Tests.csproj
run core        OhData.AspNetCore.csproj             src/OhData.AspNetCore.Tests/OhData.AspNetCore.Tests.csproj

echo "=== sweep complete $(date +%H:%M:%S)"
