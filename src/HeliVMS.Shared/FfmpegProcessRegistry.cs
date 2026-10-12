namespace HeliVMS.Shared;

/// <summary>ffmpeg 子行程的用途分類，用於健康度快照的分項統計。</summary>
public enum FfmpegJobKind
{
    /// <summary>即時監看解碼（<c>RtspClient</c>，每顯示頻道一個）。</summary>
    Decode,

    /// <summary>錄影分段（<c>SegmentRecorder</c>，每錄影頻道一個）。</summary>
    Record,

    /// <summary>WebRTC 發佈（<c>LivePublisher</c>，每被觀看通道一個）。</summary>
    Publish,

    /// <summary>匯出／轉檔等一次性工作。</summary>
    Export,

    /// <summary>ffprobe／ffmpeg 探測。</summary>
    Probe,

    /// <summary>其他。</summary>
    Other,
}

/// <summary>
/// 目前所有 ffmpeg 子行程的彙總健康快照（§13-2 行程治理）。
/// 這回答的是「這台機器現在開了幾條 ffmpeg？有沒有失控？最近一次失敗是什麼？」
/// ——先前只能個別看每個視窗／端點，沒有一個地方能一眼看出總量。
/// </summary>
public sealed record FfmpegPoolSnapshot(
    int Running,
    int Decode,
    int Record,
    int Publish,
    int Export,
    int Probe,
    int Other,
    long TotalStarted,
    long TotalFinished,
    long TotalFailed,
    int PeakConcurrent,
    int Capacity,
    bool OverCapacity,
    string? LastFailureKey,
    string? LastFailure,
    string? LastFailureUtc)
{
    /// <summary>沒有任何行程、也沒有失敗記錄時的空快照。</summary>
    public static FfmpegPoolSnapshot Empty { get; } = new(
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, null, null, null);
}

/// <summary>
/// 全行程共用的 ffmpeg 子行程登錄表（§13-2）。
///
/// <para>
/// 這是「統一」的那一半：解碼、錄影、發佈三條路徑各自開自己的 ffmpeg，但都到此登錄。
/// 於是健康視窗（與 <c>/api/system-metrics</c>）能一次看到全部 ffmpeg 的數量與尖峰，
/// 而不必逐元件拼湊。它刻意<b>只做記錄，不強制終止</b>任何行程：一個會擅自殺掉錄影
/// 行程的治理器，比不管它更危險。
/// </para>
///
/// <para>
/// <see cref="Capacity"/> 為軟性上限：0 表示不限制。設定後逾量只反映在
/// <see cref="FfmpegPoolSnapshot.OverCapacity"/>（健康度告警），不阻擋行程啟動，
/// 所以設定錯誤不會讓錄影停擺。
/// </para>
/// </summary>
public sealed class FfmpegProcessRegistry
{
    /// <summary>行程共用的全域實例。</summary>
    public static FfmpegProcessRegistry Shared { get; } = new();

    private readonly object _gate = new();
    private readonly Dictionary<long, (FfmpegJobKind Kind, string Key)> _active = new();
    private long _nextId;
    private int _capacity;
    private long _totalStarted;
    private long _totalFinished;
    private long _totalFailed;
    private int _peak;
    private string? _lastFailureKey;
    private string? _lastFailure;
    private string? _lastFailureUtc;

    /// <summary>軟性並行上限（0＝不限制）。逾量只反映在快照，不阻擋啟動。</summary>
    public int Capacity
    {
        get
        {
            lock (_gate)
            {
                return _capacity;
            }
        }

        set
        {
            lock (_gate)
            {
                _capacity = Math.Max(0, value);
            }
        }
    }

    /// <summary>
    /// 登錄一個剛啟動的 ffmpeg 子行程。回傳的租約在處置時視為「行程結束」；
    /// 失敗請先呼叫 <see cref="FfmpegJob.Fail"/> 以計入失敗統計。
    /// </summary>
    public FfmpegJob Register(FfmpegJobKind kind, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_gate)
        {
            var id = ++_nextId;
            _active[id] = (kind, key);
            _totalStarted++;
            if (_active.Count > _peak)
            {
                _peak = _active.Count;
            }

            return new FfmpegJob(this, id, kind, key);
        }
    }

    /// <summary>目前的健康快照（執行中、累計、尖峰、最近失敗）。</summary>
    public FfmpegPoolSnapshot Snapshot()
    {
        lock (_gate)
        {
            var running = _active.Count;
            return new FfmpegPoolSnapshot(
                running,
                Count(FfmpegJobKind.Decode),
                Count(FfmpegJobKind.Record),
                Count(FfmpegJobKind.Publish),
                Count(FfmpegJobKind.Export),
                Count(FfmpegJobKind.Probe),
                Count(FfmpegJobKind.Other),
                _totalStarted,
                _totalFinished,
                _totalFailed,
                _peak,
                _capacity,
                _capacity > 0 && running > _capacity,
                _lastFailureKey,
                _lastFailure,
                _lastFailureUtc);
        }
    }

    /// <summary>清空所有統計（供測試隔離；正式路徑不應呼叫）。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _active.Clear();
            _nextId = 0;
            _capacity = 0;
            _totalStarted = 0;
            _totalFinished = 0;
            _totalFailed = 0;
            _peak = 0;
            _lastFailureKey = null;
            _lastFailure = null;
            _lastFailureUtc = null;
        }
    }

    private int Count(FfmpegJobKind kind)
    {
        var n = 0;
        foreach (var entry in _active.Values)
        {
            if (entry.Kind == kind)
            {
                n++;
            }
        }

        return n;
    }

    private void RecordFailure(string key, string? detail)
    {
        lock (_gate)
        {
            _totalFailed++;
            _lastFailureKey = key;
            _lastFailure = detail;
            _lastFailureUtc = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private void Finish(long id)
    {
        lock (_gate)
        {
            if (_active.Remove(id))
            {
                _totalFinished++;
            }
        }
    }

    /// <summary>
    /// 一個已登錄 ffmpeg 子行程的租約。處置即代表行程結束（成功或失敗皆計入
    /// <see cref="FfmpegPoolSnapshot.TotalFinished"/>）。
    /// </summary>
    public sealed class FfmpegJob : IDisposable
    {
        private readonly FfmpegProcessRegistry _owner;
        private readonly long _id;
        private bool _finished;
        private bool _failed;

        internal FfmpegJob(FfmpegProcessRegistry owner, long id, FfmpegJobKind kind, string key)
        {
            _owner = owner;
            _id = id;
            Kind = kind;
            Key = key;
        }

        /// <summary>此工作的用途分類。</summary>
        public FfmpegJobKind Kind { get; }

        /// <summary>此工作的識別鍵（例如頻道代號）。</summary>
        public string Key { get; }

        /// <summary>標記此行程失敗並記錄詳情（只記第一次）。</summary>
        public void Fail(string? detail)
        {
            if (_failed)
            {
                return;
            }

            _failed = true;
            _owner.RecordFailure(Key, detail);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            _owner.Finish(_id);
        }
    }
}
