using FallbackPlan.Api;

namespace FallbackPlan.Web.DomTests;

/// <summary>
/// The fake service the DOM suite drives the console against records each
/// command it is sent, on the thread the console's request arrives on, while
/// the page keeps polling. A test reads that record while the console is
/// still sending, so a read is a copy taken under the lock each command is
/// recorded under, never the list itself mid-append. Needs no browser.
/// </summary>
/// <remarks>
/// The sending waits for the first read. Fifty thousand commands are sent in
/// milliseconds, so on a busy machine a sender left to start at once could
/// finish before the reading began, and the test would read nothing while
/// anything was sent.
/// </remarks>
[TestClass]
public sealed class FakeServiceClientTests
{
    [TestMethod]
    public async Task WhatTheConsoleSent_ReadsWhileItIsStillSending()
    {
        var client = new FakeServiceClient();
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sending = Task.Run(async () =>
        {
            await reading.Task;
            for (var sent = 0; sent < 50_000; sent++)
            {
                await client.ExecuteAsync(new ListRestoreRunsCommand(), CancellationToken.None);
            }
        });

        var reads = 0;
        while (!sending.IsCompleted)
        {
            Assert.IsEmpty(client.Received.OfType<ForgetRestoreRunCommand>());
            reads++;
            reading.TrySetResult();
        }

        await sending;
        Assert.IsGreaterThan(0, reads);
        Assert.HasCount(50_000, client.Received);
    }
}
