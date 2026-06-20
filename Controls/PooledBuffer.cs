using System.Buffers;

namespace HeliVMS.Controls;

/// <summary>Reusable frame buffer wrapping ArrayPool to eliminate per-frame GC allocations</summary>
public sealed class PooledBuffer : IDisposable
{
    private byte[]? _data;
    private bool _disposed;

    public byte[] Data => _data ?? throw new ObjectDisposedException(nameof(PooledBuffer));
    public int Width { get; set; }
    public int Height { get; set; }
    public int DataSize { get; set; }

    public PooledBuffer(int minSize)
    {
        _data = ArrayPool<byte>.Shared.Rent(minSize);
    }

    public void Dispose()
    {
        if (!_disposed && _data is not null)
        {
            ArrayPool<byte>.Shared.Return(_data);
            _data = null;
            _disposed = true;
        }
    }
}
