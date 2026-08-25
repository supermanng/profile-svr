namespace ProfileSvr.Common.MessageCentre;

/// <summary>
/// Dev fallback used when MessageCentre:BaseUrl is not configured —
/// logs the OTP instead of sending it. Never use in production.
/// </summary>
public class LoggingMessageCentre(ILogger<LoggingMessageCentre> logger) : IMessageCentre
{
    public Task SendEmailOtpAsync(string emailAddress, string code, string heading, string intro, CancellationToken ct)
    {
        logger.LogWarning("[DEV ONLY] Email OTP for {Email}: {Code}", emailAddress, code);
        return Task.CompletedTask;
    }

    public ProfileSvr.Domain.OtpChannel PhoneOtpChannel => ProfileSvr.Domain.OtpChannel.WhatsApp;

    public Task SendPhoneOtpAsync(string phoneNumber, string code, CancellationToken ct)
    {
        logger.LogWarning("[DEV ONLY] Phone OTP for {Phone}: {Code}", phoneNumber, code);
        return Task.CompletedTask;
    }
}
