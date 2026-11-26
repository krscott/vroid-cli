# Edit characters through VRoid's internal methods, not its window

The bridge runs inside VRoid and changes a character by calling VRoid's own
internal methods. vroid-cli sends VRoid no pointer or keyboard input and reads
nothing off its screen (R-EDIT-5). Input automation depends on the exact
screen layout and fails silently when the layout changes.

## Consequences

VRoid's internal names are not an interface. Every one the bridge uses is
checked only against the pinned VRoid version, and another version needs each
one checked again.

What VRoid offers only through a screen, with no method the bridge can call
safely, stays out of scope. vroid-cli does not fall back to input automation to
cover it.
