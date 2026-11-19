namespace HeliVMS.Qctek.Nemo;

/// <summary>
/// QCTek PTZ 連線上的命令種類（<c>QC_Server</c> 的 PTZ 派送器 <c>FUN_180008030</c> 與
/// 客戶端 <c>QC_IPCAM_Aver</c> 的 <c>dvrProxy_ptz</c>／<c>dvrPtz</c> 雙向比對後定案）。
/// </summary>
/// <remarks>
/// 名稱刻意對應線路上的命令，而不是 ONVIF 的動作名：PTZ 是<b>另一條 TCP 連線</b>上的
/// raw 文字協議，不經 NEMO 標頭、也不經 <c>QCV8001_AUTH</c> 挑戰。
/// </remarks>
public enum QctekPtzAction
{
    /// <summary>看不懂的命令（真機同樣不回覆、連線繼續）。</summary>
    Unknown = 0,

    /// <summary><c>PTZ_Connect &lt;ch&gt;</c>：開一條 PTZ 連線；唯一會收到回應的命令。</summary>
    Connect,

    /// <summary><c>PTZ_DisConnect</c>：客戶端關線（14 bytes、<b>不帶</b> NUL 就 close）。</summary>
    Disconnect,

    Up,
    Down,
    Left,
    Right,
    UpperLeft,
    UpperRight,
    LowerLeft,
    LowerRight,
    ZoomIn,
    ZoomOut,
    FocusIn,
    FocusOut,

    /// <summary><c>PTZ_STOP </c>（全大寫）：停止目前移動。</summary>
    Stop,

    /// <summary><c>PTZ_StopPan</c>（混合大小寫）：停止移動<b>兼</b>取消自動巡航。</summary>
    StopPan,

    /// <summary><c>PTZ_AutoPan</c>：自動巡航；成功才把狀態記成 true。</summary>
    AutoPan,

    /// <summary><c>PTZ_Preset &lt;n&gt;</c>：參數是預設點清單的索引（不是 ONVIF token）。</summary>
    Preset,

    /// <summary><c>PTZ_SetSpeed &lt;n&gt;</c>：線路值是 1-based。</summary>
    SetSpeed,
}

/// <summary>
/// 一條已解析的 PTZ 命令。
/// </summary>
/// <param name="Action">命令種類。</param>
/// <param name="Argument">
/// <c>atoi</c> 的結果（<c>PTZ_Connect</c> 的頻道號、<c>PTZ_Preset</c> 的索引、
/// <c>PTZ_SetSpeed</c> 的速度）。讀不懂＝0——真機就是這個語意，不要改成「失敗」。
/// </param>
/// <param name="Text">原始命令文字（診斷用）。</param>
public readonly record struct QctekPtzCommand(QctekPtzAction Action, int Argument, string Text)
{
    /// <summary>
    /// 解析一條 PTZ raw 命令。
    /// <para>
    /// 真機的派送器只比對<b>前 6–12 個字元</b>並忽略尾端空白，但客戶端送出的永遠是固定
    /// 字面值（<c>PanTiltProcess</c> 直接 <c>qc_strcpy</c> 一個常數），所以這裡改成
    /// 「第一個空白前逐字比對」：這樣 <c>PTZ_STOP</c>（全大寫）與 <c>PTZ_StopPan</c>
    /// （混合大小寫）不會互相吃掉——用大小寫不敏感的 <c>StartsWith("PTZ_Stop")</c>
    /// 檢查就會把停止與停止巡航混成同一個動作。
    /// </para>
    /// </summary>
    public static QctekPtzCommand Parse(string? text)
    {
        var raw = text ?? string.Empty;
        var token = raw.Trim(' ', '\0');
        if (token.Length == 0)
        {
            return new QctekPtzCommand(QctekPtzAction.Unknown, 0, raw);
        }

        var space = token.IndexOf(' ');
        var verb = space < 0 ? token : token[..space];
        var rest = space < 0 ? string.Empty : token[(space + 1)..];

        var action = verb switch
        {
            "PTZ_Connect" => QctekPtzAction.Connect,
            "PTZ_DisConnect" => QctekPtzAction.Disconnect,
            "PTZ_Up" => QctekPtzAction.Up,
            "PTZ_Down" => QctekPtzAction.Down,
            "PTZ_Left" => QctekPtzAction.Left,
            "PTZ_Right" => QctekPtzAction.Right,
            "PTZ_UPPERLEFT" => QctekPtzAction.UpperLeft,
            "PTZ_UPPERRIGHT" => QctekPtzAction.UpperRight,
            "PTZ_LOWERLEFT" => QctekPtzAction.LowerLeft,
            "PTZ_LOWERRIGHT" => QctekPtzAction.LowerRight,
            "PTZ_ZoomIn" => QctekPtzAction.ZoomIn,
            "PTZ_ZoomOut" => QctekPtzAction.ZoomOut,
            "PTZ_FocusIn" => QctekPtzAction.FocusIn,
            "PTZ_FocusOut" => QctekPtzAction.FocusOut,
            "PTZ_STOP" => QctekPtzAction.Stop,
            "PTZ_StopPan" => QctekPtzAction.StopPan,
            "PTZ_AutoPan" => QctekPtzAction.AutoPan,
            "PTZ_Preset" => QctekPtzAction.Preset,
            "PTZ_SetSpeed" => QctekPtzAction.SetSpeed,
            _ => QctekPtzAction.Unknown,
        };

        var argument = action is QctekPtzAction.Connect or QctekPtzAction.Preset or QctekPtzAction.SetSpeed
            ? Atoi(rest)
            : 0;

        return new QctekPtzCommand(action, argument, raw);
    }

    /// <summary>
    /// C 的 <c>atoi</c>：跳過前導空白、可帶正負號、讀到第一個非數字為止；讀不到就是 0。
    /// <para>
    /// 刻意不用 <see cref="int.TryParse(string, out int)"/>：那對 <c>"0 abc"</c> 直接失敗，
    /// 而真機的 <c>atoi</c> 得到 0 之後照樣動作（PTZ_Preset 的參數就是這樣解析的）。
    /// </para>
    /// </summary>
    public static int Atoi(ReadOnlySpan<char> text)
    {
        var i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        var negative = false;
        if (i < text.Length && (text[i] == '+' || text[i] == '-'))
        {
            negative = text[i] == '-';
            i++;
        }

        var value = 0L;
        var digits = 0;
        while (i < text.Length && text[i] is >= '0' and <= '9')
        {
            value = (value * 10) + (text[i] - '0');
            if (value > int.MaxValue)
            {
                value = int.MaxValue;
            }

            i++;
            digits++;
        }

        if (digits == 0)
        {
            return 0;
        }

        return (int)(negative ? -value : value);
    }
}

/// <summary>
/// <c>PTZ_Connect</c> 的回覆內容：QCC 用它初始化 PTZ 面板（自動巡航是否開著、速度、預設點數）。
/// </summary>
/// <param name="Supported">這個頻道的攝影機是否真的能 PTZ（false → 回 <c>FAILED</c>）。</param>
/// <param name="Speed">線路速度值（1-based；真機以「設定值＋1」輸出）。</param>
/// <param name="PresetCount">預設點數量（面板的清單長度）。</param>
/// <param name="AutoPan">自動巡航是否進行中。</param>
public readonly record struct QctekPtzState(
    bool Supported,
    int Speed,
    int PresetCount,
    bool AutoPan);

/// <summary>
/// PTZ 的實際動作出口：把已解析的命令換成 ONVIF 呼叫（由桌面層實作，因為
/// <c>HeliVMS.Qctek</c> 是純專案，不引用 <c>HeliVMS.Devices</c>／<c>HeliVMS.WebApi</c>）。
/// </summary>
public interface IQctekPtzDevice
{
    /// <summary>這個頻道的攝影機是否支援 PTZ（會做能力探測；實作應自行快取）。</summary>
    bool IsSupported(int channelIndex);

    /// <summary>預設點數量（面板清單長度）。</summary>
    int GetPresetCount(int channelIndex);

    /// <summary>
    /// 執行動作；回傳「命令是否真的被執行」。
    /// <para>
    /// 只有 <see cref="QctekPtzAction.AutoPan"/> 的回傳值會改變狀態（成功才記成 true）。
    /// ONVIF 沒有對應動作的（自動巡航、對焦）一律回 <c>false</c>，但仍是<b>已知</b>命令，
    /// 不可以記成未知命令。
    /// </para>
    /// </summary>
    bool Execute(int channelIndex, QctekPtzCommand command, double speedScale);
}

/// <summary>
/// 每條 PTZ 連線的狀態機：速度、自動巡航、以及把命令轉成裝置動作。
/// <para>
/// 真機把速度與自動巡航存在 <c>QC_UsageHabit</c>（每頻道），而客戶端在
/// <c>PTZ_Connect</c> 之後<b>立刻</b>送一次 <c>PTZ_STOP </c>；也就是說連線期間的所有
/// 命令都靠這裡的狀態相互配合。狀態 therefore 活得比單一連線長（由桌面端的
/// <c>QctekCompatHost</c> 建立長生命週期實例）。
/// </para>
/// </summary>
public sealed class QctekPtzController
{
    /// <summary>線路速度下界（1-based）。</summary>
    public const int MinSpeed = 1;

    /// <summary>線路速度上界。</summary>
    public const int MaxSpeed = 4;

    /// <summary>未被設定過的速度：真機的 <c>PTZPropGetSpeed</c> 預設 0，輸出時 +1。</summary>
    public const int DefaultSpeed = 1;

    private readonly IQctekPtzDevice _device;
    private readonly object _gate = new();
    private readonly Dictionary<int, ChannelState> _channels = [];

    public QctekPtzController(IQctekPtzDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
    }

    /// <summary>
    /// 處理 <c>PTZ_Connect</c>：不支援 PTZ 時回 <c>null</c>（呼叫端回 <c>FAILED</c>）。
    /// </summary>
    public QctekPtzState? Connect(int channelIndex)
    {
        bool supported;
        try
        {
            supported = _device.IsSupported(channelIndex);
        }
        catch (Exception)
        {
            // 能力探測失敗（攝影機離線、ONVIF 逾時）＝現在不能操作；讓面板顯示不支援，
            // 不要把例外丟進連線執行緒（那會讓 QCC 只看到「連線中斷」）。
            supported = false;
        }

        if (!supported)
        {
            return null;
        }

        var presets = 0;
        try
        {
            presets = Math.Max(0, _device.GetPresetCount(channelIndex));
        }
        catch (Exception)
        {
            presets = 0;
        }

        lock (_gate)
        {
            var state = GetOrCreate(channelIndex);
            return new QctekPtzState(true, state.Speed, presets, state.AutoPan);
        }
    }

    /// <summary>
    /// 套用一條已解析的命令（<see cref="QctekPtzAction.Connect"/>／
    /// <see cref="QctekPtzAction.Disconnect"/> 由呼叫端處理，這裡忽略）。
    /// </summary>
    public void Apply(int channelIndex, QctekPtzCommand command)
    {
        ChannelState state;
        lock (_gate)
        {
            state = GetOrCreate(channelIndex);
        }

        switch (command.Action)
        {
            case QctekPtzAction.SetSpeed:
                // 真機存 n−1 再以 n 回報；這裡直接存線路值（1-based），回報時原樣輸出。
                lock (_gate)
                {
                    state.Speed = Math.Clamp(command.Argument, MinSpeed, MaxSpeed);
                }

                return;

            case QctekPtzAction.StopPan:
                // 真機：process_stop 之後把 AutoPan 設為 false（只停不巡航）。
                Execute(channelIndex, QctekPtzCommandFor(QctekPtzAction.Stop), state);
                lock (_gate)
                {
                    state.AutoPan = false;
                }

                return;

            case QctekPtzAction.AutoPan:
                // 只有真的開始巡航才記成 true；ONVIF 沒有對應動作時裝置回 false。
                if (Execute(channelIndex, command, state))
                {
                    lock (_gate)
                    {
                        state.AutoPan = true;
                    }
                }

                return;

            case QctekPtzAction.Connect:
            case QctekPtzAction.Disconnect:
            case QctekPtzAction.Unknown:
                return;

            default:
                // PTZ_STOP 只停移動，**不**動 AutoPan（真機的 PTZ_STOP 分支只呼叫
                // process_stop）；把它一起清掉會讓面板的巡航狀態與攝影機不一致。
                Execute(channelIndex, command, state);
                return;
        }
    }

    /// <summary>目前狀態快照（測試與診斷用）。</summary>
    public QctekPtzState Snapshot(int channelIndex)
    {
        lock (_gate)
        {
            var state = GetOrCreate(channelIndex);
            return new QctekPtzState(true, state.Speed, 0, state.AutoPan);
        }
    }

    private bool Execute(int channelIndex, QctekPtzCommand command, ChannelState state)
    {
        var scale = QctekPtzVectors.Scale(state.Speed);
        try
        {
            return _device.Execute(channelIndex, command, scale);
        }
        catch (Exception)
        {
            // 裝置端失敗（逾時、攝影機拒收）不該中斷 PTZ 迴圈：真機對失敗的動作同樣
            // 只回 0 個位元組，客戶端繼續送下一條。
            return false;
        }
    }

    private ChannelState GetOrCreate(int channelIndex)
    {
        if (!_channels.TryGetValue(channelIndex, out var state))
        {
            state = new ChannelState { Speed = DefaultSpeed };
            _channels[channelIndex] = state;
        }

        return state;
    }

    private static QctekPtzCommand QctekPtzCommandFor(QctekPtzAction action)
        => new(action, 0, action.ToString());

    private sealed class ChannelState
    {
        public int Speed { get; set; } = DefaultSpeed;

        public bool AutoPan { get; set; }
    }
}

/// <summary>
/// PTZ 命令 → ONVIF 連續移動向量與速度尺度。
/// </summary>
/// <remarks>
/// 方向對應由 QCV8001 的面板配置決定：<c>PTZ_Up</c>＝tilt 正、<c>PTZ_Left</c>＝pan 負、
/// <c>PTZ_ZoomIn</c>＝zoom 正（與桌機 <c>PtzWindow</c> 的按鈕語意一致）。速度尺度
/// 0.25／0.5／0.75／1.0 對應線路值 1–4：QCC 的滑桿只有四段，而 ONVIF 的速度是 −1..1，
/// 對應錯會讓「最慢」看起來像全速。
/// </remarks>
public static class QctekPtzVectors
{
    /// <summary>對角線各軸的幅度；兩軸都給 1.0 在部分攝影機上會被當成單軸最大值。</summary>
    public const double Diagonal = 0.7;

    /// <summary>速度尺度（1→0.25、4→1.0），超出範圍先夾住。</summary>
    public static double Scale(int speed)
    {
        var clamped = Math.Clamp(speed, QctekPtzController.MinSpeed, QctekPtzController.MaxSpeed);
        return clamped / (double)QctekPtzController.MaxSpeed;
    }

    /// <summary>是否為會產生連續移動的動作（含縮放）。</summary>
    public static bool IsMove(QctekPtzAction action)
        => action is QctekPtzAction.Up or QctekPtzAction.Down or QctekPtzAction.Left or QctekPtzAction.Right
            or QctekPtzAction.UpperLeft or QctekPtzAction.UpperRight
            or QctekPtzAction.LowerLeft or QctekPtzAction.LowerRight
            or QctekPtzAction.ZoomIn or QctekPtzAction.ZoomOut;

    /// <summary>方向動作的 (pan, tilt, zoom) 單位向量；非移動動作回 (0, 0, 0)。</summary>
    public static (double Pan, double Tilt, double Zoom) Vector(QctekPtzAction action) => action switch
    {
        QctekPtzAction.Up => (0, 1, 0),
        QctekPtzAction.Down => (0, -1, 0),
        QctekPtzAction.Left => (-1, 0, 0),
        QctekPtzAction.Right => (1, 0, 0),
        QctekPtzAction.UpperLeft => (-Diagonal, Diagonal, 0),
        QctekPtzAction.UpperRight => (Diagonal, Diagonal, 0),
        QctekPtzAction.LowerLeft => (-Diagonal, -Diagonal, 0),
        QctekPtzAction.LowerRight => (Diagonal, -Diagonal, 0),
        QctekPtzAction.ZoomIn => (0, 0, 1),
        QctekPtzAction.ZoomOut => (0, 0, -1),
        _ => (0, 0, 0),
    };
}
