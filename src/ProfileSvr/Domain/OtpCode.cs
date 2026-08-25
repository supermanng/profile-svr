namespace ProfileSvr.Domain;

public enum OtpChannel
{
    Email,
    WhatsApp,
    Sms
}

public enum OtpPurpose
{
    Onboarding,
    EmailConfirmation,
    PhoneConfirmation,
    DeviceChange,
    PinReset,
    PasswordReset,
    KycVerification,
    DeviceRegistration
}

public class OtpCode
{
    public Guid Id { get; set; }
    /// <summary>Unique public reference for this OTP, handed to the client and quoted back at verification.</summary>
    public string RetrievalCode { get; set; } = string.Empty;
    public OtpPurpose Purpose { get; set; }
    /// <summary>Null for onboarding codes — the profile does not exist yet.</summary>
    public Guid? ProfileId { get; set; }
    /// <summary>
    /// Pre-allocated at initiation: becomes the SSO user's SourceId and the profile's id,
    /// so the token's SourceId claim always resolves the profile directly.
    /// </summary>
    public Guid? SourceId { get; set; }
    public Profile? Profile { get; set; }
    /// <summary>The device this OTP was issued for.</summary>
    public Guid? DeviceId { get; set; }
    public Device? Device { get; set; }
    public OtpChannel Channel { get; set; }
    /// <summary>The address the code was sent to (email or phone number).</summary>
    public string Target { get; set; } = string.Empty;
    /// <summary>SHA-256 hash of the code bound to its purpose and target — the plain code is never stored.</summary>
    public string CodeHash { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? ConsumedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
