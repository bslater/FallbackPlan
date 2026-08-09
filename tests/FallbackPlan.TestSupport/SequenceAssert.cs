using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FallbackPlan.TestSupport;

/// <summary>
/// Element-wise equality for sequences.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Assert.AreEqual{T}(T, T)"/> compares two arrays by reference,
/// so two distinct arrays holding identical bytes are unequal to it. xUnit's
/// <c>Assert.Equal</c> special-cased sequences and compared them element by
/// element, and a great deal of this suite means the latter — a restored file
/// matches the original, an encoding round-trips, a directory listing is in
/// the order the specification requires.
/// </para>
/// <para>
/// This is deliberately a separate name rather than an overload of
/// <c>AreEqual</c>. An overload taking <c>IEnumerable&lt;T&gt;</c> loses to
/// the exact-match generic one whenever the argument is an array, so it would
/// bind at some call sites and not others — the same assertion meaning two
/// different things depending on the static type in front of it. A distinct
/// name is impossible to get silently wrong.
/// </para>
/// </remarks>
public static class SequenceAssert
{
    /// <summary>Asserts that two sequences hold equal elements in the same order.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="expected">The expected sequence.</param>
    /// <param name="actual">The sequence under test.</param>
    public static void AreEqual<T>(IEnumerable<T>? expected, IEnumerable<T>? actual)
    {
        if (expected is null || actual is null)
        {
            Assert.IsTrue(
                expected is null && actual is null,
                $"Expected {(expected is null ? "null" : "a sequence")} but found "
                + $"{(actual is null ? "null" : "a sequence")}.");
            return;
        }

        var expectedItems = expected as IReadOnlyList<T> ?? [.. expected];
        var actualItems = actual as IReadOnlyList<T> ?? [.. actual];

        Assert.AreEqual(
            expectedItems.Count,
            actualItems.Count,
            $"Sequence lengths differ. Expected [{Render(expectedItems)}], found [{Render(actualItems)}].");

        for (var i = 0; i < expectedItems.Count; i++)
        {
            Assert.AreEqual(
                expectedItems[i],
                actualItems[i],
                $"Sequences differ at index {i}. Expected [{Render(expectedItems)}], found [{Render(actualItems)}].");
        }
    }

    /// <summary>Asserts that two sequences do not hold equal elements in the same order.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="notExpected">The sequence the subject must differ from.</param>
    /// <param name="actual">The sequence under test.</param>
    public static void AreNotEqual<T>(IEnumerable<T>? notExpected, IEnumerable<T>? actual)
    {
        if (notExpected is null || actual is null)
        {
            Assert.IsFalse(notExpected is null && actual is null, "Both sequences are null.");
            return;
        }

        Assert.IsFalse(
            notExpected.SequenceEqual(actual),
            $"Sequences are equal but were expected to differ: [{Render([.. actual])}].");
    }

    /// <summary>A short rendering for a failure message; long sequences are elided.</summary>
    private static string Render<T>(IReadOnlyList<T> items) =>
        items.Count <= 16
            ? string.Join(", ", items)
            : string.Join(", ", items.Take(16)) + $", … ({items.Count} items)";
}
