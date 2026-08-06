namespace FallbackPlan.Domain.Configuration;

/// <summary>
/// Argon2id cost parameters as carried in the repository descriptor's KDF
/// parameter map (specification 01 §3.3, 03 §2). The 16-byte salt travels
/// separately — it is descriptor data, not policy.
/// </summary>
public sealed record Argon2Parameters
{
    /// <summary>The minimum memory for a new repository: 64 MiB (specification 03 §2).</summary>
    public const uint MinimumCreationMemoryKiB = 64 * 1024;

    /// <summary>The minimum iterations for a new repository: 3 (specification 03 §2).</summary>
    public const uint MinimumCreationIterations = 3;

    /// <summary>The minimum parallelism for a new repository: 4 (specification 03 §2).</summary>
    public const byte MinimumCreationParallelism = 4;

    /// <summary>The specification's minimums, used as creation defaults.</summary>
    public static readonly Argon2Parameters CreationMinimums = new()
    {
        MemoryKiB = MinimumCreationMemoryKiB,
        Iterations = MinimumCreationIterations,
        Parallelism = MinimumCreationParallelism,
    };

    /// <summary>Memory cost in KiB.</summary>
    public required uint MemoryKiB { get; init; }

    /// <summary>Iteration count.</summary>
    public required uint Iterations { get; init; }

    /// <summary>Degree of parallelism.</summary>
    public required byte Parallelism { get; init; }

    /// <summary>
    /// Reports every parameter below the mandated creation minimums as a named
    /// defect. The findings are mode-independent facts; the caller applies
    /// <see cref="KdfValidationMode"/> — refusal when creating a repository,
    /// a warning when opening one (specification 03 §2).
    /// </summary>
    public ConfigurationValidationResult ValidateCreationMinimums()
    {
        List<ConfigurationDefect>? defects = null;

        if (MemoryKiB < MinimumCreationMemoryKiB)
        {
            (defects ??= []).Add(new ConfigurationDefect(
                "kdf_memory_below_creation_minimum",
                $"Argon2id memory is {MemoryKiB} KiB; a new repository requires at least {MinimumCreationMemoryKiB} KiB."));
        }

        if (Iterations < MinimumCreationIterations)
        {
            (defects ??= []).Add(new ConfigurationDefect(
                "kdf_iterations_below_creation_minimum",
                $"Argon2id iteration count is {Iterations}; a new repository requires at least {MinimumCreationIterations}."));
        }

        if (Parallelism < MinimumCreationParallelism)
        {
            (defects ??= []).Add(new ConfigurationDefect(
                "kdf_parallelism_below_creation_minimum",
                $"Argon2id parallelism is {Parallelism}; a new repository requires at least {MinimumCreationParallelism}."));
        }

        if (Parallelism == 0)
        {
            (defects ??= []).Add(new ConfigurationDefect(
                "kdf_parallelism_zero",
                "Argon2id parallelism of zero is not computable in any mode."));
        }

        return defects is null ? ConfigurationValidationResult.Valid : new ConfigurationValidationResult(defects);
    }
}
