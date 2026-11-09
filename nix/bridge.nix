# BepInEx, the Unity base libraries, and the bridge plugin, laid out to be
# copied over a VRoid installation.
{
  lib,
  stdenvNoCC,
  fetchurl,
  unzip,
  dotnet-sdk,
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
  pname = "vroid-bridge";
  version = "0.1.0";
  src = lib.fileset.toSource {
    root = ../.;
    fileset = lib.fileset.unions [
      ../plugin
      ../tests/protocol
    ];
  };
  nativeBuildInputs = [
    unzip
    dotnet-sdk
  ];
  env = {
    DOTNET_CLI_TELEMETRY_OPTOUT = "1";
    DOTNET_NOLOGO = "1";
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1";
  };
  buildPhase = ''
    runHook preBuild
    export DOTNET_CLI_HOME=$TMPDIR/dotnet-home
    unzip -q ${loader} -d loader
    dotnet build plugin/VroidBridge.csproj -c Release -p:LoaderDir="$PWD/loader" -o build
    runHook postBuild
  '';
  doCheck = true;
  checkPhase = ''
    runHook preCheck
    dotnet run --project tests/protocol -c Release
    runHook postCheck
  '';
  installPhase = ''
    runHook preInstall
    cp -r loader "$out"
    install -Dm444 build/VroidBridge.dll "$out/BepInEx/plugins/VroidBridge.dll"
    install -Dm444 ${unity} "$out/BepInEx/unity-libs/6000.0.62.zip"
    install -Dm444 plugin/BepInEx.cfg "$out/BepInEx/config/BepInEx.cfg"
    runHook postInstall
  '';
  meta = {
    description = "BepInEx bundle with the vroid-cli bridge plugin";
    # The plugin is MIT; BepInEx is LGPL-2.1; Unity's libraries are proprietary.
    license = with lib.licenses; [
      mit
      lgpl21Only
      unfree
    ];
    platforms = [ "x86_64-linux" ];
  };
}
