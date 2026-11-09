{
  lib,
  buildPythonApplication,
  setuptools,
  pydantic,
  pytestCheckHook,
  bash,
  coreutils,
  git,
  framewisp,
  vroid-studio,
  vroid-bridge,
}:
buildPythonApplication {
  pname = "vroid-cli";
  version = "0.1.0";
  src = lib.fileset.toSource {
    root = ./.;
    fileset = lib.fileset.unions [
      ./pyproject.toml
      ./vroid_cli
      ./tests
      ./scripts
      ./.agents/skills/vroid-cli/SKILL.md
    ];
  };
  pyproject = true;
  build-system = [ setuptools ];
  dependencies = [ pydantic ];
  nativeCheckInputs = [
    pytestCheckHook
    bash
    coreutils
    git
  ];
  pythonImportsCheck = [ "vroid_cli" ];
  makeWrapperArgs = [
    "--set"
    "VROID_BRIDGE_BUNDLE"
    "${vroid-bridge}"
    "--set"
    "VROID_CLI_SKILL"
    "${./.agents/skills/vroid-cli/SKILL.md}"
    "--prefix"
    "PATH"
    ":"
    (lib.makeBinPath [
      coreutils
      framewisp
      vroid-studio
      git
    ])
  ];
  meta = {
    description = "Edit VRoid Studio characters through its internal API on a private desktop";
    platforms = [ "x86_64-linux" ];
    mainProgram = "vroid-cli";
  };
}
