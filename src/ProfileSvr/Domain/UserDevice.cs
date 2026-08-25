namespace ProfileSvr.Domain;

/// <summary>
/// Active link between a profile and the device it is currently using.
/// Only one unreleased row may exist per profile and per device at any time.
/// Released rows are kept as history.
/// </summary>
public class UserDevice
{
    public Guid Id { get; set; }
    public Guid ProfileId { get; set; }
    public Profile? Profile { get; set; }
    public Guid DeviceId { get; set; }
    public Device? Device { get; set; }
    public DateTime LinkedAtUtc { get; set; }
    /// <summary>Null while the binding is active.</summary>
    public DateTime? ReleasedAtUtc { get; set; }
}
