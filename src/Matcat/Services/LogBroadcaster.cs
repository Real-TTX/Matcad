using System.Threading.Channels;

namespace Matcat.Services;

/// <summary>Lightweight DTO pushed to the real-time log stream (SSE).</summary>
public record LogEntry(
    DateTime Timestamp, string Host, string Method, string Path,
    int Status, string RemoteIp, double DurationMs);

/// <summary>
/// Fan-out hub for the live log stream. The ingest service publishes entries;
/// SSE connections subscribe. Also keeps a small ring buffer so a freshly
/// opened stream can show recent activity immediately.
/// Dependency-free alternative to SignalR (the stream is one-directional).
/// </summary>
public class LogBroadcaster
{
    private const int RecentSize = 50;
    private readonly object _lock = new();
    private readonly LinkedList<LogEntry> _recent = new();
    private readonly List<Channel<LogEntry>> _subscribers = new();

    public IReadOnlyList<LogEntry> Recent()
    {
        lock (_lock) return _recent.ToList();
    }

    public void Publish(LogEntry entry)
    {
        lock (_lock)
        {
            _recent.AddFirst(entry);
            while (_recent.Count > RecentSize) _recent.RemoveLast();
            foreach (var ch in _subscribers) ch.Writer.TryWrite(entry);
        }
    }

    public (ChannelReader<LogEntry> Reader, IDisposable Lease) Subscribe()
    {
        var channel = Channel.CreateBounded<LogEntry>(
            new BoundedChannelOptions(200) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (_lock) _subscribers.Add(channel);
        return (channel.Reader, new Subscription(this, channel));
    }

    private void Unsubscribe(Channel<LogEntry> channel)
    {
        lock (_lock) _subscribers.Remove(channel);
        channel.Writer.TryComplete();
    }

    private sealed class Subscription : IDisposable
    {
        private readonly LogBroadcaster _owner;
        private readonly Channel<LogEntry> _channel;
        public Subscription(LogBroadcaster owner, Channel<LogEntry> channel) { _owner = owner; _channel = channel; }
        public void Dispose() => _owner.Unsubscribe(_channel);
    }
}
