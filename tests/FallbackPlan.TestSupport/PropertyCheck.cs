using System.Reflection;
using System.Runtime.CompilerServices;
using FsCheck;
using Microsoft.FSharp.Core;

namespace FallbackPlan.TestSupport;

/// <summary>
/// Runs a property — a method whose parameters FsCheck generates — from an
/// ordinary test method.
/// </summary>
/// <remarks>
/// <para>
/// FsCheck ships runner integrations for xUnit and NUnit and none for MSTest,
/// so <c>[Property]</c> has no equivalent attribute here. What it has is the
/// thing that attribute is built on: <see cref="Check.Method"/> takes a
/// <see cref="MethodInfo"/>, generates arguments for its parameters, invokes
/// it, shrinks any counter-example, and throws on failure. That is the whole
/// of what the attribute did, so nothing about the property testing is lost —
/// only the attribute that used to declare it.
/// </para>
/// <para>
/// The property itself stays a method with generated parameters, returning
/// <see langword="bool"/> or asserting in its body, exactly as before. A
/// <c>[TestMethod]</c> beside it names the behaviour and calls this.
/// </para>
/// </remarks>
public static class PropertyCheck
{
    /// <summary>
    /// Runs the property method <paramref name="property"/> declared on the
    /// caller's type, failing the test on a counter-example.
    /// </summary>
    /// <param name="instance">The test instance declaring the property method.</param>
    /// <param name="maxTest">How many cases to generate; FsCheck's default is 100.</param>
    /// <param name="property">
    /// The test method's name, supplied by the compiler — the property run is
    /// that name with a <c>Property</c> suffix, so the usual call passes
    /// nothing.
    /// </param>
    public static void Holds(object instance, int maxTest = 100, [CallerMemberName] string property = "")
    {
        ArgumentNullException.ThrowIfNull(instance);
        Run(instance.GetType(), instance, maxTest, property);
    }

    /// <summary>Runs a property declared as a static method on <typeparamref name="TTests"/>.</summary>
    /// <typeparam name="TTests">The type declaring the property method.</typeparam>
    /// <param name="maxTest">How many cases to generate.</param>
    /// <param name="property">The property method's name; supplied by the compiler.</param>
    public static void Holds<TTests>(int maxTest = 100, [CallerMemberName] string property = "") =>
        Run(typeof(TTests), null, maxTest, property);

    private static void Run(Type declaring, object? instance, int maxTest, string property)
    {
        // The test method names the behaviour; the property that generates
        // for it carries the same name with a `Property` suffix, so the two
        // read as one thing and the compiler keeps them together.
        const BindingFlags Any =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        var method = declaring.GetMethod(property + "Property", Any)
            ?? declaring.GetMethod(property, Any)
            ?? throw new InvalidOperationException(
                $"No property method named '{property}Property' on {declaring.Name}.");

        // QuietOnSuccess: a passing property should say nothing, the way every
        // other passing test does.
        var config = Config.QuickThrowOnFailure.WithMaxTest(maxTest).WithQuietOnSuccess(true);

        Check.Method(
            config,
            method,
            instance is null ? FSharpOption<object>.None : FSharpOption<object>.Some(instance));
    }
}
