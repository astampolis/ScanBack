#!/usr/bin/env bash
set -euo pipefail
source "$(dirname -- "${BASH_SOURCE[0]}")/env.sh"
cd "$scanback_project_root"
export ASPNETCORE_ENVIRONMENT=Development
exec dotnet run --project src/ScanBack.Web --no-build --no-launch-profile --urls http://127.0.0.1:5080
