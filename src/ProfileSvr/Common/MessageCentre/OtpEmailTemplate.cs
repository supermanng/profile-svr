using System.Text;

namespace ProfileSvr.Common.MessageCentre;

/// <summary>
/// Email-client-safe HTML template for OTP messages: table layout, inline styles,
/// system fonts, no external assets — renders consistently in Gmail/Outlook/Apple Mail.
/// The brand name is optional (MessageCentre:BrandName); unbranded by default.
/// </summary>
public static class OtpEmailTemplate
{
    public static string Build(string code, int expiryMinutes, string heading, string intro, string? brandName = null)
    {
        var digitTiles = new StringBuilder();
        foreach (var digit in code)
            digitTiles.Append($"""
                <td style="padding:0 5px;">
                  <table role="presentation" cellpadding="0" cellspacing="0"><tr>
                    <td align="center" style="width:52px;height:64px;background-color:#f5f3ff;border:1px solid #ddd6fe;border-bottom:3px solid #7c3aed;border-radius:12px;font-family:'SF Mono',SFMono-Regular,Consolas,'Courier New',monospace;font-size:30px;font-weight:700;color:#312e81;">
                      {digit}
                    </td>
                  </tr></table>
                </td>
                """);

        var brandFooter = string.IsNullOrWhiteSpace(brandName)
            ? ""
            : $"<br>&copy; {DateTime.UtcNow.Year} {brandName}. All rights reserved.";

        var brandHeader = string.IsNullOrWhiteSpace(brandName)
            ? ""
            : $"""
                <p style="margin:0 0 22px 0;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;font-size:14px;font-weight:700;letter-spacing:3px;text-transform:uppercase;color:#a5b4fc;">
                  {brandName}
                </p>
                """;

        return $$"""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<meta http-equiv="X-UA-Compatible" content="IE=edge">
<title>{{heading}}</title>
</head>
<body style="margin:0;padding:0;background-color:#edeff7;-webkit-text-size-adjust:100%;">
  <!-- preheader (hidden preview text) -->
  <div style="display:none;max-height:0;overflow:hidden;mso-hide:all;">
    Your verification code is {{code}} — it expires in {{expiryMinutes}} minutes.&nbsp;&zwnj;&nbsp;&zwnj;&nbsp;&zwnj;&nbsp;&zwnj;&nbsp;&zwnj;
  </div>

  <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="background-color:#edeff7;">
    <tr>
      <td align="center" style="padding:48px 16px;">
        <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="max-width:520px;">

          <!-- card -->
          <tr>
            <td style="background-color:#ffffff;border-radius:20px;box-shadow:0 12px 32px rgba(30,27,75,0.14);overflow:hidden;">
              <table role="presentation" width="100%" cellpadding="0" cellspacing="0">

                <!-- gradient header with icon badge -->
                <tr>
                  <td align="center" style="background-color:#1e1b4b;background-image:linear-gradient(135deg,#1e1b4b 0%,#4338ca 60%,#7c3aed 100%);padding:36px 40px 40px 40px;">
                    {{brandHeader}}
                    <table role="presentation" cellpadding="0" cellspacing="0"><tr>
                      <td align="center" style="width:76px;height:76px;background-color:rgba(255,255,255,0.14);border:1px solid rgba(255,255,255,0.28);border-radius:50%;font-size:34px;line-height:76px;">
                        &#128274;
                      </td>
                    </tr></table>
                    <h1 style="margin:22px 0 8px 0;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;font-size:24px;font-weight:700;letter-spacing:-0.3px;color:#ffffff;">
                      {{heading}}
                    </h1>
                    <p style="margin:0;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;font-size:14px;line-height:1.6;color:#c7d2fe;">
                      One quick step to keep your account secure
                    </p>
                  </td>
                </tr>

                <!-- body -->
                <tr>
                  <td style="padding:40px 40px 36px 40px;">
                    <p style="margin:0 0 30px 0;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;font-size:15px;line-height:1.7;color:#4b5563;" align="center">
                      {{intro}}
                    </p>

                    <!-- digit tiles -->
                    <table role="presentation" align="center" cellpadding="0" cellspacing="0" style="margin:0 auto;">
                      <tr>
                        {{digitTiles}}
                      </tr>
                    </table>

                    <!-- expiry pill -->
                    <table role="presentation" align="center" cellpadding="0" cellspacing="0" style="margin:28px auto 0 auto;">
                      <tr>
                        <td style="background-color:#fffbeb;border:1px solid #fde68a;border-radius:999px;padding:8px 18px;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;font-size:13px;color:#92400e;">
                          &#9200;&nbsp; Expires in <strong>{{expiryMinutes}} minutes</strong> &middot; single use
                        </td>
                      </tr>
                    </table>

                    <!-- divider -->
                    <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="margin:34px 0 0 0;">
                      <tr><td style="border-top:1px solid #eceef3;font-size:0;line-height:0;">&nbsp;</td></tr>
                    </table>

                    <!-- security note -->
                    <table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="margin:22px 0 0 0;">
                      <tr>
                        <td style="background-color:#f8fafc;border-radius:12px;padding:16px 20px;">
                          <p style="margin:0;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;font-size:13px;line-height:1.7;color:#64748b;">
                            <strong style="color:#334155;">&#128737;&#65039;&nbsp; Keep it private.</strong>
                            We will never ask you for this code. If you didn't request it,
                            you can safely ignore this email — no changes will be made.
                          </p>
                        </td>
                      </tr>
                    </table>
                  </td>
                </tr>
              </table>
            </td>
          </tr>

          <!-- footer -->
          <tr>
            <td align="center" style="padding:30px 20px 0 20px;">
              <p style="margin:0;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;font-size:12px;line-height:1.7;color:#9aa3b2;">
                This is an automated message — please do not reply.{{brandFooter}}
              </p>
            </td>
          </tr>

        </table>
      </td>
    </tr>
  </table>
</body>
</html>
""";
    }
}
