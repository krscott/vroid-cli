# flake-start

Minimal Nix flake project template.

To start a project with this template, run:

```sh
./init-template.sh new_project_name
```

Start from a clean Git checkout and use a name beginning with an ASCII letter.
Initialization replaces this README with [README_TEMPLATE.md](README_TEMPLATE.md)
and removes the template license. Choose a license for your project afterward.

## Development

Update dependencies:

```sh
nix flake update
```

Enter the development shell:

```sh
nix develop
```

Build and run the dummy package:

```sh
nix build
nix run
```

Useful development commands:

```sh
just build
just run
just test
just format
just check
```

`just build` and `just run` use the local build path inside `nix develop`.
`just test` builds the local program and checks its output.
`just check` checks tracked files with the formatters and ShellCheck, then runs
`nix flake check`. Install the formatting hook with `just install-hooks`.
