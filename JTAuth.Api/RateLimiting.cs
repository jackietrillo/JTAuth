using System.Threading.RateLimiting;

namespace JTAuth.Api;

/// <summary>Per-address request limits for anonymous callers, read from the <c>RateLimits</c> section.</summary>
public sealed class RateLimitSettings
{
    public const string SectionName = "RateLimits";

    /// <summary>Every endpoint: requests per client address per minute.</summary>
    public int RequestsPerMinutePerIp { get; set; } = 120;

    /// <summary>Sign-in code requests (each one sends an email, which costs money) per client address per minute.</summary>
    public int CodeRequestsPerMinutePerIp { get; set; } = 5;

    /// <summary>Sign-in code requests per client address per hour.</summary>
    public int CodeRequestsPerHourPerIp { get; set; } = 20;
}

internal static class RateLimiting
{
    public const string CodeRequestPath = "/api/v1/auth/code/request";

    /// <summary>
    /// The limits applied to every request, chained so each must allow it: a general one per address, and two stricter ones
    /// (per minute and per hour) that only count sign-in code requests.
    /// </summary>
    public static PartitionedRateLimiter<HttpContext> CreateGlobalLimiter(RateLimitSettings settings) =>
        PartitionedRateLimiter.CreateChained(
            PerAddress("all", _ => true, settings.RequestsPerMinutePerIp, TimeSpan.FromMinutes(1)),
            PerAddress("code-minute", IsCodeRequest, settings.CodeRequestsPerMinutePerIp, TimeSpan.FromMinutes(1)),
            PerAddress("code-hour", IsCodeRequest, settings.CodeRequestsPerHourPerIp, TimeSpan.FromHours(1)));

    private static bool IsCodeRequest(HttpContext context) =>
        HttpMethods.IsPost(context.Request.Method)
        && context.Request.Path.Equals(CodeRequestPath, StringComparison.OrdinalIgnoreCase);

    private static PartitionedRateLimiter<HttpContext> PerAddress(string name, Func<HttpContext, bool> applies, int permits, TimeSpan window) =>
        PartitionedRateLimiter.Create<HttpContext, string>(context => applies(context)
            ? RateLimitPartition.GetFixedWindowLimiter(
                $"{name}:{context.Connection.RemoteIpAddress}",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = window })
            : RateLimitPartition.GetNoLimiter($"{name}:none"));
}
