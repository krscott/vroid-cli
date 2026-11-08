{
  lib,
  stdenv,
}:

stdenv.mkDerivation {
  name = "vroid-cli";

  src = lib.cleanSource ./.;

  nativeBuildInputs = [ ];
  buildInputs = [ ];

  installPhase = ''
    runHook preInstall

    mkdir -p "$out/bin"
    printf '%s\n' \
      '#!/usr/bin/env bash' \
      'set -euo pipefail' \
      'echo "Hello from vroid-cli"' \
      >"$out/bin/vroid-cli"
    chmod +x "$out/bin/vroid-cli"

    runHook postInstall
  '';

  meta.mainProgram = "vroid-cli";
}
