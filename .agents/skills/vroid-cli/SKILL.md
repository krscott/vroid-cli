---
name: vroid-cli
description: Open, inspect, edit Face/Body numeric parameters of, and save VRoid Studio .vroid projects through vroid-cli on a private background desktop.
---

# VRoid characters with vroid-cli

`vroid-cli` runs standalone VRoid Studio under Wine on a private display and
edits characters through VRoid's internal methods. It sends no mouse or
keyboard input and needs no OCR, MCP server, or account login. The user can keep
using their computer.

Get it once with `nix shell github:krscott/vroid-cli`, or use `vroid-cli` inside
the project's `nix develop` shell, and keep that build for the whole session. It supports VRoid Studio 2.16.0
on x86_64 Linux.

```sh
vroid-cli --session alice launch
vroid-cli --session alice open ./alice.vroid
vroid-cli --session alice categories Body
vroid-cli --session alice parameters Body "Whole Body"
vroid-cli --session alice set Body "Whole Body" "Head Size" 0.125
vroid-cli --session alice save ./alice-v2.vroid
vroid-cli --session alice stop
```

## Rules

- Give each agent its own `--session NAME` and keep it on every command. A
  second command on a busy session fails at once; wait and run it again.
- A successful command prints one JSON value on stdout. A failed command exits
  nonzero with the reason on stderr.
- Use category and parameter names exactly as `categories` and `parameters`
  return them. `set` accepts finite values within the parameter's `min..max`.
  Negatives like `-0.35` work; put `--` before exponent forms like `-1e-3`.
- `save` needs a new `.vroid` path in an existing directory. It never
  overwrites. Choose a new versioned filename.
- Opening another project requires `save`, `stop`, and `launch` first.
- `stop` refuses while there are unsaved changes or a pending request. Save
  or wait first; use `stop --force` only when discarding them is intended.
- Never repeat a timed-out `open`, `set`, or `save` automatically. Inspect
  `status` and the result file named in the error first.

## Timing and sandboxes

The first `launch` of a session installs VRoid and generates app bindings, which
can take 10 minutes or more. Give `launch` a tool timeout of at least 15
minutes, or run it in the background. If it times out, the app keeps starting:
run `launch` again to keep waiting. Commands need local graphics and Unix
sockets, so a sandboxed harness may need its usual escalation.

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
