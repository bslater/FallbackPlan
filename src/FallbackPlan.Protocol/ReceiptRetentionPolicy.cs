namespace FallbackPlan.Protocol;

/// <summary>
/// How long a kind of peer receipt is kept (NFR-OPS-008). Three numbers,
/// because no one of them bounds the pile honestly on its own: a count alone
/// would throw away a year of history from a pair that pushes hourly, and an
/// age alone would leave a pair that has gone quiet with nothing recent at
/// all.
/// </summary>
/// <param name="MinimumRetained">
/// How many of the newest are kept whatever their age. This is what keeps a
/// pair that stopped exchanging from ageing out of its own record entirely.
/// </param>
/// <param name="MaximumRetained">
/// The ceiling per repository, applied however new a receipt is. This is what
/// bounds a pair that exchanges faster than the window expires.
/// </param>
/// <param name="RetainedDays">
/// The window, measured from the issue time the file's name carries. Between
/// the minimum and the ceiling, a receipt older than this goes.
/// </param>
public readonly record struct ReceiptRetentionPolicy(
    int MinimumRetained,
    int MaximumRetained,
    int RetainedDays);
