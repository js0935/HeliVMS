using System.Text;
using HeliVMS.Qctek.Live;
using HeliVMS.Qctek.Nemo;
using Xunit;

namespace HeliVMS.Qctek.Tests;

/// <summary>
/// QCTek PTZ：命令解析、連線回覆、狀態機（速度／自動巡航）與交棒。
/// </summary>
/// <remarks>
/// <para>
/// 這些事實全部由 <c>QC_Server</c>（伺服器）與 <c>QC_IPCAM_Aver</c>（客戶端）逐位元組
/// 還原，任何一項寫錯的症狀都一樣：<b>QCC 的 PTZ 面板連得上但按了沒反應</b>，兩邊日誌
/// 乾淨。所以這裡守的是「線路上長什麼樣」，不是內部方法長什麼樣。
/// </para>
/// </remarks>
public class QctekPtzTests
{
    // ── 命令解析 ────────────────────────────────────────────────────────────

    [Fact]
    public void 停止與停止巡航只差大小寫但仍是兩個不同命令()
    {
        // PTZ_STOP（全大寫，process_stop）與 PTZ_StopPan（混合大小寫，stop＋取消巡航）
        // 在線路上只差大小寫。用大小寫不敏感的 StartsWith("PTZ_Stop") 會把兩者混成
        // 同一個動作：客戶端以為停了巡航，攝影機其實還在自動巡航。
        Assert.Equal(QctekPtzAction.Stop, QctekPtzCommand.Parse("PTZ_STOP ").Action);
        Assert.Equal(QctekPtzAction.Stop, QctekPtzCommand.Parse("PTZ_STOP").Action);
        Assert.Equal(QctekPtzAction.StopPan, QctekPtzCommand.Parse("PTZ_StopPan").Action);
        Assert.Equal(QctekPtzAction.Connect, QctekPtzCommand.Parse("PTZ_Connect 1").Action);
        Assert.Equal(QctekPtzAction.Disconnect, QctekPtzCommand.Parse("PTZ_DisConnect").Action);

        // 對角線是全大寫、方向是混合大小寫，逐字比對才不會互相吃掉。
        Assert.Equal(QctekPtzAction.UpperLeft, QctekPtzCommand.Parse("PTZ_UPPERLEFT ").Action);
        Assert.Equal(QctekPtzAction.LowerRight, QctekPtzCommand.Parse("PTZ_LOWERRIGHT ").Action);
        Assert.Equal(QctekPtzAction.ZoomIn, QctekPtzCommand.Parse("PTZ_ZoomIn ").Action);
    }

    [Fact]
    public void 參數讀不懂時是零而不是失敗()
    {
        // 真機的派送器一律用 atoi：讀不懂＝0，命令照樣執行（PTZ_Preset 的索引就是這樣
        // 解析的）。改成「解析失敗就忽略」會讓參數格式怪異的命令靜默消失。
        Assert.Equal(2, QctekPtzCommand.Parse("PTZ_Preset 2 ").Argument);
        Assert.Equal(0, QctekPtzCommand.Parse("PTZ_Preset abc").Argument);
        Assert.Equal(5, QctekPtzCommand.Parse("PTZ_Preset 5abc").Argument);
        Assert.Equal(-12, QctekPtzCommand.Atoi(" -12xyz"));
        Assert.Equal(0, QctekPtzCommand.Atoi(string.Empty));
        Assert.Equal(int.MaxValue, QctekPtzCommand.Atoi("99999999999999"));
    }

    [Fact]
    public void 未知命令不會被誤認成已知動作()
    {
        Assert.Equal(QctekPtzAction.Unknown, QctekPtzCommand.Parse("PTZ_Explode").Action);
        Assert.Equal(QctekPtzAction.Unknown, QctekPtzCommand.Parse("PTZ_Stop").Action); // 少一個 Pan
        Assert.Equal(QctekPtzAction.Unknown, QctekPtzCommand.Parse(string.Empty).Action);
    }

    // ── PTZ_Connect 回覆 ───────────────────────────────────────────────────

    [Fact]
    public void 連線成功回覆以NUL結尾而失敗只有七個位元組()
    {
        var device = new FakePtzDevice { Supported = true, Presets = 3 };
        var context = new PtzContext(new QctekPtzController(device));
        var request = new QctekRequest("PTZ_Connect 0", QctekFraming.Raw);

        var reply = QctekCommandHandler.HandlePtzConnect(request, context, out var accepted);

        Assert.True(accepted);
        Assert.Equal("OK 0 0 1 3 ", Encoding.ASCII.GetString(reply[..^1]));
        Assert.Equal(0, reply[^1]); // 真機寫 strlen+1 bytes：NUL 是回應的一部分

        // 失敗：QCC 只讀 16 bytes 並檢查前兩個字元是不是 "OK"，"FAILED" 剛好 7 bytes、
        // 刻意不補 NUL（真機就是這樣寫）；補了 NUL 會讓後續讀取多等一個位元組。
        var unsupported = new PtzContext(new QctekPtzController(new FakePtzDevice { Supported = false }));
        var failed = QctekCommandHandler.HandlePtzConnect(request, unsupported, out var failedAccepted);
        Assert.False(failedAccepted);
        Assert.Equal("FAILED"u8.ToArray(), failed);

        // 沒有接 PTZ 控制器（純測試／其他部署）：一樣是 FAILED，不是例外。
        var missing = new PtzContext(null);
        var none = QctekCommandHandler.HandlePtzConnect(request, missing, out var noneAccepted);
        Assert.False(noneAccepted);
        Assert.Equal("FAILED"u8.ToArray(), none);
    }

    // ── 狀態機 ──────────────────────────────────────────────────────────────

    [Fact]
    public void 速度夾在一到四並原樣回報()
    {
        var device = new FakePtzDevice();
        var controller = new QctekPtzController(device);

        Assert.Equal(1, controller.Connect(0)!.Value.Speed); // 真機預設 PTZPropGetSpeed＝0，輸出 +1

        controller.Apply(0, QctekPtzCommand.Parse("PTZ_SetSpeed 3 "));
        Assert.Equal(3, controller.Connect(0)!.Value.Speed);

        controller.Apply(0, QctekPtzCommand.Parse("PTZ_SetSpeed 9 "));
        Assert.Equal(4, controller.Connect(0)!.Value.Speed);

        controller.Apply(0, QctekPtzCommand.Parse("PTZ_SetSpeed 0 "));
        Assert.Equal(1, controller.Connect(0)!.Value.Speed);

        // 速度尺度：線路值 1–4 → 0.25–1.0。最慢被當成全速會讓「慢速微調」完全無效。
        Assert.Equal(0.25, QctekPtzVectors.Scale(1));
        Assert.Equal(1.0, QctekPtzVectors.Scale(4));
        Assert.Equal(1.0, QctekPtzVectors.Scale(99));
    }

    [Fact]
    public void 自動巡航只有成功才記成開啟而停止巡航會關掉它()
    {
        var device = new FakePtzDevice { Handler = (command, _) => command.Action == QctekPtzAction.AutoPan };
        var controller = new QctekPtzController(device);

        Assert.False(controller.Connect(0)!.Value.AutoPan);
        controller.Apply(0, QctekPtzCommand.Parse("PTZ_AutoPan"));
        Assert.True(controller.Connect(0)!.Value.AutoPan);

        // PTZ_STOP（全大寫）只停移動，**不**動自動巡航：真機的 PTZ_STOP 分支只呼叫
        // process_stop。清掉它會讓面板顯示「已停止巡航」而攝影機還在轉。
        controller.Apply(0, QctekPtzCommand.Parse("PTZ_STOP "));
        Assert.True(controller.Connect(0)!.Value.AutoPan);

        // PTZ_StopPan：停止移動並取消巡航。
        controller.Apply(0, QctekPtzCommand.Parse("PTZ_StopPan"));
        Assert.False(controller.Connect(0)!.Value.AutoPan);
        Assert.Contains(device.Executed, c => c.Action == QctekPtzAction.Stop);
    }

    [Fact]
    public void 自動巡航失敗時不會被記成開啟()
    {
        // ONVIF 沒有自動巡航：桌面端實作一律回 false。若照樣記成 true，面板會顯示
        // 巡航中、攝影機卻完全不動。
        var device = new FakePtzDevice { Handler = (_, _) => false };
        var controller = new QctekPtzController(device);

        controller.Apply(0, QctekPtzCommand.Parse("PTZ_AutoPan"));
        Assert.False(controller.Connect(0)!.Value.AutoPan);
    }

    [Fact]
    public void 不支援PTZ的頻道連線會失敗而且不會丟出例外()
    {
        var controller = new QctekPtzController(new FakePtzDevice { Supported = false });
        Assert.Null(controller.Connect(2));

        // 能力探測本身擲出例外（攝影機離線、ONVIF 逾時）時同樣是「不支援」；
        // 例外若冒到連線執行緒，QCC 只會看到「連線中斷」。
        var throwing = new QctekPtzController(new ThrowingPtzDevice());
        Assert.Null(throwing.Connect(0));
    }

    [Fact]
    public void 方向動作換算成對應的ONVIF向量()
    {
        Assert.Equal((0d, 1d, 0d), QctekPtzVectors.Vector(QctekPtzAction.Up));
        Assert.Equal((0d, -1d, 0d), QctekPtzVectors.Vector(QctekPtzAction.Down));
        Assert.Equal((-1d, 0d, 0d), QctekPtzVectors.Vector(QctekPtzAction.Left));
        Assert.Equal((1d, 0d, 0d), QctekPtzVectors.Vector(QctekPtzAction.Right));
        Assert.Equal((0d, 0d, 1d), QctekPtzVectors.Vector(QctekPtzAction.ZoomIn));
        Assert.Equal((0d, 0d, -1d), QctekPtzVectors.Vector(QctekPtzAction.ZoomOut));

        var (pan, tilt, _) = QctekPtzVectors.Vector(QctekPtzAction.UpperRight);
        Assert.Equal(QctekPtzVectors.Diagonal, pan);
        Assert.Equal(QctekPtzVectors.Diagonal, tilt);

        Assert.True(QctekPtzVectors.IsMove(QctekPtzAction.ZoomOut));
        Assert.False(QctekPtzVectors.IsMove(QctekPtzAction.Stop));
        Assert.Equal((0d, 0d, 0d), QctekPtzVectors.Vector(QctekPtzAction.Stop));
    }

    // ── 交棒（QctekSession → QctekPtzSession）──────────────────────────────

    [Fact]
    public void PTZ_Connect不經授權挑戰而直接回覆OK()
    {
        // 真機的 PTZ_Connect 自己就是「開一條新連線」的請求，不經 QCV8001_AUTH。
        // 把它放進 RequiresAuthorization 會讓客戶端收到 27 bytes 的挑戰而不是 OK：
        // PTZ 面板永遠停在「連線中」，兩邊日誌乾淨。
        var session = new QctekSession(new PtzContext(new QctekPtzController(new FakePtzDevice())));
        var result = session.Advance("PTZ_Connect 0\0"u8.ToArray());

        Assert.True(result.Ptz);
        Assert.Equal(0, result.PtzChannel);
        Assert.NotNull(result.Reply);
        Assert.StartsWith("OK ", Encoding.ASCII.GetString(result.Reply!));
        Assert.False(session.AwaitingAuthorization);
    }

    [Fact]
    public void PTZ連線失敗時不會交棒()
    {
        var session = new QctekSession(new PtzContext(new QctekPtzController(new FakePtzDevice { Supported = false })));
        var result = session.Advance("PTZ_Connect 0\0"u8.ToArray());

        Assert.False(result.Ptz);
        Assert.Equal("FAILED"u8.ToArray(), result.Reply);
    }

    [Fact]
    public void 交棒後的動作命令不回任何位元組而斷線命令結束迴圈()
    {
        var device = new FakePtzDevice();
        var context = new PtzContext(new QctekPtzController(device));
        var stream = new MemoryStream(Encoding.ASCII.GetBytes("PTZ_Up \0PTZ_Right \0PTZ_DisConnect"));

        QctekPtzSession.RunSync(stream, [], "test", context, 0, _ => { });

        Assert.Contains(device.Executed, c => c.Action == QctekPtzAction.Up);
        Assert.Contains(device.Executed, c => c.Action == QctekPtzAction.Right);
        // DisConnect 不是動作：不會被送進裝置。
        Assert.DoesNotContain(device.Executed, c => c.Action == QctekPtzAction.Disconnect);
        Assert.Equal(0, stream.Length - stream.Position); // 沒有寫回任何位元組
    }

    [Fact]
    public void 斷線命令不帶NUL時仍然會被執行()
    {
        // 客戶端送出 PTZ_DisConnect 只寫 14 bytes、不含 NUL，然後立刻 close；
        // 伺服器迴圈必須把 EOF 前的殘留緩衝當成最後一條命令處理。
        var device = new FakePtzDevice();
        var context = new PtzContext(new QctekPtzController(device));
        var stream = new MemoryStream(Encoding.ASCII.GetBytes("PTZ_Left "));

        QctekPtzSession.RunSync(stream, [], "test", context, 0, _ => { });

        Assert.Contains(device.Executed, c => c.Action == QctekPtzAction.Left);
    }

    [Fact]
    public void 非PTZ資料會關閉連線而不是卡住()
    {
        var device = new FakePtzDevice();
        var context = new PtzContext(new QctekPtzController(device));
        var stream = new MemoryStream([0x16, 0x03, 0x01, 0x00]);

        QctekPtzSession.RunSync(stream, [], "test", context, 0, _ => { });

        Assert.Empty(device.Executed);
    }

    [Fact]
    public void 沒有PTZ控制器的部署收到PTZ_Connect會回失敗()
    {
        var session = new QctekSession(new PtzContext(null));
        var result = session.Advance("PTZ_Connect 0\0"u8.ToArray());

        Assert.False(result.Ptz);
        Assert.Equal("FAILED"u8.ToArray(), result.Reply);
    }

    [Fact]
    public void 交棒成功後殘餘的命令直接由PTZ迴圈接手()
    {
        // QCC 在收到 OK 之後立刻送 PTZ_STOP；它可能與 PTZ_Connect 黏在同一個
        // TCP 區段裡。NemoSessionLoop 必須把尚未耗用的位元組原樣交給 PTZ 迴圈，
        // 否則第一條停止命令會丟頭（面板按了沒反應）。
        var device = new FakePtzDevice();
        var context = new PtzContext(new QctekPtzController(device));
        var stream = new MemoryStream();
        var initial = Encoding.ASCII.GetBytes("PTZ_Connect 0\0PTZ_STOP \0");

        NemoSessionLoop.RunSync(stream, initial, "test", context, _ => { });

        var written = Encoding.ASCII.GetString(stream.ToArray());
        Assert.StartsWith("OK 0 0 1 2 ", written);
        Assert.DoesNotContain("QCV8001_AUTH", written);
        Assert.Contains(device.Executed, c => c.Action == QctekPtzAction.Stop);
    }

    [Fact]
    public void 交棒失敗時殘餘的命令不會被當成PTZ動作執行()
    {
        var device = new FakePtzDevice { Supported = false };
        var context = new PtzContext(new QctekPtzController(device));
        var stream = new MemoryStream();
        var initial = Encoding.ASCII.GetBytes("PTZ_Connect 0\0PTZ_Up \0");

        NemoSessionLoop.RunSync(stream, initial, "test", context, _ => { });

        Assert.Equal("FAILED", Encoding.ASCII.GetString(stream.ToArray()));
        Assert.Empty(device.Executed);
    }

    private sealed class FakePtzDevice : IQctekPtzDevice
    {
        public bool Supported { get; init; } = true;

        public int Presets { get; init; } = 2;

        public Func<QctekPtzCommand, double, bool> Handler { get; init; } = (_, _) => true;

        public List<QctekPtzCommand> Executed { get; } = [];

        public bool IsSupported(int channelIndex) => Supported;

        public int GetPresetCount(int channelIndex) => Presets;

        public bool Execute(int channelIndex, QctekPtzCommand command, double speedScale)
        {
            Executed.Add(command);
            return Handler(command, speedScale);
        }
    }

    private sealed class ThrowingPtzDevice : IQctekPtzDevice
    {
        public bool IsSupported(int channelIndex) => throw new IOException("離線");

        public int GetPresetCount(int channelIndex) => throw new IOException("離線");

        public bool Execute(int channelIndex, QctekPtzCommand command, double speedScale)
            => throw new IOException("離線");
    }

    private sealed class PtzContext(QctekPtzController? ptz) : IQctekDeviceContext
    {
        public string ServerVersion => "QCV8001";

        public int ChannelCount => 1;

        public IReadOnlyList<string> ChannelNames => ["攝影機001"];

        public int MaxStreams => 2;

        public bool HasVnc => false;

        public QctekPtzController? Ptz => ptz;

        public bool TryAuthenticate(string user, string password) => user == "admin";

        public bool IsKnownUser(string user) => user == "admin";

        public IQctekLiveSource? CreateLiveSource(int channelIndex, int subStream) => null;

        public IReadOnlyList<bool> GetRecordingMonthDays(int channelIndex, int year, int month) => new bool[31];

        public IReadOnlyList<byte> GetRecordingHourMatrix(int channelIndex, int year, int month) => new byte[768];

        public QctekPlaybackSegment? FindSegmentFrom(int channelIndex, DateTime fromUtc) => null;

        public QctekPlaybackSegment? FindSegmentCovering(int channelIndex, DateTime fromUtc) => null;

        public QctekEventPage SearchEvents(QctekEventSearchRequest query, int offset, int limit)
            => QctekEventPage.Empty;

        public IQctekLiveSource? CreatePlaybackSource(string filePath) => null;
    }
}
