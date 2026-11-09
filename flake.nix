{
  description = "Edit VRoid Studio characters through its internal API on a private desktop";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    framewisp = {
      url = "github:krscott/framewisp/cf7b3ee39e330cfa095411273c9b565b10dae7b1";
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
            "vroid-studio"
            "vroid-bridge"
          ];
      };
      # Framewisp hardcodes software rendering, which is too slow for VRoid.
      framewisp-gles2 = pkgs.framewisp.overrideAttrs (old: {
        postPatch = (old.postPatch or "") + ''
          substituteInPlace framewisp/lib.py \
            --replace-fail 'WLR_RENDERER="pixman"' 'WLR_RENDERER="gles2"'
        '';
      });
      vroid-studio = pkgs.callPackage ./nix/vroid-studio.nix { };
      vroid-bridge = pkgs.callPackage ./nix/bridge.nix { };
      vroid-cli = pkgs.python3Packages.callPackage ./default.nix {
        framewisp = framewisp-gles2;
        inherit vroid-studio vroid-bridge;
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
        packages = with pkgs; [
          vroid-cli
          framewisp-gles2
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
      };

      checks.${system} = {
        inherit vroid-cli vroid-bridge;
      };

      formatter.${system} = pkgs.nixfmt;
    };
}
