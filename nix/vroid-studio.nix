# VRoid Studio installed once into the store, its BepInEx interop assemblies,
# and the launcher that runs them from a session's own Wine prefix.
{
  lib,
  stdenvNoCC,
  fetchurl,
  makeWrapper,
  wineWow64Packages,
  coreutils,
  vroid-bepinex,
  vroid-bridge,
}:
let
  version = "2.16.0";
  wine = wineWow64Packages.unstable;
  meta = {
    homepage = "https://vroid.com/en/studio";
    license = lib.licenses.unfree;
    platforms = [ "x86_64-linux" ];
  };

  # A throwaway Wine prefix for a headless build. Without the overrides Wine's
  # Mono and Gecko setup stalls; the null driver needs no display.
  headlessWine = ''
    export HOME=$TMPDIR/home WINEPREFIX=$TMPDIR/wine WINEDEBUG=-all
    mkdir -p "$HOME"
    WINEDLLOVERRIDES='mscoree,mshtml=' wine reg add 'HKCU\Software\Wine\Drivers' /v Graphics /d null /f
    wineserver -k
    wineserver -w
  '';

  app = stdenvNoCC.mkDerivation {
    pname = "vroid-studio-app";
    inherit version;
    src = fetchurl {
      url = "https://download.vroid.com/dist/2cbXSb76Bf/VRoidStudio-v${version}-win.exe";
      sha256 = "617b59733cbbd6044d1826997157cf5ead8134d7efb8ac393f2614298fe87893";
    };
    dontUnpack = true;
    nativeBuildInputs = [ wine ];
    # innoextract cannot read this Inno Setup 6.7 installer, so run it.
    buildPhase = ''
      runHook preBuild
      ${headlessWine}
      WINEDLLOVERRIDES='mscoree,mshtml=' wine "$src" \
        /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER \
        '/DIR=C:\VRoidStudio' "/LOG=Z:$TMPDIR/installer.log"
      wineserver -w
      if [[ ! -f $WINEPREFIX/drive_c/VRoidStudio/VRoidStudio.exe ]]; then
        cat "$TMPDIR/installer.log" || true
        echo "The VRoid installer did not create VRoidStudio.exe" >&2
        exit 1
      fi
      runHook postBuild
    '';
    # Doorstop loads BepInEx only from a proxy DLL next to the exe.
    installPhase = ''
      runHook preInstall
      cp -r "$WINEPREFIX/drive_c/VRoidStudio" "$out"
      rm "$out"/unins000.*
      cp ${vroid-bepinex}/winhttp.dll ${vroid-bepinex}/.doorstop_version "$out/"
      runHook postInstall
    '';
    meta = meta // {
      description = "VRoid Studio installed for read-only use from the Nix store";
    };
  };

  # BepInEx's unhollowed directory: interop/ and the extracted unity-libs/,
  # which BepInEx hashes with GameAssembly.dll to check that interop/ is current.
  interop = stdenvNoCC.mkDerivation {
    pname = "vroid-studio-interop";
    inherit version;
    dontUnpack = true;
    nativeBuildInputs = [ wine ];
    buildPhase = ''
      runHook preBuild
      ${headlessWine}
      root=$TMPDIR/bepinex
      mkdir -p "$root/config" "$out/unity-libs"
      ln -s ${vroid-bepinex}/BepInEx/core "$root/core"
      install -m644 ${vroid-bepinex}/BepInEx/config/BepInEx.cfg "$root/config/"
      cp ${vroid-bepinex}/BepInEx/unity-libs/*.zip "$out/unity-libs/"
      w() { printf 'Z:%s' "''${1//\//\\}"; }
      export WINEDLLOVERRIDES='winhttp=n,b;mscoree,mshtml='
      export BEPINEX_PRELOADER_LOG=$(w "$TMPDIR/preloader.log")
      # Generation runs out of managed memory without an explicit ceiling.
      # The heap limit is hexadecimal bytes: 0x400000000 is 16 GiB.
      export DOTNET_PROCESSOR_COUNT=2 DOTNET_GCHeapHardLimit=400000000
      wine ${app}/VRoidStudio.exe -batchmode -nographics \
        -logFile "$(w "$TMPDIR/player.log")" \
        --doorstop-enabled true \
        --doorstop-target-assembly "$(w "$root/core/BepInEx.Unity.IL2CPP.dll")" \
        --doorstop-clr-corlib-dir "$(w ${vroid-bepinex}/dotnet)" \
        --doorstop-clr-runtime-coreclr-path "$(w ${vroid-bepinex}/dotnet/coreclr.dll)" \
        --unhollowed-path "$(w "$out")" >"$TMPDIR/wine.log" 2>&1 &
      pid=$!
      # BepInEx writes the hash last, then keeps VRoid running.
      start=$SECONDS
      until [[ -s $out/interop/assembly-hash.txt ]]; do
        if ! kill -0 "$pid" 2>/dev/null || ((SECONDS - start > 1200)); then
          tail -n 40 "$root/LogOutput.log" "$TMPDIR"/{preloader,player,wine}.log || true
          echo "VRoid exited or timed out before BepInEx generated interop assemblies" >&2
          exit 1
        fi
        sleep 1
      done
      echo "Generated interop assemblies in $((SECONDS - start)) s"
      wineserver -k
      wait "$pid" || true
      rm "$out"/unity-libs/*.zip
      runHook postBuild
    '';
    dontInstall = true;
    meta = meta // {
      description = "BepInEx interop assemblies generated from VRoid Studio";
    };
  };
in
stdenvNoCC.mkDerivation {
  pname = "vroid-studio";
  inherit version;
  dontUnpack = true;
  nativeBuildInputs = [ makeWrapper ];
  # Every store path the running app reads is in its environment, which
  # keeps it from Nix's garbage collector while the app runs (R-SES-11).
  installPhase = ''
    runHook preInstall
    install -Dm555 ${../scripts/vroid-studio.sh} "$out/libexec/vroid-studio"
    patchShebangs "$out/libexec"
    makeWrapper "$out/libexec/vroid-studio" "$out/bin/vroid-studio" \
      --set VROID_WINE ${wine} \
      --set VROID_APP ${app} \
      --set VROID_INTEROP ${interop} \
      --set VROID_BEPINEX ${vroid-bepinex} \
      --set VROID_BRIDGE ${vroid-bridge} \
      --prefix PATH : ${lib.makeBinPath [ coreutils ]}
    runHook postInstall
  '';
  passthru = { inherit app interop; };
  meta = {
    description = "Runs the store's VRoid Studio with the vroid-cli bridge in a session's Wine prefix";
    homepage = "https://github.com/krscott/vroid-cli";
    platforms = [ "x86_64-linux" ];
    mainProgram = "vroid-studio";
  };
}
