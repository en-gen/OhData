using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;

namespace OhData;

/// <summary>
/// Issue #184: synthesizes a documentation-only POCO type whose public read/write properties mirror
/// an OData <em>action</em>'s body parameters (name → CLR type), so OpenAPI generators render the
/// action's real body shape (e.g. <c>{"rating": &lt;number&gt;}</c>) instead of the typeless
/// <c>{}</c> schema that <c>OhDataRequestBodyMetadata.BodyType = typeof(object)</c> produces.
/// </summary>
/// <remarks>
/// <para>
/// The emitted type is used <em>only</em> to feed a CLR <see cref="Type"/> to the request-body
/// documentation pipeline (<see cref="OhDataApiDescriptionProvider"/> → Microsoft.AspNetCore.OpenApi
/// / Swashbuckle / NSwag, all of which build a body schema from that <see cref="Type"/> by reading
/// its public properties). It is never instantiated and never used to deserialize a request — the
/// action handlers still read the JSON body by hand (see the "POST/PUT/PATCH deserialize the request
/// body by hand" note in CLAUDE.md).
/// </para>
/// <para>
/// Each property carries <c>[System.Text.Json.Serialization.JsonPropertyName(param.Name)]</c> so the
/// documented member name is exactly the parameter name the runtime deserializer looks up
/// (case-insensitively) — independent of whatever <c>PropertyNamingPolicy</c> a host configures.
/// Callers exclude the trailing <see cref="CancellationToken"/> (already dropped by
/// <c>BoundOperationDefinition.Parameters</c>) and, for entity-level actions, the leading key
/// parameter, so only real body parameters become properties.
/// </para>
/// </remarks>
internal static class ActionBodySchemaTypeFactory
{
    private static readonly ModuleBuilder s_module = CreateModule();
    private static readonly ConcurrentDictionary<string, Type> s_cache = new(StringComparer.Ordinal);
    private static readonly HashSet<string> s_usedTypeNames = new(StringComparer.Ordinal);
    private static readonly object s_defineLock = new();

    private static readonly ConstructorInfo s_jsonPropertyNameCtor =
        typeof(JsonPropertyNameAttribute).GetConstructor(new[] { typeof(string) })!;

    private static ModuleBuilder CreateModule()
    {
        var asmName = new AssemblyName("OhData.Dynamic.ActionBodies");
        // AssemblyBuilderAccess.Run: never persisted, only reflected over for schema generation.
        var asm = AssemblyBuilder.DefineDynamicAssembly(asmName, AssemblyBuilderAccess.Run);
        return asm.DefineDynamicModule(asmName.Name!);
    }

    /// <summary>
    /// Returns a synthesized POCO type documenting <paramref name="bodyParameters"/> as the request
    /// body of an action, memoized per <paramref name="uniqueKey"/> (a stable per-route identifier).
    /// </summary>
    public static Type GetOrCreate(string uniqueKey, IReadOnlyList<ParameterInfo> bodyParameters)
    {
        if (s_cache.TryGetValue(uniqueKey, out Type? existing)) return existing;
        lock (s_defineLock)
        {
            // Double-checked: another thread may have defined it between the read and the lock.
            // The lock also serializes ModuleBuilder.DefineType, which is not thread-safe.
            if (s_cache.TryGetValue(uniqueKey, out existing)) return existing;
            Type created = DefineType(uniqueKey, bodyParameters);
            s_cache[uniqueKey] = created;
            return created;
        }
    }

    private static Type DefineType(string uniqueKey, IReadOnlyList<ParameterInfo> bodyParameters)
    {
        // A descriptive, collision-free CLR type name — the OpenAPI schema component name derives
        // from it. Distinct uniqueKeys that sanitize to the same identifier get a numeric suffix.
        string baseName = "OhData.ActionBodies." + Sanitize(uniqueKey);
        string typeName = baseName;
        int suffix = 2;
        while (!s_usedTypeNames.Add(typeName))
        {
            typeName = baseName + "_" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
            suffix++;
        }

        TypeBuilder tb = s_module.DefineType(
            typeName,
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.AutoClass |
            TypeAttributes.AnsiClass | TypeAttributes.BeforeFieldInit);

        foreach (ParameterInfo param in bodyParameters)
        {
            string propName = param.Name ?? "value";
            Type propType = param.ParameterType;

            FieldBuilder field = tb.DefineField(
                "<" + propName + ">k__BackingField", propType, FieldAttributes.Private);

            PropertyBuilder prop = tb.DefineProperty(
                propName, PropertyAttributes.None, propType, parameterTypes: null);

            const MethodAttributes accessorAttrs =
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName;

            MethodBuilder getter = tb.DefineMethod(
                "get_" + propName, accessorAttrs, propType, Type.EmptyTypes);
            ILGenerator getIl = getter.GetILGenerator();
            getIl.Emit(OpCodes.Ldarg_0);
            getIl.Emit(OpCodes.Ldfld, field);
            getIl.Emit(OpCodes.Ret);

            MethodBuilder setter = tb.DefineMethod(
                "set_" + propName, accessorAttrs, returnType: null, new[] { propType });
            ILGenerator setIl = setter.GetILGenerator();
            setIl.Emit(OpCodes.Ldarg_0);
            setIl.Emit(OpCodes.Ldarg_1);
            setIl.Emit(OpCodes.Stfld, field);
            setIl.Emit(OpCodes.Ret);

            prop.SetGetMethod(getter);
            prop.SetSetMethod(setter);

            // [JsonPropertyName(param.Name)] pins the documented member name to the exact parameter
            // name, so the schema matches what the handler reads regardless of naming policy.
            prop.SetCustomAttribute(
                new CustomAttributeBuilder(s_jsonPropertyNameCtor, new object[] { propName }));
        }

        return tb.CreateType()!;
    }

    // Reduce an arbitrary route identifier to a valid, readable dotted CLR type-name segment.
    private static string Sanitize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        }
        return sb.Length == 0 ? "ActionBody" : sb.ToString();
    }
}
