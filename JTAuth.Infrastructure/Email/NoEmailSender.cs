using JTAuth.Application;

namespace JTAuth.Infrastructure.Email;

/// <summary>
/// Sends nothing. Used outside Development while <c>JTAuth:ShowCodeWithoutEmail</c> is on, where the code goes back to the page in the
/// answer to the code request instead of by email. It does not log the code either.
/// </summary>
public sealed class NoEmailSender : IEmailSender
{
    public Task SendLoginCodeAsync(string toAddress, string appName, string code, DateTimeOffset expiresUtc, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
