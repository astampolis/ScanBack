#!/usr/bin/env bash
# Source this file before using the locally installed SDK.
scanback_project_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$scanback_project_root/.runtime/dotnet-home}"
export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-$scanback_project_root/.runtime/run}"
export XDG_DATA_HOME="${XDG_DATA_HOME:-$scanback_project_root/.runtime/share}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-$scanback_project_root/.runtime/nuget}"
mkdir -p "$DOTNET_CLI_HOME" "$XDG_RUNTIME_DIR" "$XDG_DATA_HOME" "$NUGET_PACKAGES"
scanback_sdk_dir="${SCANBACK_DOTNET_DIR:-$scanback_project_root/.runtime/dotnet}"
if [[ -x "$scanback_sdk_dir/dotnet" ]]; then
  export DOTNET_ROOT="$scanback_sdk_dir"
  export PATH="$scanback_sdk_dir:$PATH"
fi
