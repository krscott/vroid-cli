#!/usr/bin/env bash
# Run the store's VRoid with the bridge from the session's own Wine prefix.
# vroid-cli launch runs this on the session's private desktop.
set -euo pipefail

fatal() {
    printf '%s\n' "$*" >&2
    exit 1
}

: "${VROID_SESSION_DIR:?Start sessions with vroid-cli launch}"
for name in VROID_WINE VROID_APP VROID_INTEROP VROID_BEPINEX VROID_BRIDGE VROID_BEPINEX_CONFIG; do
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

user_profiles() {
    shopt -s nullglob
    profiles=("$WINEPREFIX"/drive_c/users/*/AppData)
    shopt -u nullglob
}

# Create the prefix before VRoid starts. Wine updates an existing one itself.
user_profiles
if ((${#profiles[@]} == 0)); then
    WINEDLLOVERRIDES=$overrides "$wine/wineboot" -i
    "$wine/wineserver" -w
    user_profiles
fi

# R-SES-8: VRoid skips its first-run welcome, terms, and tutorial dialogs when
# its preferences record them as done, and fills in the other preferences on
# start. VRoid writes the file only after acceptance, so an existing file is
# never edited.
((${#profiles[@]} == 1)) || fatal "Expected one Wine user profile in $WINEPREFIX/drive_c/users; found ${#profiles[@]}. Inspect that directory, then delete $VROID_SESSION_DIR or use another session name."
preferences="${profiles[0]}/LocalLow/pixiv/VRoid Studio/preferences/editoroption.xml"
if [[ ! -e $preferences ]]; then
    mkdir -p "${preferences%/*}"
    {
        printf '\xef\xbb\xbf' # VRoid writes UTF-8 with a byte order mark.
        cat <<'EOF'
<?xml version="1.0" encoding="utf-8"?>
<EditorOption>
  <Preference>
    <AllAgreementsAreAccepted>Yes</AllAgreementsAreAccepted>
    <UpdatedPrivacyPolicyAreAccepted>Yes</UpdatedPrivacyPolicyAreAccepted>
    <tutorialIsRead>true</tutorialIsRead>
  </Preference>
</EditorOption>
EOF
    } >"$preferences.tmp"
    mv "$preferences.tmp" "$preferences"
else
    saved=$(<"$preferences")
    if [[ $saved != *'<AllAgreementsAreAccepted>Yes</AllAgreementsAreAccepted>'* ||
        $saved != *'<UpdatedPrivacyPolicyAreAccepted>Yes</UpdatedPrivacyPolicyAreAccepted>'* ]]; then
        fatal "VRoid's preferences in $preferences do not record its accepted terms. Delete that file to reset VRoid's preferences, then launch again."
    fi
fi

# VRoid's watcher for saved custom items stops for good if their folder is
# missing when VRoid starts, as in a new prefix, and VRoid then never marks
# worn items that are not built in, which apply relies on (R-EDIT-10).
mkdir -p "${profiles[0]}/LocalLow/pixiv/VRoid Studio/custom_items"

# BepInEx's root: its own files are links into the store; config, cache, and
# logs are the session's. BepInEx rewrites its config, so it gets a copy.
root=$VROID_SESSION_DIR/bepinex
mkdir -p "$root/config"
ln -sfn "$VROID_BEPINEX/BepInEx/core" "$root/core"
ln -sfn "$VROID_BRIDGE" "$root/plugins"
cp --no-preserve=mode "$VROID_BEPINEX_CONFIG" "$root/config/BepInEx.cfg"

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
