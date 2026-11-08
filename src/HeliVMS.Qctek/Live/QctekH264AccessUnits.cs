namespace HeliVMS.Qctek.Live;

/// <summary>一個已分組完成的 H.264 存取單元（AU），可打成一個 FRAM。</summary>
/// <param name="keyframe">AU 內含 IDR slice（決定 block1/block2 是否附上）。</param>
/// <param name="block1">SPS＋4-byte start code（僅關鍵影格；非關鍵一律空陣列）。</param>
/// <param name="block2">PPS＋4-byte start code（僅關鍵影格）。</param>
/// <param name="video">AU 的 NAL 們，Annex-B start code 原樣（不含已抽走的 SPS/PPS）。</param>
public sealed class QctekAccessUnit(
    bool keyframe,
    byte[] block1,
    byte[] block2,
    byte[] video)
{
    public bool Keyframe { get; } = keyframe;

    public byte[] Block1 { get; } = block1;

    public byte[] Block2 { get; } = block2;

    public byte[] Video { get; } = video;
}

/// <summary>
/// Annex-B 位元組流 → 完整 NAL → 存取單元（AU）。喂進 <see cref="Append"/> 的是
/// ffmpeg <c>-f h264</c> 的任意分塊輸出；回傳「已確定結束」的 AU，可直接打 FRAM。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼要自己分組：ffmpeg 的每個 AVPacket 是一個 AU，但 raw h264 輸出把 packet
/// 頭尾相接，packet 邊界資訊丟失。一個 FRAM 裝多個 AU 會讓 QCC 的調速（DecProc 以
/// 封包時戳對牆鐘）失準；把一個 AU 拆成兩個 FRAM 又會多出時戳。QCC 客戶端
/// （<c>QC_StreamDec::Put</c>）把整包餵 FFmpeg 解碼器，AU 邊界最終由解碼器 parser
/// 重切——我們的分組是為了<b>時戳與關鍵影格判定</b>，差一兩個 NAL 不致命，但
/// 每個 AU 一個 FRAM 才是線上應有的節奏。
/// </para>
/// <para>
/// 逐 NAL 的規則（真機伺服器的 <c>SplitIFrame_H264</c> 切出形狀）：
/// SPS（type 7）進 block1、PPS（type 8）進 block2，<b>都帶 4-byte start code</b>，
/// 且只跟著含 IDR 的 AU 走——客戶端以「block1 size > 0」當關鍵影格判準，SPS
/// 跟著非關鍵幀走會讓每一幀都被當關鍵影格。slice（type 1/2/5）以「slice header
/// 首位元組的最高位元＝1」（<c>first_mb_in_slice</c> 為 0）判 AU 邊界：
/// slice header 的第一個 ue(v) 欄位，值 0 編碼成「1」，最高位元組 bit＝1（多 slice
/// 編碼時第二片以 0 開頭，屬於同一個 AU）。SEI／AUD 等非 slice NAL 屬於
/// <b>下一個</b> AU（它們總在 first slice 之前到達）。
/// </para>
/// <para>
/// 切分與殘留：start code 是 00 00 01（前面可再帶 zeros，形成 4-byte 形式）。
/// 尾端可能是半個 start code（例如以 00 00 00 收尾）——這些位元組留待下一包合併，
/// 不可當成 NAL 內容輸出，否則解碼器收到壞 NAL，症狀是「畫面起得來但隨機花屏」。
/// </para>
/// </remarks>
public sealed class QctekH264AccessUnitGrouper
{
    /// <summary>殘留上限：流裡出現這麼長的無 start code 區段代表它根本不是 Annex-B，丟棄防呆。</summary>
    private const int MaxCarryLength = 1024 * 1024;

    private byte[] _carry = [];
    private byte[]? _sps;
    private byte[]? _pps;

    /// <summary>屬於「下一個 AU」的前綴 NAL（SEI、AUD；含 start code，原樣）。</summary>
    private readonly List<byte[]> _prefix = [];

    /// <summary>組裝中的 AU 的 video NAL 們（含 start code，原樣）。</summary>
    private List<byte[]>? _currentVideo;
    private bool _currentHasIdr;

    /// <summary>餵入新位元組，回傳已完成的 AU（順序不變）。</summary>
    public List<QctekAccessUnit> Append(ReadOnlySpan<byte> data)
    {
        var units = new List<QctekAccessUnit>();

        var merged = new byte[_carry.Length + data.Length];
        _carry.CopyTo(merged, 0);
        data.CopyTo(merged.AsSpan(_carry.Length));

        var start = LocateStartCode(merged, 0);
        if (start < 0)
        {
            // 還沒看到任何 start code：全部留著等下一包（上限防呆）。
            _carry = merged.Length > MaxCarryLength ? [] : merged;
            return units;
        }

        if (start > 0)
        {
            // 第一個 start code 之前的無主資料：正常流不會出現（每個 NAL 以 start code
            // 開頭），丟棄而不是猜測它的意義。
            merged = merged[start..];
        }

        var pos = 0;
        while (true)
        {
            var next = LocateStartCode(merged, pos + 3);
            if (next < 0)
            {
                break;
            }

            OnNal(merged.AsSpan(pos, next - pos), units);
            pos = next;
        }

        // 尾端（最後一個 start code 起點之後）可能是完整的 NAL、半個 start code，
        // 或兩者——一律留待下一包。
        _carry = merged[pos..].ToArray();
        return units;
    }

    /// <summary>
    /// 串流結束（ffmpeg 退出）時呼叫：殘留裡的「最後一個 start code 起點之後」就是
    /// 完整的最後一個 NAL（不會再有後續了），照常分組後交出組裝中的 AU。
    /// </summary>
    public List<QctekAccessUnit> Flush()
    {
        var units = new List<QctekAccessUnit>();
        if (_carry.Length > 0)
        {
            var tail = _carry;
            _carry = [];
            OnNal(tail, units); // 半個 start code 的殘渣在 OnNal 裡被剝掉、無害
        }

        if (_currentVideo is { Count: > 0 })
        {
            units.Add(Build(_currentHasIdr, _currentVideo));
            _currentVideo = null;
            _currentHasIdr = false;
        }

        return units;
    }

    private void OnNal(ReadOnlySpan<byte> nal, List<QctekAccessUnit> units)
    {
        var content = StripStartCode(nal);
        if (content.Length == 0)
        {
            return; // 空 NAL（00 00 01 後緊跟下一個 start code）
        }

        switch (content[0] & 0x1F)
        {
            case 7:
                _sps = content.ToArray(); // SPS 只進 block1，不進 video
                return;
            case 8:
                _pps = content.ToArray();
                return;
            case 1 or 2 or 5:
                if (content.Length >= 2 && (content[1] & 0x80) != 0)
                {
                    CompleteCurrent(units);
                    _currentVideo = [];
                    _currentHasIdr = false;
                    if (_prefix.Count > 0)
                    {
                        _currentVideo.AddRange(_prefix);
                        _prefix.Clear();
                    }
                }

                if (_currentVideo is null)
                {
                    // 非 first slice 出現在沒有 AU 的時候（流中段才開始餵）：
                    // 憑空開 AU 會讓它帶著半張影像，丟棄。
                    return;
                }

                _currentVideo.Add(nal.ToArray());
                if ((content[0] & 0x1F) == 5)
                {
                    _currentHasIdr = true;
                }

                return;
            case 3 or 4:
                // 資料分割 B/C 不帶 slice header，payload[1] 不是 first_mb——不能判 AU 邊界。
                if (_currentVideo is not null)
                {
                    _currentVideo.Add(nal.ToArray());
                }
                return;
            default:
                // SEI/AUD/其他：屬於下一個 AU 的前綴。
                _prefix.Add(nal.ToArray());
                return;
        }
    }

    private void CompleteCurrent(List<QctekAccessUnit> units)
    {
        if (_currentVideo is { Count: > 0 })
        {
            units.Add(Build(_currentHasIdr, _currentVideo));
        }
    }

    private QctekAccessUnit Build(bool keyframe, List<byte[]> parts)
    {
        var video = new byte[parts.Sum(static part => part.Length)];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(video, offset);
            offset += part.Length;
        }

        // 關鍵影格才附 SPS/PPS（4-byte start code ＋ NAL，真機 SplitIFrame_H264 的形狀）。
        // 快取還空著時保持空——客戶端以 block1 > 0 判關鍵影格，空的關鍵影格宣告只會
        // 被當非關鍵丟棄；寧可等下一個帶 SPS 的 IDR（App 端來源以 dump_extra 保證）。
        if (!keyframe || _sps is null)
        {
            return new QctekAccessUnit(keyframe, [], [], video);
        }

        return new QctekAccessUnit(true, WithStartCode(_sps), _pps is null ? [] : WithStartCode(_pps), video);
    }

    private static byte[] WithStartCode(byte[] nal)
    {
        var buffer = new byte[nal.Length + 4];
        buffer[0] = 0;
        buffer[1] = 0;
        buffer[2] = 0;
        buffer[3] = 1;
        nal.CopyTo(buffer, 4);
        return buffer;
    }

    private static ReadOnlySpan<byte> StripStartCode(ReadOnlySpan<byte> nal)
    {
        for (var i = 0; i + 2 < nal.Length; i++)
        {
            if (nal[i] == 0 && nal[i + 1] == 0 && nal[i + 2] == 1)
            {
                return nal[(i + 3)..];
            }
        }

        return default;
    }

    /// <summary>
    /// 找下一個 start code 的<b>起點</b>（含前面的連續 zeros——那些 zeros 屬於 start code，
    /// 不是上一個 NAL 的內容；合法 NAL 以 rbsp trailing bit 收尾，末位元組不會是 00）。
    /// </summary>
    private static int LocateStartCode(ReadOnlySpan<byte> buffer, int from)
    {
        for (var i = Math.Max(from, 0); i + 2 < buffer.Length; i++)
        {
            if (buffer[i] == 0 && buffer[i + 1] == 0 && buffer[i + 2] == 1)
            {
                var start = i;
                while (start > 0 && buffer[start - 1] == 0)
                {
                    start--;
                }

                return start;
            }
        }

        return -1;
    }
}
