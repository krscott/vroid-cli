# vroid-cli

Edit [VRoid Studio](https://vroid.com/en/studio) characters from the command
line while you keep using your computer. `vroid-cli` runs standalone VRoid
under Wine on a private [Framewisp](https://github.com/krscott/framewisp)
display and changes characters through VRoid's internal methods. It uses no
mouse or keyboard input, OCR, MCP server, or account login.

This first version opens existing `.vroid` projects, reads and changes Face and
Body numeric parameters, and saves projects. Character creation, presets,
colors, hair and texture tools, and VRM export are not supported yet.

## Requirements

- x86_64 Linux with Nix flakes and working GPU drivers. The compositor prefers
  an AMD or Intel GPU. NVIDIA-only systems are untested; set
  `WLR_RENDER_DRM_DEVICE=/dev/dri/renderD...` to choose a device.
- About 6 GB of disk per session and up to 16 GiB of memory during the first
  launch.
- Only VRoid Studio 2.16.0 is supported. The flake fetches its installer by
  hash; VRoid is proprietary, and the flake permits only it and the bridge
  bundle as unfree packages.

## Usage

```sh
nix develop   # or: nix shell github:krscott/vroid-cli
vroid-cli --session demo launch
vroid-cli --session demo open ./source.vroid
vroid-cli --session demo categories Body
vroid-cli --session demo parameters Body "Whole Body"
vroid-cli --session demo set Body "Whole Body" "Head Size" 0.125
vroid-cli --session demo save ./edited.vroid
vroid-cli --session demo stop
```

A successful command prints one JSON value on stdout. A failure exits nonzero
with its reason on stderr. `vroid-cli --help` lists the commands.

Use one session name per agent or task. Session state lives in `.vroid/NAME/`
at the Git worktree root (the current directory outside Git), or under
`VROID_STATE_DIR`. The state directory ignores itself in Git.

### Agents

The shared skill in [`.agents/skills/vroid-cli`](.agents/skills/vroid-cli/SKILL.md)
works with Codex, Claude Code (through `.claude/skills`), and OpenCode. In
another project, print it with `vroid-cli --agent-skill` or copy it into that
project's skills directory.

### First launch

The first `launch` of a session installs VRoid into its own Wine prefix and
generates app bindings. This can take 10 minutes or more. If `launch` times out,
the app keeps starting; run `launch` again to keep waiting.

VRoid then shows its welcome and terms dialogs once per session. vroid-cli never
accepts them: `launch` and edit commands fail with instructions until you do.
Read and accept them on the private desktop with Framewisp, then run `launch`
again (if it still refuses, `stop` and `launch`):

```sh
desktop=$(vroid-cli --session demo status | jq -r .desktop)
framewisp "$desktop" screenshot /tmp/demo.png
framewisp "$desktop" click X Y
```

## Recovery

`vroid-cli --session NAME status` reports whether the app runs, whether it is
ready, any pending request, and unsaved changes (`bridge.modified`). Logs are in
the session directory: `runner.log`, `installer.log`, `player.log`,
`desktop/app.log`, and `wine/drive_c/VRoidStudio/BepInEx/LogOutput.log`.

- **Timed-out request:** do not repeat it. It stays pending and blocks other
  commands until its result appears in `rpc/results/ID.json`. Restarting the
  session marks it as having an unknown outcome; inspect the project first.
- **Unsaved changes:** `stop` refuses them, and a pending request. Save to a
  new path, or use `stop --force` to discard them.
- **Another project:** save, `stop`, and `launch` before opening it.
- **Incomplete installation:** it is not repeated. Read `installer.log`, then
  delete the session directory or use another session name.
- **Unclean desktop shutdown:** run `framewisp ABSOLUTE/SESSION/desktop recover`
  as the error message says, then launch again.
- **Upgrades:** `launch` installs a new bridge only while the app is stopped. A
  running session keeps working with a newer CLI unless the file protocol
  changed; then save with the older build or stop and launch again.

## Development

```sh
nix develop
just build      # nix build
just run ARGS   # nix run -- ARGS
just test       # Python and bridge protocol tests
just check      # formatting, lint, type checks, nix flake check
just gui-smoke SESSION SOURCE.vroid
```

`just gui-smoke` needs graphics access and a session past its first-run
dialogs. It runs the workflow above with a restart and checks the saved values
and Framewisp's input log. See [DESIGN.md](DESIGN.md) for internals and
[REQUIREMENTS.md](REQUIREMENTS.md) for the guarantees.

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for third-party code and
downloads.
