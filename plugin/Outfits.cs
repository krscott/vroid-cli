namespace VroidBridge;

// Colours of the worn Outfit items, which VRoid's editor shows only in its
// texture editor.
public sealed partial class Plugin
{
    private const string CustomItem = "the worn item is a custom item; only built-in items' colours can be set";

    // The colours of the category's worn item, one for each colour of each of
    // its parts in VRoid's order, and whether it is a built-in item; null for
    // a category that wears no item or selects other categories' items.
    // Parts stay unnamed: VRoid's only name for one is its transferable's
    // display name (`GetTransferableDisplayNameQuery`), which names what the
    // part was modelled from and misleads, such as "Dress (Short Sleeve)" for a
    // part of Bottoms 145, or repeats, as for each of Tops 144's three parts.
    private static (Swatch[] Parts, bool BuiltIn)? OutfitSwatches(object category, int index)
    {
        if (SlotOf("Outfit", category, index) is not { IsSet: false } slot || State(slot) is not { } state) return null;
        var engine = Reflect.Get(Root, "_engineModel") ?? throw Differs("the engine model");
        var context = Reflect.Get(engine, "Context") ?? throw Differs("the engine context");
        var model = Reflect.Get(context, "ActiveModel") ?? throw Differs("the active model");
        // The texture editor's own query; it changes nothing.
        var result = Reflect.Call(Reflect.New("VRoidCore.Editing.Query.AdvancedEdit.GetClothingQuery", slot.Type), "Execute", model);
        var swatches = new List<Swatch>();
        foreach (var part in Reflect.Items(Reflect.Get(result, "sections")))
        {
            if (Kind(part) != "ClothingTemplate") throw Differs($"the parts of {slot.Path}");
            var transferable = Reflect.Get(part, "transferableId") as string ?? throw Differs($"the parts of {slot.Path}");
            var number = 0;
            foreach (var section in Reflect.Items(Reflect.Get(part, "childSections")))
            {
                // Textures are edited only in the texture editor, which turns the item into a custom one.
                if (Kind(section) == "Texture") continue;
                if (Kind(section) != "ColorGroup") throw Differs($"the colours of {slot.Path}");
                foreach (var color in Reflect.Items(Reflect.Get(section, "colors")))
                {
                    if (Kind(color) != "CommonColor") throw Differs($"the colours of {slot.Path}");
                    var group = Reflect.Get(color, "transferableGroupId") as string ?? throw Differs($"the colours of {slot.Path}");
                    var path = Reflect.Get(color, "elementKey") ?? throw Differs($"the colours of {slot.Path}");
                    var disable = Reflect.Get(color, "disableFlagElementKey") ?? throw Differs($"the colours of {slot.Path}");
                    var key = Reflect.Get(color, "translationKey") as string ?? throw Differs($"the colours of {slot.Path}");
                    var label = Reflect.CallStatic("VRoidStudio.UICulture.Translator", "Translate", key) as string;
                    if (string.IsNullOrEmpty(label) || label == key) throw Differs($"the colour labels of {slot.Path}");
                    swatches.Add(new Swatch(label, () => Reflect.Call(context, "GetColorParameter", group, path),
                        // The texture editor's colour view model writes without its screen,
                        // and the item stays a built-in item (R-EDIT-12). VRoid holds view
                        // models' event registrations weakly, so each one is collected.
                        value => Reflect.Call(Reflect.New("VRoid.Studio.TextureEditor.Color.ViewModel.CommonColorViewModel",
                            Reflect.Get(Root, "Resources") ?? throw Differs("the editor resources"), engine, group, path, disable, key,
                            Reflect.Get(Root, "ColorPalette") ?? throw Differs("the colour palette")), "set_Color", value),
                        new() { $"{transferable}:{number++}" }));
                }
            }
        }
        return (swatches.ToArray(), state != Identifiers.Custom);
    }

    private static string Kind(object value) => Reflect.Concrete(value).GetType().Name;
}
