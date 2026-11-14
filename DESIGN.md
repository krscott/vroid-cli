# vroid-cli design

`vroid-cli` edits VRoid Studio characters by calling VRoid's internal Unity
methods from an injected plugin. VRoid runs under Wine on a private Framewisp
display, so editing needs no synthetic input or OCR (R-EDIT-5) and leaves the
user's desktop alone (R-SES-7). Separate desktops are the goal; this is not a
filesystem sandbox.

## Supported versions

The pinned combination is the only validated one: VRoid Studio 2.16.0 (Unity
6000.0.62f1, IL2CPP, Windows x64) under Wine 11.21, BepInEx 6 IL2CPP be.788,
and the Framewisp revision in `flake.lock` on x86_64 Linux. The flake exposes
only `x86_64-linux`. Another VRoid version needs new Unity base libraries and
a fresh check of every internal name below.

## Components

| Path | Role |
| --- | --- |
| `vroid_cli/` | Python CLI: request and result models (`schema.py`), sessions, lifecycle, bridge requests |
| `plugin/` | C# BepInEx IL2CPP plugin ("bridge") running inside VRoid |
| `scripts/vroid-studio.sh` | Runs on the private display: prepares the session's prefix and BepInEx root, then starts VRoid |
| `nix/vroid-studio.nix` | VRoid installed into the store, its interop assemblies, and the launcher wrapper |
| `nix/bepinex.nix` | BepInEx with Doorstop and .NET, and the Unity base libraries |
| `nix/bridge.nix` | The bridge plugin alone |
| `default.nix` | The `vroid-cli` package; its wrapper sets `VROID_CLI_SKILL` and `PATH` |
| `scripts/dev/vroid-cli` | The dev shell's `vroid-cli`: runs the working tree with the shell's VRoid and Framewisp |
| `.agents/skills/vroid-cli/` | Shared agent skill, printed with the JSON Schema by `vroid-cli --agent-skill` |

The flake permits only the unfree `vroid-studio-app`, `vroid-studio-interop`,
and `vroid-bepinex` packages; the installer is fetched only to build the first.
Framewisp's headless compositor hardcodes the Pixman software renderer; the
flake substitutes GLES2, which VRoid needs for usable frame rates.

## Session state

A session is a directory `ROOT/NAME/`. `ROOT` is `VROID_STATE_DIR`, otherwise
`.vroid/` at the Git worktree root of the current directory, otherwise
`.vroid/` in the current directory. Each session directory holds a
`.gitignore` containing `*`, so it ignores itself in whichever repository holds
it without hiding anything else there (R-SES-3). Names are 1–64 letters,
digits, `_`, or `-`.

| Entry | Contents |
| --- | --- |
| `command.lock` | Controller lock |
| `desktop/` | Framewisp session (`session.json`, `inputs.jsonl`, `app.log`) |
| `wine/` | Wine prefix: VRoid's preferences and Wine's own files |
| `bepinex/` | BepInEx root: links to the store, its config, cache, `LogOutput.log`, and `preloader.log` |
| `rpc/` | File protocol, below |
| `runner.log`, `player.log` | Framewisp runner and Unity player logs |
| `previous/` | The previous app run's `player.log`, `desktop/app.log`, and BepInEx logs |

Sessions from versions that installed VRoid into the prefix also hold
`wine/drive_c/VRoidStudio`, `installed`, and `installer.log`. Nothing reads
them; deleting them only frees space.

Every command except `status` holds an exclusive, nonblocking `flock` on
`command.lock` for its whole run and fails at once if another command holds
it (R-SES-1). The detached Framewisp runner does not inherit the lock.

## Lifecycle

The CLI reads the desktop as one of three states:

- Stopped: no `desktop/session.json`. If Framewisp's ownership journal
  remains, the previous runner died and every command asks the user to run
  `framewisp DESKTOP recover`.
- Exited: Framewisp recorded VRoid's exit in `desktop/app-exit.json` and the
  runner is shutting down.
- Running: `framewisp DESKTOP status` reports the app running.

`launch` records its start time and takes the lock. For a stopped desktop it
moves the last run's `player.log`, `desktop/app.log`, and BepInEx logs into
`previous/`, replacing the run kept there, so every log in place belongs to
the app the desktop runs and a failed run's logs survive one retry. It then
starts
`framewisp DESKTOP run --x11 --width 2560 --height 1440 -- vroid-studio` in a
new process session, logging to `runner.log`. A running app is waited for. An
exited app fails with instructions to wait or stop. If the runner exits during
the wait, the error shows the end of `desktop/app.log` when Framewisp recorded
VRoid's exit during this launch, which is where the launcher script's own
errors land, followed by the end of BepInEx's log (R-SES-11). Otherwise the
desktop failed before VRoid started, and the error shows the end of
`runner.log`. A timeout shows the ends of `player.log` and BepInEx's log, or
explains an interop mismatch BepInEx reports. When neither log has changed for
30 s, or nothing has been logged for 30 s since the launch began, the app has
stalled, and the error advises `stop` and `launch` instead of waiting. A cold
first start logs nothing for about 12 s while it creates the prefix, and then
logs every few seconds until ready.

`launch` then waits for a bridge heartbeat that is fresh, was written after
the launch began, and has found the main view model (R-SES-2). The bridge also
finds it on VRoid's welcome screen, so an app that an earlier version started
and left at the first-run dialogs looks ready. When VRoid's preferences file
(below) does not exist, `launch` fails with instructions to stop and launch,
which accepts the terms (R-SES-8); `status` and request commands do not check.
An older heartbeat can look fresh for a few seconds after the previous app
exits. The wait fails
early if the runner exits. On timeout the app keeps starting; running `launch`
again resumes the wait. A bridge reporting another protocol version fails with
instructions to stop and relaunch; bridges from other builds with the same
protocol keep working until the next launch runs the new one.

BepInEx writes `bepinex/preloader.log` only when it fails to start, and then
waits behind an error dialog nobody can close, so `launch` fails as soon as
that file appears. A timeout whose BepInEx log reports an interop mismatch
fails with that explanation instead of advice to keep waiting.

The compositor uses the first AMD or Intel render node unless
`WLR_RENDER_DRM_DEVICE` is set. Automatic NVIDIA selection stalled during
testing, and NVIDIA-only hosts are unvalidated.

`stop` refuses while the last bridge status reports `modified` or
`request.json` exists, unless `force` is true (R-SES-4). It then runs `framewisp
DESKTOP stop`, which waits for the app and compositor to exit. For an exited
app it waits for the runner to finish. A stopped desktop is already stopped.

`scripts/vroid-studio.sh` runs inside the private desktop:

1. Without a Wine user profile it creates the prefix with `wineboot -i`. Wine
   updates an existing prefix itself.
2. It accepts VRoid's first-run dialogs (R-SES-8). VRoid keeps its preferences
   in `drive_c/users/USER/AppData/LocalLow/pixiv/VRoid Studio/preferences/editoroption.xml`
   (`USER` is the prefix's only profile with an `AppData` directory) and
   writes the file only after a person accepts the terms. Without it,
   VRoid shows a welcome screen, then the terms and privacy policy, then a
   tutorial dialog. With `AllAgreementsAreAccepted` and
   `UpdatedPrivacyPolicyAreAccepted` set to `Yes` and `tutorialIsRead` set to
   `true`, it skips all three, fills in its other defaults, and rewrites the
   file as it does after a manual acceptance. The launcher writes that minimal
   file, in UTF-8 with a byte order mark like VRoid's, when it is missing. It
   never edits an existing file: one without both acceptances stops the launch
   with instructions to delete it.
3. It creates VRoid's `custom_items` folder next to `preferences`. VRoid's
   watcher for saved custom items stops for good if the folder is missing at
   start, as in a new prefix, and VRoid then never marks a worn item that is
   not built in, which `apply` relies on (R-EDIT-10).
4. It points `bepinex/core` and `bepinex/plugins` at the store and copies
   `plugin/BepInEx.cfg` from the store to `bepinex/config`, replacing all
   three each launch; BepInEx rewrites its config, so the copy is writable.
5. It starts the store's VRoid with Doorstop's `--doorstop-*` arguments aimed
   at `bepinex/`, `--unhollowed-path` at the prebuilt interop assemblies,
   `-logFile` in the session, and the RPC directory in `VROID_BRIDGE_DIR`.

Every Wine call disables Wine's Mono and Gecko setup with
`WINEDLLOVERRIDES=mscoree,mshtml=`, which otherwise stalls on the private
desktop. `BEPINEX_PRELOADER_LOG` keeps a startup failure's log in the session
rather than next to the read-only exe.

## Shared install

All sessions run one read-only VRoid from the Nix store (R-SES-9). A session
holds only its prefix and BepInEx root, about 600 MB; a new one is ready in
about 15 s, most of it creating the prefix. Upgrades are new store paths, so
nothing is copied and no marker records a version.

- `vroid-studio-app` runs the vendor installer headless under Wine's `null`
  graphics driver; innoextract cannot read its Inno Setup 6.7 format. Doorstop
  loads BepInEx only through a `winhttp.dll` next to the exe, so the build
  adds it. VRoid and Unity write only to the prefix's AppData and `-logFile`.
- `vroid-studio-interop` is BepInEx's unhollowed directory, generated by
  starting VRoid with `-batchmode -nographics` until BepInEx writes
  `interop/assembly-hash.txt`, and fails early if BepInEx logs that
  generation failed. It depends on the app, BepInEx, and the loader config
  but not the plugin, so bridge changes do not regenerate it. The app depends
  only on the installer, BepInEx, and Wine. The output works the same
  each time but is not bit-reproducible. Generation runs out of managed memory
  without an explicit heap ceiling, so the build sets `DOTNET_PROCESSOR_COUNT=2`
  and `DOTNET_GCHeapHardLimit=400000000`, which is hexadecimal (16 GiB). It
  needs about 5 GB of memory.
- BepInEx hashes `GameAssembly.dll`, the extracted Unity libraries, and its
  generator versions to decide whether the interop assemblies are current;
  all are inputs to the interop build. `UpdateInteropAssemblies = false` keeps
  BepInEx from regenerating them into the store. On a mismatch it logs
  "Interop assemblies are possibly out of date" instead, and `launch` fails
  with that log. BepInEx rewrites `LogOutput.log` on every start, and the
  bridge logs `vroid-cli bridge loaded; boot ID` when it loads, so `launch`
  reads only a log naming the ready bridge's boot. be.788 passes
  `InstantFlushing` to its log writer inverted, so `false` writes each line at
  once and the line is on disk before the first heartbeat.
- The launcher wrapper sets `VROID_WINE`, `VROID_APP`, `VROID_INTEROP`,
  `VROID_BEPINEX`, `VROID_BRIDGE`, and `VROID_BEPINEX_CONFIG`, and the
  launcher passes them to Wine.
  Nix's garbage collector treats a store path in a running process's
  environment as a root, so every Wine process keeps the app's files for its
  whole life (R-SES-10). A stopped session needs nothing from the store; its
  links are replaced at the next launch.

## File protocol

The CLI and bridge exchange JSON files in `rpc/`. Every write goes to a
temporary file renamed over its target, so readers never see partial files.

- `status.json`: written by the bridge about once per second with `boot` (a
  random ID per app instance), `protocol`, `heartbeat` (Unix ms), `main`,
  `editor`, `updating`, `modified`, and `rebuilt`. It is stale after 5
  seconds. `protocol` is `PROTOCOL` in `vroid_cli/rpc.py` and
  `RpcDirectory.Protocol` in `plugin/Bridge.cs`; bump both for incompatible
  changes.
- `request.json`: at most one pending request, written by the CLI: the
  validated request's fields plus `id` (32 lowercase hex digits), `boot`, and
  `timeout_ms`. Paths are Wine paths (`Z:/...`).
- `results/ID.json`: `{"ok", "result", "error"}`, written by the bridge before
  it removes the request. Results are never deleted (R-RPC-6).
- `undo/ID.json`: an `apply` or `import` request's undo template, written by
  the bridge before the first change (R-EDIT-11). Like results, undo files are
  never deleted.

A request command takes the lock, requires a fresh
heartbeat with the same protocol (R-RPC-4), refuses if `request.json` exists
(R-RPC-3), tags the request with the heartbeat's `boot`, and waits until the
bridge removes the request, then reads the result. When the request's undo
file exists, the result gains its path as `undo`, which result validation
checks parses as an `apply` request, and every error names it; a timed-out
`apply` names it even before it appears. Waiting for the removal
keeps the next command from seeing a finished request as pending. It never
resends (R-RPC-5).

The bridge answers a request for another `boot` with an error without running
it (R-RPC-1). Each instance runs a request ID at most once (R-RPC-5). It
serializes the result once, falling back to an error result if the payload
cannot be serialized. Publishing writes the current status, then the result,
then removes the request, so a caller holding a result reads a current
`modified` (R-SES-4). If any step fails, `RpcDirectory` logs the error, keeps
the result, and retries on every later `Take` before accepting another
request. `stop` checks for `request.json` before reading the status for the
same reason.

When the bridge loads with a request still present, it records an unknown
outcome unless that request's result already exists (R-RPC-2). A request file that does not parse is moved to
`request.invalid.json`; the CLI then reports that no result was written.

The bridge checks the request's `timeout_ms` (90 s) between frames and then
reports a possibly incomplete outcome. A single blocking call, such as
`SaveSync`, can run past it and complete later. The CLI waits 30 s longer, so a
slow operation normally yields a result rather than a CLI timeout. If the CLI
still times out, the request stays pending and its result appears later.
Timers use a monotonic clock.

## Bridge

`plugin/Bridge.cs` holds the file protocol without Unity dependencies, so
`tests/protocol` runs it under the SDK. `plugin/Plugin.cs` holds the app logic;
`plugin/Reflection.cs` resolves IL2CPP types by name.

All reads and mutations run on Unity's main thread from a Harmony postfix on
`EventSystem.Update`, once per frame. Each operation is an iterator that yields
a frame while it waits; the bridge also sets `Application.runInBackground` so
the player keeps running unfocused. The bridge compiles against the loader's
bundled .NET 6 and BepInEx assemblies only, and finds app types by reflection.

The bridge tracks these app objects:

- Main view model: a `MainViewModel` constructor or startup hook did not
  capture it reliably. The bridge finds live
  `VRoidStudio.GUI.AvatarEditor.AvatarEditor` objects with
  `Resources.FindObjectsOfTypeAll<T>` and reads `_viewModel`. The similarly
  named start-screen markup type is not a Unity `Object` and does not satisfy
  that constraint.
- Edit root: `VRoid.Studio.EditRootScreen.ViewModel.OnScreenActivate`
  captures it; `BackToModelSelect` clears it.
- Selectors: `BasePresetsSelectorViewModel.UpdateSelectedPresets` captures the
  Face, Hair, Body, and Outfit category selectors by concrete type. VRoid
  calls the Outfit editor `ClothingEditor`.
- Rebuilds: `VRoidCoreModelUpdater.set_IsUpdatingModel` counts each
  true-to-false transition and captures the updater, whose `ModelOrigin` is
  the root transform of the model on screen.

Operations:

| Method | Internal call | Completion |
| --- | --- | --- |
| `open` | `MainViewModel.ImportFileAndMoveToEditRoot(path, false, false)`, then rebuilds hidden or locked accessories' colours, below | Edit root captured and one rebuild finished, then each such accessory restored |
| `new` | `MainViewModel.InitializeCurrentFileAndMoveToEditRoot(base, variant)`, below | Edit root captured and one rebuild finished |
| `categories` | Edit root `set_EditMode(FaceEditor, HairEditor, BodyEditor, or ClothingEditor)`, selector `Categories.Children[].Name` | Selector captured |
| `parameters` | Selector `set_SelectedCategory`, then the category's `Parameters[]` `Label`, `CombinedValue` or `Value`, `MinValue`, `MaxValue`, and its colours, below | No rebuild running |
| `items` | The category's item state and presets, below | Selector captured |
| `accessories` | Edit root `set_EditMode(AccessoryEditor)`, its `Instances` and their shape sliders' bounds, templates, and bones, below | Editor active |
| `dump` | `parameters` and the item for every Face, Hair, Body, and Outfit category in app order, then `accessories` | No rebuild running |
| `apply` | Items, then accessories, below; then `parameters` for each named category, and parameter `set_CombinedValue` or `set_Value`, or a colour's setter, per changed value | One rebuild finished per changed item, accessory, accessory bone, placement, hidden or locked flag, and value (R-EDIT-6) |
| `import` | The custom item registry's `ImportCustomItemByTransferableType`, below | Task completed, one more instance listed, its rebuilds finished, and its colour fields built |
| `export` | The custom item registry's `ExportAccessoryTemplate`, below | Call returned and the file names the accessory's kind |
| `save` | Edit root `_mainActionHandler.SaveSync(path, true)` | Call returned true and the file is nonempty |
| `render` | A temporary Unity camera renders `ModelOrigin`, below | PNG written to a new file |
| `thumbnails` | The category's options' `Preview.Create()`, below | PNG written to a new file |

`open` and `new` refuse once either has started in the app instance, even
one that failed or timed out while VRoid was still loading; changing
characters needs a restart. Successful `open`, `new`, and `save` clear
`modified`.

`new` makes the call that VRoid's base dialog (`BaseSelectDialogViewModel`)
makes for "Create New": `Preset_F` with model variant `F00`, or `Preset_M`
with `M00`. The bridge holds those pairs. The bases are assets in the install,
so `new` works offline, and each base gives the same `dump` in every session
and wears only built-in items (R-EDIT-14). VRoid does not count the new
character as unsaved and gives it no path; `save` always passes one. The
bridge does not use VRoid's sample models, which VRoid downloads on first use
from pixiv's CDN through a remote catalog that the pinned version does not
fix.

A template is an `apply` request: `Face`, `Hair`, `Body`, and `Outfit` map
category names to labels to values, and `Accessories` lists accessories,
below. A value is a number, or a colour as `#RRGGBB`. A category's reserved
label `item` holds its item, below; `dump` fails if a category has a value
labelled `item` (none does in 2.16.0). Labels repeat within some categories (Face Sets
and Nose each have two "Nose Size" parameters, and an outfit item has a "Base
Color" per part), so a repeated label maps to an array of its values in app
order. Categories also share parameters: within a
section, the same label in another category is the same value (Face Sets,
Eyes Sets, and Eyeliner all hold "Eye Size X"); recheck this with any VRoid
pin change. `dump` therefore lists each label under the first category in app
order that has it, omits categories left empty, and writes float32 values in
their shortest round-trip form, so applying the output reproduces it exactly
(R-EDIT-7). It fails if a later copy of a label differs, or if two categories
in a section share a name (none do in 2.16.0). The `Apply` model refuses a
label in two categories of a section, since the later value would silently
win.

`apply` runs in two passes. The first resolves every item, accessory, category
name, and value, checking each item as below and each value's label count,
live bounds, and kind, and collects every refusal. Bounds compare the value as
float32, as VRoid stores it, so a dumped bound such as 0.7 stays in range. Any
refusal fails the request before anything changes (R-EDIT-3, R-EDIT-10). When
the request may change a category's item (its own pick, a conflicting item
VRoid takes it off for, or a set in the section), the first pass skips the
colours the next item may offer differently: every Outfit colour, since each
item brings its own parts, and elsewhere colours unavailable for the current
item; an added accessory's shape sliders are known only once it is worn
too. The second pass checks those (R-EDIT-13). It changes items, then
accessories, because a new item can
reset its category's values (Irises and Eyebrows, and an Outfit item's
colours, in 2.16.0; other colours survive), then revisits the categories and
sets each value that differs, waiting for each rebuild. It stops at the first
new refusal, such as a colour the new item does not offer. It sets `modified`
before the first change, so an interrupted edit still counts as unsaved. The
result counts the changes and lists in `discarded` what `undo` cannot restore.
The bridge writes the `undo` template to its file once the first pass
succeeds, before any change. When the request changes items, `undo` is a
`dump` taken before the change, without items that are not built in, since a
new item can reset its category's values and VRoid changes other categories
with it, such as the Base Hair that each Hair Back item brings, so it includes
the accessories; otherwise it holds the previous values of what the request
names, and the previous accessories when the request lists them. A custom
accessory leaves `undo` once its removal is confirmed, and the bridge then
rewrites the file; a failed rewrite fails the request. A crash during that
removal can leave it in the file, which apply then refuses. If the second pass
fails, the bridge ends the operation, then reports what already changed and
what was discarded, and the CLI adds the undo file. Each item waits for its
section to settle, so reapplying a whole template takes tens of seconds,
within the request timeout.

### Items and accessories

Face, Hair, Body, and Outfit categories that wear items are
`PresetCategoryViewModel`s, or `ParametricPresetCategoryViewModel`s whose
`PresetCategoryViewModel` holds them. Their `_presets[]` are the built-in
items, each a `PresetOptionViewModel` whose `_model.resourceName` is a fixed
asset path such as `pixiv/VRoid/Clothing/Tops/N00/159/....transferablegroup`.
A built-in item, in the requirements and here, is one a category's selector
offers, which `items` lists. An item's identifier is that path's
second-to-last part (`159`, `Default`, `Preset_F`), unique within each
category in 2.16.0; the bridge fails if one repeats. The path, and so the identifier, does not change between sessions
and restarts (R-EDIT-9). VRoid has no item names, only thumbnails, which
`thumbnails` draws (R-VIEW-4).

A category reads as the identifier of its option whose `IsSelected` is true.
Otherwise its `TemporaryCustomItem.WearingCustomItemSavableTransferableGroup`
tells whether it wears an item no option offers, which reads as `custom`.
Such items include texture-edited outfits, whose group is a
`user/VRoid/CustomItem/...` resource (the preset stays in
`OriginalResourceName`), imported items, and sample models' parts, some of
them built-in assets the selector does not offer, such as a sample's socks.
Selecting sets the option's `IsSelected`, which runs VRoid's own selection
command; `ClearTransferableGroup` takes the item off a category whose
`IsRemovable` is true. Each takes a rebuild, after which the selector updates
after a debounce, and VRoid can change other categories a few frames later,
so the bridge waits for the rebuild, then for the category to read the new
item and every category in the section to read the same for 20 frames
without a rebuild.

VRoid takes off conflicting items itself: the selector's
`ConflictingTransferableTypes` lists their kinds, such as
`TransferableType.N00.Onepiece` for Tops, matched by the last part of each
category's `TransferableGroupType`. `apply` refuses two conflicting items in
one request (R-EDIT-3), and waits after a selection until the conflicting
categories read null.

Sets (Face Sets, Eyes Sets, Hairstyle Sets, Whole Sets; group types ending in
`Set`) select items in other categories, list those as conflicts, and read as
selected only while those items match. `dump` therefore omits their items,
since the other categories describe them, and editing one item of a dump
still applies. `apply` accepts a set, selecting it before the other items in
app order, refuses it with any item it would select, including a null, and
fails after the changes if a set no longer reads as selected.

`apply` accepts the item a category already wears, including `custom` and a
null that a category cannot take off, and changes nothing for it. `custom`
elsewhere is refused. VRoid also replaces items it does not list as
conflicts, such as a sample model's custom base hair when its overall hair
changes, so without `discard_custom`, `apply` refuses any item change in a
section that wears an item that is not built in (R-EDIT-10). With it, the
change goes ahead, and `discarded` names each such
item, found by comparing every category with before once a section's
selections end, including when one fails.
`undo` omits every item that is not built in, since `"custom"` would only keep
one still worn, but keeps their categories' values, some shared with other
categories, and restores every other category, including those VRoid
changed itself, such as a dress taken off for a top. Its own item changes in
a section that still wears an item that is not built in need
`discard_custom` too.

Accessories live in the Accessory editor (`AccessoryEditor`), whose
`Instances` are the worn accessories in app order. A built-in accessory
template's resource name is `pixiv/VRoid/Attachment/ID/...`, and its
identifier is `ID`, such as `CatEar`. VRoid keeps one group per kind of
accessory, such as glasses: the first one added is the template's own
transferable, and later ones of that kind are `AdditionalAccessoryTemplate`
copies in the same group, so an instance's group names only the first one.
`GetTransferableDisplayNameQuery` on the edit context's `editingModel`
returns the instance's display name, whose `LocalizationKey`,
`Model.N00.Transferable.ID`, names its own template; an instance whose key
names no built-in template is `custom`. So is every instance whose group's
resource (`transferableInfo.GroupResourceName`) is a
`user/VRoid/CustomItem/...` one, which `import` starts, below: an imported
accessory keeps its template's key, and a built-in one added later joins the
import's group. An accessory imported with VRoid's own button into a kind
that already had a built-in group still reads as its built-in item, since
nothing marks it; `apply` treats it as one, so a dump or undo drops its
texture. Each instance has its own colours in
its `CategoryColorSelection`, read and written like Face colours but never
shared. Every built-in accessory has one, labelled "Color", so the first pass
refuses other labels for accessories the request adds, and the second checks
it once they are worn. The editor's
`Templates` list offers only the glasses, but the asset catalog
(`_mainModel.assetStorage.AssetCatalog.TransferableGroupEntries`) also holds
animal ears, tails, and hats, which VRoid's sample models wear. Selecting a
template not in the list crashes VRoid, so `accessories` and `apply` first
append a `TransferableGroupTemplateItemViewModel` for each missing attachment
to `Templates`; the editor's own list then shows them too. Setting
`SelectedTemplate` to a different template adds one instance at the end of
`Instances`, so `apply` sets it to null first, which lets the same template
add another; `AddSelectedTemplate` would add a second instance.
Selecting an index and calling `RemoveSelectedInstance` removes one. Each
takes one rebuild.

An accessory entry holds its `item`, its colours and shape sliders by label,
its placement under the reserved keys `bone`, `position`, `rotation`, and
`scale`, and its `name`, `hidden`, and `locked` keys, below; `dump` fails if a
label equals a reserved key. Each instance's
`Parameters` are its shape sliders, read and written like Face sliders
(`Value`, or `CombinedValue` for a two-sided one). Fox Tail repeats "Tail Base
Thickness", so the repeated-label rule applies. Their bounds differ by slider
(Cat Ears 0 to 100, Tail Length -1 to 1 in 2.16.0), and an entry holds only
values, so `accessories` also lists `sliders`: for each entry of `worn`, in
the same order, its sliders as `parameters` lists a category's. Only a worn
instance has sliders, so an accessory that is not worn has no bounds to list
until `apply` adds it (R-EDIT-13). Placement is the instance's
`PartOrigin`, read with `GetPartOriginQuery(transferableId)` on the editing
model: a parent bone, VRoid's joint name such as `J_Bip_C_Head`, and a
position, rotation quaternion, and scale in its axes. A new accessory's
placement is the identity, except a hat's on the Head, which VRoid fits about
0.13 m up and tilted 10° back. The skill lists each bone's axes, measured from
renders. VRoid saves exactly the float32 values
given and does not normalize a quaternion, so they read back exactly after a
save and a reopen. The editor's own view-model setters (`PartOriginLocalScale`
and so on) only change what the gizmo shows, so `apply` runs VRoid's commands
through the edit context's `ExecuteSyncCommand`, which records them in VRoid's
history: `SetPartOriginTransformCommand(transferableId, Transform)` and
`SetPartOriginParentCommand(groupId, ResolvedBoneId)`, each one rebuild. The
parent command resets the placement to the identity, so `apply` sets the
bone first and then the placement the entry names. A
null bone is VRoid's "None", which removes the `PartOrigin` and so the
placement. `accessories` lists the bones `apply` accepts: the avatar's
`HumanBoneMap`, 54 humanoid joints in 2.16.0 (Jaw is not mapped), which
`plugin/ItemIds.cs` holds and any worn accessory's `GetAvatar()` checks.

The parent command throws while another accessory of its group is worn, and
VRoid keeps one group per kind (the catalog entry's `transferableGroupType`,
such as every pair of animal ears). A new accessory starts on its default
bone, which `plugin/ItemIds.cs` holds for every catalog accessory and each
fresh one checks: Hips for tails, Head for the rest. `DuplicateSelectedInstance` appends a copy with the
same item, bone, placement, colours, and shape. So the accessories of one kind
sit on their default bone, except copies of one item on one other bone, and
`apply` refuses any other combination before any change (R-EDIT-3).

`Accessories` in a template is every accessory to wear. Built-in accessories of
one item are interchangeable, so `apply` keeps for each wanted one the first
unmatched worn one of its item whose entry already holds what it names (on the
default bone first, for one that names no bone), then one on its wanted bone,
then for one that names no bone one on the default bone, then any of its item;
`custom` matches worn accessories that are not built in by their order, which
is all that identifies them. It removes the other worn ones from the end, moves
a kept one that is alone in its kind, then adds the rest and sets their
placement, colours, shape sliders, name, and flags, reading each back. When a
kept one must move while others of its kind stay, or an added one must start
away from its default bone while others are worn, `apply` rebuilds that kind:
it removes every one, adds the first one that is away from its default and
moves it while alone, duplicates it for the others on that bone, and adds the
rest; each takes over the values of the one it replaced. This reaches every
arrangement VRoid's own controls can make, so dumps and undo files restore
exactly (R-EDIT-7, R-EDIT-11). A custom accessory moves only while alone in its
kind. `apply` refuses an unknown item or bone, `custom` with no other worn
accessory that is not built in, more than the editor's `MaxInstanceCount`, and
removing one that is not built in without `discard_custom`. It also refuses to
add a built-in accessory to a kind whose group an import started, worn or not,
below (R-EDIT-3). `dump` and `accessories` list accessories by item, built-in
before `custom`; one item's built-in accessories are ordered by their entry,
and custom ones keep app order, so lists compare across instances whichever one
VRoid lists first. `plugin/ItemIds.cs`, which `tests/protocol` also builds,
holds the identifier, bone, and matching rules.

### Custom item files

`import` and `export` call the editor's `_customItemRegistry`
(`VRoid.Studio.ModelPresetsSelector.CustomItemRegistry`), passing the
editor's `GlobalBus`, its `_engine`, and that engine's `Context`. VRoid's own
buttons wrap these calls in file dialogs. Both take an error callback, an
`Il2CppSystem.Action<GlobalBus, CustomItemErrorType>` that `Reflect.Callback`
builds from a managed delegate, so errors such as `Incompatible` reach the
bridge and VRoid shows no dialog. A `.vroidcustomitem` is a zip archive;
`v1customitem/meta.json`'s `rootTransferableGroupTypeId` names its kind, such
as `TransferableGroupType.N00.Level1.AnimalEar`, which
`plugin/CustomItemFile.cs`, also built by `tests/protocol`, reads; it refuses
a `meta.json` over 1 MiB, which would stall the main thread. The file carries
the accessory's mesh, textures, colours, shape sliders, bone, placement,
name, and hidden and locked flags.

VRoid keeps a kind's group in the model (the editing model's
`_transferableGroups`, keyed by group with its `TypeId` and header
`ResourceName`) after the last accessory of the kind is removed, also across
a save, and adds every later accessory of the kind to it. Its
`RemoveTransferableGroupAndChildrenCommand` reports success on an empty group
but leaves it in place. An import into a kind without a group starts a
`user/VRoid/CustomItem/...` group, which reads as custom; one into a kind with
a built-in group becomes an `AdditionalAccessoryTemplate` copy there, which
nothing marks as imported. So `import` refuses a file whose kind has a
built-in group, worn or not, and `apply` refuses a built-in accessory whose
kind's group an import started (R-EDIT-3, R-EDIT-15). These refusals are
VRoid's limit rather than the request's fault, so their errors ask the agent
to report them to the user.

`import` reads the file's kind and refuses, before any change, a file that is
not a custom item, a kind that is not one of the catalog's accessory kinds, a
kind with a built-in group, and a project that already wears `MaxInstanceCount`
accessories, where VRoid would show a dialog no one can close. It writes its
undo file, an `apply` request holding the accessories from before with
`discard_custom`, then calls `ImportCustomItemByTransferableType(globalBus,
path, context, onError)`, whose task completes within a few frames. VRoid
reports an error before changing anything; otherwise it appends the accessory
to `Instances` and rebuilds twice. The bridge waits until rebuilds stop, builds
the colour fields VRoid leaves out for an accessory imported hidden or locked,
as `open` does (below), finds the new accessory as the one VRoid lists last,
and returns its entry. `custom` entries keep custom accessories in app order,
so the undo's entries keep the accessories from before and remove the import
(R-EDIT-11) only while VRoid lists those as before and the import after them.
The bridge checks that, and otherwise replaces the undo file with one that
keeps what is worn and fails, leaving the import worn. The undo leaves the
import's group, so built-in accessories of its kind stay refused.

`export` takes an index into `accessories` `worn` and calls
`ExportAccessoryTemplate(transferableId, groupId, globalBus, path, context,
engine, onError)`, which writes the file before it returns and changes
nothing in the project, not even `modified` (R-EDIT-16). The bridge checks
that the file names the accessory's kind. The CLI publishes it like a render,
below.

VRoid's accessory list can hide, lock, and rename each accessory, and saves
all three. An instance's `IsVisible` and `IsEditable` read the first two, and
`dump` writes them as `hidden` and `locked`. Its display name's
`UserDefinedString` is the user's name, which `dump` writes as `name`, or null
for VRoid's default name, which the app shows in its own language. VRoid's
Rename dialog keeps the display name's `LocalizationKey`, which names the
template, so `apply` clones the display name and sets only that string; a new
display name without the key would read as `custom`. Null builds a display
name from the key and `FallbackString` alone, or without them for a custom
accessory that has none. `""` is a name, which VRoid's dialog cannot enter but
saves and reopens. `dump` always writes the three keys, since an omitted key
keeps the worn value, so a template that left out the defaults could not
restore them (R-EDIT-7, R-EDIT-11).
`SetTransferableVisibilityCommand(transferableId, bool)` and
`SetTransferableEditabilityCommand(transferableId, bool)` take one rebuild
each; `SetTransferableDisplayNameCommand(transferableId, LocalizedString)` takes
none and reads back at once, and the bridge raises the instance's
`OnPropertyChanged` for its name, which the editor's list otherwise keeps
showing. A lock stops only `RemoveSelectedInstance`, which then does nothing:
placement, bone, colours, sliders, the other flags, and
`DuplicateSelectedInstance`, which copies the name and flags, all work on a
locked accessory. So `apply` unlocks an accessory before removing it, and sets
the name, `hidden`, then `locked` after its other values. `render` leaves a
hidden accessory out, since VRoid disables its renderers.

VRoid builds an accessory instance's colour fields (`CategoryColorSelection`)
once, when a project opens or the accessory is imported, and builds none for
one that is hidden or locked then; showing or unlocking it later does not bring
them back, and VRoid's own editor shows no colour for it. The colour stays in
the project. So `open`, and `import` for the accessory it adds, show and
unlocks each such accessory, call its `InitColorSelection`, and hide or lock it
again, which takes up to four rebuilds each. The project ends as it opened and
`modified` stays false, but VRoid's own undo history holds the toggles. `open`
restores the editor's section afterwards. Without this, `dump` would drop the
colour and `apply` would refuse it until the accessory was saved shown and
unlocked and reopened.

### Colours

A colour is a field in a category's editor panel, or for outfits in the
texture editor. Accessories' colours are above.

- Face, Hair, and Body categories: the selector's `CategoryColorSelection`
  holds `_colorSelections[]`, each with `IsAvailable` and
  `_subColorSelections[]`. A sub-selection's `TranslatedDisplayName` is the
  label and its `Color` property the value. Unavailable fields (Lips for some
  items) are not listed, and `apply` refuses them, so a template from a
  project where such a field is available refuses as a whole.
- Hair categories except Hairstyle Sets and Base Hair: the edit root's
  `HairEditor` view model's `HairMaterialTemplateColor` ("Main Color") and
  `HairMaterialTemplateEmissionColor` ("Highlight Color"). One pair covers all
  hair; Base Hair has its own field above. Hairstyle Sets' aggregate colour is
  not exposed.
- Outfit categories: VRoid's editor shows outfit colours only in its texture
  editor, whose edits save custom items. The bridge runs the texture editor's
  own query, `GetClothingQuery(TransferableGroupType).Execute` on the engine
  context's `ActiveModel`, which changes nothing. Its `ClothingTemplate`
  sections are the worn item's parts in app order. Each part's `ColorGroup`
  holds `CommonColor`s, labelled by `VRoidStudio.UICulture.Translator` from
  their translation key: in 2.16.0, "Base Color" and "Shade Color" in every
  part that has colours; `Texture` sections are skipped, and any other kind
  fails as a VRoid difference. `Context.GetColorParameter` reads a colour. A
  write creates the texture editor's `CommonColorViewModel` for the colour
  without opening its screen and sets its `Color`, which takes one rebuild.
  It keeps the item's preset resource and leaves it unmarked as updated, so
  the item stays built in (R-EDIT-12). The bridge never runs the texture
  editor's advanced edit (`BeginClothingAdvancedEditCommand`), which prepares
  textures for painting.
  A custom item's colours are refused, since they do not reproduce in another
  project. Each colour's keys are its part's transferable and its position,
  so outfit colours are never shared. Selecting an Outfit item resets its
  colours to the item's defaults.

Some colours are shared. A sub-selection's accessor writes the model keys in
`_keysToSet`: Face Eyelids, Nose, and Skin and Body Whole Body each write all
four skin keys, and Irises "Both Eyes" writes the keys of "R Eye" and "L Eye".
The bridge reads the keys live and fails if a field has none, rather than
treating it as unshared; the hair colour pair uses fixed keys. A field whose
keys strictly contain another field's in its category, such as "Both Eyes",
reads only one of its colours, so it is neither listed nor settable; `apply`
names the fields to set instead. `plugin/Colors.cs`, which `tests/protocol`
also builds, holds the remaining rules. Fields with equal keys are one colour:
`dump` lists it under the first category in app order, and fails if a later
copy differs or if two listed fields still overlap, so every dump applies. It
also fails if a colour with a repeated label is listed elsewhere, since `apply`
sets such a label as a whole array.
`apply` refuses fields with overlapping keys in one request (R-EDIT-3), since
the later write would win.

Values are the hex VRoid's editor shows, from
`VRoid.UI.Util.ColorUtil.ColorToColorHex` (R-EDIT-8). VRoid stores colours on
the editor's sRGB scale; #FAEBDE is 250/255, 235/255, 222/255. A write sets
each channel to exactly `k/255f`, with no linear conversion, and keeps the
alpha, which is not part of the value. A field that already reads as the
wanted hex is not written, so a stored channel off the 1/255 grid survives a
dump and reapply. Undo after changing such a colour restores its hex, on the
grid. After each write and its rebuild, the bridge reads the hex back and
fails if it differs. VRoid's `TryParseColorHex` throws on bad input,
so the bridge parses hex itself.

### Thumbnails

An option's `Preview` is a
`VRoid.UI.TextureSource.TransferableGroupThumbnailTextureSource` for the
option's resource. Its `Create()` returns a task that loads a prebaked 300x400
`Texture2D` from the asset storage, usually within a frame, without touching
the model, so `thumbnails` changes nothing in the app (R-VIEW-2). VRoid's
capture-based `ThumbnailProvider` serves custom items only. Every call returns
a new readable texture that nothing frees, so the bridge destroys each one once
it is copied. `thumbnails` finds the category as `items` does, which switches
the editor's section, and sets the edit root's `EditMode` back when it ends,
whether or not it succeeds. It waits for each task between frames, and fails if
one faults or returns null, or if a thumbnail is not 300x400. It blits each
texture with bilinear filtering into a 150x200 `RenderTexture`, reads that
into an RGB24 `Texture2D`, and copies the raw bytes into a `Sheet`
(`plugin/Sheet.cs`, a `Canvas`, below, which `tests/protocol` also builds).
The sheet lays cells out in rows of 10 in `items` order, separated by a
4-pixel white gutter, and draws each identifier below its thumbnail with an
embedded 5x9 bitmap font at twice its size, so no Unity font, canvas, or UI is
involved. The font covers exactly the characters an identifier may hold; a
label longer than 12 characters fails as a VRoid difference. One RGB24
texture loads the sheet's bytes for `EncodeToPNG`. A category of 82 items
takes about 2 s. The PNG is published like `render`'s, below (R-VIEW-3).

### Rendering

`render` reads the model's enabled renderers under `ModelOrigin` on every
request, so it follows rebuilds. VRoid's photo booth is a separate screen with
its own pose, and its thumbnail code poses the model and hides meshes while it
runs; both change app state, so the bridge renders with its own camera instead.
In one frame, it creates a disabled orthographic `Camera` whose culling mask
holds only the renderers' layers (all Default in 2.16.0, which nothing else in
view uses), and for each requested view renders into a 4x MSAA
`RenderTexture` cleared to light grey and reads it into an RGB `Texture2D`.
A `finally` block restores `RenderTexture.active` and destroys the three
objects, so the editor's cameras, the project, and `modified` never change
(R-VIEW-2). The scene's own directional lights, fixed in world space in front
of the model, light every view, so back views show MToon's shade colours.

`plugin/Framing.cs` places the camera without Unity, so `tests/protocol` checks
it. The model's forward direction, turned by 0, -45, -90, or 180 degrees, gives
the front, three-quarter, side, and back views; the negative turns show the
model's left side. A frame has an anchor, which sits on the image's vertical
centre line a fixed 2.4% of its height above the bottom edge, and a region it
must show around the anchor. In an image narrower, for its height, than the
region, the region's width sets the scale and all spare height lies above the
region; centring it would move the anchor whenever an edit changed its height
(R-VIEW-6). A body frame anchors at `ModelOrigin`, which in 2.16.0 is the floor
below the character's centre (the soles reach 3 mm below it). It shows the
renderers' world bounds: their height above the floor and any depth below it,
and their horizontal reach from the anchor in any direction rather than the
width seen from the view, so every body view of a model at one image size
shares its scale and ground line. A head frame anchors at the humanoid
neck bone's height below the head bone (`Animator.GetBoneTransform`) and shows
up to the top of the bounds. Its width is the horizontal reach from the head
bone, in any direction, of every renderer whose bounds are centred above the
neck, such as the head, ears, hair, and hats, so head views share their scale
too. The body, whose bounds include the arms, does not widen it, and neither
does long hair centred below the neck; the frame cuts through what lies below
the neck.

The fitted size shows that region with a 5% margin, so the bounds, which
include everything worn, set the scale: an item that makes the model taller or
wider, such as an ahoge, a hat, or a long top, changes it. Every render reports
its scale in pixels per metre, and a request may give one instead (R-VIEW-6).
Since the anchor's pixel depends only on the image size, renders of one view,
frame, and size at one scale line up across character states. A given scale at
which any requested view would cut off part of the region is refused; the check
uses each view's own width, so a tail behind the model limits only views that
show it from the side. The error names the largest scale that cuts nothing off,
edge to edge, rounded down to 0.001 so it can be passed back. A fitted scale is
always at least 2.5% inside that limit, so a reported scale passed back always
fits, and a given scale is reported as given. The camera stands outside the
bounds, so no part of the model is clipped.

All views of one request share one size, so they share their scale (R-VIEW-1).
`plugin/Canvas.cs`, which `tests/protocol` also builds, assembles the image
from the views' raw RGB bytes, left to right, on the background colour, which
the camera reads from a render of nothing first. With a `reference`, a row as
wide as the views and as tall as one sits above them (R-VIEW-5). The bridge
reads the file and decodes it with `ImageConversion.LoadImage(Texture2D,
byte[])`, which handles PNG and JPEG and returns false otherwise. On the GPU,
it fills a mipmapped `RenderTexture` the reference's size with a 1x1 texture
of the background's bytes, draws the reference over it with
`Graphics.DrawTexture`, which blends by alpha, and blits that, trilinear and
clamped, to the largest size that fits the row without changing its aspect
ratio, read back as RGB24. Blending at full size keeps the colour under
transparent pixels out of the filtering, the mipmaps make a shrink average the
pixels it skips, and only the fitted pixels reach managed memory. Colours come
through exact or within 1/255. `GL.Clear` would skip the colour space
conversion the camera makes, so the fill is a texture. IL2CPP keeps several
overloads of `LoadImage` and `DrawTexture` with the same number of parameters,
so the bridge selects them by type. The canvas centres the result in the row.
`plugin/Textures.cs` holds the blit, readback, and PNG encoding that `render`
and `thumbnails` share. A four-view sheet at 512x1024 per view with a 1774x887
reference takes under 1 s.

The bridge writes the PNG of `render` or `thumbnails` to the path it is given
with `FileMode.CreateNew` and deletes the file if the write fails. The CLI
gives it a hidden `.vroid-render-HEX.png` beside the destination, publishes it
with `os.link`, which is atomic and fails if the destination exists, and
removes the hidden file in a `finally`, so a destination is never replaced or
partly written (R-VIEW-3). `export` works the same way with a hidden
`.vroid-export-HEX.vroidcustomitem` (R-EDIT-16). A CLI that stops waiting
before the bridge answers, because it was killed, interrupted, or timed out,
can leave one.

IL2CPP reflection must convert native objects to their most derived proxy type.
`Reflect.Concrete` maps `System.*` class names to `Il2CppSystem.*` proxies and
closes generic proxies, such as `ImmutableArray<T>`, over the runtime type's
arguments. `Reflect.Items` iterates by `Count`/`Length` and an integer
`get_Item`, else by enumerator, which yields a dictionary's key-value pairs.
`plugin/Render.cs` and `plugin/Thumbnails.cs` alone call instance members of
Unity engine objects through C# `dynamic`, using the loader's bundled
`Microsoft.CSharp`, so their engine calls read like Unity code; static members,
which `dynamic` cannot reach on types found at runtime, and every VRoid type go
through `Reflect`.

## Command line

`vroid-cli SESSION REQUEST` takes the request as JSON text, `@PATH`, or `-` for
stdin. Every method is a request, so saved requests work in any session.
`vroid_cli/schema.py` defines each request as a pydantic model with
`strict=True`, `extra="forbid"`, and `allow_inf_nan=False`, discriminated on
`method`. pydantic's defaults would accept `NaN`, coerce `"0.5"`, and drop
misspelled fields (R-CLI-3). Its JSON parser keeps the last of repeated keys,
so the CLI rejects repeated keys first. Validation refuses non-finite values
and colours that are not `#RRGGBB` in either case (R-EDIT-2) and checks
`.vroid`, `.png`, and `.vroidcustomitem` suffixes, an existing `open` or
`import` source, a new `save`, `render`, `thumbnails`, or `export`
destination in an existing directory (R-EDIT-4, R-VIEW-3, R-EDIT-16), a
`render` reference that exists and starts with a PNG or JPEG signature
(R-VIEW-5), and a `render` image of at most 4096 pixels per side, all before
touching session state. The CLI always sends `render`'s `view` as a list,
and an accessory's `rotation` as a quaternion: it turns
`{"degrees": [x, y, z]}` into the turn of z degrees about the bone's z axis,
then x about x, then y about y, the order of Unity's `Quaternion.Euler`
(R-EDIT-17). The bridge, `dump`, and undo files therefore know only
quaternions, and the bridge's unit-length check (R-EDIT-3) passes every
converted angle. Paths resolve against the current directory,
and `open`, `save`, `render`, `thumbnails`, and `export` report the resolved
path rather than the bridge's Wine path. The bridge rechecks the destination
before saving. Validation errors print one `FIELD: REASON` line each. Output is
indented JSON, so saved templates diff line by line.

Every result, including the bridge's, is validated against its result model
before printing, so `--agent-skill` (or `--help`), which prints the skill and
the generated request and result JSON Schema, describes the actual output. A
result that fails validation is an error that says the method ran.

A successful command prints one JSON value (R-CLI-1). A failure prints
`vroid-cli: REASON` on stderr and nothing on stdout, exiting 1, or 2 for usage
errors (R-CLI-2).

## Testing

`just test` and the Nix package checks run:

- `tests/test_cli.py`: the CLI against a stand-in bridge and Framewisp.
- `tests/test_launcher.py`: the launcher against a stand-in Wine and a
  read-only stand-in store.
- `tests/protocol`: the bridge's file protocol, colour rules, custom item
  files, camera placement and scale, item identifiers, accessory matching and
  bones, image layout and reference fitting, and thumbnail sheet layout and
  font under the .NET SDK.

`scripts/gui-smoke.sh`, run by `just gui-smoke`, drives a real session through
open, dump, numeric and colour edits, item and accessory edits, including two
accessories of one kind with one moved to another bone, placed, reshaped,
renamed, hidden, and locked, with front renders at one scale before and after
that must line up while the fitted scale shrinks and the first fitted scale is
refused (R-VIEW-6), a check that an accessory slider's listed bounds
match `apply`'s refusal, and the undo of its removal, outfit colour edits that
keep the item built in (R-EDIT-12), refusal, save, an export of the moved cat
ears that changes nothing and refuses an existing destination (R-EDIT-16),
rendering every view alone and in sheets under a PNG and a JPEG reference it
writes itself, with refusals of a missing reference, a text file, and a
truncated PNG (R-VIEW-5), drawing the thumbnails of the edited item categories,
restart, reopen with a read-back of every edited number, colour, item, and
accessory (R-EDIT-1), of each category's item identifiers (R-EDIT-9), and of a
`dump` equal to the one taken after the save, with `modified` false, a render
with the hidden accessory shown again, which must differ, refused imports of a
file that is not an accessory and of the ears while built-in ears are worn, and
reapplying the first dump, and checks the Framewisp input log and that
BepInEx's log names the ready bridge's boot. It then creates a character from
each base in two app instances (R-EDIT-6, R-EDIT-14), with a render, and an
edit that `stop` refuses until it is saved (R-SES-4), and in a last instance
imports the exported ears into a new character, compares their values, edits
them, checks that built-in ears are refused, and undoes the import (R-EDIT-3,
R-EDIT-11, R-EDIT-15). It saves editor screenshots before and after the first
edits. It decodes each render with the Python standard library to check its
size, the framing of each view, a shared lowest row, and the reference row,
checks each thumbnail sheet's size and items, and compares
`dump` and an editor screenshot before and after rendering and drawing
thumbnails. It needs graphics access.

R-EDIT-14's offline check is by hand: for each base, launch a session without
network access, then run `new` as usual; the app keeps the namespaces `launch`
started it in. In this unprivileged launch, the outer user
namespace may mount a tmpfs, which gives Xwayland a socket directory it owns,
and the inner one restores the user that owns the session:

```sh
unshare -r --net --mount sh -c 'mount -t tmpfs tmpfs /tmp/.X11-unix &&
  exec unshare --user --map-user="$1" --map-group="$2" vroid-cli "$3" "{\"method\": \"launch\"}"' \
  - "$(id -u)" "$(id -g)" SESSION
```
