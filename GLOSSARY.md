# vroid-cli

vroid-cli lets an agent edit VRoid Studio characters on a display of its own,
while the user keeps using their desktop. This glossary names the concepts that
the code, the CLI, the requirements, and the documentation share.

## Language

### Sessions

**Session**:
A named, lasting workspace in which one agent runs VRoid. It keeps its state
between app instances.
_Avoid_: instance, desktop

**Private display**:
The display a session's app instance runs on, apart from the user's desktop.
_Avoid_: private desktop, background desktop, desktop (for the session's own
display)

**VRoid**:
VRoid Studio, the product vroid-cli drives, and how it behaves.
_Avoid_: the app

**App instance**:
One run of VRoid in a session, from `launch` until it exits.
_Avoid_: app (alone), app run, boot, process

**Bridge**:
The part of vroid-cli that runs inside VRoid and carries out requests.
_Avoid_: plugin

**Heartbeat**:
The bridge's regular report that its app instance is alive.

**Ready**:
The state of an app instance whose bridge can take requests.

### Requests

**Command**:
One invocation of `vroid-cli` on a session. It carries one request.

**Request**:
The JSON object a command carries, which says what to do.
_Avoid_: operation, call

**Method**:
The kind of a request, such as `apply` or `render`.
_Avoid_: operation, subcommand

**Pending request**:
A request that was sent to an app instance and that the bridge has not
finished with. A session has at most one.

**Unknown outcome**:
What a request has when its app instance ended before reporting a result.

**Refuse**:
To decline a request on purpose, because of what it asks for or the state it
would act on, instead of failing while carrying it out. Whether anything
changed before the refusal is stated separately.
_Avoid_: reject

**VRoid limit**:
A refusal caused by how VRoid behaves, not by a mistake in the request.

### Characters

**Character**:
The figure an app instance has open for editing. It is saved as a `.vroid`
file and may have unsaved changes.
_Avoid_: project, avatar, model (for the character)

**Base**:
One of the two starting characters VRoid ships, feminine and masculine.
_Avoid_: preset

**Sample model**:
One of VRoid's downloadable example characters. It wears custom items.

**Unsaved changes**:
Edits to a character made since it was last saved, opened, or created.
_Avoid_: modified, dirty

**Rebuild**:
VRoid regenerating the character's model after a change.

**Section**:
One of the four groups of categories, named as VRoid's editor names them: Face,
Hairstyle, Body, and Outfit. Accessories are not a section.
_Avoid_: editor, tab, Hair (for the section)

**Category**:
A named group of parameters within a section. It wears at most one item.

**Parameter**:
A named setting of a category or an accessory. It is either a slider or a
colour.

**Slider**:
A parameter that holds a number within bounds.
_Avoid_: numeric parameter

**Colour**:
A parameter that holds a colour.
_Avoid_: colour field

**Label**:
A parameter's name as VRoid's editor shows it. A label can repeat within a
category.

**Value**:
What a parameter holds: a number or a colour.

**Shared parameter**:
A parameter that appears in more than one category and holds one value.

**Template**:
An `apply` request that describes a character's look, so the look can be
saved, compared, edited, and applied to another character.
_Avoid_: template (for the design an accessory is made from), preset

**Undo file**:
The template that `apply` or `import` writes before its first change. Applying
it puts back what the request changed, apart from what it discarded.
_Avoid_: undo template

### Items and accessories

**Item**:
A wearable design, such as a hair part, an outfit piece, or a pair of cat ears.
_Avoid_: preset, accessory template

**Built-in item**:
An item that VRoid's editor lists under a category's Presets, or an accessory
item that ships with VRoid. It has an identifier.

**Custom item**:
An item that is not a built-in item, so it has no identifier. This is broader
than VRoid's own "Custom Item": a sample model's items count too.
_Avoid_: not built in, non-built-in, imported item

**Custom item file**:
A `.vroidcustomitem` file, which carries one custom item between characters.

**Identifier**:
The stable name by which requests refer to a built-in item.
_Avoid_: ID, name

**Set**:
An item that stands for a combination of items in other categories of its
section.

**Part**:
One separately coloured piece of an outfit item.

**Accessory**:
One worn copy of an accessory item. It has its own colour, shape, bone, and
placement.
_Avoid_: instance

**Accessory kind**:
One of the four families of accessory items: glasses, ears, tails, and hats.
_Avoid_: type

**Accessory entry**:
An accessory as a template describes it.
_Avoid_: entry (alone)

**Bone**:
The joint of the character that an accessory is attached to. Each built-in
accessory item has a default bone, where a new accessory of that item starts.

**Placement**:
An accessory's position, rotation, and scale relative to its bone. The bone is
not part of the placement.

**Discard**:
To lose a custom item that an undo file cannot restore.

### Views

**View**:
A picture of the character from one direction: front, three-quarter, side, or
back.
_Avoid_: angle, view (for what VRoid's editor shows)

**Frame**:
The part of the character a view shows: the body or the head.

**Render**:
One image of one or more views at one scale.
_Avoid_: sheet, screenshot

**Scale**:
The size of a render's views, in pixels per metre. A fitted scale is the one
`render` picks to show the framed part whole.

**Reference**:
An image the user supplies, drawn above a render's views for comparison.

**Thumbnail sheet**:
One image of VRoid's thumbnail of every built-in item in a category, each
labelled with its identifier.
_Avoid_: sheet (alone)
