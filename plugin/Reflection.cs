// Adapted from brainrom/vroid-studio-mcp (MIT); see LICENSE.upstream.
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace VroidBridge;

internal static class Reflect
{
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Dictionary<string, Type> Types = new();

    private static Type? FindType(string name)
    {
        if (Types.TryGetValue(name, out var found)) return found;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType(name, false);
            if (type != null) return Types[name] = type;
        }
        return null;
    }

    internal static Type Type(string name) => FindType(name)
        ?? throw new InvalidOperationException($"Missing app type: {name}");

    internal static object Concrete(object value)
    {
        if (value is not Il2CppObjectBase native) return value;
        var klass = IL2CPP.il2cpp_object_get_class(native.Pointer);
        var name = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(klass));
        var ns = Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_namespace(klass));
        var fullName = string.IsNullOrEmpty(ns) ? name : ns + "." + name;
        if (fullName == null || fullName.EndsWith("[]")) return value;
        if (fullName.StartsWith("System.")) fullName = "Il2Cpp" + fullName;
        var type = FindType(fullName);
        if (type == null) return value;
        if (type.IsGenericTypeDefinition)
        {
            var arguments = value.GetType().GetGenericArguments();
            if (arguments.Length != type.GetGenericArguments().Length) return value;
            type = type.MakeGenericType(arguments);
        }
        if (type == value.GetType()) return value;
        return type.GetConstructor(new[] { typeof(IntPtr) })?.Invoke(new object[] { native.Pointer }) ?? value;
    }

    // Views a native object as `type`, for objects `Concrete` cannot name, such
    // as instances of nested types.
    internal static object As(object value, string type) =>
        Type(type).GetConstructor(new[] { typeof(IntPtr) })!.Invoke(new object[] { ((Il2CppObjectBase)value).Pointer });

    internal static object? Get(object? value, string name)
    {
        if (value == null) return null;
        value = Concrete(value);
        var type = value.GetType();
        var property = type.GetProperty(name, Instance);
        if (property != null) return property.GetValue(value);
        return type.GetField(name, Instance)?.GetValue(value);
    }

    internal static object? Call(object value, string name, params object[] args)
    {
        value = Concrete(value);
        var candidates = value.GetType().GetMethods(Instance)
            .Where(method => method.Name == name && method.GetParameters().Length == args.Length).ToArray();
        if (candidates.Length != 1)
            throw new InvalidOperationException($"Expected one {value.GetType().FullName}.{name}/{args.Length}; found {candidates.Length}");
        // A native object passed for an interface, such as a command for
        // ISyncCommand, needs that interface's proxy.
        var parameters = candidates[0].GetParameters();
        for (var i = 0; i < args.Length; i++)
            if (args[i] is Il2CppObjectBase native && !parameters[i].ParameterType.IsInstanceOfType(native)
                && parameters[i].ParameterType.GetConstructor(new[] { typeof(IntPtr) }) is { } proxy)
                args[i] = proxy.Invoke(new object[] { native.Pointer });
        return candidates[0].Invoke(value, args);
    }

    internal static object New(string type, params object[] args) =>
        Activator.CreateInstance(Type(type), args)!;

    internal static object? CallStatic(string type, string name, params object[] args)
    {
        var candidates = Type(type).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == name && !method.IsGenericMethodDefinition && method.GetParameters().Length == args.Length).ToArray();
        if (candidates.Length != 1)
            throw new InvalidOperationException($"Expected one static {type}.{name}/{args.Length}; found {candidates.Length}");
        return candidates[0].Invoke(null, args);
    }

    // A native delegate of the IL2CPP delegate type `native`, such as
    // Il2CppSystem.Action`2 closed over app types, that passes its arguments to
    // `body`.
    internal static object Callback(Type native, Action<object?[]> body)
    {
        var convert = native.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == "op_Implicit" && method.ReturnType == native);
        var managed = convert.GetParameters()[0].ParameterType;
        var parameters = managed.GetMethod("Invoke")!.GetParameters().Select(parameter => Expression.Parameter(parameter.ParameterType)).ToArray();
        var args = Expression.NewArrayInit(typeof(object), parameters.Select(parameter => Expression.Convert(parameter, typeof(object))));
        var lambda = Expression.Lambda(managed, Expression.Invoke(Expression.Constant(body), args), parameters);
        return convert.Invoke(null, new object[] { lambda.Compile() })!;
    }

    internal static PropertyInfo StaticProperty(string type, string name) =>
        Type(type).GetProperty(name, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Missing static property {type}.{name}");

    internal static IEnumerable<object> Items(object? value)
    {
        if (value == null) yield break;
        value = Concrete(value);
        var count = Get(value, "Count") ?? Get(value, "Length");
        // Collection interfaces such as IReadOnlyCollection have a count but no indexer.
        var indexed = value.GetType().GetMethods(Instance).Any(method => method.Name == "get_Item" && method.GetParameters().Length == 1);
        if (count != null && indexed)
        {
            for (var i = 0; i < Convert.ToInt32(count); i++)
                yield return Call(value, "get_Item", i)!;
            yield break;
        }
        if (value.GetType().GetMethods(Instance).All(method => method.Name != "GetEnumerator"))
            value = As(value, "Il2CppSystem.Collections.IEnumerable");
        var enumerator = Call(value, "GetEnumerator")!;
        // Generic enumerator proxies inherit MoveNext from the non-generic interface.
        if (Concrete(enumerator).GetType().GetMethods(Instance).All(method => method.Name != "MoveNext"))
            enumerator = As(enumerator, "Il2CppSystem.Collections.IEnumerator");
        while (Call(enumerator, "MoveNext") is true)
            yield return Get(enumerator, "Current")!;
    }
}
