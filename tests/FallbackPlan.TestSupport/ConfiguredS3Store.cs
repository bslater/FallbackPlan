using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FallbackPlan.TestSupport;

/// <summary>
/// Restricts a test to runs that name a real S3-compatible store to hold the
/// provider to (ADR-0091): <c>FALLBACKPLAN_S3_TEST_ENDPOINT</c>,
/// <c>FALLBACKPLAN_S3_TEST_BUCKET</c>, <c>FALLBACKPLAN_S3_TEST_REGION</c>,
/// <c>FALLBACKPLAN_S3_TEST_ACCESS_KEY_ID</c> and
/// <c>FALLBACKPLAN_S3_TEST_SECRET_ACCESS_KEY</c>, all five. Everywhere else
/// the test reports as <b>skipped</b> with this reason, never as a pass —
/// the rule <see cref="BrowserConditionAttribute"/> keeps for the same reason.
/// </summary>
/// <remarks>
/// An opt-in rather than a default, because the store costs whoever owns it
/// money for every request, and a build should never spend somebody's money
/// because it happened to find their credentials in its environment.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class ConfiguredS3StoreAttribute : ConditionBaseAttribute
{
    /// <summary>The variables' common prefix.</summary>
    public const string Prefix = "FALLBACKPLAN_S3_TEST_";

    /// <summary>Restricts the test to runs that name a store.</summary>
    public ConfiguredS3StoreAttribute()
        : base(ConditionMode.Include)
    {
    }

    /// <inheritdoc />
    public override bool ShouldRun => Read() is not null;

    /// <inheritdoc />
    public override string? IgnoreMessage =>
        $"Runs only against an S3-compatible store a run names ({Prefix}ENDPOINT, _BUCKET, _REGION, "
        + "_ACCESS_KEY_ID and _SECRET_ACCESS_KEY) — this run named none.";

    /// <inheritdoc />
    public override string GroupName => "ConfiguredS3Store";

    /// <summary>The store this run names, or null when it names none, or names it only in part.</summary>
    public static ConfiguredS3StoreSettings? Read()
    {
        var endpoint = Environment.GetEnvironmentVariable(Prefix + "ENDPOINT");
        var bucket = Environment.GetEnvironmentVariable(Prefix + "BUCKET");
        var region = Environment.GetEnvironmentVariable(Prefix + "REGION");
        var accessKeyId = Environment.GetEnvironmentVariable(Prefix + "ACCESS_KEY_ID");
        var secretAccessKey = Environment.GetEnvironmentVariable(Prefix + "SECRET_ACCESS_KEY");
        return string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(bucket)
            || string.IsNullOrWhiteSpace(region) || string.IsNullOrWhiteSpace(accessKeyId)
            || string.IsNullOrWhiteSpace(secretAccessKey)
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
                ? null
                : new ConfiguredS3StoreSettings(uri, bucket, region, accessKeyId, secretAccessKey);
    }
}

/// <summary>An S3-compatible store a run named for the provider's contract suite.</summary>
/// <param name="Endpoint">Its base URL.</param>
/// <param name="Bucket">A bucket the credentials may write to and delete from.</param>
/// <param name="Region">The region its signatures are scoped to.</param>
/// <param name="AccessKeyId">The access key id.</param>
/// <param name="SecretAccessKey">Its secret.</param>
public sealed record ConfiguredS3StoreSettings(
    Uri Endpoint, string Bucket, string Region, string AccessKeyId, string SecretAccessKey)
{
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Endpoint = ").Append(Endpoint).Append(", Bucket = ").Append(Bucket)
            .Append(", Region = ").Append(Region).Append(", AccessKeyId = ").Append(AccessKeyId)
            .Append(", SecretAccessKey = (withheld)");
        return true;
    }
}
