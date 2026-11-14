// Renders the open character with a temporary camera owned by the bridge. It
// never touches the editor's cameras, the project, or its parameters.
//
// Instance members of Unity engine objects are called through `dynamic`, which
// binds to the IL2CPP proxy types at runtime. Static members go through
// Reflect, since `dynamic` cannot name a type that is found only at runtime.
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;

namespace VroidBridge;

internal static class CharacterRender
{
    private const int Antialiasing = 4;

    // Il2CppSystem.Type lives in a generated assembly, so it is handled as dynamic.
    private static dynamic Il2CppTypeOf(string name) =>
        typeof(Il2CppType).GetMethod(nameof(Il2CppType.From), new[] { typeof(Type) })!
            .Invoke(null, new object[] { Reflect.Type(name) })!;

    private static dynamic New(string type, params object[] args) => Reflect.New(type, args);

    private static dynamic Unity(Vec value) => New("UnityEngine.Vector3", (float)value.X, (float)value.Y, (float)value.Z);

    private static Vec Plain(dynamic value) => new((float)value.x, (float)value.y, (float)value.z);

    private static dynamic Format(string name) => Enum.Parse(Reflect.Type("UnityEngine.TextureFormat"), name);

    private static void Destroy(object? created)
    {
        if (created != null) Reflect.CallStatic("UnityEngine.Object", "DestroyImmediate", created);
    }

    /// Renders `views` of `origin`, the model's root transform, side by side,
    /// each `width` x `height`, at one scale: `scale` pixels per metre, or the
    /// largest that shows the frame. With `reference`, an image file, a row
    /// above the views shows it. Returns the PNG bytes and the scale.
    internal static (byte[] Png, double Scale) Sheet(object origin, IReadOnlyList<View> views, Frame frame,
        int width, int height, string? reference, double? scale)
    {
        // Converted once; the helpers take it as object so calls to them stay static.
        var model = Reflect.Concrete(origin);
        Vec forward = Plain(((dynamic)model).forward);
        var (parts, layers) = Visible(model);
        Vec position = Plain(((dynamic)model).position);
        var extent = frame == Frame.Head
            ? Framing.Head(parts, Bone(model, "Neck"), Bone(model, "Head"))
            : Framing.Body(parts, position);
        var size = Framing.Size(extent, (double)width / height, height, scale);
        (int Width, int Height, byte[] Pixels)? image = reference == null ? null : ReadImage(reference);
        var all = Box.Union(parts);
        var shots = views.Select(view => Framing.Place(extent, size, all, forward, view)).ToArray();
        var rendered = Capture(shots, layers, width, height);
        var top = image == null ? 0 : height;
        // Every frame leaves a margin above the character, so the top-left
        // pixel is background.
        var canvas = new Canvas(views.Count * width, top + height, rendered[0].AsSpan((height - 1) * width * 3, 3));
        for (var index = 0; index < rendered.Length; index++) canvas.Place(index * width, top, width, height, rendered[index]);
        if (image is { } loaded) canvas.Fit(0, 0, canvas.Width, height, loaded.Width, loaded.Height, loaded.Pixels);
        // A given scale is reported as given, so passing it back compares equal.
        return (Encode(canvas), scale ?? Framing.Scale(size, height));
    }

    /// Encodes `canvas` as PNG.
    internal static byte[] Encode(Canvas canvas)
    {
        dynamic? texture = null;
        try
        {
            texture = New("UnityEngine.Texture2D", canvas.Width, canvas.Height, Format("RGB24"), false);
            texture.LoadRawTextureData(new Il2CppStructArray<byte>(canvas.Pixels));
            var png = Reflect.CallStatic("UnityEngine.ImageConversion", "EncodeToPNG", (object)texture)
                ?? throw new InvalidOperationException("Unity could not encode the image");
            return ((IEnumerable<byte>)png).ToArray();
        }
        finally
        {
            Destroy(texture);
        }
    }

    // Decodes a PNG or JPEG file into RGBA32 pixels, bottom row first. Blitting
    // converts whichever format LoadImage chose.
    private static (int Width, int Height, byte[] Pixels) ReadImage(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var active = Reflect.StaticProperty("UnityEngine.RenderTexture", "active");
        var previous = active.GetValue(null);
        dynamic? texture = null, target = null, readback = null;
        try
        {
            texture = New("UnityEngine.Texture2D", 2, 2);
            var load = Reflect.Type("UnityEngine.ImageConversion")
                .GetMethod("LoadImage", new[] { Reflect.Type("UnityEngine.Texture2D"), typeof(Il2CppStructArray<byte>) })!;
            if (!(bool)load.Invoke(null, new object[] { (object)texture, new Il2CppStructArray<byte>(bytes) })!)
                throw new InvalidOperationException("Unity could not read the reference image as a PNG or JPEG");
            int width = texture.width, height = texture.height;
            target = New("UnityEngine.RenderTexture", width, height, 0);
            Reflect.Type("UnityEngine.Graphics")
                .GetMethod("Blit", new[] { Reflect.Type("UnityEngine.Texture"), Reflect.Type("UnityEngine.RenderTexture") })!
                .Invoke(null, new object[] { (object)texture, (object)target });
            active.SetValue(null, (object)target);
            readback = New("UnityEngine.Texture2D", width, height, Format("RGBA32"), false);
            readback.ReadPixels(New("UnityEngine.Rect", 0f, 0f, (float)width, (float)height), 0, 0);
            return (width, height, ((IEnumerable<byte>)readback.GetRawTextureData()).ToArray());
        }
        finally
        {
            try
            {
                active.SetValue(null, previous);
            }
            finally
            {
                foreach (var created in new object?[] { texture, target, readback }) Destroy(created);
            }
        }
    }

    // World bounds of each visible renderer, and the layers they are on.
    private static (List<Box> Parts, int Layers) Visible(object model)
    {
        var parts = new List<Box>();
        var layers = 0;
        foreach (var item in Reflect.Items(((dynamic)model).GetComponentsInChildren(Il2CppTypeOf("UnityEngine.Renderer"), false)))
        {
            dynamic renderer = Reflect.Concrete(item);
            if (!renderer.enabled) continue;
            dynamic bounds = renderer.bounds;
            parts.Add(new Box(Plain(bounds.min), Plain(bounds.max)));
            layers |= 1 << (int)renderer.gameObject.layer;
        }
        if (parts.Count == 0) throw new InvalidOperationException("The character has no visible meshes; nothing to render");
        return (parts, layers);
    }

    private static Vec Bone(object model, string name)
    {
        foreach (var item in Reflect.Items(((dynamic)model).GetComponentsInChildren(Il2CppTypeOf("UnityEngine.Animator"), true)))
        {
            dynamic animator = Reflect.Concrete(item);
            if (!animator.isHuman) continue;
            var bone = animator.GetBoneTransform((dynamic)Enum.Parse(Reflect.Type("UnityEngine.HumanBodyBones"), name));
            if (bone != null) return Plain(bone.position);
        }
        throw new InvalidOperationException($"The character has no humanoid {name} bone, so its head cannot be framed; use frame body");
    }

    // Renders each shot into a render texture in one frame and returns its
    // RGB24 pixels, bottom row first. The finally block restores the active
    // render texture and destroys everything this creates, camera first,
    // whichever step fails.
    private static byte[][] Capture(IReadOnlyList<Shot> shots, int layers, int width, int height)
    {
        var active = Reflect.StaticProperty("UnityEngine.RenderTexture", "active");
        var previous = active.GetValue(null);
        dynamic? holder = null, camera = null, target = null, readback = null;
        try
        {
            holder = New("UnityEngine.GameObject", "vroid-cli render");
            camera = Reflect.Concrete(holder.AddComponent(Il2CppTypeOf("UnityEngine.Camera")));
            // Rendered only by the explicit Render calls below.
            camera.enabled = false;
            target = New("UnityEngine.RenderTexture", width, height, 24);
            target.antiAliasing = Antialiasing;
            readback = New("UnityEngine.Texture2D", width, height, Format("RGB24"), false);

            camera.orthographic = true;
            camera.clearFlags = (dynamic)Enum.Parse(Reflect.Type("UnityEngine.CameraClearFlags"), "SolidColor");
            camera.backgroundColor = New("UnityEngine.Color", 0.9f, 0.9f, 0.9f, 1f);
            // Leaves out scene objects on layers the model does not use.
            camera.cullingMask = layers;
            camera.targetTexture = target;
            var images = new byte[shots.Count][];
            for (var index = 0; index < shots.Count; index++)
            {
                var shot = shots[index];
                camera.orthographicSize = (float)shot.Size;
                camera.nearClipPlane = (float)shot.Near;
                camera.farClipPlane = (float)shot.Far;
                camera.transform.position = Unity(shot.Position);
                camera.transform.LookAt(Unity(shot.Target));
                camera.Render();
                active.SetValue(null, (object)target);
                readback.ReadPixels(New("UnityEngine.Rect", 0f, 0f, (float)width, (float)height), 0, 0);
                images[index] = ((IEnumerable<byte>)readback.GetRawTextureData()).ToArray();
            }
            return images;
        }
        finally
        {
            try
            {
                active.SetValue(null, previous);
            }
            finally
            {
                // Destroying the holder destroys the camera, which could otherwise
                // keep drawing over the editor.
                foreach (var created in new object?[] { holder, target, readback }) Destroy(created);
            }
        }
    }
}
