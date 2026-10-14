# SQL Server development image

The standard development environment uses Microsoft's SQL Server 2022 Developer
container from `mcr.microsoft.com/mssql/server:2022-latest`. This is also the
preferred image when the registry and its download CDN are accessible.

Some cloud networks can access Docker Hub and `packages.microsoft.com` while
the Microsoft Container Registry CDN is unavailable. An alternative is to
build the same SQL Server 2022 engine from Microsoft's official signed Ubuntu
22.04 package:

```bash
python3 scripts/build-sql-dev-image.py
SCANBACK_SQL_IMAGE=scanback-sql-dev:16.0.4305.5-1 scripts/dev-up.sh
```

This requires Docker, Python 3.11 or newer, curl, and GnuPG. The Docker host must
support Linux AMD64 containers. The script preserves HTTPS verification, checks
Microsoft's signing-key fingerprint, verifies `InRelease`, verifies the package
manifest against its signed SHA256, and checks the package's SHA256 and size
before installation. Ubuntu dependency signature and checksum verification
remain enabled. The SQL package version and Ubuntu image digest are pinned.

The official Microsoft signing-key fingerprint is
`BC528686B50D79E339D3721CEB3E94ADBE1229CF`. Package provenance and verified metadata
remain in the build directory, outside the Git checkout. Existing proxy
bindings are passed to Docker without printing their values or writing them
into the Dockerfile. No community mirror or unsigned package is used.

SQL Server Developer edition is for development and testing, not production.
The image accepts Microsoft's SQL Server license terms and runs as user 10001,
matching the standard container's data-directory ownership. Passwords are
supplied at runtime through the protected environment file; they are never
included in the image. Application data stays in the external data directory.

To keep an image archive for a future cloud environment:

```bash
python3 scripts/build-sql-dev-image.py \
  --archive /workspace/scanback-runtime/sql-dev-image.tar
```

Before loading a previously generated archive, verify its saved checksum:

```bash
cd /workspace/scanback-runtime
sha256sum -c sql-dev-image.tar.sha256
docker load --input sql-dev-image.tar
```

The archive contains the development image, not the database or runtime
credentials. Keep the archive and SQL database files outside the repository.
Updates to the pinned engine package should repeat the complete signature and
checksum verification process. Production uses separately configured SQL Server
or Azure SQL, encrypted storage and backups, and certificate-validated TLS.
