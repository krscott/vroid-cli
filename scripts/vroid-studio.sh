#!/usr/bin/env bash
# Install VRoid and the bridge into the session's Wine prefix, then run VRoid.
# vroid-cli launch runs this on the session's private desktop.
set -euo pipefail
shopt -s failglob

fatal() {
    printf '%s\n' "$*" >&2
    exit 1
}

: "${VROID_SESSION_DIR:?Start sessions with vroid-cli launch}"
: "${VROID_INSTALLER:?Missing packaged VRoid installer}"
: "${VROID_BRIDGE_BUNDLE:?Missing packaged bridge bundle}"

export WINEPREFIX=$VROID_SESSION_DIR/wine
export WINEDEBUG=fixme-all
app_dir=$WINEPREFIX/drive_c/VRoidStudio

# R-SES-5: never repeat an incomplete installation automatically.
if [[ ! -f $VROID_SESSION_DIR/installed ]]; then
    if [[ -e $WINEPREFIX ]]; then
        fatal "Incomplete installation in $WINEPREFIX. Inspect $VROID_SESSION_DIR/installer.log, then delete $VROID_SESSION_DIR or use another session name."
    fi
    WINEDLLOVERRIDES='mscoree,mshtml=' wine "$VROID_INSTALLER" \
        /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER \
        '/DIR=C:\VRoidStudio' "/LOG=Z:$VROID_SESSION_DIR/installer.log"
    [[ -f $app_dir/VRoidStudio.exe ]] || fatal "VRoid installer did not create $app_dir/VRoidStudio.exe"
    touch "$VROID_SESSION_DIR/installed"
fi

# R-SES-6: replace an earlier bridge, including read-only trees copied from the Nix store.
marker=$app_dir/.vroid-bridge-bundle
if [[ ! -f $marker || $(<"$marker") != "$VROID_BRIDGE_BUNDLE" ]]; then
    for entry in "$VROID_BRIDGE_BUNDLE"/*; do
        installed=$app_dir/${entry##*/}
        if [[ -e $installed ]]; then
            chmod -R u+w "$installed"
        fi
    done
    cp -r --no-preserve=mode --remove-destination "$VROID_BRIDGE_BUNDLE/." "$app_dir/"
    printf '%s\n' "$VROID_BRIDGE_BUNDLE" >"$marker"
fi

export WINEDLLOVERRIDES='winhttp=n,b;mscoree,mshtml='
export VROID_BRIDGE_DIR=Z:$VROID_SESSION_DIR/rpc
# Interop generation runs out of managed memory without an explicit ceiling.
# The heap limit is hexadecimal bytes: 0x400000000 is 16 GiB.
export DOTNET_PROCESSOR_COUNT=${DOTNET_PROCESSOR_COUNT:-2}
export DOTNET_GCHeapHardLimit=${DOTNET_GCHeapHardLimit:-400000000}

exec wine "$app_dir/VRoidStudio.exe" \
    -screen-width 2560 -screen-height 1440 -screen-fullscreen 0 \
    -logFile "Z:$VROID_SESSION_DIR/player.log"
