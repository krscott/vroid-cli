{
  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";
    flake-utils.url = "github:numtide/flake-utils";
  };

  outputs =
    {
      nixpkgs,
      flake-utils,
      ...
    }:
    let
      supportedSystems = [
        "x86_64-linux"
        "aarch64-linux"
        "x86_64-darwin"
        "aarch64-darwin"
      ];
    in
    flake-utils.lib.eachSystem supportedSystems (
      system:
      let
        pkgs = nixpkgs.legacyPackages.${system};

        package = pkgs.callPackage ./default.nix { };
      in
      {
        packages = {
          "flake-start" = package;
          default = package;
        };

        devShells.default = pkgs.mkShell {
          inputsFrom = [ package ];

          packages = with pkgs; [
            just
            nixfmt
            shfmt
            shellcheck
            bash
          ];
        };

        formatter = pkgs.nixfmt;
      }
    );
}
