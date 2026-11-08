{
  lib,
  stdenv,
}:

stdenv.mkDerivation {
  name = "flake-start";

  src = lib.cleanSource ./.;

  nativeBuildInputs = [ ];
  buildInputs = [ ];

  installPhase = ''
    runHook preInstall

    mkdir -p "$out/bin"
    printf '%s\n' \
      '#!/usr/bin/env bash' \
      'set -euo pipefail' \
      'echo "Hello from flake-start"' \
      >"$out/bin/flake-start"
    chmod +x "$out/bin/flake-start"

    runHook postInstall
  '';

  meta.mainProgram = "flake-start";
}
