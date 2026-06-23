using System.Runtime.InteropServices;

namespace HeliVMS.Controls;

public sealed unsafe class PooledBuffer : IDisposable {
    private byte* _data;
    private int _capacity;
    private bool _disposed;

    public byte* Data => _disposed ? throw new ObjectDisposedException(nameof(PooledBuffer)) : _data;
    public IntPtr DataPtr => (IntPtr)Data;
    public int Width { get; set; }
    public int Height { get; set; }
    public int DataSize { get; set; }
    public long PtsMicroseconds { get; set; }

    public PooledBuffer(int minSize) {
        _capacity = minSize;
        _data = (byte*)NativeMemory.Alloc((nuint)minSize);
    }

    public void EnsureCapacity(int minSize) {
        if (_disposed) throw new ObjectDisposedException(nameof(PooledBuffer));
        if (_capacity >= minSize) return;
        if (_data != null) NativeMemory.Free(_data);
        _capacity = minSize;
        _data = (byte*)NativeMemory.Alloc((nuint)minSize);
    }

    public void Dispose() {
        if (!_disposed) {
            _disposed = true;
            if (_data != null) {
                NativeMemory.Free(_data);
                _data = null;
            }
            _capacity = 0;
        }
    }
}
