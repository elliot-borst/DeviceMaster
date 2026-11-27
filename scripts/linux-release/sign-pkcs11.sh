#!/bin/bash
# Authenticode-signs one Windows file in place through a PKCS#11 token (#128), e.g. Certum's cloud
# certificate via SimplySign Desktop on Linux. Used by build-release-linux.sh for DeviceMaster.exe and,
# through Inno Setup's SignTool under Wine (sign.bat), for the setup and its uninstaller.
#   sign-pkcs11.sh <file>            (a Windows path from Wine, with or without quotes, is converted)
# Environment (set by build-release-linux.sh): DM_SIGN_KEY_ID (hex object id), DM_SIGN_CHAIN (PEM: the
# certificate + its intermediate), DM_PKCS11_MODULE, DM_TIMESTAMP_URL, optional DM_SIGN_LIBDIR.
# Writes <file>.ok on success and <file>.done when finished, for sign.bat, which can't get an exit code
# out of a Unix program started from Wine.
set -u
f="${1//\"/}"
if [[ "$f" == [A-Za-z]:\\* ]]; then
  f=$(winepath -u "$f" 2>/dev/null)
fi
rm -f "$f.ok"

# the cloud token is reached through the desktop session's bus, also from SSH
export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"
export DBUS_SESSION_BUS_ADDRESS="${DBUS_SESSION_BUS_ADDRESS:-unix:path=$XDG_RUNTIME_DIR/bus}"
[ -n "${DM_SIGN_LIBDIR:-}" ] && export LD_LIBRARY_PATH="$DM_SIGN_LIBDIR"
export PKCS11_PROVIDER_MODULE="$DM_PKCS11_MODULE"
uri_id=$(echo "$DM_SIGN_KEY_ID" | sed 's/../%&/g')

if osslsigncode sign -provider pkcs11 -key "pkcs11:id=$uri_id;type=private" -certs "$DM_SIGN_CHAIN" \
     -h sha256 -ts "$DM_TIMESTAMP_URL" -n DeviceMaster -i https://github.com/elliot-borst/DeviceMaster \
     -in "$f" -out "$f.signed" >"$f.signlog" 2>&1 && mv -f "$f.signed" "$f"; then
  touch "$f.ok"; rc=0
else
  rm -f "$f.signed"; cat "$f.signlog" >&2; rc=1
fi
rm -f "$f.signlog"
touch "$f.done"
exit $rc
