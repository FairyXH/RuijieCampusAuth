using System.Collections.Concurrent;

namespace RuijieCampusAuth.Core.Protocols;

/// <summary>
/// Bridges SslStream to EAP/PEAP TLS payloads. Reads block until a server
/// fragment is fed; writes expose TLS records for the EAP transport to send.
/// </summary>
public sealed class EapTlsStream : Stream
{
    private readonly BlockingCollection<byte[]> _incoming = new();
    private readonly BlockingCollection<byte[]> _outgoing = new();
    private byte[]? _current;
    private int _offset;

    public void Feed(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty || _incoming.IsAddingCompleted)
        {
            return;
        }
        _incoming.Add(data.ToArray());
    }

    public void CompleteInput()
    {
        if (!_incoming.IsAddingCompleted)
        {
            _incoming.CompleteAdding();
        }
    }

    public byte[]? TakeOutgoing(int timeoutMilliseconds)
    {
        return _outgoing.TryTake(out var data, timeoutMilliseconds) ? data : null;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        while (true)
        {
            if (_current is not null && _offset < _current.Length)
            {
                var length = Math.Min(count, _current.Length - _offset);
                Array.Copy(_current, _offset, buffer, offset, length);
                _offset += length;
                return length;
            }
            if (_incoming.IsCompleted)
            {
                return 0;
            }
            _current = _incoming.Take();
            _offset = 0;
        }
    }

    public override int Read(Span<byte> buffer)
    {
        while (true)
        {
            if (_current is not null && _offset < _current.Length)
            {
                var length = Math.Min(buffer.Length, _current.Length - _offset);
                _current.AsSpan(_offset, length).CopyTo(buffer);
                _offset += length;
                return length;
            }
            if (_incoming.IsCompleted)
            {
                return 0;
            }
            _current = _incoming.Take();
            _offset = 0;
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (count > 0)
        {
            _outgoing.Add(buffer.AsSpan(offset, count).ToArray());
        }
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (!buffer.IsEmpty)
        {
            _outgoing.Add(buffer.ToArray());
        }
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        return Task.Run(() => Read(buffer, offset, count), cancellationToken);
    }

    public override ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        return new ValueTask<int>(
            Task.Run(() => Read(buffer.Span), cancellationToken));
    }

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }
}
