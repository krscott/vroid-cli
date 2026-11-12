namespace VroidBridge;

// Colours of the worn Outfit items, which VRoid's editor shows only in its
// texture editor.
public sealed partial class Plugin
{
    private const string NotBuiltIn = "the worn item is not built in; only built-in items' colours can be set";

    // The colour fields of the category's worn item, one per colour of each of
    // its parts in app order, and whether that item is built in; null for a
    // category that wears no item or selects other categories' items.
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
                    var label = Reflect.CallStatic("VRoidStudio.UICulture.Translator", "Translate", key) as string ?? key;
                    swatches.Add(new Swatch(label, () => Reflect.Call(context, "GetColorParameter", group, path),
                        // The texture editor's colour field writes without its screen, and
                        // keeps the item built in (R-EDIT-12).
                        value => Reflect.Call(Reflect.New("VRoid.Studio.TextureEditor.Color.ViewModel.CommonColorViewModel",
                            Reflect.Get(Root, "Resources") ?? throw Differs("the editor resources"), engine, group, path, disable, key,
                            Reflect.Get(Root, "ColorPalette") ?? throw Differs("the colour palette")), "set_Color", value),
                        new() { $"{transferable}:{number++}" }));
                }
            }
        }
        return (swatches.ToArray(), state != ItemIds.Custom);
    }

    private static string Kind(object value) => Reflect.Concrete(value).GetType().Name;
}
