{
  description = "Edit VRoid Studio characters through its internal API on a private desktop";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    framewisp = {
      url = "github:krscott/framewisp/main";
      flake = false;
    };
  };

  outputs =
    {
      self,
      nixpkgs,
      framewisp,
    }:
    let
      # The application is validated only on x86_64 Linux.
      system = "x86_64-linux";
      lib = nixpkgs.lib;
      pkgs = import nixpkgs {
        inherit system;
        overlays = [ (import (framewisp + "/overlay.nix")) ];
        config.allowUnfreePredicate =
          package:
          builtins.elem (lib.getName package) [
            "vroid-studio-app"
            "vroid-studio-interop"
            "vroid-bepinex"
          ];
      };
      # Framewisp hardcodes software rendering, which is too slow for VRoid.
      # Framewisp's own CI runs its test suite, so this rebuild skips it.
      framewisp-gles2 = pkgs.framewisp.overridePythonAttrs (old: {
        postPatch = (old.postPatch or "") + ''
          substituteInPlace framewisp/lib.py \
            --replace-fail 'WLR_RENDERER="pixman"' 'WLR_RENDERER="gles2"'
        '';
        doCheck = false;
      });
      vroid-bepinex = pkgs.callPackage ./nix/bepinex.nix { };
      vroid-bridge = pkgs.callPackage ./nix/bridge.nix { inherit vroid-bepinex; };
      vroid-studio = pkgs.callPackage ./nix/vroid-studio.nix { inherit vroid-bepinex vroid-bridge; };
      vroid-cli = pkgs.python3Packages.callPackage ./default.nix {
        framewisp = framewisp-gles2;
        inherit vroid-studio;
      };
      python = pkgs.python3.withPackages (
        ps: with ps; [
          black
          isort
          mypy
          pydantic
          pytest
        ]
      );
    in
    {
      packages.${system} = {
        default = vroid-cli;
        framewisp = framewisp-gles2;
        inherit vroid-cli vroid-studio vroid-bridge;
      };

      devShells.${system}.default = pkgs.mkShell {
        # Keep packages with Python sites out of `packages`: their setup hook pins this
        # shell's builds in PYTHONPATH, ahead of newer store commands' sites.
        packages = with pkgs; [
          (writeShellScriptBin "framewisp" ''exec ${lib.getExe framewisp-gles2} "$@"'')
          # Shadows any inherited vroid-cli when the shell hook finds no checkout.
          (writeShellScriptBin "vroid-cli" ''
            printf '%s\n' \
              "vroid-cli: this dev shell was entered outside a vroid-cli checkout, so it has no working tree to run." \
              "Enter it from the checkout instead: cd CHECKOUT && nix develop" >&2
            exit 1
          '')
          vroid-studio
          git
          python
          pyright
          dotnet-sdk
          jq
          just
          nixfmt
          shfmt
          shellcheck
          bash
        ];
        shellHook = ''
          if root=$(git rev-parse --show-toplevel 2>/dev/null) && [[ -x $root/scripts/dev/vroid-cli ]]; then
            PATH=$root/scripts/dev:$PATH
          fi
        '';
      };

      checks.${system} = {
        inherit vroid-cli vroid-bridge;
      };

      formatter.${system} = pkgs.nixfmt;
    };
}
