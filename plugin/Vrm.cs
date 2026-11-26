// Writes the open character to a VRM file through VRoid's own VRM export,
// without its dialogs (R-EDIT-18).
using Il2CppInterop.Runtime.InteropTypes;

namespace VroidBridge;

public sealed partial class Plugin
{
    private const string EditScreen = "VRoid.Studio.EditRootScreen.ViewModel";
    private const string ExportScreen = "VRoid.Studio.VRMExportScreen.ViewModel";
    private const string ExportFailedDialog = "VRoid.Studio.PhotoBooth.Dialogs.ExportFailedDialog.ViewModel";
    private const string ExportUtil = "VRoidStudio.Exporter.VRoidStudioExportUtil";

    // VRoid's Export as VRM screen, then the metadata and save dialogs its
    // Export button opens, end in the photo booth's OnVRM1SettingChanged or
    // OnVRMSettingChanged. This makes that call with the request's metadata
    // and path, then leaves the screen.
    private static IEnumerable<bool> ExportVrm(Request request)
    {
        var path = request.Text("path");
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Refusing to overwrite destination");
        var wanted = new VrmMeta(request.Text("vrm_version"), request.Text("title"), request.Text("author"));
        var root = Root;
        var bus = Reflect.Get(root, "GlobalBus") ?? throw Differs("the editor's bus");
        var navigation = Reflect.Get(bus, "ScreenNavigation") ?? throw Differs("the screen navigation");
        var dialogs = Reflect.Get(bus, "DialogProvider") ?? throw Differs("the dialog provider");
        if (Reflect.Get(dialogs, "Dialog") is { } shown)
            throw new InvalidOperationException($"VRoid shows a dialog, {TypeName(shown)}; nothing was exported");
        if (TypeName(Reflect.Get(navigation, "ActiveScreen")) != EditScreen)
            throw new InvalidOperationException("VRoid's editor is not the active screen; nothing was exported");
        var exported = false;
        Reflect.Call(root, "EnterToVRMExport");
        try
        {
            // VRoid pushes the screen a frame later, which builds the preview the export uses.
            for (var frame = 0; TypeName(Reflect.Get(navigation, "ActiveScreen")) != ExportScreen; frame++)
            {
                if (frame > 600) throw Differs("the Export as VRM screen");
                yield return true;
            }
            var booth = Reflect.Get(root, "obsoleteLegacyPhotoBoothViewModel") ?? throw Differs("the export screen's photo booth");
            CheckExportOptions(booth);
            var completion = Activator.CreateInstance(
                Reflect.Type("Il2CppSystem.Threading.Tasks.TaskCompletionSource`1").MakeGenericType(typeof(bool)))!;
            Reflect.Call(booth, wanted.Version == "1.0" ? "OnVRM1SettingChanged" : "OnVRMSettingChanged",
                VrmMetadata(wanted), path, completion);
            var task = Reflect.Get(completion, "Task") ?? throw Differs("the export's task");
            // VRoid completes the task with whether it wrote the file, in about
            // 1.5 s for a base character.
            while (Reflect.Get(task, "IsCompleted") is not true) yield return true;
            if (Reflect.Get(task, "IsFaulted") is true || Reflect.Get(task, "Result") is not true)
            {
                // VRoid opens its failure dialog around the same frame; give it time to.
                for (var frame = 0; frame < 5; frame++) yield return true;
                // Only player.log holds the reason: a Harmony hook on VRoid's
                // error callback, which could report it here, crashed VRoid.
                throw new InvalidOperationException(
                    "VRoid could not export the VRM; the session's player.log names the reason. Nothing changed in the character");
            }
            VrmMeta written;
            using (var file = File.OpenRead(path))
            {
                try
                {
                    written = VrmFile.Read(file);
                }
                catch (ArgumentException error)
                {
                    throw Differs($"the file VRoid exports, which is {error.Message}");
                }
            }
            if (written != wanted) throw Differs($"the VRM VRoid exports, which holds {written} for {wanted}");
            Reflect.Call(navigation, "Pop", Reflect.Get(navigation, "ActiveScreen")!, true);
            for (var frame = 0; TypeName(Reflect.Get(navigation, "ActiveScreen")) != EditScreen; frame++)
            {
                if (frame > 600) throw Differs("the way back from the Export as VRM screen");
                yield return true;
            }
            // Back in the editor, VRoid rebuilds the model from the unchanged
            // character, which takes about 100 frames.
            var settled = rebuilt;
            for (int frame = 0, stable = 0; stable < SettledFrames; frame++)
            {
                stable = updating || rebuilt != settled ? 0 : stable + 1;
                settled = rebuilt;
                if (frame > 1200) throw new InvalidOperationException("VRoid kept rebuilding the model after exporting the VRM");
                yield return true;
            }
            exported = true;
            result = new { exported = path, bytes = new FileInfo(path).Length, vrm_version = wanted.Version };
        }
        finally
        {
            if (!exported)
            {
                // A dialog or the export screen left open would block the private display.
                // Nothing here waits for the model's rebuild after the pop:
                // requests wait for rebuilds themselves.
                Try(() =>
                {
                    if (Reflect.Get(dialogs, "Dialog") is { } dialog && TypeName(dialog) == ExportFailedDialog) Reflect.Call(dialog, "Dismiss");
                }, "dismiss VRoid's export failure dialog");
                Try(() =>
                {
                    if (Reflect.Get(navigation, "ActiveScreen") is { } screen && TypeName(screen) == ExportScreen)
                        Reflect.Call(navigation, "Pop", screen, true);
                }, "leave the Export as VRM screen");
                Try(() => File.Delete(path), "delete the failed VRM");
            }
        }
    }

    private static string TypeName(object? value) => value == null ? "none" : Reflect.Concrete(value).GetType().FullName!;

    // VRoid's own export defaults: no polygon, hair bone, or material reduction,
    // and textures at their own size. The export screen keeps the options
    // between exports in one app instance, and vroid-cli never changes them, so a
    // difference means something else did. Setting an option rebuilds the
    // preview, so this checks the options rather than setting them.
    private static void CheckExportOptions(object booth)
    {
        var settings = Reflect.Get(booth, "ExportSettings") ?? throw Differs("the export options");
        var mesh = Reflect.Get(settings, "MeshReduction") ?? throw Differs("the polygon reduction options");
        var bones = Reflect.Get(settings, "hairBoneReduction") ?? throw Differs("the bone reduction options");
        var defaults = new (object Owner, string Name, object Value)[]
        {
            (settings, "CurrentMaterialReductionMode", "None"),
            (settings, "CurrentAtlasResolution", "Resolution_4K"),
            (settings, "CurrentTextureCompressionMode", "Original"),
            (settings, "IsEnableHairCombination", true),
            (mesh, "HairReductionStrength", 0),
            (mesh, "FaceReductionStrength", 0),
            (mesh, "BodyReductionStrength", 0),
            (mesh, "ClothingReductionStrength", 0),
            (mesh, "HairSmoothnessRate", 0),
            (mesh, "HairCrossSection", false),
            (mesh, "MeshErasure", true),
            (bones, "HairReductionStrength", 0),
        };
        var changed = defaults.Select(option => (option.Name, Value: Reflect.Get(option.Owner, option.Name)?.ToString(), Default: option.Value.ToString()))
            .Where(option => option.Value != option.Default).Select(option => $"{option.Name} is {option.Value}, not {option.Default}").ToArray();
        if (changed.Length > 0)
            throw new InvalidOperationException($"VRoid's VRM export options differ from its defaults: {string.Join(", ", changed)}. "
                + "Stop and launch the session to reset them. Nothing was exported");
    }

    // VRoid's default metadata, which has its most restrictive licence, with the
    // request's title and author.
    private static object VrmMetadata(VrmMeta wanted)
    {
        if (wanted.Version == "1.0")
        {
            var meta = Reflect.CallStatic(ExportUtil, "BuildDefaultVRM10MetaData") ?? throw Differs("VRoid's VRM 1.0 metadata");
            Reflect.Call(meta, "set_Name", wanted.Title);
            var authors = Reflect.Get(meta, "Authors") ?? throw Differs("VRoid's VRM 1.0 authors");
            Reflect.Call(authors, "Clear");
            Reflect.Call(authors, "Add", wanted.Author);
            return meta;
        }
        var legacy = Reflect.CallStatic(ExportUtil, "BuildDefaultVRM0MetaData") ?? throw Differs("VRoid's VRM 0.0 metadata");
        Reflect.Call(legacy, "set_title", wanted.Title);
        Reflect.Call(legacy, "set_author", wanted.Author);
        return legacy;
    }

    // The export screen rebuilds a preview copy of the model through its own
    // updater, whose rebuilds are not the model's.
    private static bool IsPreviewUpdater(object instance) =>
        root != null && instance is Il2CppObjectBase native
        && Reflect.Get(Reflect.Get(root, "GlobalBus"), "PreviewModelUpdater") is Il2CppObjectBase preview
        && preview.Pointer == native.Pointer;
}
