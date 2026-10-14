#!/usr/bin/env bash
set -euo pipefail
source "$(dirname -- "${BASH_SOURCE[0]}")/env.sh"
cd "$scanback_project_root"
export ScanBackTestConfig="${ScanBackTestConfig:-${SCANBACK_RUNTIME_DIR:-$scanback_project_root/.runtime}/test-config.json}"
dotnet test ScanBack.sln --no-restore --nologo --logger 'trx;LogFileName=scanback.trx' --results-directory artifacts/test-results
