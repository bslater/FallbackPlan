using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Emit;

namespace FallbackPlan.ArchitectureTests;

/// <summary>
/// Where an assembly's compiled code calls a method, read from the IL of
/// every method body in it.
/// </summary>
/// <remarks>
/// A call is charged to the method its source was written in. The compiler
/// moves the body of a lambda, a local function or an async method into a
/// type or method of its own, and names it after the method it came from —
/// <c>&lt;RunAsync&gt;d__12</c>, <c>&lt;RunAsync&gt;b__0</c> — so that name
/// is read back here, and a call inside a lambda in <c>RunAsync</c> is
/// <c>RunAsync</c>'s.
/// </remarks>
internal static class CallSites
{
    private const BindingFlags Declared =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
        | BindingFlags.DeclaredOnly;

    private static readonly Dictionary<short, OpCode> OpCodesByValue =
        typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (OpCode)field.GetValue(null)!)
            .GroupBy(code => code.Value)
            .ToDictionary(group => group.Key, group => group.First());

    /// <summary>
    /// Every method in <paramref name="assembly"/> whose body calls
    /// <paramref name="target"/>, as <c>Namespace.Type::Method</c>.
    /// </summary>
    public static IReadOnlySet<string> Of(MethodBase target, Assembly assembly)
    {
        var callers = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(Declared).Concat<MethodBase>(type.GetConstructors(Declared)))
            {
                if (Calls(method, target))
                {
                    callers.Add(WrittenIn(method));
                }
            }
        }

        return callers;
    }

    private static bool Calls(MethodBase method, MethodBase target)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null)
        {
            return false;
        }

        var typeArguments = method.DeclaringType is { IsGenericType: true } declaring
            ? declaring.GetGenericArguments()
            : null;
        var methodArguments = method.IsGenericMethod ? method.GetGenericArguments() : null;

        for (var at = 0; at < il.Length;)
        {
            var value = (short)il[at++];
            if (value == 0xFE)
            {
                value = unchecked((short)(0xFE00 | il[at++]));
            }

            var code = OpCodesByValue[value];
            if (code.OperandType == OperandType.InlineMethod
                && Resolve(method.Module, BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(at)), typeArguments, methodArguments)
                    is { } called
                && called.Module == target.Module
                && called.MetadataToken == target.MetadataToken)
            {
                return true;
            }

            at += OperandSize(code, il, at);
        }

        return false;
    }

    private static MethodBase? Resolve(Module module, int token, Type[]? typeArguments, Type[]? methodArguments)
    {
        try
        {
            return module.ResolveMethod(token, typeArguments, methodArguments);
        }
        catch (ArgumentException)
        {
            // A member of a generic instantiation this method's context does
            // not supply. The method looked for is not generic, so it is
            // never one of these.
            return null;
        }
    }

    private static int OperandSize(OpCode code, byte[] il, int at) => code.OperandType switch
    {
        OperandType.InlineNone => 0,
        OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
        OperandType.InlineVar => 2,
        OperandType.InlineI8 or OperandType.InlineR => 8,
        OperandType.InlineSwitch => 4 + (4 * BinaryPrimitives.ReadInt32LittleEndian(il.AsSpan(at))),
        _ => 4,
    };

    private static string WrittenIn(MethodBase method)
    {
        var type = method.DeclaringType!;
        var name = SourceNameIn(method.Name);
        while (type.Name.Contains('<', StringComparison.Ordinal) && type.DeclaringType is { } outer)
        {
            name ??= SourceNameIn(type.Name);
            type = outer;
        }

        return $"{type.FullName}::{name ?? method.Name}";
    }

    /// <summary>
    /// The method name a compiler-generated name was made from — the first
    /// <c>&lt;Name&gt;</c> in it — or null when it carries none, as a
    /// display class's <c>&lt;&gt;c__DisplayClass4_0</c> does not.
    /// </summary>
    private static string? SourceNameIn(string generated)
    {
        for (var open = generated.IndexOf('<', StringComparison.Ordinal);
             open >= 0;
             open = generated.IndexOf('<', open + 1))
        {
            var close = generated.IndexOf('>', open + 1);
            if (close > open + 1 && generated[open + 1] != '<')
            {
                return generated[(open + 1)..close];
            }
        }

        return null;
    }
}
