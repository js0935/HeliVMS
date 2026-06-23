using HeliVMS.Controls;

namespace HeliVMS.Services;

/// <summary>
/// 播放解碼器介面 — 支援兩種實作：<br/>
/// 1. DecoderSession：透過 Named Pipe 與外部解碼程序 (HeliVMS.Decoder.exe) 通訊<br/>
/// 2. InProcessPlaybackDecoder：在同行程直接使用 FFmpeg.AutoGen 解碼 (無外部程序)
/// </summary>
public interface IPlaybackDecoder : IDisposable {
    /// <summary>攝影機編號</summary>
    string CameraId { get; }
    /// <summary>目前串流總時長（微秒）</summary>
    long DurationMicroseconds { get; }
    /// <summary>解碼執行緒是否正在運作</summary>
    bool IsRunning { get; }

    /// <summary>解出一幀畫面，交予協調器進行 PTS 同步後顯示</summary>
    event Action<PooledBuffer>? FrameReady;
    /// <summary>播放位置變更（參數：目前位置微秒, 總時長微秒）</summary>
    event Action<long, long>? PositionChanged;
    /// <summary>播放/暫停狀態變更</summary>
    event Action<bool>? PlaybackStatusChanged;
    /// <summary>檔案播放完畢</summary>
    event Action? EOFReached;

    /// <summary>開啟指定檔案並開始解碼</summary>
    void Open(string filePath, long seekUs = 0, double? targetFps = null, int targetDecodeHeight = 0);
    /// <summary>跳至指定微秒位置</summary>
    void Seek(long microseconds);
    /// <summary>暫停解碼</summary>
    void Pause();
    /// <summary>繼續解碼</summary>
    void Resume();
    /// <summary>停止解碼並釋放資源</summary>
    void Stop();
    /// <summary>設定播放速率（0.25x ~ 32x）</summary>
    void SetPlaybackRate(double rate);
    /// <summary>設定目標輸出幀率上限（用於多路時降低 CPU 負載）</summary>
    void SetTargetFps(double fps);
}
