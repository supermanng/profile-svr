namespace ProfileSvr.Domain;

public enum ActivityType
{
    AuthCreated,
    ProfileCreated,
    EmailVerified,
    PhoneNumberSet,
    PhoneVerified,
    ProfileCompleted,
    LoggedIn,
    PasswordChanged,
    PasswordReset,
    PinSet,
    PinChanged,
    PinReset,
    DeviceChanged,
    KycVerified,
    AccountsProvisioned
}

/// <summary>Audit trail of every significant action on a profile.</summary>
public class Activity
{
    public Guid Id { get; set; }
    public Guid ProfileId { get; set; }
    public Profile? Profile { get; set; }
    /// <summary>The device the action was performed from, when known.</summary>
    public Guid? DeviceId { get; set; }
    public Device? Device { get; set; }
    public ActivityType Type { get; set; }
    public string? Description { get; set; }
    /// <summary>Client IP the action came from (X-Forwarded-For aware).</summary>
    public string? IpAddress { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
