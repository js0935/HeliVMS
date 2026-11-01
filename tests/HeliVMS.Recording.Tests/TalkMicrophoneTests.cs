using HeliVMS.Recording.Talk;
using HeliVMS.Shared;

namespace HeliVMS.Recording.Tests;

/// <summary>
/// 對講的「主機麥克風」來源契約（喊話）。
/// </summary>
/// <remarks>
/// 與檔案來源共用同一條回傳通道與編碼（G.711 A-law、8 kHz、單聲道、RTSP over TCP），
/// 差別只有輸入：檔案要 <c>-re</c>（照檔速送），麥克風<b>不能</b>要（dshow 本身就是即時時鐘，
/// 再套 -re 會讓聲音一路延遲越積越多）。裝置清單的解析則是另一個會靜靜壞掉的點：
/// ffmpeg 換版之後措辭變了，清單就空了，而空清單會被誤認成「這台機器沒有麥克風」。
/// </remarks>
public sealed class TalkMicrophoneTests
{
    private const string Mic = "麥克風 (Realtek Audio)";
    private static readonly TalkTarget Target =
        new("rtsp://cam.example.invalid/talk", TalkTransport.RtspBackchannel, "rtsp-backchannel");

    [Fact]
    public void 麥克風來源用dshow擷取且不帶_re()
    {
        var command = TalkCommandBuilder.BuildLiveMicrophone(Mic, Target, TalkCodec.Onvif);
        var args = command.Arguments.ToList();

        // 來源是本機的 DirectShow 錄音裝置，不是檔案。
        Assert.Equal("dshow", args[args.IndexOf("-f") + 1]);
        Assert.Equal($"audio={Mic}", args[args.IndexOf("-i") + 1]);
        Assert.DoesNotContain("-re", args);

        // 編碼與傳輸跟檔案來源同一套（回傳通道的格式不因來源而變）。
        Assert.Equal("1", args[args.IndexOf("-ac") + 1]);
        Assert.Equal("8000", args[args.IndexOf("-ar") + 1]);
        Assert.Equal("pcm_alaw", args[args.IndexOf("-c:a") + 1]);
        Assert.Equal("tcp", args[args.IndexOf("-rtsp_transport") + 1]);
        Assert.Equal(Target.Url, args[^1]);
    }

    [Fact]
    public void 依請求選來源_給了麥克風就不用檔案()
    {
        var request = new TalkRequest(5, "rtsp://cam/live", string.Empty, MicrophoneDevice: Mic);

        var command = TalkCommandBuilder.Build(request, Target);

        Assert.Contains("-f dshow", string.Join(' ', command.Arguments));
        Assert.DoesNotContain("-re", command.Arguments);
    }

    [Fact]
    public void 沒給麥克風時退回檔案來源()
    {
        var request = new TalkRequest(5, "rtsp://cam/live", @"C:\clip.wav", MicrophoneDevice: "  ");

        var command = TalkCommandBuilder.Build(request, Target);

        // 空白裝置視同沒有：回退到檔案模式（帶 -re、輸入是檔案路徑）。
        Assert.Contains("-re", command.Arguments);
        Assert.Contains(@"C:\clip.wav", command.Arguments);
    }

    [Fact]
    public void 空白的麥克風名稱會被拒絕()
    {
        Assert.Throws<ArgumentException>(
            () => TalkCommandBuilder.BuildLiveMicrophone("   ", Target, TalkCodec.Onvif));
    }

    [Fact]
    public void 裝置清單只取音訊區段的名稱()
    {
        const string output = """
            [dshow @ 0000025c4d6f8d00] DirectShow video devices (some may be both video and audio devices):
            [dshow @ 0000025c4d6f8d00]  "USB Camera"
            [dshow @ 0000025c4d6f8d00] DirectShow audio devices (some may be both video and audio devices):
            [dshow @ 0000025c4d6f8d00]  "Microphone (Realtek Audio)"
            [dshow @ 0000025c4d6f8d00]  "Stereo Mix"
            """;

        var devices = MicrophoneCatalog.Parse(output);

        // 影像裝置（含攝影機自己的名稱）不能混進麥克風清單：選了它對講就是整段無聲。
        Assert.Equal(["Microphone (Realtek Audio)", "Stereo Mix"], devices);
    }

    [Fact]
    public void 清單為空或沒有音訊區段時不會亂猜()
    {
        Assert.Empty(MicrophoneCatalog.Parse(string.Empty));
        Assert.Empty(MicrophoneCatalog.Parse("ffmpeg version 9.0\r\n"));
        Assert.Empty(MicrophoneCatalog.Parse(
            "[dshow @ 0] DirectShow video devices:\r\n[dshow @ 0]  \"USB Camera\"\r\n"));
    }
}
