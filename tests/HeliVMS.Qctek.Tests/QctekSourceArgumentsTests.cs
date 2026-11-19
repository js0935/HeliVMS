using HeliVMS.Qctek.Live;

namespace HeliVMS.Qctek.Tests;

/// <summary>
/// 影像來源的 ffmpeg 引數（即時影像與 QCC 遠端回放共用）。
/// </summary>
/// <remarks>
/// <para>
/// 這裡釘的是線路上看不出來的兩件事：檔案來源有沒有 <c>-re</c>（沒有＝QCC 回放進度條
/// 瞬間跑到尾端），以及 <c>-re</c> 在不在 <c>-i</c> 之前（排到後面 ffmpeg 以
/// "cannot be applied to output" 直接結束，症狀是「回放連得上但沒有畫面」）。
/// 兩者都只會表現成「QCC 那邊怪怪的」，伺服器端日誌乾淨。
/// </para>
/// </remarks>
public sealed class QctekSourceArgumentsTests
{
    private static List<string> Build(string source) => [.. QctekSourceArguments.Build(source)];

    private static List<string> Build(string source, int audioPort) => [.. QctekSourceArguments.Build(source, audioPort)];

    [Fact]
    public void 錄影檔來源會加re而且排在i之前()
    {
        var arguments = Build(@"C:\recordings\ch01\20261119\seg-120000.mp4");

        var re = arguments.IndexOf("-re");
        Assert.True(re >= 0, "檔案來源少了 -re：ffmpeg 會用磁碟速度把整段灌給 QCC，回放看起來像快轉。");
        Assert.True(
            re < arguments.IndexOf("-i"),
            "-re 是輸入選項，排在 -i 之後 ffmpeg 會直接結束（連得上、沒畫面）。");
    }

    [Fact]
    public void RTSP來源不加re並使用tcp傳輸()
    {
        var arguments = Build("rtsp://192.168.1.10/live/main");

        // 即時串流本身就是即時速率，再加 -re 只是多一層節流；而 -rtsp_transport tcp
        // 是現場唯一能穿過 NAT／不穩網路的設定（UDP 會掉封包，牆上是一格一格的黑）。
        Assert.DoesNotContain("-re", arguments);
        Assert.Equal("tcp", arguments[arguments.IndexOf("-rtsp_transport") + 1]);
    }

    [Fact]
    public void 輸出參數都在輸入之後且輸出到stdout()
    {
        foreach (var source in new[] { "rtsp://cam/live", @"D:\rec\seg-120000.mp4" })
        {
            var arguments = Build(source);
            var input = arguments.IndexOf("-i");

            Assert.Equal(source, arguments[input + 1]);
            foreach (var option in new[] { "-an", "-c:v", "-bsf:v", "-f" })
            {
                Assert.True(
                    arguments.IndexOf(option) > input,
                    $"{option} 必須是輸出選項（在 -i 之後），否則 ffmpeg 會把它套到輸入上。");
            }

            Assert.Equal("h264", arguments[arguments.IndexOf("-f") + 1]);
            Assert.Equal("-", arguments[^1]);
        }
    }

    [Fact]
    public void 錄影檔的位元流濾鏡先轉AnnexB再補參數集()
    {
        // mp4／fMP4 的 H.264 是 AVCC（長度前綴）封裝。ffmpeg 對 `-f h264` 會自動插入
        // h264_mp4toannexb，但**只要指定了 -bsf:v 就不會**：只寫 dump_extra 時實測
        // （本機 ffmpeg 8.0.1、mp4 與 fMP4 皆同）ffmpeg 以
        // "Invalid data found when processing input" 結束、stdout 零位元組——
        // QCC 回放連得上、沒有畫面，兩邊日誌都只有這一行的差別。
        var arguments = Build(@"D:\rec\seg-120000.mp4");
        Assert.Equal("h264_mp4toannexb,dump_extra", arguments[arguments.IndexOf("-bsf:v") + 1]);

        // 即時串流的封包本來就是 Annex-B，再轉一次會把 NAL 弄壞（QCC 端花屏或整格黑）。
        var live = Build("rtsp://192.168.1.10/live/main");
        Assert.Equal("dump_extra", live[live.IndexOf("-bsf:v") + 1]);
    }

    [Fact]
    public void 看不懂的來源字串當檔案處理()
    {
        // 打錯字的 RTSP 位址（少了 scheme）寧可當檔案加一層節流，也不要讓未來的
        // 網路來源少了 -re 就全速灌幀。
        Assert.False(QctekSourceArguments.IsLiveSource(@"D:\rec\seg-120000.mp4"));
        Assert.False(QctekSourceArguments.IsLiveSource("192.168.1.10/live"));
        Assert.True(QctekSourceArguments.IsLiveSource("RTSPS://cam/live"));
    }

    [Fact]
    public void 沒有音訊埠時引數與加入音訊前完全相同()
    {
        // 預設（沒有麥克風、操作員沒打開收音）必須逐項等於舊行為：多一個 -map 0:a:0
        // 就足以讓沒有音軌的攝影機整個頻道黑屏。結尾是 stdout 的 "-"。
        foreach (var source in new[] { "rtsp://cam/live", @"D:\rec\seg-120000.mp4" })
        {
            var arguments = Build(source);

            Assert.DoesNotContain("-map", arguments);
            Assert.DoesNotContain("-acodec", arguments);
            Assert.Equal("-", arguments[^1]);
            Assert.Equal(arguments, Build(source, 0));
        }
    }

    [Fact]
    public void 有音訊埠時在影像輸出之後追加µlaw的第二個輸出()
    {
        var arguments = Build("rtsp://cam/live", 45678);

        // 音訊是第二個輸出路徑：一定要排在影像的 `-f h264 -` 之後，否則 ffmpeg 會把
        // 它當成影像輸出的參數（或反之），最常見的下場就是「只有畫面、沒有聲音」。
        var video = arguments.IndexOf("-f");
        Assert.Equal("h264", arguments[video + 1]);
        Assert.Equal("-", arguments[video + 2]);

        var audio = arguments.IndexOf("-f", video + 1);
        Assert.True(audio > video + 2, "音訊輸出必須在影像輸出之後。");
        Assert.Equal("mulaw", arguments[audio + 1]);
        Assert.Equal("udp://127.0.0.1:45678", arguments[^1]);

        // `-an` 只作用於影像輸出（它排在影像的 -f 之前）：影像沒有音軌、音訊只有一個輸出。
        Assert.Equal(1, arguments.Count(argument => argument == "-an"));
        Assert.True(arguments.IndexOf("-an") < video + 1);
        Assert.Equal(1, arguments.Count(argument => argument == "-map"));
    }

    [Fact]
    public void 音訊埠是負數時直接擋下來() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => QctekSourceArguments.Build("rtsp://cam/live", -1));
}
