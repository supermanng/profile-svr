namespace ProfileSvr.Domain;

/// <summary>
/// An incoming credit reported by the virtual-account provider's webhook, queued until the
/// posting job settles it into the CBA (naira) account. TransactionRef is the idempotency key.
/// </summary>
public class VirtualAccountCredit
{
    public Guid Id { get; set; }
    /// <summary>Provider transaction reference — unique; used to dedupe redeliveries.</summary>
    public string TransactionRef { get; set; } = string.Empty;
    /// <summary>The virtual account that was credited.</summary>
    public string VirtualAccount { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string? SourceAccount { get; set; }
    public string? SourceBank { get; set; }
    public string? SenderName { get; set; }
    public string? Narration { get; set; }
    public DateTime TransactionDate { get; set; }
    /// <summary>Provider status as received; only "SUCCESSFUL" credits are posted.</summary>
    public string? Status { get; set; }
    /// <summary>True once the credit has been posted into the CBA deposit module.</summary>
    public bool CreditPosted { get; set; }
    /// <summary>Raw response from the last CBA posting attempt (for diagnostics).</summary>
    public string? PostingResponse { get; set; }
    /// <summary>Number of failed CBA posting attempts so far.</summary>
    public int Attempts { get; set; }
    /// <summary>True once the retry budget is spent or a permanent error occurred (dead letter).</summary>
    public bool PostingAbandoned { get; set; }
    /// <summary>Last posting error, for diagnostics / dead-letter review.</summary>
    public string? LastError { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public void MarkPosted(string? response)
    {
        CreditPosted = true;
        PostingResponse = Truncate(response);
        LastError = null;
    }

    /// <summary>Records a (transient) failed attempt; abandons once the retry budget is spent.</summary>
    public void RecordFailure(string? error, int maxAttempts)
    {
        Attempts++;
        LastError = Truncate(error);
        if (Attempts >= maxAttempts)
            PostingAbandoned = true;
    }

    /// <summary>Gives up immediately — for permanent errors that retrying can never fix.</summary>
    public void Abandon(string? error)
    {
        Attempts++;
        LastError = Truncate(error);
        PostingAbandoned = true;
    }

    private static string? Truncate(string? value) =>
        value is { Length: > 1000 } ? value[..1000] : value;
}
