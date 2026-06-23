using System.IO;
using System.Runtime.InteropServices;

namespace HeliVMS.Services;

public sealed class AudioTalkService : IAudioTalkService, IDisposable {
    private IntPtr _waveInHandle;
    private IntPtr _bufferHeader;
    private byte[]? _buffer;
    private GCHandle _bufferGcHandle;
    private bool _isTalking;
    private string? _currentCameraId;
    private string _outputDir;
    private FileStream? _outputStream;
    private int _recordedBytes;

    public bool IsTalking => _isTalking;
    public event Action<float>? AudioLevelChanged;

    private const int SampleRate = 8000;
    private const int BitsPerSample = 16;
    private const int Channels = 1;
    private const int BufferSize = 4096;

    private static readonly Guid GuidMicArray = new("77A0A0F8-5E7B-4B43-89E9-A3C0B3C8A6A7");

    public AudioTalkService() {
        _outputDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "AudioTalk");
        Directory.CreateDirectory(_outputDir);
    }

    public bool StartTalking(string cameraId) {
        if (_isTalking) StopTalking();
        _currentCameraId = cameraId;

        var fmt = new WaveFormat {
            wFormatTag = 1,
            nChannels = Channels,
            nSamplesPerSec = SampleRate,
            wBitsPerSample = BitsPerSample,
            nBlockAlign = (short)(Channels * BitsPerSample / 8),
            nAvgBytesPerSec = SampleRate * Channels * BitsPerSample / 8,
            cbSize = 0,
        };

        var result = waveInOpen(out _waveInHandle, -1, ref fmt, IntPtr.Zero, 0, 1);
        if (result != MmResult.NoError) {
            Serilog.Log.Debug("[AudioTalk] waveInOpen failed: {Result}", result);
            return false;
        }

        _buffer = new byte[BufferSize];
        _bufferGcHandle = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
        _bufferHeader = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHdr>());

        var header = new WaveHdr {
            lpData = _bufferGcHandle.AddrOfPinnedObject(),
            dwBufferLength = BufferSize,
            dwBytesRecorded = 0,
            dwUser = IntPtr.Zero,
            dwFlags = 0,
            dwLoops = 0,
            lpNext = IntPtr.Zero,
            reserved = IntPtr.Zero,
        };
        Marshal.StructureToPtr(header, _bufferHeader, false);

        waveInPrepareHeader(_waveInHandle, _bufferHeader, Marshal.SizeOf<WaveHdr>());
        waveInAddBuffer(_waveInHandle, _bufferHeader, Marshal.SizeOf<WaveHdr>());

        var outputPath = Path.Combine(_outputDir, $"{cameraId}_{DateTime.Now:yyyyMMdd_HHmmss}.raw");
        _outputStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        _recordedBytes = 0;

        result = waveInStart(_waveInHandle);
        if (result != MmResult.NoError) {
            Cleanup();
            return false;
        }

        _isTalking = true;
        _ = Task.Run(() => CaptureLoop());
        return true;
    }

    public void StopTalking() {
        if (!_isTalking) return;
        _isTalking = false;
        Cleanup();
    }

    private async Task CaptureLoop() {
        while (_isTalking) {
            try {
                await Task.Delay(100).ConfigureAwait(false);
                if (!_isTalking || _waveInHandle == IntPtr.Zero) break;

                var header = Marshal.PtrToStructure<WaveHdr>(_bufferHeader);

                if (header.dwBytesRecorded > 0) {
                    var bytesToRead = (int)header.dwBytesRecorded;
                    _outputStream?.Write(_buffer!, 0, bytesToRead);
                    _recordedBytes += bytesToRead;

                    float level = 0;
                    for (var i = 0; i < bytesToRead - 1; i += 2) {
                        var sample = (short)(_buffer![i] | (_buffer[i + 1] << 8));
                        var abs = Math.Abs(sample / 32768f);
                        if (abs > level) level = abs;
                    }
                    AudioLevelChanged?.Invoke(level);
                }

                header.dwBytesRecorded = 0;
                header.dwFlags = 0;
                Marshal.StructureToPtr(header, _bufferHeader, false);

                waveInPrepareHeader(_waveInHandle, _bufferHeader, Marshal.SizeOf<WaveHdr>());
                waveInAddBuffer(_waveInHandle, _bufferHeader, Marshal.SizeOf<WaveHdr>());
            } catch (Exception ex) {
                Serilog.Log.Debug("[AudioTalk] CaptureLoop error: {Msg}", ex.Message);
                break;
            }
        }
    }

    private void Cleanup() {
        try {
            if (_waveInHandle != IntPtr.Zero) {
                waveInStop(_waveInHandle);
                waveInReset(_waveInHandle);
                waveInUnprepareHeader(_waveInHandle, _bufferHeader, Marshal.SizeOf<WaveHdr>());
                waveInClose(_waveInHandle);
                _waveInHandle = IntPtr.Zero;
            }
        } catch { }

        if (_bufferHeader != IntPtr.Zero) {
            Marshal.FreeHGlobal(_bufferHeader);
            _bufferHeader = IntPtr.Zero;
        }

        if (_bufferGcHandle.IsAllocated) {
            _bufferGcHandle.Free();
        }

        if (_outputStream is not null) {
            _outputStream.Dispose();
            _outputStream = null;
        }

        _buffer = null;
        _recordedBytes = 0;
        AudioLevelChanged?.Invoke(0);
    }

    public void Dispose() {
        StopTalking();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormat {
        public short wFormatTag;
        public short nChannels;
        public int nSamplesPerSec;
        public int nAvgBytesPerSec;
        public short nBlockAlign;
        public short wBitsPerSample;
        public short cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHdr {
        public IntPtr lpData;
        public int dwBufferLength;
        public int dwBytesRecorded;
        public IntPtr dwUser;
        public int dwFlags;
        public int dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    private enum MmResult : uint {
        NoError = 0,
        BadDeviceId = 2,
    }

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern MmResult waveInOpen(out IntPtr phwi, int uDeviceID, ref WaveFormat pwfx, IntPtr dwCallback, int dwCallbackInstance, int fdwOpen);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern MmResult waveInClose(IntPtr hwi);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern MmResult waveInPrepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern MmResult waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, int cbwh);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern MmResult waveInAddBuffer(IntPtr hwi, IntPtr pwh, int cbwh);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern MmResult waveInStart(IntPtr hwi);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern MmResult waveInStop(IntPtr hwi);

    [DllImport("winmm.dll", SetLastError = true)]
    private static extern MmResult waveInReset(IntPtr hwi);
}
