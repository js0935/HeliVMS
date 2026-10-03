using System.Threading.Channels;

namespace HeliVMS.Shared;

/// <summary>
/// Fan-out for live alert/board updates (M118, section 14.3).
/// WebApi serves it over <c>/api/alerts/ws</c>; the desktop app uses the same type
/// in-process so the alarm windows refresh on push instead of waiting for a poll.
/// </summary>
public sealed record AlertUpdate(string Kind, long EventId, string? Status = null, string? Priority = null);

/// <summary>
/// Pub/sub relay between alarm mutations and subscribed consumers.
/// Publishers call <see cref="Publish"/>; consumers opt in through <see cref="Subscribe"/>.
/// </summary>
public sealed class AlertBroadcastHub
{
    private readonly object _gate = new();
    private readonly List<ChannelWriter<AlertUpdate>> _writers = new();

    public IDisposable Subscribe(out ChannelReader<AlertUpdate> reader)
    {
        var channel = Channel.CreateUnbounded<AlertUpdate>();
        reader = channel.Reader;
        lock (_gate)
        {
            _writers.Add(channel.Writer);
        }

        return new Lease(channel.Writer, this);
    }

    public void Publish(AlertUpdate update)
    {
        ChannelWriter<AlertUpdate>[] snapshot;
        lock (_gate)
        {
            snapshot = _writers.ToArray();
        }

        foreach (var writer in snapshot)
        {
            writer.TryWrite(update);
        }
    }

    private void Remove(ChannelWriter<AlertUpdate> writer)
    {
        lock (_gate)
        {
            _writers.Remove(writer);
        }

        writer.TryComplete();
    }

    private sealed class Lease : IDisposable
    {
        private readonly ChannelWriter<AlertUpdate> _writer;
        private readonly AlertBroadcastHub _owner;
        private bool _disposed;

        public Lease(ChannelWriter<AlertUpdate> writer, AlertBroadcastHub owner)
        {
            _writer = writer;
            _owner = owner;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner.Remove(_writer);
        }
    }
}