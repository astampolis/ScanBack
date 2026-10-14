#!/usr/bin/env python3
"""Build a development-only SQL Server 2022 image from verified Microsoft packages.

This is a fallback for networks that can reach packages.microsoft.com and Docker
Hub but cannot reach the Microsoft Container Registry's download CDN. It does
not change proxy settings or disable TLS, repository signatures, or checksums.
"""

from __future__ import annotations

import argparse
import hashlib
import ipaddress
import os
from pathlib import Path
import re
import shutil
import socket
import subprocess
import sys
import urllib.parse


MICROSOFT_KEY_URL = "https://packages.microsoft.com/keys/microsoft.asc"
MICROSOFT_KEY_FINGERPRINT = "BC528686B50D79E339D3721CEB3E94ADBE1229CF"
REPOSITORY = "https://packages.microsoft.com/ubuntu/22.04/mssql-server-2022"
SQL_VERSION = "16.0.4305.5-1"
IMAGE = f"scanback-sql-dev:{SQL_VERSION}"
UBUNTU_IMAGE = (
    "ubuntu:22.04@sha256:"
    "5ec03bb3441e8b0bf3b4f9cd4629a1ae763010dc3035bb8da3ae6cf026486401"
)


def run(arguments: list[str], *, env: dict[str, str] | None = None, quiet: bool = False) -> str:
    result = subprocess.run(
        arguments,
        env=env,
        text=True,
        stdout=subprocess.PIPE if quiet else None,
        stderr=subprocess.PIPE if quiet else None,
        check=False,
    )
    if result.returncode:
        # Tool arguments never contain credentials. Keep proxy-bearing command
        # output private rather than echoing arbitrary diagnostic text.
        raise RuntimeError(f"{arguments[0]} failed (exit {result.returncode}).")
    return result.stdout or ""


def download(url: str, destination: Path) -> None:
    temporary = destination.with_name(destination.name + ".download")
    run([
        "curl", "--fail", "--location", "--silent", "--show-error",
        "--retry", "2", "--max-time", "300", url, "--output", str(temporary),
    ], quiet=True)
    temporary.replace(destination)


def sha256_file(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def parse_package(text: str) -> dict[str, str]:
    result: dict[str, str] = {}
    for line in text.splitlines():
        if line and not line.startswith(" ") and ": " in line:
            key, value = line.split(": ", 1)
            result[key] = value
    return result


def verified_package(work: Path) -> tuple[Path, str]:
    gpg_home = work / "gnupg"
    gpg_home.mkdir(mode=0o700, exist_ok=True)
    gpg_home.chmod(0o700)
    key = work / "microsoft.asc"
    keyring = work / "microsoft.gpg"
    release = work / "InRelease"
    manifest = work / "Packages"
    download(MICROSOFT_KEY_URL, key)
    key_info = run([
        "gpg", "--homedir", str(gpg_home), "--batch", "--show-keys", "--with-colons", str(key),
    ], quiet=True)
    primary_fingerprint = next(
        (line.split(":")[9] for line in key_info.splitlines() if line.startswith("fpr:")), None
    )
    primary_key_count = sum(line.startswith("pub:") for line in key_info.splitlines())
    if primary_key_count != 1 or primary_fingerprint != MICROSOFT_KEY_FINGERPRINT:
        raise RuntimeError("Microsoft repository signing-key fingerprint does not match the pinned key.")
    run([
        "gpg", "--homedir", str(gpg_home), "--batch", "--yes", "--dearmor",
        "--output", str(keyring), str(key),
    ], quiet=True)
    download(f"{REPOSITORY}/dists/jammy/InRelease", release)
    run([
        "gpg", "--homedir", str(gpg_home), "--batch", "--no-default-keyring",
        "--keyring", str(keyring), "--verify", str(release),
    ], quiet=True)
    release_text = release.read_text()
    manifest_hash = re.search(
        r"^ ([0-9a-f]{64})\s+\d+ main/binary-amd64/Packages$", release_text, re.MULTILINE
    )
    if manifest_hash is None:
        raise RuntimeError("Signed release does not contain the expected SHA256 package-manifest entry.")
    download(f"{REPOSITORY}/dists/jammy/main/binary-amd64/Packages", manifest)
    if sha256_file(manifest) != manifest_hash.group(1):
        raise RuntimeError("Package manifest does not match the signed release checksum.")
    selected = next((parse_package(section) for section in manifest.read_text().split("\n\n")
                     if parse_package(section).get("Package") == "mssql-server"
                     and parse_package(section).get("Version") == SQL_VERSION
                     and parse_package(section).get("Architecture") == "amd64"), None)
    if selected is None:
        raise RuntimeError(f"Pinned SQL Server package {SQL_VERSION} is absent from the signed repository.")
    package_hash = selected.get("SHA256", "")
    filename = selected.get("Filename", "")
    if not re.fullmatch(r"[0-9a-f]{64}", package_hash) or not filename.startswith("pool/main/m/mssql-server/"):
        raise RuntimeError("Unexpected signed package metadata.")
    if ".." in Path(filename).parts or urllib.parse.urlsplit(filename).scheme:
        raise RuntimeError("Invalid signed package location.")
    package = work / "mssql-server.deb"
    package_size = int(selected["Size"])
    if not package.is_file() or package.stat().st_size != package_size or sha256_file(package) != package_hash:
        # Microsoft appends this routing flag to its own package redirect. The
        # same official artifact is served directly by the verified HTTPS host.
        download(f"{REPOSITORY}/{filename}?geofence=true", package)
    if package.stat().st_size != package_size or sha256_file(package) != package_hash:
        raise RuntimeError("SQL Server package size or SHA256 does not match signed metadata.")
    (work / "selected-package.txt").write_text("\n".join(f"{key}: {value}" for key, value in selected.items()) + "\n")
    print(f"Verified Microsoft signing key, release, manifest, and SQL Server {SQL_VERSION} package.", flush=True)
    return package, package_hash


def docker_build(work: Path, package_hash: str) -> dict[str, str]:
    (work / "mssql-server.sha256").write_text(f"{package_hash}  mssql-server.deb\n")
    (work / ".dockerignore").write_text("*\n!Dockerfile\n!mssql-server.deb\n!mssql-server.sha256\n")
    (work / "Dockerfile").write_text(f"""FROM {UBUNTU_IMAGE}
# Package verified against Microsoft's pinned signing key and signed repository metadata.
COPY mssql-server.deb /tmp/mssql-server.deb
COPY mssql-server.sha256 /tmp/mssql-server.sha256
ENV ACCEPT_EULA=Y MSSQL_PID=Developer MSSQL_MEMORY_LIMIT_MB=2048
RUN cd /tmp && sha256sum -c mssql-server.sha256 && apt-get update -o APT::Update::Error-Mode=any && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends /tmp/mssql-server.deb && rm -rf /var/lib/apt/lists/* /tmp/mssql-server.deb /tmp/mssql-server.sha256 && mkdir -p /var/opt/mssql && chown -R mssql:mssql /var/opt/mssql
RUN usermod -u 10001 mssql && groupmod -g 10001 mssql && chown -R mssql:mssql /var/opt/mssql
LABEL org.opencontainers.image.source="{REPOSITORY}" com.scanback.sqlserver.version="{SQL_VERSION}" com.scanback.sqlserver.package-sha256="{package_hash}"
USER mssql
EXPOSE 1433
CMD ["/opt/mssql/bin/sqlservr"]
""")
    docker_env = dict(os.environ)
    docker_env.setdefault("DOCKER_CONFIG", str(work.parent / "docker-client"))
    Path(docker_env["DOCKER_CONFIG"]).mkdir(parents=True, exist_ok=True)
    arguments = ["docker", "build", "--tag", IMAGE]
    proxy_hosts: set[str] = set()
    for name in ("HTTP_PROXY", "HTTPS_PROXY", "http_proxy", "https_proxy", "NO_PROXY", "no_proxy"):
        if docker_env.get(name):
            # Docker's predefined proxy arguments are omitted from image
            # history. Values are inherited and never written to the Dockerfile.
            arguments += ["--build-arg", name]
        if name.lower() != "no_proxy" and docker_env.get(name):
            hostname = urllib.parse.urlsplit(docker_env[name]).hostname
            if hostname:
                proxy_hosts.add(hostname)
    for hostname in sorted(proxy_hosts):
        try:
            ipaddress.ip_address(hostname)
        except ValueError:
            # BuildKit may lack the cloud host's internal DNS. Resolve the
            # existing proxy hostname; retain its URL and access controls.
            arguments += ["--add-host", hostname + ":" + socket.gethostbyname(hostname)]
    arguments.append(str(work))
    run(arguments, env=docker_env)
    return docker_env


def main() -> int:
    repository = Path(__file__).resolve().parent.parent
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--work-dir", type=Path, default=repository.parent / "scanback-runtime" / "sql-build")
    parser.add_argument("--archive", type=Path, help="Optional docker-save archive outside the checkout, with a SHA256 sidecar.")
    args = parser.parse_args()
    for tool in ("curl", "gpg", "docker"):
        if not shutil.which(tool):
            raise RuntimeError(f"Required tool is unavailable: {tool}")
    work = args.work_dir.resolve()
    if work.is_relative_to(repository):
        raise RuntimeError("Keep generated SQL packages and build output outside the checkout.")
    if args.archive and args.archive.resolve().is_relative_to(repository):
        raise RuntimeError("Keep the large SQL image archive outside the checkout.")
    work.mkdir(parents=True, exist_ok=True)
    _, checksum = verified_package(work)
    docker_env = docker_build(work, checksum)
    if args.archive:
        archive = args.archive.resolve()
        archive.parent.mkdir(parents=True, exist_ok=True)
        temporary = archive.with_name(archive.name + ".tmp")
        run(["docker", "save", "--output", str(temporary), IMAGE], env=docker_env)
        temporary.replace(archive)
        archive.with_name(archive.name + ".sha256").write_text(f"{sha256_file(archive)}  {archive.name}\n")
        print(f"Saved verified development image archive: {archive}", flush=True)
    print(f"Development image ready: {IMAGE}")
    print(f"Start using SCANBACK_SQL_IMAGE={IMAGE} scripts/dev-up.sh")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
        print(f"SQL development image setup failed: {error}", file=sys.stderr)
        raise SystemExit(1)
