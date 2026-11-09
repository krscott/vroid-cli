# vroid-cli design

`vroid-cli` edits VRoid Studio characters by calling VRoid's internal Unity
methods from an injected plugin. VRoid runs under Wine on a private Framewisp
display, so editing needs no synthetic input or OCR (R-EDIT-5) and leaves the
user's desktop alone (R-SES-7). Separate desktops are the goal; this is not a
filesystem sandbox.

## Supported versions

The pinned combination is the only validated one: VRoid Studio 2.16.0 (Unity
6000.0.62f1, IL2CPP, Windows x64) under Wine 11.21, BepInEx 6 IL2CPP be.788,
and Framewisp `cf7b3ee` on x86_64 Linux. The flake exposes only
`x86_64-linux`. Another VRoid version needs new Unity base libraries and a
fresh check of every internal name below.

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

`launch` records its start time and takes the lock. A stopped desktop has its
BepInEx logs removed, so any BepInEx log belongs to the app the desktop runs,
and then starts
`framewisp DESKTOP run --x11 --width 2560 --height 1440 -- vroid-studio` in a
new process session, logging to `runner.log`. A running app is waited for. An
exited app fails with instructions to wait or stop. If the runner exits during
the wait, the error shows the end of `desktop/app.log` when Framewisp recorded
VRoid's exit during this launch, which is where the launcher script's own
errors land, followed by the end of BepInEx's log (R-SES-11). Otherwise the
desktop failed before VRoid started, `app.log` may be from an earlier run, and
the error shows the end of `runner.log`. A timeout also shows the end of
BepInEx's log, or explains an interop mismatch it reports.

BepInEx writes `bepinex/preloader.log` only when it fails to start, and then
waits behind an error dialog nobody can close, so `launch` fails as soon as
that file appears.

It then waits for a bridge heartbeat that is fresh, was written after the
launch began, and has found the main view model (R-SES-2). The bridge finds the
main view model on VRoid's welcome screen, before the user accepts the terms.
VRoid records acceptance as `<AllAgreementsAreAccepted>Yes` in
`wine/drive_c/users/*/AppData/LocalLow/pixiv/VRoid Studio/preferences/editoroption.xml`.
`launch` and every request command fail with instructions while that is missing,
and `status` reports it as `terms_accepted` (R-SES-8). An older heartbeat
can look fresh for a few seconds after the previous app exits. The wait fails
early if the runner exits. On timeout the app keeps starting; running `launch`
again resumes the wait. A bridge reporting another protocol version fails with
instructions to stop and relaunch; bridges from other builds with the same
protocol keep working until the next launch runs the new one.

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
2. It points `bepinex/core` and `bepinex/plugins` at the store and copies
   `plugin/BepInEx.cfg` from the store to `bepinex/config`, replacing all
   three each launch; BepInEx rewrites its config, so the copy is writable.
3. It starts the store's VRoid with Doorstop's `--doorstop-*` arguments aimed
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

A request command takes the lock, requires accepted terms, requires a fresh
heartbeat with the same protocol (R-RPC-4), refuses if `request.json` exists
(R-RPC-3), tags the request with the heartbeat's `boot`, and waits until the
bridge removes the request, then reads the result. Waiting for the removal
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
  Face and Body category selectors by concrete type.
- Rebuilds: `VRoidCoreModelUpdater.set_IsUpdatingModel` counts each
  true-to-false transition and captures the updater, whose `ModelOrigin` is
  the root transform of the model on screen.

Operations:

| Method | Internal call | Completion |
| --- | --- | --- |
| `open` | `MainViewModel.ImportFileAndMoveToEditRoot(path, false, false)` | Edit root captured and one rebuild finished |
| `categories` | Edit root `set_EditMode(FaceEditor or BodyEditor)`, selector `Categories.Children[].Name` | Selector captured |
| `parameters` | Selector `set_SelectedCategory`, then the category's `Parameters[]` `Label`, `CombinedValue` or `Value`, `MinValue`, `MaxValue` | No rebuild running |
| `dump` | `parameters` for every Face and Body category in app order | No rebuild running |
| `apply` | `parameters` for each named category, then parameter `set_CombinedValue` or `set_Value` per changed value | One rebuild finished per changed value (R-EDIT-6) |
| `save` | Edit root `_mainActionHandler.SaveSync(path, true)` | Call returned true and the file is nonempty |
| `render` | A temporary Unity camera renders `ModelOrigin`, below | PNG written to a new file |

`open` refuses once a project is open; changing projects needs a restart.
Successful `open` and `save` clear `modified`.

A template is an `apply` request: `Face` and `Body` map category names to
parameter labels to values. Labels repeat within some categories (Face Sets
and Nose each have two "Nose Size" parameters), so a repeated label maps to an
array of its values in app order. Categories also share parameters: within a
section, the same label in another category is the same value (Face Sets,
Eyes Sets, and Eyeliner all hold "Eye Size X"); recheck this with any VRoid
pin change. `dump` therefore lists each label under the first category in app
order that has it, omits categories left empty, and writes float32 values in
their shortest round-trip form, so applying the output reproduces it exactly
(R-EDIT-7). It fails if a later copy of a label differs, or if two categories
in a section share a name (none do in 2.16.0). The `Apply` model refuses a
label in two categories of a section, since the later value would silently
win.

`apply` runs in two passes. The first resolves every category name and every
value to its parameters, checking the label's count and the live bounds, and
collects every refusal. Bounds compare the value as float32, as VRoid stores
it, so a dumped bound such as 0.7 stays in range. Any refusal fails the
request before a value changes (R-EDIT-3). The second pass revisits the
categories and sets each value that differs, waiting for its rebuild, and
stops at the first new refusal. It sets `modified` before the first change, so
an interrupted edit still counts as unsaved. The result counts the changed
values and holds an `undo` template of the previous values. If the second pass
fails, the error lists the values already changed. Each rebuild takes a few
frames, so even a template that changes every value finishes far within the
request timeout.

`render` reads the model's enabled renderers under `ModelOrigin` on every
request, so it follows rebuilds. VRoid's photo booth is a separate screen with
its own pose, and its thumbnail code poses the model and hides meshes while it
runs; both change app state, so the bridge renders with its own camera instead.
In one frame, it creates a disabled orthographic `Camera` whose culling mask
holds only the renderers' layers (all Default in 2.16.0, which nothing else in
view uses), renders into a 4x MSAA `RenderTexture` cleared to light grey, reads
it into an RGB `Texture2D`, and encodes it with `ImageConversion.EncodeToPNG`.
A `finally` block restores `RenderTexture.active` and destroys the three
objects, so the editor's cameras, the project, and `modified` never change
(R-VIEW-2). The scene's own directional lights, fixed in world space in front
of the model, light every view, so back views show MToon's shade colours.

`plugin/Framing.cs` places the camera without Unity, so `tests/protocol` checks
it. The model's forward direction, turned by 0, -45, -90, or 180 degrees, gives
the front, three-quarter, side, and back views; the negative turns show the
model's left side. A body frame fits the renderers' world bounds with a 5%
margin: their height, and their horizontal radius rather than the width seen
from the view, so every body view of a model at one image size shares its scale
and ground line. A head frame spans from the humanoid neck bone to the top of
the bounds, centred on the head bone (`Animator.GetBoneTransform`). Its width
is the horizontal reach from the head bone, in any direction, of every renderer
whose bounds are centred above the neck, such as the head, ears, hair, and
hats, so head views share their scale too. The body, whose bounds include the
arms, does not widen it, and neither does long hair centred below the neck. The
camera stands outside the bounds, so no part of the model is clipped.

The bridge writes the PNG to the path it is given with `FileMode.CreateNew` and
deletes the file if the write fails. The CLI gives it a hidden
`.vroid-render-HEX.png` beside the destination, publishes it with `os.link`,
which is atomic and fails if the destination exists, and removes the hidden
file in a `finally`, so a destination is never replaced or partly written
(R-VIEW-3). A CLI that stops waiting before the bridge answers, because it was
killed, interrupted, or timed out, can leave one. A bridge that predates
`render` answers `Unknown method: render`; the CLI adds that the session needs
a stop and launch to update it.

IL2CPP reflection must convert native objects to their most derived proxy type.
`Reflect.Concrete` maps `System.*` class names to `Il2CppSystem.*` proxies and
closes generic proxies, such as `ImmutableArray<T>`, over the runtime type's
arguments. `Reflect.Items` iterates by `Count`/`Length` and `get_Item`, else by
enumerator. `plugin/Render.cs` alone calls instance members of Unity engine
objects through C# `dynamic`, using the loader's bundled `Microsoft.CSharp`,
so its engine calls read like Unity code; static members, which `dynamic`
cannot reach on types found at runtime, and every VRoid type go through
`Reflect`.

## Command line

`vroid-cli SESSION REQUEST` takes the request as JSON text, `@PATH`, or `-` for
stdin. Every method is a request, so saved requests work in any session.
`vroid_cli/schema.py` defines each request as a pydantic model with
`strict=True`, `extra="forbid"`, and `allow_inf_nan=False`, discriminated on
`method`. pydantic's defaults would accept `NaN`, coerce `"0.5"`, and drop
misspelled fields (R-CLI-3). Its JSON parser keeps the last of repeated keys,
so the CLI rejects repeated keys first. Validation refuses non-finite values
(R-EDIT-2) and checks `.vroid` and `.png` suffixes, an existing `open` source,
and a new `save` or `render` destination in an existing directory (R-EDIT-4,
R-VIEW-3), all before touching session state. Paths resolve against the current
directory, and `open`, `save`, and `render` report the resolved path rather
than the bridge's Wine path. The bridge rechecks the destination before saving.
Validation errors print one `FIELD: REASON` line each. Output is indented JSON,
so saved templates diff line by line.

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
- `tests/protocol`: the bridge's file protocol and camera placement under the
  .NET SDK.

`scripts/gui-smoke.sh`, run by `just gui-smoke`, drives a real session through
open, dump, edit, refusal, save, rendering every view, restart, reopen, and
reapplying the first dump, and checks the Framewisp input log and that
BepInEx's log names the ready bridge's boot. It decodes each render with the
Python standard library to check its size and framing, and compares `dump` and
an editor screenshot before and after rendering. It needs graphics access and a
session whose first-run dialogs are complete.
