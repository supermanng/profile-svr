namespace ProfileSvr.Common.MessageCentre;

/// <summary>
/// Outbound messaging gateway (the Message Centre service).
/// </summary>
public interface IMessageCentre
{
    /// <summary>The transport phone OTPs use (MessageCentre:PhoneChannel — WhatsApp or Sms).</summary>
    ProfileSvr.Domain.OtpChannel PhoneOtpChannel { get; }

    Task SendEmailOtpAsync(string emailAddress, string code, string heading, string intro, CancellationToken ct);
    Task SendPhoneOtpAsync(string phoneNumber, string code, CancellationToken ct);
}
