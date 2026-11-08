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
| `vroid_cli/` | Python CLI: argument checks, sessions, lifecycle, requests |
| `plugin/` | C# BepInEx IL2CPP plugin ("bridge") running inside VRoid |
| `scripts/vroid-studio.sh` | Runs on the private display: installs VRoid and the bridge, then starts VRoid |
| `nix/vroid-studio.nix` | The installer, fetched by hash, and the launcher wrapper with Wine |
| `nix/bridge.nix` | BepInEx, Unity base libraries, plugin, and loader config as one bundle |
| `default.nix` | The `vroid-cli` package; its wrapper sets `VROID_BRIDGE_BUNDLE`, `VROID_CLI_SKILL`, and `PATH` |
| `.agents/skills/vroid-cli/` | Shared agent skill, also printed by `vroid-cli --agent-skill` |

The flake permits only the unfree `vroid-studio` and `vroid-bridge` packages.
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
| `wine/` | Wine prefix; VRoid in `drive_c/VRoidStudio` |
| `installed` | Marks a completed installation |
| `rpc/` | File protocol, below |
| `runner.log`, `installer.log`, `player.log` | Framewisp runner, installer, and Unity player logs |

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

`launch` records its start time and takes the lock. A stopped desktop starts
`framewisp DESKTOP run --x11 --width 2560 --height 1440 -- vroid-studio` in a
new process session, logging to `runner.log`. A running app is waited for. An
exited app fails with instructions to wait or stop. If the runner exits during
the wait, the error shows the end of `desktop/app.log` when Framewisp recorded
VRoid's exit during this launch, which is where the launcher script's own
errors land (R-SES-5).
Otherwise the desktop failed before VRoid started, `app.log` may be from an
earlier run, and the error shows the end of `runner.log`.

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
protocol keep working until the next launch installs the new one.

The compositor uses the first AMD or Intel render node unless
`WLR_RENDER_DRM_DEVICE` is set. Automatic NVIDIA selection stalled during
testing, and NVIDIA-only hosts are unvalidated.

`stop` refuses while the last bridge status reports `modified` or
`request.json` exists, unless `--force` (R-SES-4). It then runs `framewisp
DESKTOP stop`, which waits for the app and compositor to exit. For an exited
app it waits for the runner to finish. A stopped desktop is already stopped.

`scripts/vroid-studio.sh` runs inside the private desktop:

1. Without the `installed` marker it installs VRoid silently into a new prefix.
   An existing prefix without the marker is an incomplete installation; it
   stops with recovery steps instead of reinstalling (R-SES-5).
2. When `drive_c/VRoidStudio/.vroid-bridge-bundle` does not name the current
   bundle path, it makes existing copies of the bundle's top-level entries
   writable, copies the bundle with `cp -r --no-preserve=mode
   --remove-destination`, and only then writes the marker (R-SES-6). Store
   files are read-only, and earlier copies kept their modes. Upgrades happen
   only here, so only while the app is stopped.
3. It starts VRoid with `winhttp` overridden to load BepInEx's Doorstop, the
   RPC directory in `VROID_BRIDGE_DIR`, and the `.NET` settings below.

On first start BepInEx generates interop assemblies from the installed game.
Proprietary VRoid assemblies are neither bundled nor built against. The Unity
base libraries are prefetched into `BepInEx/unity-libs` instead of downloaded.
Generation runs out of managed memory without an explicit heap ceiling: the
launcher defaults to `DOTNET_PROCESSOR_COUNT=2` and
`DOTNET_GCHeapHardLimit=400000000`, which is hexadecimal (16 GiB). Both can be
overridden from the environment.

## File protocol

The CLI and bridge exchange JSON files in `rpc/`. Every write goes to a
temporary file renamed over its target, so readers never see partial files.

- `status.json`: written by the bridge about once per second with `boot` (a
  random ID per app instance), `protocol`, `heartbeat` (Unix ms), `main`,
  `editor`, `updating`, `modified`, and `rebuilt`. It is stale after 5
  seconds. `protocol` is `PROTOCOL` in `vroid_cli/rpc.py` and
  `RpcDirectory.Protocol` in `plugin/Bridge.cs`; bump both for incompatible
  changes.
- `request.json`: at most one pending request, written by the CLI, with `id`
  (32 lowercase hex digits), `boot`, `method`, `timeout_ms`, and arguments.
  Paths are Wine paths (`Z:/...`).
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
  true-to-false transition.

Operations:

| Method | Internal call | Completion |
| --- | --- | --- |
| `open` | `MainViewModel.ImportFileAndMoveToEditRoot(path, false, false)` | Edit root captured and one rebuild finished |
| `categories` | Edit root `set_EditMode(FaceEditor or BodyEditor)`, selector `Categories.Children[].Name` | Selector captured |
| `parameters` | Selector `set_SelectedCategory`, then the category's `Parameters[]` `Label`, `CombinedValue` or `Value`, `MinValue`, `MaxValue` | No rebuild running |
| `set` | Parameter `set_CombinedValue` or `set_Value` | One rebuild finished (R-EDIT-6) |
| `save` | Edit root `_mainActionHandler.SaveSync(path, true)` | Call returned true and the file is nonempty |

`open` refuses once a project is open; changing projects needs a restart.
`set` checks the value against the live bounds before assigning it (R-EDIT-3)
and sets `modified` first, so an interrupted edit still counts as unsaved.
Successful `open` and `save` clear `modified`. Duplicate category or parameter
labels exist (Face Sets has two "Nose Size" entries); selecting one by an
ambiguous name fails instead of guessing.

IL2CPP reflection must convert native objects to their most derived proxy type.
`Reflect.Concrete` maps `System.*` class names to `Il2CppSystem.*` proxies and
closes generic proxies, such as `ImmutableArray<T>`, over the runtime type's
arguments. `Reflect.Items` iterates by `Count`/`Length` and `get_Item`, else by
enumerator.

## Command line

The CLI validates arguments before touching session state: section names,
finite `set` values (R-EDIT-2), `.vroid` suffixes, an existing `open` source,
and a new `save` destination in an existing directory (R-EDIT-4). The bridge
rechecks the destination before saving.

A successful command prints one JSON value (R-CLI-1). A failure prints
`vroid-cli: REASON` on stderr and nothing on stdout, exiting 1, or 2 for usage
errors (R-CLI-2).

## Testing

`just test` and the Nix package checks run:

- `tests/test_cli.py`: the CLI against a stand-in bridge and Framewisp.
- `tests/test_launcher.py`: the launcher against a stand-in Wine.
- `tests/protocol`: the bridge's file protocol under the .NET SDK.

`scripts/gui-smoke.sh`, run by `just gui-smoke`, drives a real session through
open, edit, refusal, save, restart, and reopen, and checks the Framewisp input
log. It needs graphics access and a session whose first-run dialogs are
complete.
