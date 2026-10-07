using JTAuth.Application;
using Microsoft.Extensions.Logging;

namespace JTAuth.Infrastructure.Email;

/// <summary>
/// Development stand-in for a real email service: the message that would be sent is written to the log, code included,
/// so a developer can sign in without a mailbox. It must never be registered outside Development.
/// </summary>
public sealed partial class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendLoginCodeAsync(string toAddress, string appName, string code, DateTimeOffset expiresUtc, CancellationToken cancellationToken)
    {
        LogCode(logger, toAddress, appName, code, expiresUtc);
        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Email to {Address}: Your {AppName} code is {Code} (valid until {ExpiresUtc:u})")]
    private static partial void LogCode(ILogger logger, string address, string appName, string code, DateTimeOffset expiresUtc);
}
