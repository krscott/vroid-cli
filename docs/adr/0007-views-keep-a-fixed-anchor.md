# A view keeps its anchor fixed and fits the widest reach

Every view puts one anchor at a pixel that depends only on the image size: on
the vertical centre line, a fixed distance above the bottom edge. The anchor is
the floor under the character in a body frame and the point below the head at
the neck's height in a head frame, and the framed part is fitted around it by the farthest
anything reaches from it in any horizontal direction, not by the width the
view's own direction shows. Renders of one view, frame, size, and scale
therefore line up before and after an edit, and every view of one frame and
size shares a fitted scale (R-VIEW-1, R-VIEW-6).

## Consequences

An image that is narrow for its height leaves all its spare height above the
framed part.

A part that sticks out, such as a tail, shrinks the character in every body
view.

## Considered options

- Centring the framed part in the image: rejected because the anchor would
  move whenever an edit changed the character's height.
- Fitting the height and letting a wide part reach past its view: rejected
  because each view is drawn alone on an opaque background, so the part would
  be cut off at its view's edge, which could hide the change being checked.
- Fitting each view by its own width: rejected because it gains only the margin
  in a render that includes the view showing the part side on, and single
  renders of different views at one size would stop sharing a fitted scale.
