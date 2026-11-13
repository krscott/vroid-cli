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
`nix shell github:krscott/vroid-cli github:krscott/vroid-cli#framewisp`, and
keep that build for the whole session. Inside the vroid-cli project's
`nix develop` shell, `vroid-cli` runs that working tree instead. It supports
VRoid Studio 2.16.0 on x86_64 Linux.

Every command is `vroid-cli SESSION REQUEST`. `REQUEST` is a JSON object,
`@path` to read one from a file, or `-` to read stdin. The first `launch` of a
new name creates the session; to delete one, `stop` it and remove its
directory (Recovery, below):

```sh
vroid-cli alice '{"method": "launch"}'
vroid-cli alice '{"method": "open", "path": "./alice.vroid"}'
vroid-cli alice '{"method": "categories", "section": "Body"}'
vroid-cli alice '{"method": "parameters", "section": "Body", "category": "Whole Body"}'
vroid-cli alice '{"method": "apply", "Body": {"Whole Body": {"Head Size": 0.125}}}'
vroid-cli alice '{"method": "apply", "Hair": {"Front": {"Main Color": "#FF8C00"}}}'
vroid-cli alice '{"method": "items", "section": "Outfit", "category": "Tops"}'
vroid-cli alice '{"method": "thumbnails", "section": "Outfit", "category": "Tops", "path": "./tops.png"}'
vroid-cli alice '{"method": "apply", "Outfit": {"Tops": {"item": "159"}}}'
vroid-cli alice '{"method": "apply", "Outfit": {"Tops": {"Base Color": ["#B22222", "#FFFFFF"]}}}'
vroid-cli alice '{"method": "accessories"}'
vroid-cli alice '{"method": "apply", "Accessories": [{"item": "CatEar", "Color": "#3A2A20", "scale": [1.2, 1.2, 1.2]}]}'
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
Body editors, sets the base and shade colours of built-in outfit items, selects
VRoid's built-in items (hairstyle parts, face parts and presets, body presets,
outfit items), draws a category's built-in item thumbnails labelled with their
identifiers, adds, removes, recolours, places, and reshapes built-in
accessories (glasses, cat and rabbit ears, cat, fox, and rabbit tails, hats),
including their bone and shape sliders, renders the character to PNG from the
front, three-quarter, side, or back, and saves projects. It can't change
outfit textures or the colours of custom items, hide, lock, or rename
accessories, import or create custom items, create characters, or export VRM.
To see the character, use `render`, not screenshots of the editor window. Edits
go through `vroid-cli`, never Framewisp input. When a request needs anything
vroid-cli can't do, do the parts it can and tell the user which parts you
couldn't.

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
  `parameters` order. Outfit items have a "Base Color" and "Shade Color" per
  part, so an item with several parts takes an array of colours; `render` to
  see which part each one colours. A new item's parts are known only once it
  is worn: select it, list its `parameters`, then set its colours.
- `apply` accepts finite values within each parameter's `min..max`, as
  `parameters` lists them, or `accessories` `sliders` for accessories, and
  colours as `"#RRGGBB"` where `parameters` lists a colour. It checks every
  value before changing any, and one bad value refuses the whole request,
  except a new item's colours and an added accessory's shape sliders, which
  it checks once they are worn; a refusal there stops `apply` after changes. Its
  result is `changed`, `discarded`, and `undo`, the path of a file written
  before the first change that holds an `apply` request restoring the
  previous values. Undo with `vroid-cli NAME @PATH`. When `apply` fails after
  changing something, its error names the same file. Undo files stay in the
  session; copy one to keep it after the session is deleted.
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
  items and the one it wears, as `"item"` in a template category. To choose
  one, view `thumbnails` for the category: one `.png` of VRoid's own
  thumbnail of each item, 10 per row in `items` order, labelled with its
  identifier. It takes about 2 s, changes nothing, and shows items in VRoid's
  default colours. Then select candidates and `render` them to compare.
  Selecting an item keeps every colour except the Outfit item's own, which
  start at the item's defaults, and resets Face Irises and Eyebrows values,
  so `apply` changes items first, then values. Setting an outfit colour keeps
  the item built in. `"item": null` takes
  off the item where `items` reports `removable`.
- VRoid takes off conflicting items itself, such as the dress when a top is
  selected; a request naming both is refused. Each Hair Back item also brings
  its own Base Hair item, or none: Back `146` selects Base Hair `001`, `326`
  selects `Default`. Set Base Hair after Back, or in the same request, to keep
  another. `changed` counts only what the request names, but `undo` restores
  these too. Sets (Face Sets, Eyes Sets, Hairstyle Sets, Whole Sets) select
  the items of other categories, so name either a set or those items. `dump`
  omits sets.
- `"custom"` is an item `items` does not list, such as a texture-edited
  outfit or a sample model's part. `apply` refuses to change any item in a
  section that wears one, since VRoid may replace it and `undo` cannot
  restore it. To change items there anyway, `save` a copy of the project
  first, then add `"discard_custom": true`; the result's `discarded` names
  what VRoid replaced, which may be nothing. Undoing such a change needs the
  flag too. In a template, `"custom"` only keeps the target's own such item;
  it cannot copy one to another project, where `apply` refuses it.
- `Accessories` in `apply` is every accessory to wear; `apply` adds and
  removes to match, and an omitted key leaves them alone. To add one, append
  it to `worn` from `accessories`, as the example's list would otherwise
  remove the rest. A pair of ears is one accessory; VRoid allows five.
- An accessory entry has an `item` from `accessories`, its "Color", its shape
  sliders by label, and its
  placement: `bone`, one of `accessories` `bones` such as `J_Bip_C_Head`;
  `position` `[x, y, z]` in metres; `rotation`, a unit quaternion
  `[x, y, z, w]`; and `scale` `[x, y, z]`, all relative to the bone and the
  identity at VRoid's default placement. A rotation of θ about the x axis is
  `[sin(θ/2), 0, 0, cos(θ/2)]`. `"bone": null` is VRoid's None, with no
  placement. An omitted key keeps a worn accessory's value, or VRoid's default
  for an added one, whose sliders are checked only once it is worn.
- `accessories` `sliders` holds one list per `worn` entry, in the same order:
  its shape sliders with `label`, `value`, `min`, and `max`, as `parameters`
  lists them. Bounds differ by slider, such as 0 to 100 for "Ear Size
  (Bigger)" and -1 to 1 for "Tail Length". An accessory that is not worn has
  no sliders yet: add it with `apply`, then list `accessories`.
- VRoid moves an accessory to another bone only while no other of its kind
  (all glasses, all ears, all tails, all hats) is worn, so `apply` may remove
  and re-add that kind to reach a template. Accessories of one item are
  interchangeable, and `dump` orders them by their values. Within a kind,
  only copies of one item may leave the default bone, all on one bone;
  `apply` refuses other combinations.
- `categories`, `parameters`, `items`, `accessories`, `dump`, and `apply`
  change which section VRoid's editor shows (`thumbnails` and `render` do
  not), and `parameters`, `dump`, and
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
  shape. Views are orthographic, and body views of one character state at one
  `width` and `height` share their scale, so they compare directly with a
  reference turnaround. The body frame fits everything worn, so an item that
  makes the character taller or wider, such as an ahoge, a hat, or a long top,
  changes the scale and can move the ground line.
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
last heartbeat, including `modified` for unsaved changes and `rebuilt`, the
number of model rebuilds since the app started; only differences between two
readings mean anything. Session state lives in
`.vroid/NAME/` at the Git worktree root (the current directory outside Git), or
under `VROID_STATE_DIR`. On failure, read `runner.log`, `player.log`,
`bepinex/LogOutput.log`, and `bepinex/preloader.log` there.

- A pending request blocks new commands. Its result appears in
  `rpc/results/ID.json`. Restarting the session marks it as having an unknown
  outcome; check the project before editing again.
- If the private desktop was not shut down cleanly, run
  `framewisp /abs/path/.vroid/NAME/desktop recover` as the error says.
