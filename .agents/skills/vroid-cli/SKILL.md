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
vroid-cli alice '{"method": "items", "section": "Outfit", "category": "Tops"}'
vroid-cli alice '{"method": "apply", "Outfit": {"Tops": {"item": "159"}}}'
vroid-cli alice '{"method": "accessories"}'
vroid-cli alice '{"method": "apply", "Accessories": [{"item": "CatEar", "Color": "#3A2A20"}]}'
vroid-cli alice '{"method": "dump"}' > alice.json
vroid-cli alice @alice.json
vroid-cli alice '{"method": "save", "path": "./alice-v2.vroid"}'
vroid-cli alice '{"method": "render", "path": "./alice-side.png", "view": "side"}'
vroid-cli alice '{"method": "stop"}'
```

The JSON Schema at the end of `vroid-cli --agent-skill` lists every request
field and result.

## Scope

vroid-cli opens existing `.vroid` projects, reads and sets the numeric sliders
in the Face and Body editors and the colour fields in the Face, Hairstyle, and
Body editors, selects VRoid's built-in items (hairstyle parts, face parts and
presets, body presets, outfit items), adds, removes, and recolours built-in
accessories (glasses, cat and rabbit ears, cat, fox, and rabbit tails, hats),
renders the character to PNG from the front, three-quarter, side, or back, and
saves projects. It can't yet place or reshape accessories, change outfit
colours or textures, import or create custom items, create characters, or
export VRM. To see the character, use `render`, not screenshots of the editor
window. Edits go through `vroid-cli`, never Framewisp input. When a request
needs anything vroid-cli can't do, do the parts it can and tell the user which
parts you couldn't.

Face "Ear" parameters shape human ears at the sides of the head. Animal ears
and tails are accessories.

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
- Items have identifiers, not names: `items` lists a category's built-in
  items and the one it wears, as `"item"` in a template category. Choose by
  selecting and rendering. Selecting an item resets that category's values and
  colours, so `apply` changes items first, then values. `"item": null` takes
  off the item where `items` reports `removable`.
- VRoid takes off conflicting items itself, such as the dress when a top is
  selected; a request naming both is refused. Sets (Face Sets, Eyes Sets,
  Hairstyle Sets, Whole Sets) select the items of other categories, so name
  either a set or those items. `dump` omits sets.
- `"custom"` is an item that is not built in, such as a texture-edited outfit
  or a sample model's part. `apply` refuses to replace or remove one, since
  `undo` cannot restore it. To discard it anyway, `save` a copy of the
  project first, then add `"discard_custom": true`; the result's `discarded`
  names what was lost.
- `Accessories` in `apply` is every accessory to wear; `apply` adds and
  removes to match, and an omitted key leaves them alone. Each entry has an
  `item` from `accessories` and its colours. Added ones take VRoid's default
  placement. A pair of ears is one accessory.
- `categories`, `parameters`, `items`, `accessories`, `dump`, and `apply`
  change which section VRoid's editor shows, and `parameters`, `dump`, and
  `apply` change its category, even when they change no value. `accessories`
  and accessory edits also add the hidden accessories to the editor's list.
  For comparable editor screenshots, run the same `parameters` request before
  each one.
- Categories share parameters: "Eye Size X" in Face Sets and in Eyeliner is
  one value. An `apply` request may name each label in only one category per
  section.
- `dump` returns an `apply` request with every item, accessory, and Face,
  Hair, and Body value and colour, each value listed once under the first
  category that has it (skin under Face Eyelids, hair under Hair Front). Save
  it as a template, edit or diff it with JSON tools, and apply it in any
  session.
- Paths in requests, including saved ones, resolve against the current
  directory, not the request file's.
- `save` needs a new `.vroid` path in an existing directory. It never
  overwrites. Choose a new versioned filename.
- `render` writes a new `.png` of the open character, without changing it:
  `view` is `front`, `three_quarter`, `side`, or `back`; `three_quarter` and
  `side` show its left side. `frame: "head"` fits everything above the neck,
  so a front head view is a face close-up and a side head view shows ear
  shape. Views are orthographic, and body views at one `width` and `height`
  share their scale, so they compare directly with a reference turnaround.
  `width` and `height` default to 1024.
- Opening another project requires `save`, `stop`, and `launch` first.
- `stop` refuses while there are unsaved changes or a pending request. Save
  or wait first; send `{"method": "stop", "force": true}` only when discarding
  them is intended.
- Never repeat a timed-out `open`, `apply`, or `save` automatically. Inspect
  `status` and the result file named in the error first.

## Timing and sandboxes

A new session's first `launch` is ready in about 15 seconds, later ones in a
few seconds; the default timeout is 120 seconds. Give `launch` a tool timeout
above that, or run it in the background. If it times out, the app keeps
starting: run `launch` again to keep waiting. Commands need local
graphics and Unix sockets, so a sandboxed harness may need its usual
escalation.

## Recovery

`status` reports `app_running`, `ready`, `pending_request`, and the bridge's
last heartbeat, including `modified` for unsaved changes. Session state lives in
`.vroid/NAME/` at the Git worktree root (the current directory outside Git), or
under `VROID_STATE_DIR`. On failure, read `runner.log`, `player.log`,
`bepinex/LogOutput.log`, and `bepinex/preloader.log` there.

- A pending request blocks new commands. Its result appears in
  `rpc/results/ID.json`. Restarting the session marks it as having an unknown
  outcome; check the project before editing again.
- If the private desktop was not shut down cleanly, run
  `framewisp /abs/path/.vroid/NAME/desktop recover` as the error says.
