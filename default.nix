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
      # tests/test_cli.py checks the bridge's load message, which launch reads.
      ./plugin/Plugin.cs
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
  # Ignore caller Python imports; Nix fixup still adds our packaged sites.
  postInstall = ''
    sed -i '1s/$/ -I/' "$out/bin/vroid-cli"
  '';
  makeWrapperArgs = [
    "--set"
    "VROID_CLI_SKILL"
    "${./.agents/skills/vroid-cli/SKILL.md}"
    "--prefix"
    "PATH"
    ":"
    (lib.makeBinPath [
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
