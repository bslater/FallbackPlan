using FallbackPlan.Domain.Configuration;

namespace FallbackPlan.Domain.Tests.Configuration;

/// <summary>
/// Exercises Argon2id parameter validation (specification 03 §2): each value
/// below a creation minimum yields its named defect; the findings are
/// mode-independent facts the caller turns into refusal or warning.
/// </summary>
[TestClass]
public sealed class Argon2ParameterValidationTests
{
    [TestMethod]
    public void ValidateCreationMinimums_TheCreationMinimums_ReportNoDefect()
    {
        Assert.IsTrue(Argon2Parameters.CreationMinimums.ValidateCreationMinimums().IsValid);
    }

    [TestMethod]
    [DataRow(64u * 1024 - 1, 3u, (byte)4, "kdf_memory_below_creation_minimum")]
    [DataRow(64u * 1024, 2u, (byte)4, "kdf_iterations_below_creation_minimum")]
    [DataRow(64u * 1024, 3u, (byte)3, "kdf_parallelism_below_creation_minimum")]
    public void ValidateCreationMinimums_OneValueBelowItsMinimum_NamesThatDefect(
        uint memoryKiB, uint iterations, byte parallelism, string expectedDefect)
    {
        var parameters = new Argon2Parameters { MemoryKiB = memoryKiB, Iterations = iterations, Parallelism = parallelism };

        var result = parameters.ValidateCreationMinimums();

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Has(expectedDefect));
    }

    [TestMethod]
    public void ValidateCreationMinimums_WhenEveryValueIsBelowMinimum_ShouldReportThemTogether()
    {
        var parameters = new Argon2Parameters { MemoryKiB = 8, Iterations = 1, Parallelism = 1 };

        var result = parameters.ValidateCreationMinimums();

        Assert.AreEqual(3, result.Defects.Count);
    }

    [TestMethod]
    public void RepositoryCreationSettings_KdfParametersAreInvalid_CarryTheDefectsThrough()
    {
        var settings = RepositoryCreationSettings.Default with
        {
            KdfParameters = new Argon2Parameters { MemoryKiB = 8, Iterations = 1, Parallelism = 1 },
            CreatedBy = " ",
        };

        var result = settings.Validate();

        Assert.IsFalse(result.IsValid);
        Assert.IsTrue(result.Has("kdf_memory_below_creation_minimum"));
        Assert.IsTrue(result.Has("created_by_empty"));
    }
}
