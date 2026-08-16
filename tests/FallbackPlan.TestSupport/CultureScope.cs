using System.Globalization;

namespace FallbackPlan.TestSupport;

/// <summary>
/// Runs a block under a named culture and restores the previous one, so a
/// test can prove a behaviour is the same everywhere instead of the same
/// wherever the build machine happens to be configured.
/// </summary>
/// <remarks>
/// <para>
/// The culture is set on the current thread and on
/// <see cref="CultureInfo.DefaultThreadCurrentCulture"/> together. The first
/// covers the calling thread and every async continuation resumed on it — in
/// .NET the current culture flows with the execution context — and the second
/// covers threads created inside the block, which would otherwise start from
/// the process default and quietly make the test prove nothing.
/// </para>
/// <para>
/// The UI culture moves with it. Resource lookup choosing a different
/// language is not itself a defect, but a test asserting on a message needs
/// to know which language it is reading.
/// </para>
/// </remarks>
public sealed class CultureScope : IDisposable
{
    private readonly CultureInfo _previousCulture;
    private readonly CultureInfo _previousUiCulture;
    private readonly CultureInfo? _previousDefaultCulture;
    private readonly CultureInfo? _previousDefaultUiCulture;
    private bool _disposed;

    /// <summary>Enters <paramref name="name"/>, e.g. <c>tr-TR</c>.</summary>
    public CultureScope(string name)
    {
        var culture = CultureInfo.GetCultureInfo(name);

        _previousCulture = CultureInfo.CurrentCulture;
        _previousUiCulture = CultureInfo.CurrentUICulture;
        _previousDefaultCulture = CultureInfo.DefaultThreadCurrentCulture;
        _previousDefaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;

        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
    }

    /// <summary>
    /// The cultures worth running anything through, and why each is here.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>tr-TR</c> — the dotless <c>ı</c>: <c>"I".ToLower()</c> is not
    /// <c>"i"</c>, so every case-insensitive comparison of ASCII is a
    /// candidate defect.</item>
    /// <item><c>de-DE</c> — comma decimal separator and dot group separator,
    /// which swap the meaning of <c>1.234</c>.</item>
    /// <item><c>fr-CA</c> — comma decimal with a narrow no-break space for
    /// groups, a separator that is not the space anyone types.</item>
    /// <item><c>ar-SA</c> — a non-Gregorian default calendar and
    /// Arabic-Indic digit shapes, so a formatted date may not round-trip
    /// through an invariant parse at all.</item>
    /// <item><c>en-US</c> — the culture most code was written under, present
    /// so it is asserted rather than assumed.</item>
    /// </list>
    /// </remarks>
    public static string[] Hostile => ["tr-TR", "de-DE", "fr-CA", "ar-SA", "en-US"];

    /// <summary>Restores the previous cultures.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CultureInfo.CurrentCulture = _previousCulture;
        CultureInfo.CurrentUICulture = _previousUiCulture;
        CultureInfo.DefaultThreadCurrentCulture = _previousDefaultCulture;
        CultureInfo.DefaultThreadCurrentUICulture = _previousDefaultUiCulture;
    }
}
