#!/usr/bin/env bash
set -euo pipefail
umask 077
source "$(dirname -- "${BASH_SOURCE[0]}")/env.sh"
cd "$scanback_project_root"
runtime_root="${SCANBACK_RUNTIME_DIR:-$scanback_project_root/.runtime}"
mkdir -p "$runtime_root"
runtime_root="$(cd -- "$runtime_root" && pwd)"
export SCANBACK_RUNTIME_DIR="$runtime_root"
chmod 700 "$runtime_root"
# Fresh servers pin a local TLS certificate, including for production-host tests.
if ! docker container inspect scanback-sql >/dev/null 2>&1; then
  command -v openssl >/dev/null || { printf '%s\n' 'Install openssl for development SQL TLS.' >&2; exit 1; }
  tls_root="$runtime_root/sql-tls"
  mkdir -p "$tls_root"
  if [[ ! -f "$tls_root/server.key" || ! -f "$runtime_root/sql-server.cer" ]]; then
    if [[ -f "$tls_root/server.key" || -f "$runtime_root/sql-server.cer" ]]; then
      printf '%s\n' 'Incomplete existing SQL TLS files: restore the matching certificate and key before setup.' >&2; exit 1
    fi
    openssl req -x509 -newkey rsa:3072 -sha256 -days 365 -nodes -subj '/CN=localhost' \
      -addext 'subjectAltName=DNS:localhost,IP:127.0.0.1' \
      -addext 'extendedKeyUsage=serverAuth' -addext 'keyUsage=critical,digitalSignature,keyEncipherment' \
      -keyout "$tls_root/server.key" -out "$tls_root/server.pem" 2>"$tls_root/certificate-setup.log"
    openssl x509 -in "$tls_root/server.pem" -outform DER -out "$runtime_root/sql-server.cer"
  fi
  printf '%s\n' '[network]' 'tlscert = /var/opt/mssql/secrets/server.pem' \
    'tlskey = /var/opt/mssql/secrets/server.key' 'forceencryption = 1' 'tlsprotocols = 1.2' > "$tls_root/mssql.conf"
fi
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
certificate=root/'sql-server.cer'
if certificate.exists():
    connection=connection.replace('Encrypt=true;TrustServerCertificate=true;', 'Encrypt=Mandatory;TrustServerCertificate=false;Server Certificate='+str(certificate)+';')
settings=pathlib.Path('src/ScanBack.Web/appsettings.Local.json')
if not settings.exists():
    settings.write_text(json.dumps({
        'ConnectionStrings':{'DefaultConnection':connection},
        'Site':{'BaseUrl':'http://localhost:5080'},
        'Encryption':{'KeyDirectory':str(root/'keys')},
        'Storage':{'PhotoDirectory':str(root/'photos')}
    },indent=2));settings.chmod(0o600)
test=root/'test-config.json'
if not test.exists():test.write_text(json.dumps({'TestConnectionString':connection}));test.chmod(0o600)
PY
sql_image="${SCANBACK_SQL_IMAGE:-mcr.microsoft.com/mssql/server:2022-latest}"
if ! docker image inspect "$sql_image" >/dev/null 2>&1; then docker pull "$sql_image"; fi
if ! docker container inspect scanback-sql >/dev/null 2>&1; then
  mkdir -p "$runtime_root/sql-data"
  docker run --rm --user 0 --entrypoint /bin/sh -v "$runtime_root/sql-data:/data" -v "$runtime_root/sql-tls:/tls:ro" "$sql_image" -c 'mkdir -p /data/secrets && cp /tls/server.pem /tls/server.key /data/secrets/ && cp /tls/mssql.conf /data/mssql.conf && chown -R 10001:0 /data && chmod 770 /data && chmod 700 /data/secrets && chmod 600 /data/secrets/server.key'
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
# Schema changes and baseline initialization belong to the operator, never to
# web startup. Build the deployment tool if its output is not present yet.
development_config="$scanback_project_root/src/ScanBack.Web/appsettings.Local.json"
dotnet run --project src/ScanBack.Deployment --no-restore -- migrate --environment Development --config "$development_config"
dotnet run --project src/ScanBack.Deployment --no-build --no-restore -- initialize --environment Development --config "$development_config"
printf '%s\n' 'SQL Server, the initialized development database, and the local SMTP inbox are ready. Start the application with scripts/run.sh.'
