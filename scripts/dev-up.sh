#!/usr/bin/env bash
set -euo pipefail
source "$(dirname -- "${BASH_SOURCE[0]}")/env.sh"
cd "$scanback_project_root"
runtime_root="${SCANBACK_RUNTIME_DIR:-$scanback_project_root/.runtime}"
export SCANBACK_RUNTIME_DIR="$runtime_root"
mkdir -p "$runtime_root"
chmod 700 "$runtime_root"
python3 - <<'PY'
import json,os,pathlib,secrets
root=pathlib.Path(os.environ['SCANBACK_RUNTIME_DIR'])
env=root/'sql.env'
if not env.exists():
    password=secrets.token_urlsafe(32)+'Aa1!'
    env.write_text('ACCEPT_EULA=Y\nMSSQL_PID=Developer\nMSSQL_SA_PASSWORD='+password+'\nMSSQL_MEMORY_LIMIT_MB=3072\n')
    env.chmod(0o600)
password=next(line.split('=',1)[1] for line in env.read_text().splitlines() if line.startswith('MSSQL_SA_PASSWORD='))
connection='Server=127.0.0.1,14333;Database=ScanBack;User Id=sa;Password='+password+';Encrypt=true;TrustServerCertificate=true;'
settings=pathlib.Path('src/ScanBack.Web/appsettings.Local.json')
if not settings.exists():
    settings.write_text(json.dumps({'ConnectionStrings':{'DefaultConnection':connection}},indent=2));settings.chmod(0o600)
test=root/'test-config.json'
if not test.exists():test.write_text(json.dumps({'TestConnectionString':connection}));test.chmod(0o600)
PY
sql_image="${SCANBACK_SQL_IMAGE:-mcr.microsoft.com/mssql/server:2022-latest}"
if ! docker image inspect "$sql_image" >/dev/null 2>&1; then docker pull "$sql_image"; fi
if ! docker container inspect scanback-sql >/dev/null 2>&1; then
  mkdir -p "$runtime_root/sql-data"
  docker run --rm --user 0 --entrypoint /bin/sh -v "$runtime_root/sql-data:/data" "$sql_image" -c 'chown -R 10001:0 /data && chmod 770 /data'
  docker run -d --name scanback-sql --label com.scanback.component=sql --env-file "$runtime_root/sql.env" -p 127.0.0.1:14333:1433 -v "$runtime_root/sql-data:/var/opt/mssql" "$sql_image" >/dev/null
else
  docker start scanback-sql >/dev/null
fi
if ! docker container inspect scanback-mail >/dev/null 2>&1; then
  docker run -d --name scanback-mail --label com.scanback.component=mail -p 127.0.0.1:1025:1025 -p 127.0.0.1:8025:8025 axllent/mailpit:v1.29 >/dev/null
else
  docker start scanback-mail >/dev/null
fi
ready=0
started_at="$(docker inspect --format '{{.State.StartedAt}}' scanback-sql)"
for attempt in {1..30}; do
  if docker logs --since "$started_at" scanback-sql 2>&1 | rg -q 'SQL Server is now ready for client connections'; then ready=1; break; fi
  sleep 2
done
if [[ "$ready" != 1 ]]; then printf '%s\n' 'SQL Server did not become ready. Inspect docker logs scanback-sql.' >&2; exit 1; fi
printf '%s\n' 'SQL Server and the local SMTP inbox are running. Start the application with scripts/run.sh.'
