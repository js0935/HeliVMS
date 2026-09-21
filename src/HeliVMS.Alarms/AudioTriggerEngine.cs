using HeliVMS.Shared.Models;
using HeliVMS.Storage;

namespace HeliVMS.Alarms;

/// <summary>PCM 時塊等級（RMS dBFS 門檻分類）。</summary>
public enum AudioBlockKind
{
    Quiet,
    Burst,
    Sustained,
    Silent,
}

/// <summary>單一時塊的判讀結果（供測試／UI 訊號）。</summary>
public readonly record struct AudioBlockResult(AudioBlockKind Kind, double RmsDb, DateTime Utc);

/// <summary>音訊事件引擎設定參數。</summary>
public sealed class AudioTriggerConfig
{
    public int SampleRateHz { get; init; } = 16000;
    public int BlockSamples { get; init; } = 512;
    public double BurstDb { get; init; } = -12;
    public double SustainDb { get; init; } = -32;
    public double SilenceDb { get; init; } = -66;
    public double SustainSeconds { get; init; } = 2.0;
    public double SilenceSeconds { get; init; } = 30.0;
    public int BlocksToConfirm { get; init; } = 2;
    public long MinEventMs { get; init; } = 120;
    public long CooldownMs { get; init; } = 1500;
}

/// <summary>
/// 音訊事件引擎（§5.8）：輸入 16k mono PCM（<see cref="short"/>），依分塊 RMS dBFS 分四類
/// （<see cref="AudioBlockKind"/>），以「連續偵測合成單一事件」去重：爆音／持續噪音／音訊斷路
/// 開窗→冷卻收尾→寫一筆 alarm_events（audio_burst／audio_sustained／audio_break，無快照）。
/// 斷線重連／頻道切換呼叫 <see cref="Reset"/>。
/// </summary>
public sealed class AudioTriggerEngine : IDisposable
{
    private readonly int _channelId;
    private readonly AlarmEventRepository _repo;
    private readonly AudioTriggerConfig _cfg;
    private readonly object _gate = new();
    private readonly List<short> _pending = new(4096);

    private string? _armedKind;
    private DateTime _armedUtc = DateTime.MinValue;
    private DateTime _lastActiveUtc = DateTime.MinValue;
    private double _peakDb = double.MinValue;
    private double _sumDb;
    private int _countDb;
    private int _burstStreak;
    private int _sustainStreak;
    private int _silenceStreak;
    private double _blockSeconds;

    public AudioTriggerEngine(int channelId, AlarmEventRepository repo, AudioTriggerConfig? config = null)
    {
        _channelId = channelId;
        _repo = repo;
        _cfg = config ?? new AudioTriggerConfig();
        if (_cfg.BlockSamples <= 0 || _cfg.SampleRateHz <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(config), "BlockSamples 與 SampleRateHz 須為正數。");
        }

        _blockSeconds = (double)_cfg.BlockSamples / _cfg.SampleRateHz;
    }

    /// <summary>進入作用中（burst／sustained 觸發）通知 UI。</summary>
    public event EventHandler<double>? AudioSignal;

    /// <summary>事件已寫入 alarm_events 後引發（呼叫端勿在 handler 內做重工作；此處於鎖內 raise）。</summary>
    public event EventHandler<AlarmEventRecord>? EventInserted;

    /// <summary>送入 PCM 樣本（跨次呼叫自動累積至整塊），回傳本批逐塊判讀。</summary>
    public IReadOnlyList<AudioBlockResult> Feed(IReadOnlyList<short> pcm, DateTime utc)
    {
        lock (_gate)
        {
            var results = new List<AudioBlockResult>(pcm.Count / _cfg.BlockSamples + 1);
            _pending.AddRange(pcm);
            while (_pending.Count >= _cfg.BlockSamples)
            {
                var block = _pending.GetRange(0, _cfg.BlockSamples).ToArray();
                _pending.RemoveRange(0, _cfg.BlockSamples);
                results.Add(ProcessBlock(block, utc));
            }

            return results;
        }
    }

    /// <summary>停止前收尾：未關窗立即以目前時間結算。</summary>
    public void Flush()
    {
        lock (_gate)
        {
            TryFinalize(true);
        }
    }

    /// <summary>清空狀態（頻道切換／斷線重連）；進行中的窗口直接捨棄，不產生事件。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _armedKind = null;
            _armedUtc = DateTime.MinValue;
            _lastActiveUtc = DateTime.MinValue;
            _peakDb = double.MinValue;
            _sumDb = 0;
            _countDb = 0;
            _burstStreak = 0;
            _sustainStreak = 0;
            _silenceStreak = 0;
        }
    }

    public void Dispose() => Flush();

    private AudioBlockResult ProcessBlock(short[] block, DateTime nowUtc)
    {
        var rmsDb = RmsDb(block);
        var kind = Classify(rmsDb);
        var blockSpan = TimeSpan.FromSeconds(_blockSeconds);

        switch (kind)
        {
            case AudioBlockKind.Burst:
                _burstStreak++;
                _sustainStreak = 0;
                _silenceStreak = 0;
                if (_armedKind == "silence")
                {
                    Finalize(nowUtc); // 音訊恢復：斷路事件立即結算
                }

                if (_armedKind is null && _burstStreak >= _cfg.BlocksToConfirm)
                {
                    Arm("burst", nowUtc);
                }

                if (_armedKind is "burst" or "sustained")
                {
                    _lastActiveUtc = nowUtc;
                    Accrue(rmsDb);
                }
                break;

            case AudioBlockKind.Sustained:
                _sustainStreak++;
                _burstStreak = 0;
                _silenceStreak = 0;
                if (_armedKind == "silence")
                {
                    Finalize(nowUtc); // 音訊恢復：斷路事件立即結算
                }

                if (_armedKind is null && _sustainStreak * _blockSeconds >= _cfg.SustainSeconds)
                {
                    var start = nowUtc - TimeSpan.FromSeconds((_sustainStreak - 1) * _blockSeconds);
                    Arm("sustained", start);
                }

                if (_armedKind is "burst" or "sustained")
                {
                    _lastActiveUtc = nowUtc;
                    Accrue(rmsDb);
                }
                break;

            case AudioBlockKind.Silent:
                _silenceStreak++;
                _burstStreak = 0;
                _sustainStreak = 0;
                if (_armedKind == "silence")
                {
                    _lastActiveUtc = nowUtc;
                    Accrue(rmsDb);
                }
                else if (_armedKind is null && _silenceStreak * _blockSeconds >= _cfg.SilenceSeconds)
                {
                    var start = nowUtc - TimeSpan.FromSeconds((_silenceStreak - 1) * _blockSeconds);
                    Arm("silence", start);
                    _lastActiveUtc = nowUtc;
                    Accrue(rmsDb);
                }
                else if (_armedKind is "burst" or "sustained")
                {
                    TryFinalize(nowUtc - _lastActiveUtc >= CooldownSpan());
                }
                break;

            default: // Quiet
                _burstStreak = 0;
                _sustainStreak = 0;
                _silenceStreak = 0;
                if (_armedKind == "silence")
                {
                    // 音訊恢復：斷路事件立即結算（不待冷卻）
                    Finalize(nowUtc);
                }
                else if (_armedKind is "burst" or "sustained")
                {
                    TryFinalize(nowUtc - _lastActiveUtc >= CooldownSpan());
                }
                break;
        }

        return new AudioBlockResult(kind, rmsDb, nowUtc);
    }

    private TimeSpan CooldownSpan() => TimeSpan.FromMilliseconds(_cfg.CooldownMs);

    private void Arm(string kind, DateTime startUtc)
    {
        _armedKind = kind;
        _armedUtc = startUtc;
        _lastActiveUtc = startUtc;
        _peakDb = double.MinValue;
        _sumDb = 0;
        _countDb = 0;
        AudioSignal?.Invoke(this, _cfg.BurstDb);
    }

    private void Accrue(double rmsDb)
    {
        if (rmsDb > _peakDb)
        {
            _peakDb = rmsDb;
        }

        _sumDb += rmsDb;
        _countDb++;
    }

    private void TryFinalize(bool cooldownExpired)
    {
        if (_armedKind is null)
        {
            return;
        }

        if (cooldownExpired)
        {
            Finalize(_lastActiveUtc + CooldownSpan());
        }
    }

    // 呼叫者須持鎖
    private void Finalize(DateTime resolvedUtc)
    {
        var kind = _armedKind ?? throw new InvalidOperationException("Finalize 需已開窗。");
        var duration = (resolvedUtc - _armedUtc).TotalMilliseconds;
        var peak = _peakDb;
        var mean = _countDb > 0 ? _sumDb / _countDb : 0;
        _armedKind = null;
        _burstStreak = 0;
        _sustainStreak = 0;
        _silenceStreak = 0;

        if (duration < _cfg.MinEventMs)
        {
            return;
        }

        var eventType = kind switch
        {
            "burst" => "audio_burst",
            "sustained" => "audio_sustained",
            "silence" => "audio_break",
            _ => $"audio_{kind}",
        };
        var detail = $"kind={kind};peak_db={peak:0.0};mean_db={mean:0.0};duration={duration:0}ms";
        var id = _repo.Insert(_channelId, eventType, _armedUtc, null, detail);
        _repo.UpdateEnd(id, resolvedUtc, null);
        EventInserted?.Invoke(this, new AlarmEventRecord
        {
            Id = id,
            ChannelId = _channelId,
            EventType = eventType,
            StartUtc = _armedUtc,
            EndUtc = resolvedUtc,
            SnapshotPath = null,
            Detail = detail,
        });
    }

    private AudioBlockKind Classify(double rmsDb)
    {
        if (rmsDb >= _cfg.BurstDb)
        {
            return AudioBlockKind.Burst;
        }

        if (rmsDb >= _cfg.SustainDb)
        {
            return AudioBlockKind.Sustained;
        }

        if (rmsDb <= _cfg.SilenceDb)
        {
            return AudioBlockKind.Silent;
        }

        return AudioBlockKind.Quiet;
    }

    /// <summary>RMS dBFS：20·log10(rms/32768)，rms=0 時固定 −120。</summary>
    private static double RmsDb(IReadOnlyList<short> block)
    {
        long sumSq = 0;
        for (var i = 0; i < block.Count; i++)
        {
            var s = block[i];
            sumSq += (long)s * s;
        }

        var rms = Math.Sqrt((double)sumSq / block.Count);
        if (rms < 1e-9)
        {
            return -120.0;
        }

        return 20.0 * Math.Log10(rms / 32768.0);
    }
}