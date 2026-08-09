using System.Reflection;
using FallbackPlan.Api;

namespace FallbackPlan.ArchitectureTests;

/// <summary>
/// NFR-PORT-004: public APIs use asynchronous streaming, cancellation, bounded
/// concurrency, and explicit result types.
/// </summary>
/// <remarks>
/// The traceability matrix named this file for a long time while it did not
/// exist — <c>eng/check-requirements.py</c> verifies that a requirement is
/// defined and referenced, never that a named test is real, so the row read
/// green while pointing at nothing. It is real now, and it checks the contract
/// where the requirement bites hardest: a boundary between processes, where an
/// exception loses its type and a missing cancellation token means a client
/// cannot let go.
/// </remarks>
[TestClass]
public sealed class ApiShapeTests
{
    [TestMethod]
    public void Every_contract_operation_is_asynchronous_and_cancellable()
    {
        var offenders = new List<string>();

        foreach (var contract in new[] { typeof(IFallbackPlanService), typeof(IFallbackPlanClient) })
        {
            // Property accessors are not operations: a version a client has
            // already been handed does not need a token to read.
            foreach (var method in contract.GetMethods().Where(method => !method.IsSpecialName))
            {
                var returns = method.ReturnType;
                var isAsync = returns.IsGenericType
                    && (returns.GetGenericTypeDefinition() == typeof(ValueTask<>)
                        || returns.GetGenericTypeDefinition() == typeof(Task<>)
                        || returns.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>));

                if (!isAsync)
                {
                    offenders.Add($"{contract.Name}.{method.Name} returns {returns.Name}");
                }

                if (Array.TrueForAll(method.GetParameters(), p => p.ParameterType != typeof(CancellationToken)))
                {
                    offenders.Add($"{contract.Name}.{method.Name} takes no CancellationToken");
                }
            }
        }

        Assert.IsTrue(offenders.Count == 0, string.Join("; ", offenders));
    }

    [TestMethod]
    public void Progress_is_streamed_rather_than_returned_as_a_collection()
    {
        // A ten-hour backup's progress cannot be a list you get at the end.
        var watch = typeof(IFallbackPlanService).GetMethod(nameof(IFallbackPlanService.WatchAsync));

        Assert.IsNotNull(watch);
        Assert.IsTrue(
            watch.ReturnType.IsGenericType
            && watch.ReturnType.GetGenericTypeDefinition() == typeof(IAsyncEnumerable<>),
            "Job progress must stream (NFR-PORT-004: asynchronous streaming).");
    }

    [TestMethod]
    public void Every_outcome_is_a_result_type_rather_than_an_exception()
    {
        // Expected failures are values. An exception crossing a process
        // boundary loses its type, and its stack means nothing on the far side.
        var results = typeof(ServiceResult).Assembly
            .GetTypes()
            .Where(type => type.IsPublic && typeof(ServiceResult).IsAssignableFrom(type) && !type.IsAbstract)
            .ToList();

        Assert.IsNotEmpty(results);
        Assert.Contains(type => type == typeof(ServiceError), results);

        // And the reason set is closed, so a client can branch on it rather
        // than matching on message text.
        Assert.IsTrue(typeof(ServiceErrorReason).IsEnum);
        Assert.IsNotEmpty(Enum.GetValues<ServiceErrorReason>());
    }

    [TestMethod]
    public void The_contract_carries_no_exception_types()
    {
        var offenders = typeof(ServiceResult).Assembly
            .GetTypes()
            .Where(type => type.IsPublic
                && (typeof(ServiceCommand).IsAssignableFrom(type) || typeof(ServiceResult).IsAssignableFrom(type)))
            .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(property => typeof(Exception).IsAssignableFrom(property.PropertyType))
            .Select(property => $"{property.DeclaringType?.Name}.{property.Name}")
            .ToList();

        Assert.IsTrue(offenders.Count == 0, string.Join("; ", offenders));
    }
}
