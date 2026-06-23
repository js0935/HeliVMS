// Direct3D9 swap-chain video renderer via HwndHost.
// Single shared D3D9 device with per-HwndHost swap chains.
// Underlying pixel data arrives as IntPtr (native memory, zero-copy pipeline).

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HeliVMS.Controls;

internal unsafe sealed class D3D9SwapChain : IDisposable {
    private IntPtr _device;
    private IntPtr _swapChain;
    private bool _disposed;
    public int Width { get; private set; }
    public int Height { get; private set; }

    private delegate int CreateAdditionalSwapChainDelegate(IntPtr self, void* pParams, out IntPtr ppSwapChain);
    private delegate int GetBackBufferDelegate(IntPtr self, int index, uint type, out IntPtr ppSurface);
    private delegate int PresentDelegate(IntPtr self, void* srcRect, void* destRect, IntPtr destWindow, void* dirtyRegion);
    private delegate int LockRectDelegate(IntPtr self, out D3DLOCKED_RECT pLockedRect, void* pRect, uint flags);
    private delegate int UnlockRectDelegate(IntPtr self);
    private delegate int ReleaseDelegate(IntPtr self);

#pragma warning disable CS0649
    private struct D3DLOCKED_RECT { public int Pitch; public IntPtr pBits; }
#pragma warning restore CS0649

    private static T Vtbl<T>(IntPtr obj, int idx) where T : Delegate {
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), idx * IntPtr.Size));
    }

    public D3D9SwapChain(IntPtr device, IntPtr hwnd, int width, int height) {
        _device = device;
        Width = width;
        Height = height;

        var pp = stackalloc int[14];
        pp[0] = width;               // BackBufferWidth
        pp[1] = height;              // BackBufferHeight
        pp[2] = 21;                  // BackBufferFormat = D3DFMT_X8R8G8B8
        pp[3] = 1;                   // BackBufferCount
        pp[4] = 0;                   // MultiSampleType
        pp[5] = 0;                   // MultiSampleQuality
        pp[6] = 1;                   // SwapEffect = D3DSWAPEFFECT_DISCARD
        pp[7] = hwnd.ToInt32();      // hDeviceWindow
        pp[8] = 1;                   // Windowed = true
        pp[9] = 0;                   // EnableAutoDepthStencil
        pp[10] = 0;                  // AutoDepthStencilFormat
        pp[11] = 0;                  // Flags
        pp[12] = 0;                  // FullScreen_RefreshRateInHz
        pp[13] = unchecked((int)0x80000000); // PresentationInterval = D3DPRESENT_INTERVAL_IMMEDIATE

        var create = Vtbl<CreateAdditionalSwapChainDelegate>(device, 16);
        var hr = create(device, pp, out _swapChain);
        if (hr < 0 || _swapChain == IntPtr.Zero)
            throw new InvalidOperationException($"CreateAdditionalSwapChain: 0x{hr:X8}");
    }

    public void PresentFrame(IntPtr data, int dataSize, int stride) {
        if (_disposed || _swapChain == IntPtr.Zero) return;

        var getBackBuf = Vtbl<GetBackBufferDelegate>(_swapChain, 8);
        var present = Vtbl<PresentDelegate>(_swapChain, 9);

        if (getBackBuf(_swapChain, 0, 0, out var backBuf) < 0 || backBuf == IntPtr.Zero) return;

        var lockRect = Vtbl<LockRectDelegate>(backBuf, 15);
        var unlockRect = Vtbl<UnlockRectDelegate>(backBuf, 16);
        var release = Vtbl<ReleaseDelegate>(backBuf, 2);

        try {
            if (lockRect(backBuf, out var locked, null, 0x2000) >= 0) {
                try {
                    var srcStride = stride;
                    var dstStride = locked.Pitch;
                    var h = Height;

                    if (srcStride == dstStride) {
                        Buffer.MemoryCopy((void*)data, (void*)locked.pBits,
                            dstStride * h, Math.Min(dataSize, dstStride * h));
                    } else {
                        var src = (byte*)data;
                        var dst = (byte*)locked.pBits;
                        var copyLen = Math.Min(srcStride, dstStride);
                        for (var y = 0; y < h; y++) {
                            Buffer.MemoryCopy(src, dst, copyLen, copyLen);
                            src += srcStride;
                            dst += dstStride;
                        }
                    }
                } finally { unlockRect(backBuf); }
            }
            present(_swapChain, null, null, IntPtr.Zero, null);
        } finally { release(backBuf); }
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        if (_swapChain != IntPtr.Zero) {
            Vtbl<ReleaseDelegate>(_swapChain, 2)(_swapChain);
            _swapChain = IntPtr.Zero;
        }
        _device = IntPtr.Zero;
    }
}

internal sealed unsafe class D3D9DeviceManager : IDisposable {
    private static D3D9DeviceManager? _instance;
    private static readonly Lock _lock = new();
    private IntPtr _d3d;
    private IntPtr _device;
    private int _refCount;
    private bool _disposed;

    public IntPtr Device => _device;

    private D3D9DeviceManager() { }

    public static D3D9DeviceManager Instance {
        get { lock (_lock) { _instance ??= new D3D9DeviceManager(); return _instance; } }
    }

    public bool TryAcquire() {
        lock (_lock) {
            if (_disposed) return false;
            if (_device == IntPtr.Zero && !Initialize()) return false;
            _refCount++;
            return true;
        }
    }

    public void Release() {
        lock (_lock) {
            _refCount--;
            if (_refCount <= 0) Dispose();
        }
    }

    private bool Initialize() {
        var hMod = NativeMethods.LoadLibrary("d3d9.dll");
        if (hMod == IntPtr.Zero) return false;

        var createFn = NativeMethods.GetProcAddress(hMod, "Direct3DCreate9");
        if (createFn == IntPtr.Zero) return false;

        var create9 = Marshal.GetDelegateForFunctionPointer<Direct3DCreate9>(createFn);
        _d3d = create9(32);
        if (_d3d == IntPtr.Zero) return false;

        var pp = stackalloc int[14];
        pp[0] = 1;                   // BackBufferWidth
        pp[1] = 1;                   // BackBufferHeight
        pp[2] = 21;                  // BackBufferFormat
        pp[3] = 1;                   // BackBufferCount
        pp[4] = 0; pp[5] = 0;       // MultiSample
        pp[6] = 1;                   // SwapEffect = DISCARD
        pp[7] = 0;                   // hDeviceWindow = null
        pp[8] = 1;                   // Windowed
        pp[9] = 0; pp[10] = 0;      // AutoDepthStencil
        pp[11] = 0;                  // Flags
        pp[12] = 0;                  // RefreshRate
        pp[13] = unchecked((int)0x80000000); // PresentInterval

        var vtbl = Marshal.ReadIntPtr(_d3d);
        var createDev = Marshal.GetDelegateForFunctionPointer<CreateDevice>(
            Marshal.ReadIntPtr(vtbl, 16 * IntPtr.Size));
        var hr = createDev(_d3d, 0, 1, pp, 0x40 | 0x20, pp, out _device);
        return hr >= 0 && _device != IntPtr.Zero;
    }

    private delegate int Direct3DCreate9(int sdkVersion);
    private delegate int CreateDevice(IntPtr self, uint adapter, uint deviceType,
        void* pParams, uint behaviorFlags, void* pParams2, out IntPtr ppDevice);
    private delegate int ReleaseDelegate(IntPtr self);

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        if (_device != IntPtr.Zero) {
            var vtbl = Marshal.ReadIntPtr(_device);
            var release = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(
                Marshal.ReadIntPtr(vtbl, 2 * IntPtr.Size));
            release(_device);
            _device = IntPtr.Zero;
        }
        if (_d3d != IntPtr.Zero) {
            var vtbl = Marshal.ReadIntPtr(_d3d);
            var release = Marshal.GetDelegateForFunctionPointer<ReleaseDelegate>(
                Marshal.ReadIntPtr(vtbl, 2 * IntPtr.Size));
            release(_d3d);
            _d3d = IntPtr.Zero;
        }
    }
}

/// <summary>HwndHost that renders video frames via Direct3D9 swap chain, bypassing WPF compositor.</summary>
public sealed unsafe class Direct3DVideoHost : HwndHost {
    private D3D9SwapChain? _swapChain;
    private bool _d3dAcquired;
    private bool _disposed = false;

    public static bool IsSupported {
        get {
            try { return D3D9DeviceManager.Instance.TryAcquire(); }
            catch { return false; }
            finally { D3D9DeviceManager.Instance.Release(); }
        }
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent) {
        var hwnd = NativeMethods.CreateWindowEx(0, "static", "",
            0x40000000 | 0x10000000 | 0x100,
            0, 0, 1, 1, hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return new HandleRef(this, hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd) {
        _swapChain?.Dispose();
        _swapChain = null;
        if (_d3dAcquired) { D3D9DeviceManager.Instance.Release(); _d3dAcquired = false; }
        if (hwnd.Handle != IntPtr.Zero) NativeMethods.DestroyWindow(hwnd.Handle);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo) {
        base.OnRenderSizeChanged(sizeInfo);
        // Drop swap chain so PresentFrame recreates at new size
        if (_swapChain != null) { _swapChain.Dispose(); _swapChain = null; }
    }

    public void PresentFrame(IntPtr data, int dataSize, int stride, int width, int height) {
        if (_disposed) return;

        if (_swapChain == null) {
            if (!_d3dAcquired) {
                _d3dAcquired = D3D9DeviceManager.Instance.TryAcquire();
                if (!_d3dAcquired) return;
            }
            try { _swapChain = new D3D9SwapChain(D3D9DeviceManager.Instance.Device, Handle, width, height); }
            catch { return; }
        }
        _swapChain.PresentFrame(data, dataSize, stride);
    }
}

internal static partial class NativeMethods {
    [LibraryImport("kernel32", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr LoadLibrary(string lpFileName);

    [LibraryImport("kernel32", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [LibraryImport("user32", StringMarshalling = StringMarshalling.Utf8)]
    public static partial IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName,
        int dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent,
        IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [LibraryImport("user32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(IntPtr hWnd);
}
