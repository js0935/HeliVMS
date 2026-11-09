using HeliVMS.Media;
using HeliVMS.Shared;

namespace HeliVMS.App.Services;

/// <summary>
/// 把「接收到的 PCM 片段」播出來：取樣率跟裝置對齊、片段進有界佇列、停止時把兩邊都收乾淨。
/// </summary>
/// <remarks>
/// <para>
/// 取樣率由裝置決定（<see cref="WaveOutPlayer.ProbeSampleRate"/>），而 ffmpeg 的 <c>-ar</c>
/// 在行程啟動前就寫定了同一個值（<see cref="ChannelManager"/> 建立工作階段時傳入）——兩邊不一致
/// 的症狀是聲音忽快忽慢、音調偏移，而畫面完全正常，現場只會被描述成「聲音怪怪的」。
/// </para>
/// <para>
/// 沒有音訊裝置（遠端桌面、音效卡被獨佔）不算錯誤：<see cref="StartAsync"/> 回 false，
/// 呼叫端把圖示退回未按即可，不必跳錯誤對話框。裝置層級的原因寫在
/// <c>%TEMP%\helivms-audio.log</c>。
/// </para>
/// </remarks>
public sealed class AudioOutput : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WaveOutPlayer? _player;
    private AudioStreamReceiver? _receiver;
    private EventHandler<byte[]>? _pump;
    private int _generation;
    private int _volumePercent = AudioVolume.Default;
    private bool _disposed;

    /// <summary>是否正在播放。</summary>
    public bool IsPlaying => _player is not null;

    /// <summary>實際協商出的取樣率；尚未播放過時為 null。</summary>
    public int? SampleRate { get; private set; }

    /// <summary>最近一次失敗的原因（沒有裝置、裝置開不起來）。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 設定收音音量（0–100；QC_Live 的 CameraVolume 對齊）。正在播時立即套用，
    /// 沒在播時記住，下一次 <see cref="StartAsync"/> 套上。
    /// </summary>
    public void SetVolume(int percent)
    {
        _volumePercent = AudioVolume.Clamp(percent);
        _player?.SetVolume(_volumePercent);
    }

    /// <summary>開始播放某個接收器的片段；返回 false＝沒有音訊裝置。</summary>
    public async Task<bool> StartAsync(AudioStreamReceiver receiver, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(receiver);

        await StopAsync().ConfigureAwait(true);

        if (WaveOutPlayer.ProbeSampleRate() is not { } rate)
        {
            LastError = "no-audio-device";
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var player = new WaveOutPlayer(rate, volumePercent: AudioVolume.Clamp(_volumePercent));
        player.Start();
        if (!player.IsAvailable)
        {
            LastError = player.LastError ?? "no-audio-device";
            player.Dispose();
            return false;
        }

        var generation = Interlocked.Increment(ref _generation);
        EventHandler<byte[]> pump = (_, chunk) =>
        {
            // 換來源／停止之後，舊接收器可能還有一兩個片段在路上：丟掉，
            // 否則會播出上一台攝影機的聲音。
            if (Volatile.Read(ref _generation) == generation)
            {
                player.Queue.Enqueue(chunk);
            }
        };
        receiver.ChunkReady += pump;

        SampleRate = rate;
        _player = player;
        _receiver = receiver;
        _pump = pump;

        receiver.Start();
        return true;
    }

    /// <summary>停止播放（接收器回到「綁著但沒人讀」的狀態）。</summary>
    public async Task StopAsync()
    {
        Interlocked.Increment(ref _generation);

        var player = _player;
        var receiver = _receiver;
        var pump = _pump;
        _player = null;
        _receiver = null;
        _pump = null;

        if (player is null && receiver is null)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (receiver is not null && pump is not null)
            {
                receiver.ChunkReady -= pump;
            }

            try
            {
                receiver?.Stop();
            }
            catch (ObjectDisposedException)
            {
                // 工作階段已釋放；接收器跟著沒了，不必再停。
            }

            if (player is not null)
            {
                // 停止會 Join 播放執行緒（上限 2 秒）；丟到執行緒池，UI 執行緒才不會被卡住。
                await Task.Run(player.Dispose).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}
