using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FallbackPlan.TestSupport;

/// <summary>
/// Restricts a test to runs that name a real Azure Blob container to hold
/// the provider to (ADR-0093): <c>FALLBACKPLAN_AZURE_TEST_ACCOUNT</c>,
/// <c>FALLBACKPLAN_AZURE_TEST_CONTAINER</c>, and a credential —
/// <c>FALLBACKPLAN_AZURE_TEST_ACCOUNT_KEY</c> or
/// <c>FALLBACKPLAN_AZURE_TEST_SAS</c> — with
/// <c>FALLBACKPLAN_AZURE_TEST_ENDPOINT</c> for an account the public service
/// does not host. Everywhere else the test reports as <b>skipped</b> with
/// this reason, never as a pass — the rule <see cref="ConfiguredS3StoreAttribute"/>
/// keeps for the same reason.
/// </summary>
/// <remarks>
/// An opt-in rather than a default, because the store costs whoever owns it
/// money for every request, and a build should never spend somebody's money
/// because it happened to find their credentials in its environment.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class ConfiguredAzureBlobStoreAttribute : ConditionBaseAttribute
{
    /// <summary>The variables' common prefix.</summary>
    public const string Prefix = "FALLBACKPLAN_AZURE_TEST_";

    /// <summary>Restricts the test to runs that name a container.</summary>
    public ConfiguredAzureBlobStoreAttribute()
        : base(ConditionMode.Include)
    {
    }

    /// <inheritdoc />
    public override bool ShouldRun => Read() is not null;

    /// <inheritdoc />
    public override string? IgnoreMessage =>
        $"Runs only against an Azure Blob container a run names ({Prefix}ACCOUNT, _CONTAINER, and _ACCOUNT_KEY or "
        + "_SAS, with _ENDPOINT where the account has one of its own) — this run named none.";

    /// <inheritdoc />
    public override string GroupName => "ConfiguredAzureBlobStore";

    /// <summary>The container this run names, or null when it names none, or names it only in part.</summary>
    public static ConfiguredAzureBlobStoreSettings? Read()
    {
        var account = Environment.GetEnvironmentVariable(Prefix + "ACCOUNT");
        var container = Environment.GetEnvironmentVariable(Prefix + "CONTAINER");
        var accountKey = Environment.GetEnvironmentVariable(Prefix + "ACCOUNT_KEY");
        var sas = Environment.GetEnvironmentVariable(Prefix + "SAS");
        var endpoint = Environment.GetEnvironmentVariable(Prefix + "ENDPOINT");
        Uri? endpointUri = null;
        return string.IsNullOrWhiteSpace(account) || string.IsNullOrWhiteSpace(container)
            || (string.IsNullOrWhiteSpace(accountKey) && string.IsNullOrWhiteSpace(sas))
            || (!string.IsNullOrWhiteSpace(endpoint) && !Uri.TryCreate(endpoint, UriKind.Absolute, out endpointUri))
                ? null
                : new ConfiguredAzureBlobStoreSettings(
                    account, container, endpointUri,
                    string.IsNullOrWhiteSpace(accountKey) ? null : accountKey,
                    string.IsNullOrWhiteSpace(sas) ? null : sas);
    }
}

/// <summary>An Azure Blob container a run named for the provider's contract suite.</summary>
/// <param name="Account">The storage account.</param>
/// <param name="Container">A container the credential may write to, read from, list and delete from.</param>
/// <param name="Endpoint">The account's blob endpoint, or null for the public service's.</param>
/// <param name="AccountKey">The account key, when the run signs with it.</param>
/// <param name="Sas">A shared access signature for the container, when the run carries one instead.</param>
public sealed record ConfiguredAzureBlobStoreSettings(
    string Account, string Container, Uri? Endpoint, string? AccountKey, string? Sas)
{
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Account = ").Append(Account).Append(", Container = ").Append(Container)
            .Append(", Endpoint = ").Append(Endpoint).Append(", AccountKey = (withheld), Sas = (withheld)");
        return true;
    }
}
