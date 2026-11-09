---
name: vroid-cli
description: Use to run VRoid Studio in a background desktop.
---

# VRoid characters with vroid-cli

`vroid-cli` runs standalone VRoid Studio under Wine on a private display and
edits characters through VRoid's internal methods. It sends no mouse or
keyboard input and needs no OCR, MCP server, or account login. The user can keep
using their computer.

Get it once with
`nix shell github:krscott/vroid-cli github:krscott/vroid-cli#framewisp`, or
use `vroid-cli` and `framewisp` inside the project's `nix develop` shell. Keep
that build for the whole session. It supports VRoid Studio 2.16.0 on x86_64
Linux.

Every command is `vroid-cli SESSION REQUEST`. `REQUEST` is a JSON object,
`@path` to read one from a file, or `-` to read stdin:

```sh
vroid-cli alice '{"method": "launch"}'
vroid-cli alice '{"method": "open", "path": "./alice.vroid"}'
vroid-cli alice '{"method": "categories", "section": "Body"}'
vroid-cli alice '{"method": "parameters", "section": "Body", "category": "Whole Body"}'
vroid-cli alice '{"method": "apply", "Body": {"Whole Body": {"Head Size": 0.125}}}'
vroid-cli alice '{"method": "apply", "Hair": {"Front": {"Main Color": "#FF8C00"}}}'
vroid-cli alice '{"method": "dump"}' > alice.json
vroid-cli alice @alice.json
vroid-cli alice '{"method": "save", "path": "./alice-v2.vroid"}'
vroid-cli alice '{"method": "stop"}'
```

The JSON Schema at the end of `vroid-cli --agent-skill` lists every request
field and result.

## Scope

vroid-cli opens existing `.vroid` projects, reads and sets the numeric sliders
in the Face and Body editors and the colour fields in the Face, Hairstyle, and
Body editors, and saves projects. It can't yet change outfit colours, textures,
hairstyles, outfits, accessories, or presets, create characters, render the
character, or export VRM. To see the character, take a Framewisp screenshot of
the private desktop that `status` reports as `desktop` (see First run); it
shows the editor window. When a request needs anything vroid-cli can't do, do
the parts it can and tell the user which parts you couldn't.

Face "Ear" parameters shape human ears at the sides of the head, not animal
ears, which vroid-cli can't add yet.

## Rules

- Give each agent its own session name and use it on every command. A second
  command on a busy session fails at once; wait and run it again.
- A successful command prints one JSON value on stdout. A failed command exits
  nonzero with the reason on stderr; exit 2 is a usage error, such as an
  invalid request or session name, and nothing ran.
- Requests are strict: misspelled fields, strings for numbers, and repeated
  keys are refused.
- Use category and parameter names exactly as `categories` and `parameters`
  return them. A label that appears more than once in a category, like "Nose
  Size" in Face Sets, takes an array with one value per occurrence, in
  `parameters` order.
- `apply` accepts finite values within each parameter's `min..max`, and
  colours as `"#RRGGBB"` where `parameters` lists a colour. It checks every
  value before changing any; one bad value refuses the whole request. Its
  result's `undo` is an `apply` request that restores the previous values.
- Colours are the hex VRoid's editor shows, without alpha. Hair's "Main Color"
  and "Highlight Color" (under Front, Back, and the other hair parts) are one
  pair for all hair; Base Hair has its own "Main Color".
- Some colours are shared: skin is one colour in Face Eyelids, Nose, and Skin
  and Body Whole Body. Set each in one place per request; the error names the
  conflict. Set Irises "R Eye" and "L Eye" separately; "Both Eyes" is refused.
- A colour that is unavailable for the project's selected item, such as Face
  Lips "Color" when the selected lips have no colour, is refused. Remove it
  from a template made in another project.
- `categories`, `parameters`, `dump`, and `apply` change which section VRoid's
  editor shows, and all but `categories` change its category, even when they
  change no value. For comparable screenshots, run the same `parameters`
  request before each one.
- Categories share parameters: "Eye Size X" in Face Sets and in Eyeliner is
  one value. An `apply` request may name each label in only one category per
  section.
- `dump` returns an `apply` request with every Face, Hair, and Body value and
  colour, each listed once under the first category that has it (skin under
  Face Eyelids, hair under Hair Front). Save it as a template, edit or diff it
  with JSON tools, and apply it in any session.
- Paths in requests, including saved ones, resolve against the current
  directory, not the request file's.
- `save` needs a new `.vroid` path in an existing directory. It never
  overwrites. Choose a new versioned filename.
- Opening another project requires `save`, `stop`, and `launch` first.
- `stop` refuses while there are unsaved changes or a pending request. Save
  or wait first; send `{"method": "stop", "force": true}` only when discarding
  them is intended.
- Never repeat a timed-out `open`, `apply`, or `save` automatically. Inspect
  `status` and the result file named in the error first.

## Timing and sandboxes

The first `launch` of a session installs VRoid and generates app bindings, which
can take 10 minutes or more. Give `launch` a tool timeout of at least 15
minutes, or run it in the background, and pass `"timeout": 1200`; the default
is 600 seconds. If it times out, the app keeps starting: run `launch` again to
keep waiting. Commands need local graphics and Unix sockets, so a sandboxed
harness may need its usual escalation.

## First run

A new session shows VRoid's welcome and terms dialogs once. Until the user has
accepted them, `launch` and edit commands fail with instructions, and
`status` reports `terms_accepted: false`. vroid-cli never accepts them. Ask the
user to read and accept them, or to authorize you to, on the private desktop
named in the error:

```sh
framewisp /abs/path/.vroid/alice/desktop screenshot /tmp/alice.png
framewisp /abs/path/.vroid/alice/desktop click X Y
```

Then run `launch` again; if it still refuses, `stop` and `launch`. Framewisp is
for setup, observation, and recording only; edits go through `vroid-cli`.

## Recovery

`status` reports `app_running`, `ready`, `pending_request`, and the bridge's
last heartbeat, including `modified` for unsaved changes. Session state lives in
`.vroid/NAME/` at the Git worktree root (the current directory outside Git), or
under `VROID_STATE_DIR`. On failure, read `runner.log`, `player.log`,
`installer.log`, and `wine/drive_c/VRoidStudio/BepInEx/LogOutput.log` there.

- A pending request blocks new commands. Its result appears in
  `rpc/results/ID.json`. Restarting the session marks it as having an unknown
  outcome; check the project before editing again.
- An incomplete first installation is not repeated. Keep its logs, then delete
  the session directory or use another session name.
- If the private desktop was not shut down cleanly, run
  `framewisp /abs/path/.vroid/NAME/desktop recover` as the error says.
