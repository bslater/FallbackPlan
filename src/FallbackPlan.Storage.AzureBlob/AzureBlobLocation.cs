using System.Globalization;
using System.Text;
using FallbackPlan.Storage.AzureBlob.Resources;

namespace FallbackPlan.Storage.AzureBlob;

/// <summary>
/// Where a store's blobs live (ADR-0093): a storage account, a container in
/// it, and a prefix inside the container that every key is written under — a
/// destination's own, and under it a repository's — reached at the account's
/// blob endpoint.
/// </summary>
/// <remarks>
/// <para>
/// The endpoint is the account's, as the account itself states it. Left out,
/// it is the account's host at the public service. Declared, it is either a
/// host of the account's own — the account named by the host, as the public
/// service names it — or, for a store on this machine, an address whose one
/// path segment is the account's name. Nothing else is an account's endpoint,
/// so nothing else is accepted.
/// </para>
/// <para>
/// Spoken in clear only to this machine. Anywhere else the endpoint must be
/// <c>https</c>: the blobs are sealed already, but their names, their sizes
/// and the account name are not, and a store on the far side of a network is
/// reached through whoever is between.
/// </para>
/// </remarks>
public sealed class AzureBlobLocation
{
    /// <summary>The host suffix under which the public service gives each storage account its blob endpoint.</summary>
    public const string PublicServiceSuffix = "blob.core.windows.net";

    private readonly string _base;

    /// <summary>Names a location, refusing one that could not be addressed.</summary>
    /// <param name="account">The storage account.</param>
    /// <param name="container">The container, by the API's naming rules.</param>
    /// <param name="prefix">Where in the container keys are written, or null for its top.</param>
    /// <param name="endpoint">The account's blob endpoint, or null for its host at the public service.</param>
    /// <exception cref="ArgumentException">Any part of the location could not be addressed.</exception>
    public AzureBlobLocation(string account, string container, string? prefix = null, Uri? endpoint = null)
    {
        if (DefectOf(account, container, prefix, endpoint) is { } defect)
        {
            throw new ArgumentException(defect);
        }

        Account = account;
        Container = container;
        Prefix = string.IsNullOrEmpty(prefix) ? null : prefix;
        DeclaredEndpoint = endpoint;
        Endpoint = endpoint ?? new Uri($"https://{account}.{PublicServiceSuffix}");

        // Path-style when the endpoint's path names the account, as on this
        // machine; otherwise the host names it and the path starts with the
        // container.
        var authority = Endpoint.GetLeftPart(UriPartial.Authority);
        _base = Endpoint.AbsolutePath.Trim('/').Length == 0 ? authority : $"{authority}/{account}";
    }

    /// <summary>The storage account.</summary>
    public string Account { get; }

    /// <summary>The container.</summary>
    public string Container { get; }

    /// <summary>Where in the container keys are written, or null for its top.</summary>
    public string? Prefix { get; }

    /// <summary>The account's blob endpoint, as the requests reach it.</summary>
    public Uri Endpoint { get; }

    /// <summary>The endpoint as declared, or null when the public service's is meant.</summary>
    public Uri? DeclaredEndpoint { get; }

    /// <summary>The same location one folder deeper.</summary>
    /// <param name="component">The folder: one key component.</param>
    public AzureBlobLocation Under(string component) =>
        new(Account, Container, Prefix is null ? component : $"{Prefix}/{component}", DeclaredEndpoint);

    /// <summary>The container's URL, which a listing is sent to.</summary>
    public Uri ContainerUri => new($"{_base}/{Encode(Container)}");

    /// <summary>One blob's URL.</summary>
    /// <param name="key">The blob's full name in the container, prefix included.</param>
    public Uri BlobUri(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new Uri($"{_base}/{Encode(Container)}/{string.Join('/', key.Split('/').Select(Encode))}");
    }

    /// <summary>
    /// What is wrong with a location, in one sentence, or null when nothing
    /// is. The checks a configuration's address defect makes too, so the two
    /// cannot disagree about what can be reached.
    /// </summary>
    /// <param name="account">The storage account.</param>
    /// <param name="container">The container.</param>
    /// <param name="prefix">The prefix in the container, or null.</param>
    /// <param name="endpoint">The account's blob endpoint, or null for the public service's.</param>
    public static string? DefectOf(string? account, string? container, string? prefix, Uri? endpoint)
    {
        if (account is null || !IsAccountName(account))
        {
            return Strings.FormatAzureBlobLocation_AccountInvalid(account ?? string.Empty);
        }

        if (endpoint is not null)
        {
            if (!endpoint.IsAbsoluteUri
                || endpoint.Scheme is not ("https" or "http")
                || endpoint.UserInfo.Length > 0
                || endpoint.Query.Length > 0
                || endpoint.Fragment.Length > 0
                || endpoint.AbsolutePath.Trim('/') is var path && path.Length > 0 && path != account)
            {
                return Strings.FormatAzureBlobLocation_EndpointNotTheAccounts(endpoint.OriginalString, account);
            }

            if (endpoint.Scheme == "http" && !endpoint.IsLoopback)
            {
                return Strings.FormatAzureBlobLocation_EndpointInClear(endpoint.OriginalString);
            }
        }

        if (container is null || !IsContainerName(container))
        {
            return Strings.FormatAzureBlobLocation_ContainerInvalid(container ?? string.Empty);
        }

        return string.IsNullOrEmpty(prefix) || IsPrefix(prefix)
            ? null
            : Strings.FormatAzureBlobLocation_PrefixInvalid(prefix);
    }

    /// <summary>The API's account naming rule: 3 to 24 lowercase letters and digits.</summary>
    private static bool IsAccountName(string account) =>
        account.Length is >= 3 and <= 24 && account.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9'));

    /// <summary>
    /// The API's container naming rule: 3 to 63 of a–z, 0–9 and '-', starting
    /// with a letter or digit, every hyphen between two letters or digits.
    /// </summary>
    private static bool IsContainerName(string container) =>
        container.Length is >= 3 and <= 63
        && container.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')
        && char.IsAsciiLetterOrDigit(container[0])
        && char.IsAsciiLetterOrDigit(container[^1])
        && !container.Contains("--", StringComparison.Ordinal);

    /// <summary>Components of the object key alphabet, none starting with a dot, joined by '/'.</summary>
    private static bool IsPrefix(string prefix) =>
        prefix.Length <= 512
        && prefix.Split('/').All(component =>
            component.Length is > 0 and <= 255
            && component[0] != '.'
            && component.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-'));

    /// <summary>RFC 3986 percent-encoding of everything but the unreserved characters.</summary>
    internal static string Encode(string text)
    {
        var encoded = new StringBuilder(text.Length);
        foreach (var value in Encoding.UTF8.GetBytes(text))
        {
            var c = (char)value;
            if (c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_' or '.' or '~')
            {
                encoded.Append(c);
            }
            else
            {
                encoded.Append('%').Append(value.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return encoded.ToString();
    }
}
