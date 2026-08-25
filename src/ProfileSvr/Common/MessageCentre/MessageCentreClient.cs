using System.Net.Http.Json;

namespace ProfileSvr.Common.MessageCentre;

/// <summary>
/// Client for the Digitvant Message Centre Service (MCS).
///   Email:    POST /api/notifications/send          { channels: ["Email"], from, to, subject, message }
///   WhatsApp: POST /api/notifications/whatsapp/send { to, message, from }
/// Configure via MessageCentre:BaseUrl, MessageCentre:BearerToken,
/// and optional sender overrides MessageCentre:EmailFrom / MessageCentre:WhatsAppFrom.
/// </summary>
public class MessageCentreClient(
    HttpClient http,
    IConfiguration configuration,
    ILogger<MessageCentreClient> logger) : IMessageCentre
{
    private record NotificationRequest(string[] Channels, string? From, string To, string? Subject, string Message);

    private record WhatsAppSendRequest(string To, string Message, string? From);

    private record McsResponse(bool IsSuccessful, string? Message, string? Code);

    public async Task SendEmailOtpAsync(string emailAddress, string code, string heading, string intro, CancellationToken ct)
    {
        var html = OtpEmailTemplate.Build(
            code,
            Otp.ExpiryMinutes,
            heading,
            intro,
            brandName: configuration["MessageCentre:BrandName"]);

        var request = new NotificationRequest(
            Channels: ["Email"],
            From: configuration["MessageCentre:EmailFrom"],
            To: emailAddress,
            Subject: "Your verification code",
            Message: html);

        await SendAsync("/api/notifications/send", request, "email", emailAddress, ct);
    }

    public ProfileSvr.Domain.OtpChannel PhoneOtpChannel =>
        string.Equals(configuration["MessageCentre:PhoneChannel"], "Sms", StringComparison.OrdinalIgnoreCase)
            ? ProfileSvr.Domain.OtpChannel.Sms
            : ProfileSvr.Domain.OtpChannel.WhatsApp;

    public async Task SendPhoneOtpAsync(string phoneNumber, string code, CancellationToken ct)
    {
        // The Message Centre expects digits only (e.g. 2348060168634, no "+").
        phoneNumber = new string(phoneNumber.Where(char.IsDigit).ToArray());
        var body = $"Your verification code is {code}. It expires in {Otp.ExpiryMinutes} minutes.";

        if (PhoneOtpChannel == ProfileSvr.Domain.OtpChannel.Sms)
        {
            var sms = new NotificationRequest(
                Channels: ["Sms"],
                From: configuration["MessageCentre:SmsFrom"],
                To: phoneNumber,
                Subject: null,
                Message: body);
            await SendAsync("/api/notifications/send", sms, "sms", phoneNumber, ct);
            return;
        }

        var request = new WhatsAppSendRequest(
            To: phoneNumber,
            Message: body,
            From: configuration["MessageCentre:WhatsAppFrom"]);

        await SendAsync("/api/notifications/whatsapp/send", request, "whatsapp", phoneNumber, ct);
    }

    private async Task SendAsync<T>(string path, T payload, string channel, string to, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync(path, payload, ct);

        McsResponse? body = null;
        try
        {
            body = await response.Content.ReadFromJsonAsync<McsResponse>(ct);
        }
        catch (System.Text.Json.JsonException)
        {
            // fall through — treated as failure below unless the status code alone is trustworthy
        }

        if (!response.IsSuccessStatusCode || body is not { IsSuccessful: true })
        {
            logger.LogError(
                "Message Centre rejected {Channel} message to {To}: HTTP {StatusCode}, code={Code}, message={Message}",
                channel, to, (int)response.StatusCode, body?.Code, body?.Message);
            throw new HttpRequestException(
                $"Message Centre {channel} send failed: {body?.Message ?? $"HTTP {(int)response.StatusCode}"}");
        }

        logger.LogInformation("Message Centre accepted {Channel} message to {To}", channel, to);
    }
}
