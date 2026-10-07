using System.Text;
using FallbackPlan.Storage.S3.Resources;

namespace FallbackPlan.Storage.S3;

/// <summary>
/// The access key a destination's requests are signed with (ADR-0091). The
/// secret signs and is never sent: what crosses the wire is a signature it
/// made, and what this record prints leaves it out.
/// </summary>
public sealed record S3Credentials
{
    /// <summary>Holds an access key.</summary>
    /// <param name="accessKeyId">The access key id the store knows the key by.</param>
    /// <param name="secretAccessKey">The secret that signs for it.</param>
    /// <exception cref="ArgumentException">Either is blank.</exception>
    public S3Credentials(string accessKeyId, string secretAccessKey)
    {
        if (string.IsNullOrWhiteSpace(accessKeyId) || string.IsNullOrWhiteSpace(secretAccessKey))
        {
            throw new ArgumentException(Strings.S3Credentials_Incomplete);
        }

        AccessKeyId = accessKeyId;
        SecretAccessKey = secretAccessKey;
    }

    /// <summary>The access key id, which the store sees in every request.</summary>
    public string AccessKeyId { get; }

    /// <summary>The secret that signs; never written anywhere but the credential store.</summary>
    public string SecretAccessKey { get; }

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("AccessKeyId = ").Append(AccessKeyId).Append(", SecretAccessKey = (withheld)");
        return true;
    }
}
