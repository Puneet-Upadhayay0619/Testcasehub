namespace TestCaseHub.Api.Services;

// Pluggable so a real provider (Resend, Brevo, SendGrid, etc.) can be swapped in later via DI
// without touching AuthController — just register a different implementation in Program.cs
// and set its API key in configuration. Until that's wired up, LoggingEmailSender below is the
// default: it doesn't fail the request, it just logs what WOULD have been sent, so
// forgot-password is fully testable end-to-end right now without any real email account.
public interface IEmailSender
{
    Task SendPasswordResetAsync(string toEmail, string resetToken, string resetUrlBase);
}

public class LoggingEmailSender : IEmailSender
{
    private readonly ILogger<LoggingEmailSender> _logger;
    public LoggingEmailSender(ILogger<LoggingEmailSender> logger) => _logger = logger;

    public Task SendPasswordResetAsync(string toEmail, string resetToken, string resetUrlBase)
    {
        // NOTE: no email provider is configured yet — swap this implementation for a real one
        // (Resend/Brevo/SendGrid) when that's set up. Until then the reset link is only
        // recoverable from this log, which is fine for local/dev testing but NOT a substitute
        // for real delivery in production.
        _logger.LogWarning(
            "[DEV ONLY — no email provider configured] Password reset requested for {Email}. " +
            "Reset link: {ResetUrlBase}?token={Token} (valid 30 minutes).",
            toEmail, resetUrlBase, resetToken);
        return Task.CompletedTask;
    }
}
