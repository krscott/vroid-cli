# The vroid-cli bridge plugin, built against BepInEx's assemblies.
{
  lib,
  stdenvNoCC,
  dotnet-sdk,
  vroid-bepinex,
}:
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
  nativeBuildInputs = [ dotnet-sdk ];
  env = {
    DOTNET_CLI_TELEMETRY_OPTOUT = "1";
    DOTNET_NOLOGO = "1";
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1";
  };
  buildPhase = ''
    runHook preBuild
    export DOTNET_CLI_HOME=$TMPDIR/dotnet-home
    dotnet build plugin/VroidBridge.csproj -c Release -p:LoaderDir=${vroid-bepinex} -o build
    runHook postBuild
  '';
  doCheck = true;
  checkPhase = ''
    runHook preCheck
    dotnet run --project tests/protocol -c Release
    runHook postCheck
  '';
  # Sessions link BepInEx's plugins directory here.
  installPhase = ''
    runHook preInstall
    install -Dm444 build/VroidBridge.dll "$out/VroidBridge.dll"
    runHook postInstall
  '';
  meta = {
    description = "BepInEx plugin that lets vroid-cli call VRoid Studio's internal methods";
    license = lib.licenses.mit;
    platforms = [ "x86_64-linux" ];
  };
}
