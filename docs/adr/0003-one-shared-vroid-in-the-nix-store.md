# All sessions share one read-only VRoid in the Nix store

VRoid, BepInEx, the interop assemblies, and the bridge are built into the Nix
store once, and every session runs them from there, holding only its own Wine
prefix and BepInEx working files. Installing VRoid into each session took ten
minutes or more and about 2 GB, which made parallel agents and evals
impractical, and a session's own copy of the bridge could go stale or be left
partly copied (R-SES-9).

## Consequences

vroid-cli needs Nix. An upgrade is a new store path, so no install is copied
into a session and no marker records a version.

Nothing may write next to VRoid. Its installer runs once, headless, when the
store path is built. BepInEx cannot regenerate the interop assemblies at
launch, so a mismatch fails the launch instead.

Garbage collection must not remove the store paths of a running app instance,
so its processes name them in their environment (R-SES-10).
