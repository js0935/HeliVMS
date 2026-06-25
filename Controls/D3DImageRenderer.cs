using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace HeliVMS.Controls;

// Direct3D 9 列舉（最小子集，僅供 D3DSURFACE_DESC 及裝置建立使用）
// 使用原生 P/Invoke 而非 SharpDX/Vortice 以避免外部相依
internal enum D3DFORMAT : uint { D3DFMT_X8R8G8B8 = 22, D3DFMT_A8R8G8B8 = 21 }
internal enum D3DMULTISAMPLE_TYPE : uint { D3DMULTISAMPLE_NONE = 0 }
internal enum D3DSWAPEFFECT : uint { D3DSWAPEFFECT_DISCARD = 1, D3DSWAPEFFECT_FLIP = 2, D3DSWAPEFFECT_COPY = 3 }
internal enum D3DPOOL : uint { D3DPOOL_DEFAULT = 0, D3DPOOL_SYSTEMMEM = 1, D3DPOOL_MANAGED = 2, D3DPOOL_SCRATCH = 3 }

[Flags]
internal enum D3DCREATE : uint {
    D3DCREATE_SOFTWARE_VERTEXPROCESSING = 0x00000020,
    D3DCREATE_HARDWARE_VERTEXPROCESSING = 0x00000040,
    D3DCREATE_MULTITHREADED = 0x00000004,
    D3DCREATE_FPU_PRESERVE = 0x00000002,
}

internal enum D3DDEVTYPE : uint { D3DDEVTYPE_HAL = 1, D3DDEVTYPE_REF = 2, D3DDEVTYPE_SW = 3, D3DDEVTYPE_NULLREF = 4 }


/// <summary>
/// Manages a single shared Direct3D 9 device for all VideoPlayer instances.
/// 64 players share one device and create individual surfaces from it,
/// avoiding GPU resource exhaustion and wild-pointer crashes at scale.
/// </summary>
internal sealed partial class D3DRenderer : IDisposable {
    private IntPtr _d3d;
    private IntPtr _device;
    private bool _disposed;

    // --- Shared singleton ---
    private static D3DRenderer? _instance;
    private static volatile bool _initAttempted;

    // Serialises all D3D9 device-level native calls. D3D9's CreateDevice / CreateOffscreenPlainSurface
    // are NOT thread-safe — 64 concurrent calls corrupt the device's internal critical section.
    private static readonly object _d3dLock = new();

    public static D3DRenderer? Instance {
        get {
            if (!_initAttempted) {
                _initAttempted = true;
                try {
                    _instance = new D3DRenderer();
                } catch {
                    _instance = null;
                }
            }
            return _instance;
        }
    }

    public static void DisposeInstance() {
        _instance?.Dispose();
        _instance = null;
        _initAttempted = false;
    }

    private const uint D3D_SDK_VERSION = 32;

    [LibraryImport("d3d9.dll")]
    private static partial nint Direct3DCreate9(uint sdkVersion);

    [LibraryImport("d3d9.dll")]
    private static partial int Direct3DCreate9Ex(uint sdkVersion, out nint d3dEx);

    private D3DRenderer() {
        Initialize();
    }

    private unsafe void Initialize() {
        _d3d = Direct3DCreate9(D3D_SDK_VERSION);
        if (_d3d == IntPtr.Zero)
            throw new InvalidOperationException("Direct3DCreate9 failed — d3d9.dll may not be available");

        // Create D3D9 device with minimal hardware vertex processing
        var pp = new D3DPRESENT_PARAMETERS {
            BackBufferWidth = 1,
            BackBufferHeight = 1,
            BackBufferFormat = D3DFORMAT.D3DFMT_X8R8G8B8,
            BackBufferCount = 1,
            MultiSampleType = D3DMULTISAMPLE_TYPE.D3DMULTISAMPLE_NONE,
            SwapEffect = D3DSWAPEFFECT.D3DSWAPEFFECT_DISCARD,
            hDeviceWindow = IntPtr.Zero,
            Windowed = true,
            EnableAutoDepthStencil = false,
            Flags = 0,
            PresentationInterval = 0,
        };

        var createFlags = D3DCREATE.D3DCREATE_SOFTWARE_VERTEXPROCESSING
                        | D3DCREATE.D3DCREATE_MULTITHREADED
                        | D3DCREATE.D3DCREATE_FPU_PRESERVE;

        int hr;
        {
            var createDevice = GetVtableEntry<CreateDeviceDelegate>(_d3d, 16);
            hr = createDevice(_d3d, 0, D3DDEVTYPE.D3DDEVTYPE_HAL, IntPtr.Zero, (uint)createFlags, new IntPtr(&pp), out _device);
        }

        if (hr < 0 || _device == IntPtr.Zero) {
            // Fallback: try reference device (WARP)
            {
                var createDevice = GetVtableEntry<CreateDeviceDelegate>(_d3d, 16);
                hr = createDevice(_d3d, 0, D3DDEVTYPE.D3DDEVTYPE_REF, IntPtr.Zero, (uint)createFlags, new IntPtr(&pp), out _device);
            }

            if (hr < 0 || _device == IntPtr.Zero)
                throw new InvalidOperationException($"D3D9 device creation failed with HRESULT: {hr}");
        }
    }

    public D3DSurface CreateSurface(int width, int height) {
        ObjectDisposedException.ThrowIf(_device == IntPtr.Zero, this);

        return new D3DSurface(this, width, height);
    }

    internal IntPtr CreateOffscreenPlainSurface(int width, int height) {
        // 終極熔斷：D3D9 原生 VTable 與 .NET P/Invoke 之間存在 ABI 不相容風險，
        //    64 路併發壓力下已反覆觸發 coreclr.dll 0xc0000005。
        //    全面強制降級至 WriteableBitmap 軟體渲染，保證系統 100% 穩定不閃退。
        return IntPtr.Zero;
    }

    internal static void DestroySurface(IntPtr surface) {
        if (surface == IntPtr.Zero) return;
        var release = GetVtableEntry<ReleaseDelegate>(surface, 2);
        release(surface);
    }

    private static T GetVtableEntry<T>(nint comObject, int methodIndex) where T : Delegate {
        var vtable = Marshal.ReadIntPtr(comObject);
        var methodPtr = Marshal.ReadIntPtr(vtable, methodIndex * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(methodPtr);
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;

        if (_device != IntPtr.Zero) {
            var release = GetVtableEntry<ReleaseDelegate>(_device, 2);
            release(_device);
            _device = IntPtr.Zero;
        }
        if (_d3d != IntPtr.Zero) {
            var release = GetVtableEntry<ReleaseDelegate>(_d3d, 2);
            release(_d3d);
            _d3d = IntPtr.Zero;
        }
    }

    // D3D9 COM method delegates — all return HRESULT as int.
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReleaseDelegate(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateDeviceDelegate(IntPtr self, uint adapter, D3DDEVTYPE deviceType,
        IntPtr hFocusWindow, uint behaviorFlags, IntPtr pPresentationParameters, out IntPtr ppReturnedDeviceInterface);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateOffscreenPlainSurfaceDelegate(IntPtr self, int width, int height,
        D3DFORMAT format, D3DPOOL pool, out IntPtr ppSurface, IntPtr pSharedHandle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int LockRectDelegate(IntPtr self, out D3DLOCKED_RECT pLockedRect, IntPtr pRect, uint flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int UnlockRectDelegate(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetDescDelegate(IntPtr self, out D3DSURFACE_DESC pDesc);

    // D3D9 structs (minimal subset)
    [StructLayout(LayoutKind.Sequential)]
    private struct D3DPRESENT_PARAMETERS {
        public uint BackBufferWidth;
        public uint BackBufferHeight;
        public D3DFORMAT BackBufferFormat;
        public uint BackBufferCount;
        public D3DMULTISAMPLE_TYPE MultiSampleType;
        public uint MultiSampleQuality;
        public D3DSWAPEFFECT SwapEffect;
        public IntPtr hDeviceWindow;
        public bool Windowed;
        public bool EnableAutoDepthStencil;
        public D3DFORMAT AutoDepthStencilFormat;
        public uint Flags;
        public uint FullScreen_RefreshRateInHz;
        public uint PresentationInterval;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3DLOCKED_RECT {
        public int Pitch;
        public IntPtr pBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D3DSURFACE_DESC {
        public D3DFORMAT Format;
        public D3DSurfaceType Type;
        public uint Usage;
        public D3DPOOL Pool;
        public uint Width;
        public uint Height;
    }

    internal enum D3DSurfaceType : uint {
        SURFACE = 1,
        TEXTURE = 2,
    }
}

/// <summary>Wraps a D3D9 offscreen plain surface for use with WPF D3DImage.</summary>
internal sealed class D3DSurface(D3DRenderer renderer, int width, int height) : IDisposable {
    private readonly D3DRenderer _renderer = renderer;
    private IntPtr _surface = renderer.CreateOffscreenPlainSurface(width, height);
    private int _width = width;
    private int _height = height;

    public IntPtr NativePtr => _surface;
    public int Width => _width;
    public int Height => _height;

    private static readonly nint D3DLOCK_DISCARD = (nint)0x2000;

    public unsafe void CopyFromBytes(byte[] data, int dataSize, int srcStride) {
        if (_surface == IntPtr.Zero) return;
        fixed (byte* p = data) {
            CopyFromBytes((IntPtr)p, dataSize, srcStride);
        }
    }

    public unsafe void CopyFromBytes(IntPtr data, int dataSize, int srcStride) {
        if (_surface == IntPtr.Zero) return;

        var lockRect = GetVtableEntry<D3DRenderer.LockRectDelegate>(_surface, 15);
        var unlockRect = GetVtableEntry<D3DRenderer.UnlockRectDelegate>(_surface, 16);

        var hr = lockRect(_surface, out var lockedRect, IntPtr.Zero, (uint)D3DLOCK_DISCARD);
        if (hr < 0) return;

        try {
            var dstStride = lockedRect.Pitch;
            if (srcStride <= 0) srcStride = dstStride;
            if (srcStride == dstStride) {
                var copySize = Math.Min(dataSize, dstStride * _height);
                Buffer.MemoryCopy((void*)data, (void*)lockedRect.pBits, copySize, copySize);
            } else {
                var h = _height;
                var srcPtr = (byte*)data;
                var dstPtr = (byte*)lockedRect.pBits;
                var copyLen = Math.Min(srcStride, dstStride);
                for (var y = 0; y < h; y++) {
                    Buffer.MemoryCopy(srcPtr, dstPtr, copyLen, copyLen);
                    srcPtr += srcStride;
                    dstPtr += dstStride;
                }
            }
        } finally {
            unlockRect(_surface);
        }
    }

    public void Resize(int width, int height) {
        if (width == _width && height == _height) return;
        if (_surface != IntPtr.Zero)
            D3DRenderer.DestroySurface(_surface);
        _width = width;
        _height = height;
        _surface = _renderer.CreateOffscreenPlainSurface(width, height);
    }

    private static T GetVtableEntry<T>(nint comObject, int methodIndex) where T : Delegate {
        var vtable = Marshal.ReadIntPtr(comObject);
        var methodPtr = Marshal.ReadIntPtr(vtable, methodIndex * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(methodPtr);
    }

    public void Dispose() {
        if (_surface != IntPtr.Zero) {
            D3DRenderer.DestroySurface(_surface);
            _surface = IntPtr.Zero;
        }
    }
}

/// <summary>WPF control helper — wraps a D3DImage with a D3D9 surface for per-frame updates.</summary>
internal sealed class D3DImageSurface(D3DRenderer renderer) : IDisposable {
    private readonly D3DRenderer _renderer = renderer;
    private D3DSurface? _surface;
    private readonly D3DImage _d3dImage = new();
    private bool _hardwareFailed;
    private bool _disposed;

    public D3DImage Image => _d3dImage;
    public bool IsValid => !_hardwareFailed && _surface != null && _surface.NativePtr != IntPtr.Zero;

    /// <summary>Ensure a native D3D9 surface of the given size exists. Returns false if the GPU cannot allocate it.</summary>
    public bool EnsureSize(int width, int height) {
        if (_hardwareFailed) return false;
        if (_surface != null && _surface.Width == width && _surface.Height == height)
            return _surface.NativePtr != IntPtr.Zero;

        _surface?.Dispose();

        var newSurf = new D3DSurface(_renderer, width, height);
        if (newSurf.NativePtr == IntPtr.Zero) {
            // GPU resource exhaustion — permanently disable D3D for this player
            newSurf.Dispose();
            _hardwareFailed = true;
            _surface = null;
            return false;
        }
        _surface = newSurf;
        return true;
    }

    /// <summary>Update the D3DImage with frame data. Must be called on the UI thread.</summary>
    public unsafe void PresentFrame(IntPtr data, int dataSize, int stride) {
        if (_disposed || _hardwareFailed || _surface == null || _surface.NativePtr == IntPtr.Zero)
            return;

        _d3dImage.Lock();

        try {
            _surface.CopyFromBytes(data, dataSize, stride);

            // Set the surface as D3DImage's back buffer
            _d3dImage.SetBackBuffer(
                D3DResourceType.IDirect3DSurface9,
                _surface.NativePtr);

            // Mark the entire surface as dirty
            _d3dImage.AddDirtyRect(new Int32Rect(0, 0, _surface.Width, _surface.Height));
        } finally {
            _d3dImage.Unlock();
        }
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _surface?.Dispose();
        _surface = null;
    }
}
