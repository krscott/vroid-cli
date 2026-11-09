# BepInEx with its Doorstop proxy, .NET runtime, and Unity base libraries.
# Sessions link to it read-only. The bridge plugin and BepInEx.cfg are
# separate, so changing them reinstalls neither VRoid nor BepInEx.
{
  lib,
  stdenvNoCC,
  fetchurl,
  unzip,
}:
let
  loader = fetchurl {
    url = "https://builds.bepinex.dev/projects/bepinex_be/788/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788%2B5b766a3.zip";
    sha256 = "f4cc496bd098a0df4164b81e3737297707f13a47c2478dba2f60eefab784817a";
  };
  unity = fetchurl {
    url = "https://unity.bepinex.dev/libraries/6000.0.62.zip";
    sha256 = "a3c6490d9692d8cf22930b10c80081d1b3c5e93ce98c8306562e77accf2bd268";
  };
in
stdenvNoCC.mkDerivation {
  pname = "vroid-bepinex";
  version = "6.0.0-be.788";
  dontUnpack = true;
  nativeBuildInputs = [ unzip ];
  installPhase = ''
    runHook preInstall
    unzip -q ${loader} -d "$out"
    install -Dm444 ${unity} "$out/BepInEx/unity-libs/6000.0.62.zip"
    runHook postInstall
  '';
  meta = {
    description = "BepInEx IL2CPP loader with Unity base libraries for VRoid Studio";
    # BepInEx is LGPL-2.1; Unity's libraries are proprietary.
    license = with lib.licenses; [
      lgpl21Only
      unfree
    ];
    platforms = [ "x86_64-linux" ];
  };
}
