using System.Text;

namespace Storporate.Infrastructure.Email;

/// <summary>
/// Produces the (HTML, plain-text) bodies for the OTP email used by every
/// <see cref="Storporate.SharedKernel.Abstractions.IEmailSender"/> implementation
/// (Resend, SMTP, and any future provider).
///
/// The HTML is table-based (Outlook + Gmail + most clients still ship the best layout fidelity
/// from 2008-vintage tables), inline-styled everywhere (many clients strip <c>&lt;style&gt;</c>
/// blocks entirely), 600px content centered on a full-width wrapper. The palette mirrors the
/// frontend's "Bolivian Beauty" tokens from <c>storporate.frontend/src/app/globals.css</c>; the
/// digit-box row mirrors <c>storporate.frontend/src/components/auth/otp-boxes.tsx</c>'s
/// <c>rounded-xl border</c> + cream tint + plum-text styling exactly so the email matches what
/// the user just signed up against on screen.
///
/// The "Storporate" wordmark at the top is text-only by design: an earlier version of this
/// template base64-inlined a PNG badge via a <c>data:image/png;base64,…</c> URI, but Gmail's web
/// and mobile clients strip inline <c>data:</c>-URI images from received mail for security, so
/// the badge rendered as a broken-image icon. A remote HTTPS URL is out of scope until a real
/// CDN/domain exists; until then, the wordmark is text-only and renders correctly everywhere.
/// </summary>
public static class OtpEmailTemplateBuilder
{
    /// <summary>Length of an OTP code, from <c>OtpOptions.CodeLength</c> default.</summary>
    private const int CodeLength = 6;

    /// <summary>Builds the HTML and plain-text bodies for an OTP email carrying <paramref name="code"/>.</summary>
    public static (string Html, string Text) Build(string code)
    {
        ArgumentException.ThrowIfNullOrEmpty(code);

        // Plain-text fallback (kept clean — no raw HTML, no markdown) for clients that refuse to
        // render HTML. Matches the copy the user sees under "Show original" / "View plain text"
        // in Gmail, Outlook, etc. The 10-minute expiry matches OtpOptions.ExpiryMinutes default.
        var text =
            $"Your verification code is {code}. It expires in 10 minutes and can only be used once. "
            + "If you didn't request this, you can safely ignore this email.";

        var html = BuildHtml(code);

        return (html, text);
    }

    private static string BuildHtml(string code)
    {
        // Pad / truncate to CodeLength defensively — the OTP generator is the only producer and
        // it always emits exactly CodeLength digits, but building 6 boxes regardless of input
        // means a wrong-length code (e.g. a future change) still produces a renderable email
        // rather than a malformed row. Truncation happens first if the input is too long.
        var digits = code.Length switch
        {
            > CodeLength => code[..CodeLength],
            < CodeLength => code.PadRight(CodeLength, ' '),
            _ => code,
        };

        var digitBoxes = new StringBuilder(CodeLength * 256);
        for (var i = 0; i < CodeLength; i++)
        {
            if (i > 0)
            {
                digitBoxes.Append("""
                  <td style="width:8px;"></td>

""");
            }

            // Single-line raw string — closing """ allowed inline because the opening """ is on
            // the same line as the content. The interpolated `{digits[i]}` keeps the .Append
            // chain out of the call site.
            digitBoxes.Append(
                $"""
                  <td style="width:48px; height:52px; border:2px solid #e7dfc0; background-color:#f3efdd; border-radius:12px;" align="center" valign="middle">
                    <span style="font-family:'Space Grotesk','Helvetica Neue',Arial,sans-serif; font-weight:600; font-size:24px; color:#2a1830;">{digits[i]}</span>
                  </td>
""");
        }

        // Multi-line raw string template — closing """ on its own line. The digit-box row is
        // spliced in via {digitBoxes}. No other dynamic content in the body — the design was
        // finalized in the plan and any change here should go back through that approval flow.
        var html = $"""
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<title>Your Storporate verification code</title>
</head>
<body style="margin:0; padding:0; background-color:#fbf8ec;">
<div style="width:100%; background-color:#fbf8ec; padding:48px 16px; box-sizing:border-box; font-family:'Manrope', -apple-system, 'Helvetica Neue', Arial, sans-serif;">
  <table role="presentation" width="600" cellpadding="0" cellspacing="0" border="0" align="center" style="width:600px; max-width:100%; margin:0 auto;">

    <!-- Wordmark row (text-only: see class remarks for why the badge was removed) -->
    <tr>
      <td align="center" style="padding-bottom:32px;">
        <span style="font-family:'Space Grotesk','Helvetica Neue',Arial,sans-serif; font-weight:700; font-size:22px; color:#2a1830;">Storporate</span>
      </td>
    </tr>

    <!-- Card -->
    <tr>
      <td style="background-color:#ffffff; border:1px solid #e7dfc0; border-radius:20px; padding:44px 40px;">
        <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0">
          <tr>
            <td align="center">
              <div style="font-family:'Space Grotesk','Helvetica Neue',Arial,sans-serif; font-weight:700; font-size:25px; line-height:1.3; color:#2a1830;">
                Your verification code
              </div>
            </td>
          </tr>
          <tr>
            <td align="center" style="padding-top:10px;">
              <div style="font-family:'Manrope',-apple-system,'Helvetica Neue',Arial,sans-serif; font-weight:400; font-size:14px; line-height:1.5; color:#6e6488;">
                Enter this code to continue signing in to Storporate.
              </div>
            </td>
          </tr>

          <!-- OTP digit boxes: one <td> per digit of {code}, matching storporate.frontend's otp-boxes.tsx styling exactly (rounded-xl border, cream-tint fill, plum text), separated by 8px spacer <td>s -->
          <tr>
            <td align="center" style="padding-top:30px;">
              <table role="presentation" cellpadding="0" cellspacing="0" border="0">
                <tr>
{digitBoxes}
                </tr>
              </table>
            </td>
          </tr>

          <tr>
            <td align="center" style="padding-top:26px;">
              <div style="font-family:'Manrope',-apple-system,'Helvetica Neue',Arial,sans-serif; font-weight:400; font-size:13px; line-height:1.6; color:#6e6488;">
                Expires in 10 minutes. Works once.
              </div>
            </td>
          </tr>
          <tr>
            <td align="center" style="padding-top:4px;">
              <div style="font-family:'Manrope',-apple-system,'Helvetica Neue',Arial,sans-serif; font-weight:400; font-size:13px; line-height:1.6; color:#6e6488;">
                Didn't request this? You can ignore this email.
              </div>
            </td>
          </tr>
        </table>
      </td>
    </tr>

    <!-- Footer -->
    <tr>
      <td align="center" style="padding-top:26px;">
        <span style="font-family:'Manrope',-apple-system,'Helvetica Neue',Arial,sans-serif; font-weight:400; font-size:11px; color:#b0a8c2;">&copy; Storporate</span>
      </td>
    </tr>

  </table>
</div>
</body>
</html>
""";

        return html;
    }
}
