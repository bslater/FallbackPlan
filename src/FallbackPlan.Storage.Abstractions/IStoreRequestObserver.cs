namespace FallbackPlan.Storage.Abstractions;

/// <summary>
/// Told of every request a network store sends, as it is sent (FR-DEST-019,
/// ADR-0111): what a store bills by, kept by whoever is counting.
/// </summary>
/// <remarks>
/// <para>
/// Told once an attempt, because a store bills each attempt: a put retried
/// twice is three requests, and a listing is one request a page. An attempt
/// whose connection failed before an answer came back is told too, since the
/// store may have taken it. One its caller cancelled is not.
/// </para>
/// <para>
/// A reader of the requests, never a party to them: it is told on the
/// sending path, so it must return promptly and must not throw. A provider
/// with none sends exactly as it would with one.
/// </para>
/// </remarks>
public interface IStoreRequestObserver
{
    /// <summary>Records one request sent.</summary>
    /// <param name="operation">
    /// What was asked, in the in-box meter's words: <c>put</c>, <c>get</c>,
    /// <c>head</c>, <c>list</c> or <c>delete</c>.
    /// </param>
    /// <param name="bytesSent">The request body's length; zero for a request with none.</param>
    /// <param name="bytesReceived">
    /// The length the store's answer said its body was; zero for a head,
    /// whose answer describes an object without carrying it, and for an
    /// attempt nothing answered.
    /// </param>
    void Requested(string operation, long bytesSent, long bytesReceived);
}
