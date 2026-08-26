namespace ProfileSvr.Domain;

/// <summary>
/// Maps a virtual (collection) account to the profile's CBA naira account, so the
/// credit-posting job can resolve where to post incoming credits.
/// </summary>
public class VirtualAccountMapping
{
    public Guid Id { get; set; }
    public Guid ProfileId { get; set; }
    /// <summary>The virtual account number at the collection provider (unique).</summary>
    public string VirtualAccount { get; set; } = string.Empty;
    /// <summary>The core-banking (naira) account credits are posted to.</summary>
    public string CbaAccount { get; set; } = string.Empty;
    public string? AccountName { get; set; }
    public string? Bank { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
