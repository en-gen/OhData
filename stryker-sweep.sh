#!/usr/bin/env bash
#
# Mutation sweep. One Stryker run per shipping project, or one named project.
#
#   ./stryker-sweep.sh            # all six, smallest first; the core run takes ~2h
#   ./stryker-sweep.sh core       # just one
#
# Stryker mutates ONE project per invocation, so "the whole product" is six runs. They are
# sequenced rather than parallelised because every run rebuilds the same solution and concurrent
# runs contend for the test assemblies' bin/ ("the process cannot access the file ...
# OhData.AspNetCore.dll").
#
# Each run gets a generated config FILE rather than CLI flags: coverage-analysis is config-only in
# Stryker 5, and it is the setting that makes this affordable -- 84% of the main suite boots a
# TestServer, so running every test per mutant is the difference between hours and weeks.
#
# Reports land in StrykerOutput/<timestamp>/reports/sweep-<name>.{json,html}; logs and the generated
# configs go beside them under StrykerOutput/sweep/, so one .gitignore entry covers everything.
set -u

# Every path below is repo-relative, and a two-hour run is a bad place to discover otherwise.
cd "$(dirname "${BASH_SOURCE[0]}")" || exit 1

OUT=StrykerOutput/sweep
mkdir -p "${OUT}"

failed=0

run () {
  local name="$1" proj="$2"; shift 2
  local tests_json; tests_json=$(printf '"%s",' "$@"); tests_json="${tests_json%,}"
  local cfg="${OUT}/${name}.json"

  cat > "${cfg}" <<JSON
{
  "\$schema": "https://raw.githubusercontent.com/stryker-mutator/stryker-net/master/src/Stryker.Core/Stryker.Core/Schemas/stryker-config.json",
  "stryker-config": {
    "project": "${proj}",
    "solution": "src/OhData.sln",
    "target-framework": "net10.0",
    "test-projects": [${tests_json}],
    "coverage-analysis": "perTest",
    "concurrency": 16,
    "thresholds": { "high": 80, "low": 60, "break": 0 },
    "reporters": ["json", "html", "cleartext", "progress"],
    "report-file-name": "sweep-${name}",
    "mutation-level": "Standard"
  }
}
JSON

  echo "=== ${name}  start $(date +%H:%M:%S)"
  dotnet stryker --config-file "${cfg}" > "${OUT}/${name}.log" 2>&1
  local rc=$?
  [ "${rc}" -ne 0 ] && failed=1
  echo "    ${name}: exit=${rc} $(grep -o 'final mutation score is [0-9.]*' "${OUT}/${name}.log" | tail -1) end $(date +%H:%M:%S)"
}

want () { [ "$#" -eq 0 ] || [ -z "${FILTER}" ] || [ "${FILTER}" = "$1" ]; }

FILTER="${1:-}"

want swashbuckle && run swashbuckle OhData.AspNetCore.Swashbuckle.csproj src/OhData.AspNetCore.Swashbuckle.Tests/OhData.AspNetCore.Swashbuckle.Tests.csproj
want nswag       && run nswag       OhData.AspNetCore.NSwag.csproj       src/OhData.AspNetCore.NSwag.Tests/OhData.AspNetCore.NSwag.Tests.csproj
want openapi     && run openapi     OhData.AspNetCore.OpenApi.csproj     src/OhData.AspNetCore.OpenApi.Tests/OhData.AspNetCore.OpenApi.Tests.csproj
# The delta half of the mapper is covered by the CORE suite's files until #675 lands, so both.
want mapper      && run mapper      OhData.AspNetCore.Mapper.csproj      src/OhData.AspNetCore.Mapper.Tests/OhData.AspNetCore.Mapper.Tests.csproj src/OhData.AspNetCore.Tests/OhData.AspNetCore.Tests.csproj
want client      && run client      OhData.Client.csproj                 src/OhData.Client.Tests/OhData.Client.Tests.csproj
want core        && run core        OhData.AspNetCore.csproj             src/OhData.AspNetCore.Tests/OhData.AspNetCore.Tests.csproj

echo "=== sweep complete $(date +%H:%M:%S)"

# Only net10.0 is mutated. Both shipping libraries multi-target, and CLAUDE.md is explicit that the
# net8.0 branches are load-bearing rather than vestigial (FindInvalidDynamicKey, StableTypeName), so
# a sweep is not a statement about them.
exit "${failed}"
