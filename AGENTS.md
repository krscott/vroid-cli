# Agent guide for vroid-cli

Read `REQUIREMENTS.md` and `DESIGN.md` before changing behaviour.

## Change order

- Work flows from `REQUIREMENTS.md` to `DESIGN.md` to code and tests. Start at the highest layer the change affects; most PRs change only design and code, or only code.
- For a bug fix, check whether a requirement was missing or violated. Add one when it captures a valuable durable guarantee; an implementation defect does not automatically create a new product promise.
- Any diff to `REQUIREMENTS.md` (addition, edit or retirement) needs explicit approval from a human reviewer. List every requirement ID, added, changed, or retired, near the top of the PR description. Don't merge until a human reviewer approves them.
- Never renumber, reuse or delete a requirement ID; tag it `[retired]` and delete its text and rationale instead. Tests covering a `[test]` requirement carry its ID in a comment.

## Rules

- Never commit VRoid installers or files, generated interop assemblies, Wine
  prefixes, or session state (`.vroid/`).
- Every internal VRoid name the bridge uses is validated only against the
  pinned VRoid 2.16.0. Changing the VRoid, BepInEx, or Unity library pins
  needs a new live check with `just gui-smoke`.
- `just test` and `nix flake check` need no graphics. `just gui-smoke` needs
  graphics, Unix sockets, and a session past VRoid's first-run dialogs; run it
  outside agent sandboxes.
- The Scope section of `.agents/skills/vroid-cli/SKILL.md` is the one list of
  what vroid-cli can and can't do. Update it with each feature; don't
  repeat the list elsewhere.
- The bridge's file protocol lives in both `vroid_cli/rpc.py` and
  `plugin/Bridge.cs`. Change them together.
