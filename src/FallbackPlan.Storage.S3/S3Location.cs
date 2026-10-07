using System.Text;
using FallbackPlan.Storage.S3.Resources;

namespace FallbackPlan.Storage.S3;

/// <summary>How a request names its bucket: in the path, or as part of the host.</summary>
public enum S3Addressing
{
    /// <summary><c>https://endpoint/bucket/key</c> — what every S3-compatible store answers.</summary>
    Path,

    /// <summary><c>https://bucket.endpoint/key</c> — what a store that has retired path addressing needs.</summary>
    VirtualHost,
}

/// <summary>
/// Where a store's objects live (ADR-0091): an endpoint, a bucket in it, the
/// region its signatures are scoped to, and a prefix inside the bucket that
/// every key is written under — a destination's own, and under it a
/// repository's.
/// </summary>
/// <remarks>
/// Spoken in clear only to this machine. Anywhere else the endpoint must be
/// <c>https</c>: the objects are sealed already, but their keys, their sizes
/// and the access key id are not, and a store on the far side of a network
/// is reached through whoever is between.
/// </remarks>
public sealed class S3Location
{
    /// <summary>Names a location, refusing one that could not be addressed.</summary>
    /// <param name="endpoint">The store's base URL: a scheme, a host and an optional port, nothing more.</param>
    /// <param name="bucket">The bucket, by the API's naming rules.</param>
    /// <param name="region">The region signatures are scoped to.</param>
    /// <param name="prefix">Where in the bucket keys are written, or null for its top.</param>
    /// <param name="addressing">How requests name the bucket.</param>
    /// <exception cref="ArgumentException">Any part of the location could not be addressed.</exception>
    public S3Location(
        Uri endpoint, string bucket, string region, string? prefix = null, S3Addressing addressing = S3Addressing.Path)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (DefectOf(endpoint, bucket, region, prefix) is { } defect)
        {
            throw new ArgumentException(defect);
        }

        Endpoint = endpoint;
        Bucket = bucket;
        Region = region;
        Prefix = string.IsNullOrEmpty(prefix) ? null : prefix;
        Addressing = addressing;
    }

    /// <summary>The store's base URL.</summary>
    public Uri Endpoint { get; }

    /// <summary>The bucket.</summary>
    public string Bucket { get; }

    /// <summary>The region signatures are scoped to.</summary>
    public string Region { get; }

    /// <summary>Where in the bucket keys are written, or null for its top.</summary>
    public string? Prefix { get; }

    /// <summary>How requests name the bucket.</summary>
    public S3Addressing Addressing { get; }

    /// <summary>The same location one folder deeper.</summary>
    /// <param name="component">The folder: one key component.</param>
    public S3Location Under(string component) =>
        new(Endpoint, Bucket, Region, Prefix is null ? component : $"{Prefix}/{component}", Addressing);

    /// <summary>The bucket's URL, which a listing is sent to.</summary>
    public Uri BucketUri => Addressing == S3Addressing.Path
        ? new Uri($"{Origin(Endpoint.Host)}/{Encode(Bucket)}")
        : new Uri($"{Origin($"{Bucket}.{Endpoint.Host}")}/");

    /// <summary>One object's URL.</summary>
    /// <param name="key">The object's full key in the bucket, prefix included.</param>
    public Uri ObjectUri(string key)
    {
        var path = string.Join('/', key.Split('/').Select(Encode));
        return Addressing == S3Addressing.Path
            ? new Uri($"{Origin(Endpoint.Host)}/{Encode(Bucket)}/{path}")
            : new Uri($"{Origin($"{Bucket}.{Endpoint.Host}")}/{path}");
    }

    /// <summary>
    /// What is wrong with a location, in one sentence, or null when nothing
    /// is. The checks a configuration's address defect makes too, so the two
    /// cannot disagree about what can be reached.
    /// </summary>
    /// <param name="endpoint">The store's base URL.</param>
    /// <param name="bucket">The bucket.</param>
    /// <param name="region">The region.</param>
    /// <param name="prefix">The prefix in the bucket, or null.</param>
    public static string? DefectOf(Uri endpoint, string? bucket, string? region, string? prefix)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri
            || endpoint.Scheme is not ("https" or "http")
            || endpoint.UserInfo.Length > 0
            || endpoint.AbsolutePath != "/"
            || endpoint.Query.Length > 0
            || endpoint.Fragment.Length > 0)
        {
            return Strings.FormatS3Location_EndpointNotABaseUrl(endpoint.OriginalString);
        }

        if (endpoint.Scheme == "http" && !endpoint.IsLoopback)
        {
            return Strings.FormatS3Location_EndpointInClear(endpoint.OriginalString);
        }

        if (bucket is null || !IsBucketName(bucket))
        {
            return Strings.FormatS3Location_BucketNameInvalid(bucket ?? string.Empty);
        }

        if (string.IsNullOrWhiteSpace(region) || !region.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
        {
            return Strings.FormatS3Location_RegionInvalid(region ?? string.Empty);
        }

        return string.IsNullOrEmpty(prefix) || IsPrefix(prefix)
            ? null
            : Strings.FormatS3Location_PrefixInvalid(prefix);
    }

    private string Origin(string host) =>
        Endpoint.IsDefaultPort ? $"{Endpoint.Scheme}://{host}" : $"{Endpoint.Scheme}://{host}:{Endpoint.Port}";

    /// <summary>The API's bucket naming rule: 3 to 63 of a–z, 0–9, '.' and '-', starting and ending alphanumeric.</summary>
    private static bool IsBucketName(string bucket) =>
        bucket.Length is >= 3 and <= 63
        && bucket.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-')
        && char.IsAsciiLetterOrDigit(bucket[0])
        && char.IsAsciiLetterOrDigit(bucket[^1]);

    /// <summary>Components of the object key alphabet, none starting with a dot, joined by '/'.</summary>
    private static bool IsPrefix(string prefix) =>
        prefix.Length <= 512
        && prefix.Split('/').All(component =>
            component.Length is > 0 and <= 255
            && component[0] != '.'
            && component.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-'));

    /// <summary>RFC 3986 percent-encoding of everything but the unreserved characters, as SigV4 encodes.</summary>
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
                encoded.Append('%').Append(value.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return encoded.ToString();
    }
}
