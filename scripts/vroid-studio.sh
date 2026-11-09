#!/usr/bin/env bash
# Run the store's VRoid with the bridge from the session's own Wine prefix.
# vroid-cli launch runs this on the session's private desktop.
set -euo pipefail

: "${VROID_SESSION_DIR:?Start sessions with vroid-cli launch}"
for name in VROID_WINE VROID_APP VROID_INTEROP VROID_BEPINEX VROID_BRIDGE; do
    : "${!name:?Missing packaged $name; run the vroid-studio wrapper}"
done

export WINEPREFIX=$VROID_SESSION_DIR/wine
export WINEDEBUG=fixme-all
# Without these overrides Wine's Mono and Gecko setup stalls on the private desktop.
overrides='mscoree,mshtml='
wine=$VROID_WINE/bin

# Windows path of a Unix path.
windows() {
    printf 'Z:%s' "${1//\//\\}"
}

# Create the prefix before VRoid starts. Wine updates an existing one itself.
shopt -s nullglob
profiles=("$WINEPREFIX"/drive_c/users/*/AppData)
shopt -u nullglob
if ((${#profiles[@]} == 0)); then
    WINEDLLOVERRIDES=$overrides "$wine/wineboot" -i
    "$wine/wineserver" -w
fi

# BepInEx's root: its own files are links into the store; config, cache, and
# logs are the session's. BepInEx rewrites its config, so it gets a copy.
root=$VROID_SESSION_DIR/bepinex
mkdir -p "$root/config"
ln -sfn "$VROID_BEPINEX/BepInEx/core" "$root/core"
ln -sfn "$VROID_BRIDGE" "$root/plugins"
cp --no-preserve=mode "$VROID_BEPINEX/BepInEx/config/BepInEx.cfg" "$root/config/BepInEx.cfg"

export WINEDLLOVERRIDES="winhttp=n,b;$overrides"
export VROID_BRIDGE_DIR=Z:$VROID_SESSION_DIR/rpc
# BepInEx otherwise writes startup failures next to the read-only exe.
export BEPINEX_PRELOADER_LOG
BEPINEX_PRELOADER_LOG=$(windows "$root/preloader.log")

exec "$wine/wine" "$VROID_APP/VRoidStudio.exe" \
    -screen-width 2560 -screen-height 1440 -screen-fullscreen 0 \
    -logFile "$(windows "$VROID_SESSION_DIR/player.log")" \
    --doorstop-enabled true \
    --doorstop-target-assembly "$(windows "$root/core/BepInEx.Unity.IL2CPP.dll")" \
    --doorstop-clr-corlib-dir "$(windows "$VROID_BEPINEX/dotnet")" \
    --doorstop-clr-runtime-coreclr-path "$(windows "$VROID_BEPINEX/dotnet/coreclr.dll")" \
    --unhollowed-path "$(windows "$VROID_INTEROP")"
