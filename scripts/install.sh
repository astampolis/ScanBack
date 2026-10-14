#!/usr/bin/env bash
set -euo pipefail
source "$(dirname -- "${BASH_SOURCE[0]}")/env.sh"
cd "$scanback_project_root"
if ! command -v dotnet >/dev/null || ! dotnet --list-sdks | rg -q '^10\.0\.4'; then
  export SCANBACK_DOTNET_DIR="$scanback_sdk_dir"
  python3 - <<'PY'
import hashlib,json,os,pathlib,tarfile,tempfile,urllib.request
version=json.load(open('global.json'))['sdk']['version']
with urllib.request.urlopen('https://raw.githubusercontent.com/dotnet/core/main/release-notes/10.0/releases.json',timeout=30) as r:
    releases=json.load(r)
artifact=next(f for release in releases['releases'] for sdk in release.get('sdks',[]) if sdk['version']==version
              for f in sdk['files'] if f['rid']=='linux-x64' and f['name'].endswith('tar.gz'))
root=pathlib.Path(os.environ['SCANBACK_DOTNET_DIR']);root.mkdir(parents=True,exist_ok=True)
with tempfile.TemporaryDirectory() as tmp:
    archive=pathlib.Path(tmp)/'sdk.tar.gz'
    with urllib.request.urlopen(artifact['url'],timeout=60) as response,archive.open('wb') as out:
        while block:=response.read(1024*1024):out.write(block)
    digest=hashlib.sha512()
    with archive.open('rb') as source:
        while block:=source.read(1024*1024):digest.update(block)
    if digest.hexdigest()!=artifact['hash']:raise SystemExit('SDK integrity check failed')
    with tarfile.open(archive) as tf:tf.extractall(root,filter='data')
print('Verified .NET SDK installed:',version)
PY
  source "$scanback_project_root/scripts/env.sh"
fi
dotnet restore ScanBack.sln --locked-mode
dotnet tool restore
dotnet build ScanBack.sln --no-restore --nologo
