#!/bin/bash
# Builds the Linux release asset (#110) for the update helper: DeviceMaster-linux-x64.tar.gz (the
# self-contained headless publish, root-owned, the binary executable) + its .sha256. Needs Docker;
# compiles in a memory-capped dotnet/sdk:9.0 container (never on the Unraid host).
#   scripts/update-helper/build-linux-release.sh <commit> <out dir>
# then attach both files to the GitHub release with the Windows setup exe.
set -eu
COMMIT="${1:?commit}"
OUT="${2:?out dir}"
SRC=$(mktemp -d)
trap 'docker run --rm -v "$SRC":/s mcr.microsoft.com/dotnet/sdk:9.0 rm -rf /s/repo >/dev/null 2>&1; rm -rf "$SRC"' EXIT
git clone -q https://github.com/elliot-borst/DeviceMaster.git "$SRC/repo"
git -C "$SRC/repo" checkout -q "$COMMIT"
docker volume create dm-nuget >/dev/null
docker run --rm --memory 4g --cpus 4 -v "$SRC/repo":/src -v dm-nuget:/root/.nuget/packages -w /src mcr.microsoft.com/dotnet/sdk:9.0 sh -c "
  git config --global --add safe.directory /src
  dotnet publish src/DeviceMaster.App.Headless/DeviceMaster.App.Headless.csproj -c Release -r linux-x64 --self-contained true \
    -p:EnableWindowsTargeting=true -p:PublishSingleFile=false -p:SourceRevisionId=$COMMIT -o /out -nologo -v q
  chmod 755 /out/DeviceMaster.App.Headless
  tar --owner=0 --group=0 -C /out -czf /src/DeviceMaster-linux-x64.tar.gz .
  cd /src && sha256sum DeviceMaster-linux-x64.tar.gz > DeviceMaster-linux-x64.tar.gz.sha256"
mkdir -p "$OUT"
cp "$SRC/repo/DeviceMaster-linux-x64.tar.gz" "$SRC/repo/DeviceMaster-linux-x64.tar.gz.sha256" "$OUT/"
cat "$OUT/DeviceMaster-linux-x64.tar.gz.sha256"
