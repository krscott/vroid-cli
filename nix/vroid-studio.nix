{
  lib,
  stdenvNoCC,
  fetchurl,
  makeWrapper,
  wineWow64Packages,
  coreutils,
}:
stdenvNoCC.mkDerivation {
  pname = "vroid-studio";
  version = "2.16.0";
  src = fetchurl {
    url = "https://download.vroid.com/dist/2cbXSb76Bf/VRoidStudio-v2.16.0-win.exe";
    sha256 = "617b59733cbbd6044d1826997157cf5ead8134d7efb8ac393f2614298fe87893";
  };
  dontUnpack = true;
  nativeBuildInputs = [ makeWrapper ];
  installPhase = ''
    runHook preInstall
    install -Dm444 "$src" "$out/share/vroid-studio/installer.exe"
    install -Dm555 ${../scripts/vroid-studio.sh} "$out/libexec/vroid-studio"
    patchShebangs "$out/libexec"
    makeWrapper "$out/libexec/vroid-studio" "$out/bin/vroid-studio" \
      --set VROID_INSTALLER "$out/share/vroid-studio/installer.exe" \
      --prefix PATH : ${
        lib.makeBinPath [
          wineWow64Packages.unstable
          coreutils
        ]
      }
    runHook postInstall
  '';
  meta = {
    description = "Standalone VRoid Studio for per-session Wine prefixes";
    homepage = "https://vroid.com/en/studio";
    license = lib.licenses.unfree;
    platforms = [ "x86_64-linux" ];
    mainProgram = "vroid-studio";
  };
}
