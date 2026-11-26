# Renders come from the bridge's own camera

`render` draws the character with a camera that the bridge creates and
destroys within one frame, not with VRoid's photo booth, VRoid's thumbnail
capture, or a screenshot of the editor. The photo booth is a separate screen
with its own pose, and the thumbnail capture poses the character and hides
meshes while it runs, so both change what VRoid shows, and a render must change
nothing (R-VIEW-2). A screenshot of the editor includes its panels, cuts off
the feet, and cannot show the side or the back (R-VIEW-1).

## Consequences

The bridge frames the character itself; see
[0007](0007-views-keep-a-fixed-anchor.md).
