using System.Collections.Concurrent;
using LocalAI.Core.Exceptions;
using Microsoft.Extensions.Options;

namespace LocalAI.Core.Services;

/// <summary>
/// Slows password guessing: after <see cref="AccountOptions.MaxFailedLogins"/> failures for a
/// username within the lockout window, further attempts are refused until the window ends.
/// Keyed by username (not IP) so it also works behind the reverse proxy. In-process only.
/// </summary>
public sealed class LoginThrottle(IOptions<AccountOptions> options, TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <exception cref="AuthException">Too many recent failures for this username.</exception>
    public void EnsureAllowed(string normalizedUsername)
    {
        if (_entries.TryGetValue(normalizedUsername, out var entry) && entry.LockedUntil is { } until)
        {
            var remaining = until - timeProvider.GetUtcNow();
            if (remaining > TimeSpan.Zero)
            {
                throw new AuthException(
                    AuthErrorCode.TooManyAttempts,
                    $"Too many failed sign-in attempts. Try again in {Math.Ceiling(remaining.TotalMinutes)} minute(s).",
                    remaining);
            }
        }
    }

    public void RecordFailure(string normalizedUsername)
    {
        var now = timeProvider.GetUtcNow();
        var window = options.Value.LockoutDuration;
        _entries.AddOrUpdate(
            normalizedUsername,
            _ => new Entry(1, now, null),
            (_, e) =>
            {
                // Start a new counting window once the previous one (or lockout) has expired.
                var fresh = now - e.WindowStart > window || (e.LockedUntil is { } u && u <= now);
                var failures = fresh ? 1 : e.Failures + 1;
                var start = fresh ? now : e.WindowStart;
                return new Entry(failures, start, failures >= options.Value.MaxFailedLogins ? now + window : null);
            });
    }

    public void RecordSuccess(string normalizedUsername) => _entries.TryRemove(normalizedUsername, out _);

    private sealed record Entry(int Failures, DateTimeOffset WindowStart, DateTimeOffset? LockedUntil);
}
