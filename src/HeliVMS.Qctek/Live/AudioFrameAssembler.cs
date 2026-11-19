namespace HeliVMS.Qctek.Live;

/// <summary>
/// 把 ffmpeg 送來的 µ-law 位元組流切成固定長度的音訊幀（8 kHz 每包 125ms＝1000 bytes）。
/// </summary>
/// <remarks>
/// <para>
/// UDP 的切法與時間無關：一個 datagram 可能是 200 bytes 也可能把好幾包黏在一起，
/// 所以一定要先累積到剛好 <see cref="QctekAudioArguments.FrameLength"/> 才交出一幀，
/// 否則 QCC 收到的音軌長度忽長忽短、播放速度跟著抖。
/// </para>
/// <para>
/// 佇列有界（<see cref="QctekAudioArguments.MaxQueuedFrames"/>，半秒），滿了丟<b>最舊</b>的
/// <b>整幀</b>：音訊遲到就沒有價值，而且從半包處丟會讓之後每一幀都錯位（µ-law 是
/// 位元組對齊的取樣）。丟棄數只有 <see cref="DroppedFrames"/> 一個痕跡，不寫日誌——
/// 一次網路抖動就會丟好幾幀，寫日誌只會把真正的原因洗掉。
/// </para>
/// </remarks>
public sealed class AudioFrameAssembler
{
    private readonly object _gate = new();
    private readonly Queue<byte[]> _frames;
    private readonly byte[] _partial;
    private readonly int _frameLength;
    private readonly int _capacity;
    private int _partialLength;

    /// <param name="frameLength">一幀的位元組數（預設 <see cref="QctekAudioArguments.FrameLength"/>）。</param>
    /// <param name="capacity">最多排隊幾幀（預設 <see cref="QctekAudioArguments.MaxQueuedFrames"/>）。</param>
    public AudioFrameAssembler(
        int frameLength = QctekAudioArguments.FrameLength,
        int capacity = QctekAudioArguments.MaxQueuedFrames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frameLength, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _frameLength = frameLength;
        _capacity = capacity;
        _partial = new byte[frameLength];
        _frames = new Queue<byte[]>(capacity);
    }

    /// <summary>累計丟棄的整幀數（診斷用：不為 0 代表這一輪聲音曾經落後半秒以上）。</summary>
    public long DroppedFrames { get; private set; }

    /// <summary>目前排隊中、可以取走的完整幀數。</summary>
    public int QueuedFrames
    {
        get
        {
            lock (_gate)
            {
                return _frames.Count;
            }
        }
    }

    /// <summary>還沒湊成一幀的位元組數。</summary>
    public int PartialBytes
    {
        get
        {
            lock (_gate)
            {
                return _partialLength;
            }
        }
    }

    /// <summary>追加位元組；湊滿一幀就排進佇列（超過上限時先丟最舊的一幀）。</summary>
    public void Append(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            while (!data.IsEmpty)
            {
                var take = Math.Min(_frameLength - _partialLength, data.Length);
                data[..take].CopyTo(_partial.AsSpan(_partialLength));
                _partialLength += take;
                data = data[take..];

                if (_partialLength < _frameLength)
                {
                    continue;
                }

                if (_frames.Count >= _capacity)
                {
                    _frames.Dequeue();
                    DroppedFrames++;
                }

                _frames.Enqueue(_partial.ToArray());
                _partialLength = 0;
            }
        }
    }

    /// <summary>
    /// 取走一幀到 <paramref name="destination"/>（長度必須 ≥ 一幀）；沒有完整幀時回
    /// <c>false</c> 且不寫入任何位元組。
    /// </summary>
    public bool TryTakeFrame(Span<byte> destination)
    {
        if (destination.Length < _frameLength)
        {
            throw new ArgumentException($"緩衝區至少需要 {_frameLength} bytes。", nameof(destination));
        }

        lock (_gate)
        {
            if (_frames.Count == 0)
            {
                return false;
            }

            _frames.Dequeue().CopyTo(destination);
            return true;
        }
    }

    /// <summary>丟掉排隊中的幀與未完成的半幀（來源重建時呼叫：舊聲音不該接到新畫面上）。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _frames.Clear();
            _partialLength = 0;
        }
    }
}
