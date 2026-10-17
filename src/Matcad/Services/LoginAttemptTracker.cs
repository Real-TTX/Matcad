using System.Collections.Concurrent;

namespace Matcad.Services;

/// <summary>The counters one sign-in attempt has been booked on (see <see cref="LoginAttemptTracker.Begin"/>).</summary>
public sealed record AttemptTicket(string Account, string AccountOnClient, string Client);

/// <summary>
/// Slows down password guessing on the admin login and on the Matcad login portal (which faces the internet).
/// An attempt is booked <b>before</b> the password is checked, so a burst of parallel requests cannot all slip through
/// a check that only looks at earlier failures; a successful sign-in takes its booking back. Once a counter is over its
/// limit inside the window, attempts are refused until the window has passed. In memory only; a restart clears it.
///
/// Three counters: per account (whoever asks - a spoofed client address does not help), per account and client
/// address (so one noisy client does not lock a legitimate user out for long), and per client address (guessing across
/// many accounts).
/// </summary>
public sealed class LoginAttemptTracker
{
    public const int MaxPerAccount = 50;
    public const int MaxPerAccountOnClient = 8;
    public const int MaxPerClient = 30;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private const int PruneThreshold = 10_000;

    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset Since)> _entries = new();

    public LoginAttemptTracker(TimeProvider time) => _time = time;

    /// <summary>Books an attempt. Returns the ticket to hand to <see cref="Succeeded"/>, or null when the attempt is refused.</summary>
    public AttemptTicket? Begin(string scope, string login, string? clientIp)
    {
        var ticket = new AttemptTicket(
            $"{scope}|{login.Trim().ToLowerInvariant()}",
            $"{scope}|{login.Trim().ToLowerInvariant()}|{clientIp}",
            $"{scope}|{clientIp}");

        // All three are booked, whatever the others say.
        var account = Book(ticket.Account, MaxPerAccount);
        var accountOnClient = Book(ticket.AccountOnClient, MaxPerAccountOnClient);
        var client = Book(ticket.Client, MaxPerClient);
        return account && accountOnClient && client ? ticket : null;
    }

    /// <summary>The password was right: the account is clean again, and the attempt does not count against the client address.</summary>
    public void Succeeded(AttemptTicket ticket)
    {
        _entries.TryRemove(ticket.Account, out _);
        _entries.TryRemove(ticket.AccountOnClient, out _);
        _entries.AddOrUpdate(ticket.Client, _ => (0, _time.GetUtcNow()), (_, e) => (Math.Max(0, e.Count - 1), e.Since));
    }

    private bool Book(string key, int limit)
    {
        var now = _time.GetUtcNow();
        if (_entries.Count > PruneThreshold) Prune(now);
        var entry = _entries.AddOrUpdate(key,
            _ => (1, now),
            (_, e) => now - e.Since >= Window ? (1, now) : (e.Count + 1, e.Since));
        return entry.Count <= limit;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, e) in _entries)
            if (now - e.Since >= Window) _entries.TryRemove(key, out _);
    }
}
