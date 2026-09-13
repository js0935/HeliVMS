using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Alarms;

/// <summary>
/// 運動事件引擎（§5.3）：串接 <see cref="FrameDifferenceMotionDetector"/>，
/// 以「連續偵測合成單一事件」方式去重——運動中止（冷卻窗口）超過門檻才寫一筆
/// alarm_events（motion），並以首動幀儲存 BMP 快照。斷線重連／頻道切換呼叫 <see cref="Reset"/>。
/// </summary>
public sealed class MotionEventEngine : IDisposable
{
    private const long CooldownMs = 800;
    private const long MinEventMs = 400;

    private readonly int _channelId;
    private readonly AlarmEventRepository _repo;
    private readonly string _snapshotDir;
    private readonly FrameDifferenceMotionDetector _detector;
    private readonly object _gate = new();

    private bool _armed;
    private DateTime _armedUtc;
    private DateTime _lastMotionUtc;
    private byte[] _firstMotionPixels = [];
    private int _firstMotionWidth;
    private int _firstMotionHeight;
    private bool _snapshotReady;
    private double _peakRatio;
    private VideoFrame? _lastFrame;

    public MotionEventEngine(int channelId, AlarmEventRepository repo, string snapshotDir, double sensitivity = 0.5)
    {
        _channelId = channelId;
        _repo = repo;
        _snapshotDir = snapshotDir;
        _detector = new FrameDifferenceMotionDetector(sensitivity);
    }

    /// <summary>運動觸發（rising edge）通知 UI（參數為當下變動比例）。</summary>
    public event EventHandler<double>? MotionSignal;

    /// <summary>偵測幀數（診斷用）。</summary>
    public int FramesProcessed { get; private set; }

    /// <summary>送入取樣幀（呼叫端負責抽幀頻率，如每 250ms）。</summary>
    public void OnFrame(VideoFrame frame)
    {
        DateTime nowUtc;
        double ratio;
        lock (_gate)
        {
            _lastFrame = frame;
            FramesProcessed++;
            ratio = _detector.Update(frame);
            nowUtc = DateTime.UtcNow;
            var motion = _detector.IsMotion(ratio);

            if (motion)
            {
                if (!_armed)
                {
                    _armed = true;
                    _armedUtc = nowUtc;
                    _peakRatio = ratio;
                    _firstMotionPixels = (byte[])frame.Pixels.Clone();
                    _firstMotionWidth = frame.Width;
                    _firstMotionHeight = frame.Height;
                    _snapshotReady = true;
                    MotionSignal?.Invoke(this, ratio);
                }

                _lastMotionUtc = nowUtc;
                _peakRatio = Math.Max(_peakRatio, ratio);
            }
            else if (_armed)
            {
                var sinceMotion = (nowUtc - _lastMotionUtc).TotalMilliseconds;
                if (sinceMotion >= CooldownMs)
                {
                    FinalizeLocked(nowUtc);
                }
            }
        }
    }

    /// <summary>停止前收尾：若仍在運動窗口內，立即以目前時間關閉事件。</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (_armed)
            {
                FinalizeLocked(DateTime.UtcNow);
            }
        }
    }

    /// <summary>清空狀態（頻道切換／斷線重連）；進行中的運動窗口直接捨棄，不產生事件。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _armed = false;
            _snapshotReady = false;
            _detector.Reset();
            _lastFrame = null;
        }
    }

    public void Dispose() => Flush();

    // 呼叫者須持鎖
    private void FinalizeLocked(DateTime resolvedUtc)
    {
        var duration = (resolvedUtc - _armedUtc).TotalMilliseconds;
        _armed = false;

        if (duration < MinEventMs)
        {
            _snapshotReady = false;
            return;
        }

        string? snapshotPath = null;
        if (_snapshotReady && (_firstMotionWidth > 0) && (_firstMotionHeight > 0) && _firstMotionPixels.Length > 0)
        {
            try
            {
                Directory.CreateDirectory(_snapshotDir);
                snapshotPath = Path.Combine(
                    _snapshotDir,
                    $"motion-{_channelId}-{_armedUtc:HHmmss}.bmp");
                BmpSnapshotWriter.Save(
                    new VideoFrame
                    {
                        Width = _firstMotionWidth,
                        Height = _firstMotionHeight,
                        Pixels = _firstMotionPixels,
                        TimestampUtc = _armedUtc,
                        PtsMs = 0,
                    },
                    snapshotPath);
            }
            catch (IOException)
            {
                snapshotPath = null;
            }
            catch (UnauthorizedAccessException)
            {
                snapshotPath = null;
            }
        }

        _snapshotReady = false;
        var id = _repo.Insert(
            _channelId,
            "motion",
            _armedUtc,
            snapshotPath,
            $"duration={duration:0}ms peak={_peakRatio:0%}");
        var end = _lastMotionUtc > _armedUtc ? _lastMotionUtc : resolvedUtc;
        _repo.UpdateEnd(id, end, snapshotPath);
        _lastFrame = null;
    }
}