using FallbackPlan.Storage.Abstractions;
using FallbackPlan.Storage.Local;
using Microsoft.Extensions.Logging;

namespace FallbackPlan.Cli;

/// <summary>
/// The CLI's one decision about which provider serves a local archive path
/// (ADR-0012). The direct-mode client composes its own store exactly as the
/// agent does; everything downstream holds <see cref="IObjectStore"/>.
/// </summary>
internal static class StoreComposition
{
    /// <summary>Opens the store rooted at a local path.</summary>
    internal static IObjectStore OpenLocal(string rootPath, ILogger? logger = null) =>
        new LocalFileSystemObjectStore(rootPath, logger);
}
