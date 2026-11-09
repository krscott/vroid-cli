// Renders the open character with a temporary camera owned by the bridge. It
// never touches the editor's cameras, the project, or its parameters.
//
// Instance members of Unity engine objects are called through `dynamic`, which
// binds to the IL2CPP proxy types at runtime. Static members go through
// Reflect, since `dynamic` cannot name a type that is found only at runtime.
using Il2CppInterop.Runtime;

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

    /// Renders `origin`, the model's root transform, to PNG bytes.
    internal static byte[] Png(object origin, View view, Frame frame, int width, int height)
    {
        // Converted once; the helpers take it as object so calls to them stay static.
        var model = Reflect.Concrete(origin);
        Vec forward = Plain(((dynamic)model).forward);
        var (parts, layers) = Visible(model);
        var aspect = (double)width / height;
        Shot shot = frame == Frame.Head
            ? Framing.Head(parts, Bone(model, "Neck"), Bone(model, "Head"), forward, view, aspect)
            : Framing.Body(parts, forward, view, aspect);
        return Capture(shot, layers, width, height);
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

    // Renders one frame into a render texture and encodes it. The finally block
    // restores the active render texture and destroys everything this creates,
    // camera first, whichever step fails.
    private static byte[] Capture(Shot shot, int layers, int width, int height)
    {
        var active = Reflect.StaticProperty("UnityEngine.RenderTexture", "active");
        var previous = active.GetValue(null);
        dynamic? holder = null, camera = null, target = null, readback = null;
        try
        {
            holder = New("UnityEngine.GameObject", "vroid-cli render");
            camera = Reflect.Concrete(holder.AddComponent(Il2CppTypeOf("UnityEngine.Camera")));
            // Rendered only by the explicit Render call below.
            camera.enabled = false;
            target = New("UnityEngine.RenderTexture", width, height, 24);
            target.antiAliasing = Antialiasing;
            readback = New("UnityEngine.Texture2D", width, height,
                (dynamic)Enum.Parse(Reflect.Type("UnityEngine.TextureFormat"), "RGB24"), false);

            camera.orthographic = true;
            camera.orthographicSize = (float)shot.Size;
            camera.nearClipPlane = (float)shot.Near;
            camera.farClipPlane = (float)shot.Far;
            camera.clearFlags = (dynamic)Enum.Parse(Reflect.Type("UnityEngine.CameraClearFlags"), "SolidColor");
            camera.backgroundColor = New("UnityEngine.Color", 0.9f, 0.9f, 0.9f, 1f);
            // Leaves out scene objects on layers the model does not use.
            camera.cullingMask = layers;
            camera.targetTexture = target;
            camera.transform.position = Unity(shot.Position);
            camera.transform.LookAt(Unity(shot.Target));
            camera.Render();

            active.SetValue(null, (object)target);
            readback.ReadPixels(New("UnityEngine.Rect", 0f, 0f, (float)width, (float)height), 0, 0);
            readback.Apply();
            var png = Reflect.CallStatic("UnityEngine.ImageConversion", "EncodeToPNG", (object)readback)
                ?? throw new InvalidOperationException("Unity could not encode the image");
            return ((IEnumerable<byte>)png).ToArray();
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
                foreach (var created in new object?[] { holder, target, readback })
                    if (created != null) Reflect.CallStatic("UnityEngine.Object", "DestroyImmediate", created);
            }
        }
    }
}
