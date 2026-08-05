using System.Text.Json.Serialization;

namespace FallbackPlan.Api.Transport;

/// <summary>
/// One frame on the wire. The framing is deliberately dull — length-prefixed
/// JSON — because the interesting decisions are in the contract and the
/// binding, not the encoding. ADR-0003 permits it: repository encoding is
/// canonical CBOR, while wire protocols are versioned independently and may use
/// a different encoding.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "frame")]
[JsonDerivedType(typeof(HelloFrame), "hello")]
[JsonDerivedType(typeof(HelloAcknowledgementFrame), "hello_ack")]
[JsonDerivedType(typeof(RequestFrame), "request")]
[JsonDerivedType(typeof(ResponseFrame), "response")]
[JsonDerivedType(typeof(WatchFrame), "watch")]
[JsonDerivedType(typeof(ProgressFrame), "progress")]
public abstract record WireFrame;

/// <summary>The client's opening frame, naming the contract it speaks.</summary>
/// <param name="ContractVersion">The client's contract version, <c>major.minor</c>.</param>
/// <param name="ClientName">What the client calls itself, for the service's log.</param>
public sealed record HelloFrame(string ContractVersion, string ClientName) : WireFrame;

/// <summary>
/// The service's reply. A refusal here names <b>both</b> versions (FR-SVC-007)
/// rather than closing the connection, so the client can say what is wrong
/// instead of showing a blank window.
/// </summary>
/// <param name="ContractVersion">The service's contract version.</param>
/// <param name="Accepted">Whether the service will proceed.</param>
/// <param name="Message">The refusal message, when it will not.</param>
public sealed record HelloAcknowledgementFrame(string ContractVersion, bool Accepted, string? Message) : WireFrame;

/// <summary>One command, tagged so its response can be matched.</summary>
/// <param name="Id">The correlation identifier.</param>
/// <param name="Command">The command.</param>
public sealed record RequestFrame(long Id, ServiceCommand Command) : WireFrame;

/// <summary>One command's outcome.</summary>
/// <param name="Id">The correlation identifier of the request.</param>
/// <param name="Result">The outcome.</param>
public sealed record ResponseFrame(long Id, ServiceResult Result) : WireFrame;

/// <summary>
/// Turns a connection into a progress stream. A watch takes its own connection
/// rather than multiplexing over the request channel: a stream and a
/// request/response exchange have different lifetimes, and multiplexing them
/// buys nothing on a local socket.
/// </summary>
public sealed record WatchFrame : WireFrame;

/// <summary>One progress event travelling to a watching client.</summary>
/// <param name="Event">The event.</param>
public sealed record ProgressFrame(JobProgressEvent Event) : WireFrame;
