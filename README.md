# vroid-cli

Edit [VRoid Studio](https://vroid.com/en/studio) characters from the command
line while you keep using your computer. `vroid-cli` runs standalone VRoid
under Wine on a private [Framewisp](https://github.com/krscott/framewisp)
display and changes characters through VRoid's internal methods. It uses no
mouse or keyboard input, OCR, MCP server, or account login.

> **Terms:** vroid-cli accepts VRoid Studio's first-run
> [terms of use](https://policies.pixiv.net/en.html#vroidstudio) and
> [privacy policy](https://policies.pixiv.net/en/privacy_policy.html) in every
> session it launches. Using vroid-cli assumes you have read and accepted them,
> and VRoid Studio's [guidelines](https://vroid.com/en/studio/guidelines).

It opens existing `.vroid` projects or creates new characters, changes them,
and saves them. The agent skill's
[Scope](.agents/skills/vroid-cli/SKILL.md#scope) lists what it can do so far.

## Requirements

- x86_64 Linux with Nix flakes and working GPU drivers. The compositor prefers
  an AMD or Intel GPU. NVIDIA-only systems are untested; set
  `WLR_RENDER_DRM_DEVICE=/dev/dri/renderD...` to choose a device.
- About 600 MB of disk per session. All sessions share one read-only VRoid
  install in the Nix store (about 1.4 GB). Building it once takes about a
  minute and 5 GB of memory.
- Only VRoid Studio 2.16.0 is supported. The flake fetches its installer by
  hash and installs it into the store; VRoid and Unity's libraries are
  proprietary, and the flake permits only the packages built from them as
  unfree.

## Usage

Every command is `vroid-cli SESSION REQUEST`, where `REQUEST` is a JSON object,
`@path` to read one from a file, or `-` to read stdin:

```sh
nix develop   # or: nix shell github:krscott/vroid-cli github:krscott/vroid-cli#framewisp
vroid-cli demo '{"method": "launch"}'
vroid-cli demo '{"method": "open", "path": "./source.vroid"}'
vroid-cli demo '{"method": "categories", "section": "Body"}'
vroid-cli demo '{"method": "parameters", "section": "Body", "category": "Whole Body"}'
vroid-cli demo '{"method": "apply", "Body": {"Whole Body": {"Head Size": 0.125}}}'
vroid-cli demo '{"method": "save", "path": "./edited.vroid"}'
vroid-cli demo '{"method": "stop"}'
```

A successful command prints one JSON value on stdout. A failure exits nonzero
with its reason on stderr. `vroid-cli --help` prints the agent guide and the
JSON Schema of every request and result.

### Templates

`dump` prints every item and accessory, and every Face, Hair, and Body value
and colour, as an `apply` request, so a character's look can be saved,
diffed, edited, and applied to another project:

```sh
vroid-cli demo '{"method": "dump"}' > template.json
vroid-cli other @template.json
```

`apply` checks every item and value before changing any, against the bounds
`parameters` lists, or for a worn accessory's shape sliders `accessories`, except colours of a
category whose item the request may change and the shape sliders of an added
accessory, which it checks once the new item or accessory is worn. Before the first change it writes an `undo` file, an `apply` request that restores the previous
ones; its result names the file. A category's
`"item"` names one of VRoid's built-in items, listed by `items`, or `"custom"`
for one that is not built in; `accessories` lists accessories the same way. A label that repeats within a
category, such as "Nose Size" in Face Sets, maps to an array of its values.
Categories share parameters ("Eye Size X" is in Face Sets, Eyes Sets, and
Eyeliner), so `dump` lists each once, and a request may name each label in only
one category per section. Colours are `"#RRGGBB"` as VRoid's editor shows them;
shared colours, such as skin in Face and Body, may also be set in only one
place per request.

Use one session name per agent or task; the first `launch` creates it.
Session state lives in `.vroid/NAME/` at the Git worktree root (the current
directory outside Git), or under `VROID_STATE_DIR`. The state directory
ignores itself in Git.

### Views

`render` writes a PNG of the open character on a plain background, without
changing the project or the editor's view. It shows the front, three-quarter,
side, or back of the whole character, or of its head with `"frame": "head"`,
or several views side by side, optionally under a reference image. Views are
orthographic, and the views in one image share their scale, like a turnaround
sheet. An item that makes the character taller or wider changes the fitted
scale; pass a result's `scale` back to keep it, so before and after renders
line up:

```sh
vroid-cli demo '{"method": "render", "path": "./side.png", "view": "side"}'
vroid-cli demo '{"method": "render", "path": "./face.png", "view": "front", "frame": "head"}'
vroid-cli demo '{"method": "render", "path": "./sheet.png", "view": ["front", "side", "back"], "width": 512, "height": 1024, "reference": "./turnaround.png"}'
```

VRoid's built-in items have no names, only thumbnails. `thumbnails` draws
VRoid's thumbnail of every built-in item in a category to one PNG, each
labelled with the identifier `apply` takes, also without changing anything:

```sh
vroid-cli demo '{"method": "thumbnails", "section": "Outfit", "category": "Tops", "path": "./tops.png"}'
```

Like `save`, neither command overwrites a file.

### Agents

The shared skill in [`.agents/skills/vroid-cli`](.agents/skills/vroid-cli/SKILL.md)
works with Codex, Claude Code (through `.claude/skills`), and OpenCode. In
another project, print it with `vroid-cli --agent-skill` or copy it into that
project's skills directory.

### First launch

A new session's first `launch` creates its Wine prefix and is ready in about
15 seconds; later launches take a few seconds. If `launch` times out, its
error says whether the app keeps starting, and `launch` again keeps waiting,
or has stalled, and `stop` then `launch` restarts it.

## Recovery

`vroid-cli NAME '{"method": "status"}'` reports whether the app runs, whether
it is ready, any pending request, and unsaved changes (`bridge.modified`).
Logs are in the session directory: `runner.log`, `player.log`,
`desktop/app.log`, `bepinex/LogOutput.log`, and, if BepInEx failed to start,
`bepinex/preloader.log`. `launch` moves the previous run's logs to
`previous/`.

- **Timed-out request:** do not repeat it. It stays pending and blocks other
  commands until its result appears in `rpc/results/ID.json`. Restarting the
  session marks it as having an unknown outcome; inspect the project first.
- **Unsaved changes:** `stop` refuses while there are unsaved changes or a
  pending request. Save to a new path, or send
  `{"method": "stop", "force": true}` to discard them.
- **Another project:** save, `stop`, and `launch` before opening it.
- **Unclean desktop shutdown:** run `framewisp ABSOLUTE/SESSION/desktop recover`
  as the error message says, then launch again.
- **Stalled launch:** before `stop`, save a backtrace of every thread of the
  session's Wine processes with the Wine build that runs them, and attach it
  with the logs to a bug report:

  ```sh
  session=ABSOLUTE/SESSION
  for pid in $(pgrep -x wineserver); do
      grep -qxzF "WINEPREFIX=$session/wine" "/proc/$pid/environ" &&
          WINEDEBUG=-all WINEPREFIX=$session/wine \
              "$(dirname "$(readlink "/proc/$pid/exe")")/winedbg" \
              --command 'bt all' >"$session/backtrace.txt"
  done
  ```
- **Upgrades:** `launch` runs the VRoid and bridge of the vroid-cli build
  that ran it. A running session keeps working with a newer CLI unless the
  file protocol changed; then save with the older build or stop and launch
  again.
- **Sessions from earlier versions** launch with the shared install and keep
  their VRoid preferences. Their own copy of VRoid in
  `wine/drive_c/VRoidStudio`, `installed`, and `installer.log` are no longer
  used. Deleting them is optional and only frees about 1.4 GB.

## Development

```sh
nix develop
just build      # nix build
just test       # Python and bridge protocol tests
just check      # formatting, lint, type checks, nix flake check
just gui-smoke SESSION SOURCE.vroid
```

In the dev shell, `vroid-cli` runs the working tree's Python. Enter the
shell from inside the checkout; elsewhere its `vroid-cli` refuses to run. VRoid, the
bridge, and Framewisp are the shell's builds; re-enter the shell (or
`direnv reload`) after changing them.

`just gui-smoke` needs graphics access. It runs the workflow above with a
restart and checks the saved values and Framewisp's input log. See
[DESIGN.md](DESIGN.md) for internals and [REQUIREMENTS.md](REQUIREMENTS.md) for
the guarantees.

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for third-party code and
downloads.
