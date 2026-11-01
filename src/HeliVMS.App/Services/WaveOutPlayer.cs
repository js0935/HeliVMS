using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using HeliVMS.Media;

namespace HeliVMS.App.Services;

/// <summary>
/// 用 Windows <c>winmm</c>（<c>waveOut*</c>）把 PCM 片段播出來。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼不用第三方音訊庫：這個頁面（SPA 以外）出貨時零外部相依，桌面端也不引入新套件；
/// <c>waveOut</c> 是 Windows 內建、對「單聲道語音」足夠且不需要 COM 初始化。
/// </para>
/// <para>
/// 為什麼播放留在 App 而不在 <c>HeliVMS.Media</c>：Media 專案要能在沒有音效卡的 CI 上
/// 跑測試，把 <c>winmm</c> 的 P/Invoke 放進去等於讓整個模組綁死 Windows。
/// </para>
/// <para>
/// 失敗一律「安靜地不播」並寫 <c>%TEMP%\helivms-audio.log</c>：現場常是遠端桌面
/// （沒有音訊裝置）或音效卡被其他程式獨佔。聽不到聲音不該讓監看牆或回放跟著壞掉，
/// 但也不能完全沒有痕跡——否則「按了沒反應」只能靠猜。
/// </para>
/// </remarks>
public sealed class WaveOutPlayer : IDisposable
{
    /// <summary>WAVE_MAPPER：交由系統挑預設播放裝置。</summary>
    private const uint WaveMapper = 0xFFFFFFFF;

    /// <summary><c>WAVEHDR.dwFlags</c>：這塊緩衝已播完，可以回收。</summary>
    private const uint WaveHeaderDone = 0x00000001;

    /// <summary>同時在佇列中的緩衝數：兩塊就夠連續播放，三塊留有排程抖動的餘裕。</summary>
    private const int BufferCount = 3;

    /// <summary>沒有資料時播放執行緒的等待上限（避免空轉，又能在停止時快速收尾）。</summary>
    private static readonly TimeSpan IdleWait = TimeSpan.FromMilliseconds(20);

    private static readonly object ProbeGate = new();
    private static int? _probedRate;

    private readonly PcmChunkQueue _queue;
    private readonly int _maxChunkBytes;
    private readonly object _gate = new();
    private Thread? _thread;
    private IntPtr _device;
    private volatile bool _stop;

    /// <param name="sampleRate">裝置取樣率；必須等於送進來的 PCM 取樣率（不符會忽快忽慢）。</param>
    /// <param name="queue">片段來源；null＝自行建立預設容量的佇列。</param>
    public WaveOutPlayer(int sampleRate, PcmChunkQueue? queue = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);

        SampleRate = sampleRate;
        _queue = queue ?? new PcmChunkQueue();
        // 一段 100 ms 的位元組數；留兩倍餘裕給取樣率換算與最後一段，緩衝區就永遠夠放。
        _maxChunkBytes = Math.Max(2048, sampleRate * 2 / 5);
    }

    /// <summary>裝置取樣率（單聲道、16 bit）。</summary>
    public int SampleRate { get; }

    /// <summary>片段佇列（呼叫端把 PCM 放進來就會播）。</summary>
    public PcmChunkQueue Queue => _queue;

    /// <summary>裝置是否已開啟；false 時所有寫入都會被忽略。</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>最近一次失敗的原因（給診斷頁與日誌）；成功時為 null。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 依序試 16 kHz、44.1 kHz、48 kHz、8 kHz，回傳第一個開得起來的取樣率。
    /// </summary>
    /// <remarks>
    /// 16 kHz 優先：語音用途已經足夠，資料量只有 44.1 kHz 立體聲的十分之一（同時間的
    /// 音訊 ffmpeg 行程也輕得多）。都開不起來時回 null 並寫日誌——遠端桌面與無音效卡
    /// 的機器是常態，不是錯誤。
    /// </remarks>
    public static int? ProbeSampleRate()
    {
        lock (ProbeGate)
        {
            if (_probedRate is { } cached)
            {
                return cached;
            }

            var failures = new List<string>();
            foreach (var rate in new[] { 16000, 44100, 48000, 8000 })
            {
                var result = TryOpen(rate, out var handle);
                if (result == 0)
                {
                    CloseQuietly(handle);
                    _probedRate = rate;
                    return rate;
                }

                failures.Add($"{rate}Hz={ErrorText(result)}");
            }

            Log("沒有可用的音訊播放裝置：" + string.Join(", ", failures));
            return null;
        }
    }

    /// <summary>開啟裝置並啟動播放執行緒；失敗不丟例外（音訊是加值功能）。</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_thread is not null)
            {
                return;
            }

            var result = TryOpen(SampleRate, out var handle);
            if (result != 0)
            {
                LastError = ErrorText(result);
                IsAvailable = false;
                Log($"waveOutOpen({SampleRate}Hz) 失敗：{LastError}");
                return;
            }

            _device = handle;
            IsAvailable = true;
            _stop = false;
            _thread = new Thread(Run) { IsBackground = true, Name = "helivms-audio-out" };
            _thread.Start();
        }
    }

    /// <summary>停止播放並關閉裝置；返回後可以再次 <see cref="Start"/>。</summary>
    public void Stop()
    {
        Thread? thread;
        lock (_gate)
        {
            _stop = true;
            thread = _thread;
        }

        if (thread is null)
        {
            return;
        }

        try
        {
            // 立刻讓正在播的緩衝回來（否則要等它播完，最多 100 ms）。
            if (_device != IntPtr.Zero)
            {
                WaveOutReset(_device);
            }
        }
        catch (Exception)
        {
            // 裝置已消失；執行緒會自己收尾。
        }

        thread.Join(TimeSpan.FromSeconds(2));

        lock (_gate)
        {
            if (!thread.IsAlive)
            {
                _thread = null;
            }
        }

        IsAvailable = false;
        _queue.Clear();
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    private void Run()
    {
        var headers = Marshal.AllocHGlobal(HeaderSize * BufferCount);
        var buffers = Marshal.AllocHGlobal(_maxChunkBytes * BufferCount);
        var inFlight = new bool[BufferCount];

        for (var i = 0; i < BufferCount; i++)
        {
            Marshal.StructureToPtr(default(WaveHeader), HeaderPtr(headers, i), false);
        }

        try
        {
            while (!_stop)
            {
                Reap(headers, inFlight);

                var free = Array.IndexOf(inFlight, false);
                if (free >= 0 && _queue.TryDequeue(out var chunk, IdleWait))
                {
                    Write(headers, buffers, inFlight, free, chunk);
                }
                else if (free < 0)
                {
                    // 全部在播：等一小段再回收，不必忙等。
                    Thread.Sleep(5);
                }
            }
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log("播放執行緒結束：" + ex.Message);
        }
        finally
        {
            WaveOutReset(_device);
            for (var i = 0; i < BufferCount; i++)
            {
                if (inFlight[i])
                {
                    WaveOutUnprepareHeader(_device, HeaderPtr(headers, i), HeaderSize);
                }
            }

            CloseQuietly(_device);
            _device = IntPtr.Zero;
            Marshal.FreeHGlobal(headers);
            Marshal.FreeHGlobal(buffers);
            IsAvailable = false;
        }
    }

    private void Write(IntPtr headers, IntPtr buffers, bool[] inFlight, int index, byte[] chunk)
    {
        if (chunk.Length > _maxChunkBytes)
        {
            // 不該發生（片段大小由取樣率決定）；截斷也比讓 Marshal.Copy 覆寫下一塊好。
            Array.Resize(ref chunk, _maxChunkBytes);
        }

        var data = buffers + (index * _maxChunkBytes);
        Marshal.Copy(chunk, 0, data, chunk.Length);

        var header = new WaveHeader
        {
            lpData = data,
            dwBufferLength = (uint)chunk.Length,
            dwFlags = 0,
        };
        var ptr = HeaderPtr(headers, index);
        Marshal.StructureToPtr(header, ptr, false);

        var result = WaveOutPrepareHeader(_device, ptr, HeaderSize);
        if (result != 0)
        {
            LastError = ErrorText(result);
            return;
        }

        result = WaveOutWrite(_device, ptr, HeaderSize);
        if (result != 0)
        {
            WaveOutUnprepareHeader(_device, ptr, HeaderSize);
            LastError = ErrorText(result);
            return;
        }

        inFlight[index] = true;
    }

    private void Reap(IntPtr headers, bool[] inFlight)
    {
        for (var i = 0; i < BufferCount; i++)
        {
            if (!inFlight[i])
            {
                continue;
            }

            var ptr = HeaderPtr(headers, i);
            var header = Marshal.PtrToStructure<WaveHeader>(ptr);
            if ((header.dwFlags & WaveHeaderDone) == 0)
            {
                continue;
            }

            WaveOutUnprepareHeader(_device, ptr, HeaderSize);
            inFlight[i] = false;
        }
    }

    private static IntPtr HeaderPtr(IntPtr headers, int index) => headers + (index * HeaderSize);

    private static int HeaderSize => Marshal.SizeOf<WaveHeader>();

    private static uint TryOpen(int sampleRate, out IntPtr handle)
    {
        var format = new WaveFormat
        {
            wFormatTag = 1,                                  // WAVE_FORMAT_PCM
            nChannels = 1,
            nSamplesPerSec = (uint)sampleRate,
            nAvgBytesPerSec = (uint)(sampleRate * 2),        // 單聲道 × 16 bit
            nBlockAlign = 2,
            wBitsPerSample = 16,
            cbSize = 0,
        };

        return WaveOutOpen(out handle, WaveMapper, ref format, IntPtr.Zero, IntPtr.Zero, 0);
    }

    private static void CloseQuietly(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            WaveOutClose(handle);
        }
        catch (Exception)
        {
            // 裝置已消失。
        }
    }

    private static string ErrorText(uint code)
    {
        var text = new StringBuilder(256);
        try
        {
            return WaveOutGetErrorText(code, text, text.Capacity) == 0
                ? $"{text} (0x{code:X})"
                : $"MMSYSERR 0x{code:X}";
        }
        catch (Exception)
        {
            return $"MMSYSERR 0x{code:X}";
        }
    }

    private static void Log(string message)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "helivms-audio.log");
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // 日誌寫不進去不該影響播放。
        }
    }

    /// <summary>winmm 的 <c>WAVEFORMATEX</c>（mmreg.h 以 1 byte 對齊包裝）。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormat
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    /// <summary>winmm 的 <c>WAVEHDR</c>。</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveHeader
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [DllImport("winmm.dll", EntryPoint = "waveOutOpen")]
    private static extern uint WaveOutOpen(
        out IntPtr hWaveOut, uint uDeviceID, ref WaveFormat lpFormat,
        IntPtr dwCallback, IntPtr dwInstance, uint dwFlags);

    [DllImport("winmm.dll", EntryPoint = "waveOutClose")]
    private static extern uint WaveOutClose(IntPtr hwo);

    [DllImport("winmm.dll", EntryPoint = "waveOutPrepareHeader")]
    private static extern uint WaveOutPrepareHeader(IntPtr hwo, IntPtr pwhdr, int cbwhdr);

    [DllImport("winmm.dll", EntryPoint = "waveOutUnprepareHeader")]
    private static extern uint WaveOutUnprepareHeader(IntPtr hwo, IntPtr pwhdr, int cbwhdr);

    [DllImport("winmm.dll", EntryPoint = "waveOutWrite")]
    private static extern uint WaveOutWrite(IntPtr hwo, IntPtr pwhdr, int cbwhdr);

    [DllImport("winmm.dll", EntryPoint = "waveOutReset")]
    private static extern uint WaveOutReset(IntPtr hwo);

    [DllImport("winmm.dll", EntryPoint = "waveOutGetErrorText")]
    private static extern uint WaveOutGetErrorText(uint mmrError, StringBuilder pszText, int cchText);
}
