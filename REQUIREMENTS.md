# vroid-cli requirements

Each requirement captures valuable behaviour or an invariant users rely on,
including latent durability, privacy, compatibility, and recoverability
guarantees. `DESIGN.md` describes how the requirements are met.

## Format

Write one requirement per bullet, as `- **R-AREA-N** [tag] Statement.` State
observable behaviour, not implementation, in short sentences.

Follow new or substantively changed requirements with a brief, indented
`Rationale:` paragraph. Explain why the guarantee matters, not how to implement
it; name non-obvious constraints when useful. Backfill existing rationales only
when relevant to the task, without a project-wide sweep. If the reason is unknown,
investigate or ask; never invent a rationale.

For new or substantively changed `[manual]` requirements, add a separate
indented `Manual test:` paragraph with concrete steps and expected results.
It says how to verify the behaviour; `Rationale:` says why it matters. Apply
this format when adding or editing requirements too; do not migrate or
backfill unrelated requirements.

- The ID is stable. Never renumber or reuse an ID. To drop a requirement,
  change its tag to `[retired]` and delete its text and rationale; the ID
  stays taken.
- `[test]`: a test that `just test` or `nix flake check` runs covers it. Each
  covering test carries the ID in a comment.
- `[manual]`: no automated test yet. A `Manual test:` paragraph says how an
  agent checks it.
- `[retired]`: no longer required. Kept so the ID stays taken.

## Sessions (SES)

- **R-SES-1** [test] A command that controls a session fails without effect
  while another command controls the same session.

  Rationale: Interleaved commands from two agents could open, edit, or save the
  wrong project, and neither agent could tell what happened.

- **R-SES-2** [test] `launch` reports success only after the app reports ready
  after that launch began.

  Rationale: A heartbeat file left by the previous process looks recent for a
  few seconds and would otherwise report a dead or still-starting app as ready.

- **R-SES-3** [test] Session state never appears as untracked files in Git,
  including when it is created inside another repository.

  Rationale: Session state holds proprietary app files, Wine prefixes, and
  logs that must never be committed by accident.

- **R-SES-4** [test] `stop` refuses while the app last reported unsaved changes
  or a request is pending, unless forced.

  Rationale: Stopping discards unsaved edits, which may be the only copy of an
  agent's work.

- **R-SES-5** [retired]

- **R-SES-6** [retired]

- **R-SES-7** [manual: while a session is running, confirm no VRoid window
  appears on the user's desktop and the user's input reaches only their own
  apps] A session's app runs on a private display. It never draws on or reads
  input from the user's desktop.

  Rationale: Users keep working on their computer while agents edit
  characters.

- **R-SES-8** [test] `launch` never stops at VRoid's first-run welcome and terms
  dialogs. It accepts VRoid's terms in every session it starts, including a
  stopped session an earlier version left at those dialogs, and keeps VRoid's
  other preferences.

  Rationale: Agents launch sessions unattended, often in parallel and in evals,
  and cannot stop to ask a person to click through dialogs. The README states
  that using vroid-cli assumes the user has read and accepted VRoid Studio's
  terms.

- **R-SES-9** [test] A session needs no installation step. `launch` runs the
  VRoid and bridge that came with the vroid-cli build that ran it, in new
  sessions and in sessions that earlier versions installed, and no session can
  change them.

  Rationale: Per-session installs took 10 minutes or more and about 2 GB each,
  which made parallel agents and evals impractical. A per-session copy of the
  bridge could go stale or be left partly copied, and users could not repair an
  app directory they did not create.

- **R-SES-10** [test] Garbage collection of the Nix store while a session's app
  runs never removes files that app uses.

  Rationale: Sessions run for hours next to the user's other Nix work. A
  collection that removed VRoid's files would crash the app and lose unsaved
  edits.

- **R-SES-11** [test] When `launch` fails because the app exited or did not
  become ready, its error shows the last lines of the log that explains why.

  Rationale: The app runs on a private display, so an agent cannot see its
  errors any other way.

## Requests (RPC)

- **R-RPC-1** [test] A request runs only in the app instance it was addressed
  to.

  Rationale: A request prepared for an app that has since restarted refers to
  state that no longer exists.

- **R-RPC-2** [test] A request interrupted by an app restart is reported as
  having an unknown outcome and never runs again. A result written before the
  restart is kept.

  Rationale: Replaying a mutation after an unknown outcome could apply it twice
  or apply it to a different project.

- **R-RPC-3** [test] A pending request blocks every new request and stays in
  place.

  Rationale: The pending request's outcome must be known before another edit
  builds on it.

- **R-RPC-4** [test] Commands never send requests to an app whose heartbeat is
  stale.

  Rationale: A request to a dead or hung app stays pending and blocks the
  session until it restarts.

- **R-RPC-5** [test] A request runs at most once. The CLI never repeats a
  request automatically.

  Rationale: A repeated mutation can apply twice after an ambiguous failure.

- **R-RPC-6** [test] Every request's result stays available for inspection
  after its command ends.

  Rationale: After a timeout or crash, the result shows whether an edit or save
  happened.

## Command output (CLI)

- **R-CLI-1** [test] A successful command prints exactly one JSON value on
  stdout and exits 0.

  Rationale: Agents and scripts parse stdout directly.

- **R-CLI-2** [test] A failed command exits nonzero, prints its reason on
  stderr, and prints nothing on stdout.

  Rationale: Callers must never mistake a partial or diagnostic output for a
  result.

- **R-CLI-3** [test] A request with an unknown method or field, a value of the
  wrong type, or a repeated key fails as a usage error before any session state
  is touched.

  Rationale: A misspelled option or coerced value would otherwise be ignored or
  reinterpreted, and the agent would believe it took effect.

## Editing (EDIT)

- **R-EDIT-1** [manual] Every value `apply` can set reads back unchanged after
  a save, a full restart and a reopen.

  Rationale: Saved projects are the deliverable of an editing session.

  Manual test: Run `just gui-smoke SESSION SOURCE.vroid` with an editable
  project. It saves at least one value of each kind `apply` can set, including
  an item in a Hair and an Outfit category, an Outfit colour, and an accessory
  with a colour, a bone other than its default, a position, rotation and scale,
  and a shape slider, and passes only if each reads back unchanged.

- **R-EDIT-2** [test] `apply` rejects a value that is not finite, or a colour
  that is not `#RRGGBB`, before contacting the app.

  Rationale: Non-finite values can corrupt a model without any visible error.
  A malformed colour has no single meaning; guessing one, such as shorthand or
  alpha, would change the character silently.

- **R-EDIT-3** [manual] `apply` refuses a request in which any value is
  outside its parameter's bounds or of the wrong kind, names a category,
  parameter, item or bone that is unknown or ambiguous, gives the wrong number
  of values for a label, sets one shared colour more than once, selects items
  that VRoid cannot wear together, puts accessories of one kind on bones that
  VRoid cannot combine, or adds a built-in accessory to a kind that wears or
  wore an accessory that is not built in, and changes nothing. Values that
  R-EDIT-13 covers are checked as R-EDIT-13 says.

  Rationale: VRoid's own controls never produce out-of-range values, so the
  app's behaviour for them is unknown. A partly applied template leaves a
  character that matches neither the old nor the new version. A shared colour
  set twice in one request would take whichever value came last. VRoid
  silently takes off an item that conflicts with a newly selected one, such as
  a top when a dress is selected, so the template would not read back. VRoid
  changes an accessory's bone only while no other accessory of its kind is
  worn, and gives every other one the default bone or a copy of another's, so
  some combinations of bones cannot be built. A rotation that is not a unit
  quaternion, or a scale that is not positive, distorts the accessory; VRoid's
  own controls never produce one. VRoid adds a built-in accessory to the group
  of a kind's imported one, so it would read as `custom` and lose its
  identifier. VRoid keeps that group after the last accessory of the kind is
  removed.

  Manual test: `just gui-smoke` applies one valid value, one above its
  maximum, an unknown parameter, an unknown category, a number for a colour,
  one shared colour in two categories, an unknown item, a dress with a top, an
  accessory rotation that is not a unit quaternion, an unknown bone, and an
  accessory shape slider above its maximum, then checks the error and that the
  values are unchanged.

- **R-EDIT-4** [test] `save` refuses an existing destination and leaves it
  unchanged.

  Rationale: The destination may be an earlier deliverable.

- **R-EDIT-5** [manual: `just gui-smoke` checks that the session's Framewisp
  input log is unchanged by the workflow] Editing sends no pointer or keyboard
  input to the app.

  Rationale: Synthetic input depends on the exact screen layout and fails
  silently when it changes.

- **R-EDIT-6** [manual] `open`, `new`, and `apply` succeed only after the
  model has finished rebuilding with the result.

  Rationale: Commands that run during a rebuild read stale values or save a
  half-rebuilt model.

  Manual test: `just gui-smoke` checks that `status` reports a higher
  `rebuilt` count and no rebuild running right after each `apply` and `new`,
  and that values read back after `open`.

- **R-EDIT-7** [manual: `just gui-smoke` applies a template dumped before its
  edits after a restart and compares a new dump with it, including items and
  accessories] Applying `dump` output reproduces every dumped value exactly,
  in any app instance, except non-built-in items, which have no identifier
  (R-EDIT-10).

  Rationale: Agents save, diff, and reapply templates to reuse characters. A
  template that drops or rounds values changes a character silently.

- **R-EDIT-8** [manual: `just gui-smoke` checks that each colour it sets reads
  back as the same hex; compare one field with VRoid's colour field in a
  Framewisp screenshot] A colour reads and writes as the `#RRGGBB` value
  VRoid's editor shows for that field.

  Rationale: Agents match colours against reference sheets and VRoid's own
  editor. A colour-space, rounding, or alpha mismatch would shift every colour
  without an error.

- **R-EDIT-9** [manual: `just gui-smoke` compares `items` output and a `dump`
  from before and after a restart] An item identifier names the same built-in
  VRoid item in every session and after every restart of the pinned VRoid
  version.

  Rationale: Agents keep templates and notes that refer to items by identifier
  and apply them in other sessions. An identifier that changed meaning would
  dress the character in the wrong item without an error.

- **R-EDIT-10** [manual: open a project that wears a non-built-in item, apply a
  built-in item to that category without the flag and check it's refused and
  `dump` is unchanged, then apply it with the flag and check the result names
  the replaced item] `apply` replaces or removes an item that is not one of
  VRoid's built-in items only when the request explicitly allows it, and its
  result then names each such item that its undo template cannot restore.
  Otherwise it refuses the request and changes nothing.

  Rationale: Such items, like texture-edited outfits or parts of sample models,
  have no identifier, so the undo template cannot put them back. An agent that
  means to discard one says so, and saves a copy of the project first if it may
  want the item back.

- **R-EDIT-11** [manual] Before `apply` or `import` changes anything, it
  writes a template to a new file that restores everything the request
  changes, including what VRoid changes with an item, except what its result
  names in `discarded` (R-EDIT-10). Its result, and its error after a change,
  name the file.

  Rationale: Agents try edits and back out the ones that don't match a
  reference. The file survives a failed or interrupted `apply`, and keeping the
  template out of the result keeps the result short enough to read.

  Manual test: `just gui-smoke` applies the undo file of a value `apply`, of an
  item and accessory `apply`, and of an `apply` that removes one of two
  accessories of one kind after moving it to another bone, placing it and
  reshaping it, and compares `dump` with one taken before each.

- **R-EDIT-12** [manual] Setting the colour of a built-in outfit item keeps it
  the same built-in item.

  Rationale: An item that became custom would lose its identifier, so
  templates and undo files could not restore it, and later item changes in its
  section would need `discard_custom` (R-EDIT-10). VRoid's texture editor
  saves edits as custom items, so this guarantee is easy to lose.

  Manual test: `just gui-smoke` sets an Outfit colour, then checks that
  `items` names the same built-in item after the edit and after a save, a full
  restart and a reopen.

- **R-EDIT-13** [manual] `apply` checks a colour of a category whose item the
  request changes, directly or through VRoid, and a colour or shape slider of
  an accessory the request adds, once the new item or accessory is worn. A
  refusal then stops `apply`, and its undo file restores what changed
  (R-EDIT-11).

  Rationale: An item's colours and an accessory's shape sliders are known only
  once it is worn: a new outfit item brings its own parts and colours, a new
  Face item can make a colour unavailable, and a new accessory brings its own
  sliders.

  Manual test: `just gui-smoke` applies a request that selects another top
  with more colours than it has, and one that adds an accessory with a shape
  slider above its maximum. It checks that each error names the undo file, and
  that applying the undo file restores a `dump` taken before.

- **R-EDIT-14** [manual] `new` creates a character from VRoid's built-in
  feminine or masculine base without network access. Each base gives the same
  character in every session, and it wears only built-in items.

  Rationale: Agents start characters from scratch, often in sandboxes and
  evals without network access. A base that differed between sessions would
  make templates and renders from one session misleading in another, and an
  item that is not built in would need `discard_custom` (R-EDIT-10) before any
  item change in its section.

  Manual test: `just gui-smoke` creates a character from each base, checks
  that `dump` names no `"custom"` item and that it matches a `dump` of the
  same base taken after a restart, and renders it. After a change to the
  VRoid pin, also check by hand that `new` succeeds with each base in a
  session launched without network access.

- **R-EDIT-15** [manual] `import` adds the accessory in a `.vroidcustomitem`
  file to the character without a file dialog and changes nothing else. The
  accessory reads as `custom`, and `apply` recolours, reshapes, and places it
  like a built-in accessory. `import` refuses a file that VRoid cannot import
  or that holds no accessory, and an accessory that VRoid's accessory limits
  or a built-in accessory of its kind that the project wears or wore does not
  allow, and then changes nothing.

  Rationale: Reference characters wear accessories that VRoid does not ship,
  such as bows, which users buy or make as custom item files. VRoid's file
  dialog, and the dialog it shows at its accessory limit, would block the
  private display, where no one can close them. VRoid puts an accessory
  imported next to a built-in one of its kind into the built-in's group, where
  nothing marks it as imported, so it would read as the built-in item, and
  templates and undo files would silently drop its texture. VRoid keeps that
  group after the last accessory of the kind is removed.

  Manual test: `just gui-smoke` exports an accessory it recoloured, reshaped,
  and moved, imports the file into a new character, and checks that the new
  accessory reads as `custom` with the same values and that `apply` changes
  its colour and placement. It checks that a file that holds no accessory,
  and an accessory of a kind that wears a built-in one, are refused with
  `dump` unchanged, and that the import's undo file restores a `dump` taken
  before it.

- **R-EDIT-16** [manual] `export` writes a worn accessory to a new
  `.vroidcustomitem` file without a file dialog and changes nothing in the
  app. `import` of that file in any project adds an accessory with the same
  colours, shape sliders, and placement. `export` refuses an existing
  destination and leaves it unchanged.

  Rationale: An accessory that is not built in has no identifier, so a
  template cannot carry it to another project; a file can. The destination
  may be an earlier deliverable.

  Manual test: `just gui-smoke` exports a recoloured, reshaped, and moved
  accessory, checks that `dump` and `modified` are unchanged, imports the file
  into another project, and compares the new accessory's entry with the
  exported one. It checks that an existing destination is refused and left
  unchanged.

## Views (VIEW)

- **R-VIEW-1** [manual: `just gui-smoke` renders each view after an `apply`
  and checks the image size, that its edges are background, and that it shows
  the character] `render` writes a PNG of the open character at the requested
  size, from the front, three-quarter, side, or back, framed to show the whole
  character or its whole head on a plain background.

  Rationale: Agents judge edits by comparing the character with reference
  turnarounds. Screenshots of the app window include editor panels, cut off
  the feet, and cannot show the side or back.

- **R-VIEW-2** [manual: `just gui-smoke` checks that `dump`, `modified`, and
  the Framewisp input log are unchanged by rendering and by drawing
  thumbnails] Rendering and drawing thumbnails change nothing in the app: the
  project, its unsaved state, and the editor's view stay as they were, and
  they send no pointer or keyboard input.

  Rationale: Agents render between edits to check them. A render that changed
  the character, marked it unsaved, or moved the user's view would make an
  inspection look like an edit.

- **R-VIEW-3** [test] `render` and `thumbnails` refuse an existing destination
  and leave it unchanged. The destination never holds a partial image.

  Rationale: The destination may be an earlier image an agent is comparing
  against, and a reader that finds the file must be able to use it.

- **R-VIEW-4** [manual: `just gui-smoke` draws the thumbnails of a Hair and an
  Outfit category and checks the image size and that `items` lists in the
  result exactly the category's `items`; compare a few cells with VRoid's item
  picker in a Framewisp screenshot] `thumbnails` writes one PNG that shows
  VRoid's own thumbnail of every built-in item in a category, each labelled
  with its identifier.

  Rationale: VRoid has no item names, only thumbnails. Without them, an agent
  finds an item matching a reference only by wearing and rendering each
  candidate, which took about 45 minutes for 450 items in an eval.
