using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Alarms;

/// <summary>
/// 遮蔽事件引擎（M39 §14.4）：串接 <see cref="TamperDetector"/>，需連續 N 次同類遮蔽才開窗
/// （避免瞬時誤報），遮蔽中止（冷卻窗口）後寫一筆 alarm_events（tamper）並存首幀 BMP 快照。
/// 斷線重連／頻道切換呼叫 <see cref="Reset"/>。
/// </summary>
public sealed class TamperEventEngine : IDisposable
{
    private const long CooldownMs = 1500;
    private const long MinEventMs = 300;

    private readonly int _channelId;
    private readonly AlarmEventRepository _repo;
    private readonly string _snapshotDir;
    private readonly TamperDetector _detector;
    private readonly int _consecutive;
    private readonly object _gate = new();

    private bool _armed;
    private DateTime _armedUtc;
    private DateTime _lastTamperUtc;
    private TamperKind _kind = TamperKind.None;
    private TamperKind _candidateKind = TamperKind.None;
    private int _candidateStreak;
    private byte[] _firstPixels = [];
    private int _firstWidth;
    private int _firstHeight;
    private double _peakBrightness;
    private double _minEdge;

    public TamperEventEngine(
        int channelId,
        AlarmEventRepository repo,
        string snapshotDir,
        TamperDetector? detector = null,
        int consecutive = 6)
    {
        _channelId = channelId;
        _repo = repo;
        _snapshotDir = snapshotDir;
        _detector = detector ?? new TamperDetector();
        _consecutive = Math.Max(1, consecutive);
    }

    /// <summary>遮蔽事件已寫入 alarm_events 後引發。</summary>
    public event EventHandler<AlarmEventRecord>? EventInserted;

    /// <summary>遮蔽開窗（rising edge）通知 UI（參數為當下觀察值）。</summary>
    public event EventHandler<TamperObservation>? TamperSignal;

    /// <summary>偵測幀數（診斷用）。</summary>
    public int FramesProcessed { get; private set; }

    /// <summary>送入取樣幀（呼叫端負責抽幀頻率，如每 250ms）。</summary>
    public void OnFrame(VideoFrame frame)
    {
        lock (_gate)
        {
            FramesProcessed++;
            var obs = _detector.Update(frame);
            var now = DateTime.UtcNow;

            if (!_armed)
            {
                if (obs.Kind == TamperKind.None)
                {
                    _candidateKind = TamperKind.None;
                    _candidateStreak = 0;
                    return;
                }

                if (obs.Kind == _candidateKind)
                {
                    _candidateStreak++;
                }
                else
                {
                    _candidateKind = obs.Kind;
                    _candidateStreak = 1;
                }

                if (_candidateStreak >= _consecutive)
                {
                    _armed = true;
                    _armedUtc = now;
                    _lastTamperUtc = now;
                    _kind = obs.Kind;
                    _firstPixels = (byte[])frame.Pixels.Clone();
                    _firstWidth = frame.Width;
                    _firstHeight = frame.Height;
                    _peakBrightness = obs.Brightness;
                    _minEdge = obs.EdgeEnergy;
                    _candidateKind = TamperKind.None;
                    _candidateStreak = 0;
                    TamperSignal?.Invoke(this, obs);
                }

                return;
            }

            if (obs.Kind == _kind)
            {
                _lastTamperUtc = now;
                _peakBrightness = _kind == TamperKind.Whiteout
                    ? Math.Max(_peakBrightness, obs.Brightness)
                    : Math.Min(_peakBrightness, obs.Brightness);
                _minEdge = Math.Min(_minEdge, obs.EdgeEnergy);
            }
            else if (obs.Kind == TamperKind.None)
            {
                if ((now - _lastTamperUtc).TotalMilliseconds >= CooldownMs)
                {
                    FinalizeLocked(now);
                }
            }
            else
            {
                // 不同類遮蔽：先結算舊窗口，再重新累積新候選（不立即開窗）。
                FinalizeLocked(now);
                _candidateKind = obs.Kind;
                _candidateStreak = 1;
            }
        }
    }

    /// <summary>停止前收尾：若仍在遮蔽窗口內，立即以目前時間關閉事件。</summary>
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

    /// <summary>清空狀態（頻道切換／斷線重連）；進行中的遮蔽窗口直接捨棄，不產生事件。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _armed = false;
            _kind = TamperKind.None;
            _candidateKind = TamperKind.None;
            _candidateStreak = 0;
            _firstPixels = [];
            _detector.Reset();
        }
    }

    public void Dispose() => Flush();

    /// <summary>遮蔽種類 → 事件 detail 用字串。</summary>
    public static string KindText(TamperKind kind) => kind switch
    {
        TamperKind.Blackout => "blackout",
        TamperKind.Whiteout => "whiteout",
        TamperKind.Covered => "covered",
        _ => "none",
    };

    // 呼叫者須持鎖
    private void FinalizeLocked(DateTime resolvedUtc)
    {
        var kind = _kind;
        var duration = (resolvedUtc - _armedUtc).TotalMilliseconds;
        _armed = false;
        _kind = TamperKind.None;

        if (duration < MinEventMs)
        {
            _firstPixels = [];
            return;
        }

        string? snapshotPath = null;
        if (_firstWidth > 0 && _firstHeight > 0 && _firstPixels.Length > 0)
        {
            try
            {
                Directory.CreateDirectory(_snapshotDir);
                snapshotPath = Path.Combine(
                    _snapshotDir,
                    $"tamper-{_channelId}-{_armedUtc:HHmmss}.bmp");
                BmpSnapshotWriter.Save(
                    new VideoFrame
                    {
                        Width = _firstWidth,
                        Height = _firstHeight,
                        Pixels = _firstPixels,
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

        _firstPixels = [];
        var detail = $"kind={KindText(kind)};brightness={_peakBrightness:0};edge={_minEdge:0};duration={duration:0}ms";
        var id = _repo.Insert(_channelId, "tamper", _armedUtc, snapshotPath, detail);
        _repo.UpdateEnd(id, resolvedUtc, snapshotPath);
        EventInserted?.Invoke(this, new AlarmEventRecord
        {
            Id = id,
            ChannelId = _channelId,
            EventType = "tamper",
            StartUtc = _armedUtc,
            EndUtc = resolvedUtc,
            SnapshotPath = snapshotPath,
            Detail = detail,
        });
    }
}
