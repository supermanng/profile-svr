namespace ProfileSvr.Domain;

public class Device
{
    public Guid Id { get; set; }
    /// <summary>Client-supplied unique identifier (installation id, fingerprint, etc.).</summary>
    public string DeviceIdentifier { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>ios, android, web, ...</summary>
    public string Platform { get; set; } = string.Empty;
    public string? OsName { get; set; }
    public string? OsVersion { get; set; }
    public string? Manufacturer { get; set; }
    public string? Model { get; set; }
    /// <summary>Version of the app installed on the device.</summary>
    public string? AppVersion { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }

    public ICollection<Profile> Profiles { get; set; } = [];
}
