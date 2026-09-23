using HeliVMS.Storage;
using Xunit;

namespace HeliVMS.Storage.Tests;

/// <summary>本機攝影機設定讀取器（M149 補充）：純解析 .secrets/cameras.local.yaml，不連網、不探測。</summary>
public sealed class LocalCameraConfigTests
{
    [Fact]
    public void Parse_ReadsCamerasListFromYaml()
    {
        const string yaml = """
            cameras:
              - id: cam-01
                name: Lobby
                url: "rtsp://192.168.100.10:554/Streaming/Channels/101"
                username: u1
                password: p1
              - id: cam-02
                name: Yard
                url: rtsp://192.168.100.11:554/Streaming/Channels/101
                username: u2
                password: p2
            """;

        var list = LocalCameraConfigReader.Parse(yaml);

        Assert.Equal(2, list.Count);
        Assert.Equal("cam-01", list[0].Id);
        Assert.Equal("Lobby", list[0].Name);
        Assert.Equal("rtsp://192.168.100.10:554/Streaming/Channels/101", list[0].RtspUrl);
        Assert.Equal("u1", list[0].Username);
        Assert.Equal("p1", list[0].Password);
        Assert.Equal("Yard", list[1].Name);
    }

    [Fact]
    public void Parse_IgnoresCommentsAndReturnsEmptyWhenNoCameras()
    {
        const string yaml = """
            # 註解行應被忽略
            cameras:
            """;

        Assert.Empty(LocalCameraConfigReader.Parse(yaml));
    }

    [Fact]
    public void Load_ReturnsEmptyWhenFileMissing()
    {
        Assert.Empty(LocalCameraConfigReader.Load("Z:\\definitely\\missing\\dir"));
    }
}