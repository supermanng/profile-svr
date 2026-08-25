namespace ProfileSvr.Domain;

public enum ProfileStatus
{
    /// <summary>Email verified and SSO auth created; profile details not yet submitted.</summary>
    AuthCreated,
    /// <summary>Profile fully created (create-profile step completed).</summary>
    Active
}

public class Profile
{
    public Guid Id { get; set; }
    public ProfileStatus Status { get; set; }
    public string EmailAddress { get; set; } = string.Empty;
    /// <summary>Null until the user adds a phone number after onboarding.</summary>
    public string? PhoneNumber { get; set; }
    public bool EmailConfirmed { get; set; }
    public bool PhoneNumberConfirmed { get; set; }
    /// <summary>Personal details, collected in the final onboarding step.</summary>
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? MiddleName { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public string? Gender { get; set; }
    /// <summary>KYC tier (0 = unverified, 1 = basic KYC passed, ...).</summary>
    public int Tier { get; set; }
    /// <summary>Core-banking customer id, assigned externally.</summary>
    public string? Cif { get; set; }
    public string? Address { get; set; }
    public string? Bvn { get; set; }
    public string? Nin { get; set; }
    public bool BvnIsVerified { get; set; }
    public bool NinIsVerified { get; set; }
    /// <summary>The device used to create this profile.</summary>
    public Guid? DeviceId { get; set; }
    public Device? Device { get; set; }
    /// <summary>Per-profile random salt for the transaction PIN (PBKDF2).</summary>
    public string? TransactionPinSalt { get; set; }
    /// <summary>PBKDF2 hash of the transaction PIN — the plain PIN is never stored.</summary>
    public string? TransactionPinHash { get; set; }
    public bool HasSetTransactionPin { get; set; }
    /// <summary>Set whenever the profile's active device is changed via the OTP flow.</summary>
    public DateTime? DeviceChangedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }

    /// <summary>Auto-computed: true while the latest device change is less than 24 hours old.</summary>
    public bool DeviceRecentlyChanged =>
        DeviceChangedAtUtc is { } changedAt && DateTime.UtcNow - changedAt < TimeSpan.FromHours(24);
}
