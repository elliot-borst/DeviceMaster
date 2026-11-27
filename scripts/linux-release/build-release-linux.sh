#!/bin/bash
# Builds a whole DeviceMaster release on Linux (#128), the second release location next to the Windows
# PC's build-installer.ps1: the Windows setup (DeviceMaster.exe cross-compiled for win-x64, Inno Setup
# under Wine) and the Linux build for the hosts' update helper (#110). Same asset names as a Windows-built
# release; publish them with `gh release create` as in CLAUDE.md.
#
#   build-release-linux.sh <commit> <out dir> [--require-signing]
#
# Signing (#123): with DM_SIGN_KEY_ID set (hex PKCS#11 object id of the code-signing key; in the environment or
# ~/.config/devicemaster/release.env) and the token
# reachable, DeviceMaster.exe, the setup and its uninstaller are Authenticode-signed (SHA-256, timestamped)
# through sign-pkcs11.sh. For Certum's cloud certificate that means SimplySign Desktop logged in on this
# machine's desktop. Token not reachable: warning and an unsigned build, or an error with --require-signing.
#
# Needs: git, a .NET 9 SDK (DOTNET, default ~/.dotnet-dm/dotnet), Wine with Inno Setup 6 (WINEPREFIX,
# default ~/.wine-dm, ISCC at C:\InnoSetup6\ISCC.exe), xvfb-run, osslsigncode, pkcs11-provider, opensc.
set -euo pipefail
COMMIT="${1:?usage: build-release-linux.sh <commit> <out dir> [--require-signing]}"
OUT="${2:?out dir}"
REQUIRE_SIGNING=0
[ "${3:-}" = "--require-signing" ] && REQUIRE_SIGNING=1

# this machine's settings (e.g. DM_SIGN_KEY_ID=…), kept outside the repo
CONF="${XDG_CONFIG_HOME:-$HOME/.config}/devicemaster/release.env"
# shellcheck source=/dev/null
[ -f "$CONF" ] && . "$CONF"

DOTNET="${DOTNET:-$HOME/.dotnet-dm/dotnet}"
export WINEPREFIX="${WINEPREFIX:-$HOME/.wine-dm}" WINEDEBUG=-all
ISCC='C:\InnoSetup6\ISCC.exe'
export DM_PKCS11_MODULE="${DM_PKCS11_MODULE:-/usr/lib/libSimplySignPKCS.so}"
export DM_SIGN_LIBDIR="${DM_SIGN_LIBDIR:-/usr/lib/procertum-simplysign-desktop}"
export DM_TIMESTAMP_URL="${DM_TIMESTAMP_URL:-http://time.certum.pl}"
INTERMEDIATE="${DM_SIGN_INTERMEDIATE:-$DM_SIGN_LIBDIR/CACerts/ccsca2021.der}"
HERE="$(cd "$(dirname "$0")" && pwd)"

WORK=$(mktemp -d "$HOME/dm-release.XXXXXX")
trap 'rm -rf "$WORK"' EXIT
git clone -q https://github.com/elliot-borst/DeviceMaster.git "$WORK/src"
git -C "$WORK/src" checkout -q "$COMMIT"
cd "$WORK/src"
VERSION=$(grep -oP '<Version>\K\d+' Directory.Build.props)
echo "DeviceMaster v$VERSION at $COMMIT"

# ---- signing: is the key reachable? ----
SIGN=0
why=""
if [ -z "${DM_SIGN_KEY_ID:-}" ]; then
  why="no DM_SIGN_KEY_ID set"
else
  export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"
  export DBUS_SESSION_BUS_ADDRESS="${DBUS_SESSION_BUS_ADDRESS:-unix:path=$XDG_RUNTIME_DIR/bus}"
  if LD_LIBRARY_PATH="$DM_SIGN_LIBDIR" pkcs11-tool --module "$DM_PKCS11_MODULE" --read-object --type cert \
       --id "$DM_SIGN_KEY_ID" -o "$WORK/cert.der" >/dev/null 2>&1; then
    openssl x509 -inform der -in "$WORK/cert.der" -out "$WORK/chain.pem"
    openssl x509 -inform der -in "$INTERMEDIATE" >> "$WORK/chain.pem"
    if openssl x509 -in "$WORK/chain.pem" -noout -checkend 0 >/dev/null; then
      export DM_SIGN_CHAIN="$WORK/chain.pem"
      SIGN=1
      echo "Signing as: $(openssl x509 -in "$WORK/chain.pem" -noout -subject)"
    else
      why="the certificate has expired"
    fi
  else
    why="the signing token isn't reachable (is SimplySign Desktop logged in on this machine's desktop?)"
  fi
fi
if [ $SIGN = 0 ]; then
  [ $REQUIRE_SIGNING = 1 ] && { echo "Signing required but $why" >&2; exit 1; }
  echo "WARNING: building UNSIGNED: $why" >&2
fi

# ---- Windows app (cross-compiled) ----
"$DOTNET" publish src/DeviceMaster.Ui -c Release -r win-x64 --self-contained -p:PublishSingleFile=true \
  -p:EnableWindowsTargeting=true -p:SourceRevisionId="$COMMIT" -o dist/ui -nologo -v q
rm -f dist/ui/*.pdb
[ $SIGN = 1 ] && bash "$HERE/sign-pkcs11.sh" dist/ui/DeviceMaster.exe >/dev/null

# ---- installer (Inno Setup under Wine; it signs the setup and uninstaller through sign.bat) ----
iscc_args=("/Q" "/DMyAppVersion=$VERSION")
if [ $SIGN = 1 ]; then
  # Wine can start a Unix program but can't wait for it or read its exit code: sign-pkcs11.sh leaves
  # <file>.done and <file>.ok markers that this batch file waits for (up to 3 minutes)
  sh_unix="$HERE/sign-pkcs11.sh"
  printf '@del "%%~1.ok" "%%~1.done" 2>nul\r\n@start /unix %s "%%~1"\r\n@set /a n=0\r\n:wait\r\n@if exist "%%~1.done" goto done\r\n@set /a n+=1\r\n@if %%n%% geq 180 exit /b 2\r\n@timeout /t 1 /nobreak >nul\r\n@goto wait\r\n:done\r\n@del "%%~1.done"\r\n@if exist "%%~1.ok" (del "%%~1.ok" & exit /b 0)\r\n@exit /b 1\r\n' "$sh_unix" > "$WORK/sign.bat"
  iscc_args+=("/Sdmsign=$(winepath -w "$WORK/sign.bat") \$f" "/DSignTool=dmsign")
fi
xvfb-run -a wine "$ISCC" "${iscc_args[@]}" installer.iss
if [ $SIGN = 1 ]; then
  for f in dist/ui/DeviceMaster.exe dist/DeviceMaster-Setup.exe; do
    v=$(osslsigncode verify -in "$f" 2>&1 || true)
    grep -q "Subject: CN=" <<<"$v" || { echo "$f carries no signature:" >&2; echo "$v" | tail -5 >&2; exit 1; }
  done
fi

# ---- Linux build for the update helper ----
"$DOTNET" publish src/DeviceMaster.App.Headless/DeviceMaster.App.Headless.csproj -c Release -r linux-x64 \
  --self-contained true -p:EnableWindowsTargeting=true -p:PublishSingleFile=false -p:SourceRevisionId="$COMMIT" \
  -o "$WORK/linux" -nologo -v q
chmod 755 "$WORK/linux/DeviceMaster.App.Headless"
tar --owner=0 --group=0 -C "$WORK/linux" -czf dist/DeviceMaster-linux-x64.tar.gz .
(cd dist && sha256sum DeviceMaster-linux-x64.tar.gz > DeviceMaster-linux-x64.tar.gz.sha256)

mkdir -p "$OUT"
cp dist/DeviceMaster-Setup.exe dist/DeviceMaster-linux-x64.tar.gz dist/DeviceMaster-linux-x64.tar.gz.sha256 "$OUT/"
(cd "$OUT" && sha256sum DeviceMaster-Setup.exe DeviceMaster-linux-x64.tar.gz)
echo "Done: v$VERSION in $OUT ($([ $SIGN = 1 ] && echo signed || echo UNSIGNED))"
