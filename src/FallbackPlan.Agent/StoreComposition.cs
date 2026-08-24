using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Agent;

/// <summary>
/// The host's one decision about which provider serves a local archive path
/// (ADR-0012: the store contract is the provider seam, and Amendment 2 makes
/// it the fan-out seam too).
/// </summary>
/// <remarks>
/// Before this existed, nine call sites across the agent each constructed
/// <see cref="LocalFileSystemObjectStore"/> for themselves — which meant a
/// second provider (peer-served, S3, Azure) could not become a configuration
/// choice without editing nine places that had each made the choice
/// implicitly. Composition is a host's job, and a host does it once.
/// Everything downstream holds <see cref="IObjectStore"/> and cannot tell.
/// </remarks>
internal static class StoreComposition
{
    /// <summary>Opens the store rooted at a local path.</summary>
    internal static IObjectStore OpenLocal(string rootPath, ILogger? logger = null) =>
        new LocalFileSystemObjectStore(rootPath, logger);
}
