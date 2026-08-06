namespace FallbackPlan.Domain.Configuration;

/// <summary>
/// One named reason a configuration is invalid (NFR-OPS-003). The
/// <see cref="Name"/> is a stable snake_case identifier that tests and
/// operators can match on; the <see cref="Message"/> is for humans.
/// </summary>
public sealed record ConfigurationDefect(string Name, string Message);
