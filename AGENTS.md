# Agent guide for vroid-cli

Read `REQUIREMENTS.md` and `GLOSSARY.md` before changing behaviour.

## Glossary and decisions

- `GLOSSARY.md` defines the domain terms. It is the naming authority for code,
  the CLI, file formats and docs: use its terms, and do not use a word listed
  under a term's _Avoid_ for that term's concept. When a concept has no term,
  or a term no longer fits, change the glossary in the same PR. Keep
  implementation detail out of it.
  The requirements, README, and skill still use some avoided words; issue #63
  rewords them. Use the glossary's terms in new work.
  TODO: remove these two sentences once #63 is closed.
- `docs/adr/` records decisions that are hard to reverse, surprising without
  context, and the result of a real trade-off. Read the ADRs for an area before
  changing it, and add one only when all three hold.
- A reason that explains one piece of code belongs in a comment at that code.
  So does anything an external tool, such as VRoid or Wine, was observed to
  do that the code depends on: nothing else records it.

## Change order

- Work flows from `REQUIREMENTS.md` to code and tests. Start at the highest layer the change affects; most PRs change only code.
- For a bug fix, check whether a requirement was missing or violated. Add one when it captures a valuable durable guarantee; an implementation defect does not automatically create a new product promise.
- Never renumber, reuse or delete a requirement ID; tag it `[retired]` and delete its text and rationale instead. Tests covering a `[test]` requirement carry its ID in a comment.

## Rules

- Never commit VRoid installers or files, generated interop assemblies, Wine
  prefixes, or session state (`.vroid/`).
- Every internal VRoid name the bridge uses, and every VRoid preference key
  the launcher writes, is validated only against the pinned VRoid 2.16.0.
  Changing the VRoid, BepInEx, Unity library, nixpkgs (which supplies Wine),
  or Framewisp pins needs a new live check with `just gui-smoke`; a VRoid pin
  change also needs R-EDIT-14's and R-EDIT-18's by-hand checks (their
  `Manual test:` paragraphs). Framewisp tracks `main`; bump it with
  `nix flake update framewisp`, not by editing its URL.
- `just test` and `nix flake check` need no graphics. `just gui-smoke` needs
  graphics and Unix sockets; run it outside agent sandboxes.
- The Scope section of `.agents/skills/vroid-cli/SKILL.md` is the one list of
  what vroid-cli can and can't do. Update it with each feature; don't
  repeat the list elsewhere.
- The bridge's file protocol lives in both `vroid_cli/rpc.py` and
  `plugin/Bridge.cs`. Change them together.
