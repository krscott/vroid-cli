# flake-start

Project description goes here.

## Development

Enter the development shell with `nix develop`, then run:

```sh
just build
just run
just test
just format
just check
```

`just build` and `just run` use the local build path. To build or run the Nix
package, use `nix build` or `nix run`.
`just test` builds the local program and checks its output.

`just check` checks formatting, runs ShellCheck, and validates the flake without
rewriting source files. Formatting and shell checks cover tracked files; stage
new files with `git add` to include them.

Install the formatting hook with `just install-hooks`. It formats fully staged
files and leaves partially staged files alone.

Update dependencies with `nix flake update`.
