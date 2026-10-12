using HeliVMS.Shared;

namespace HeliVMS.Media.Tests;

/// <summary>
/// 驗證 ffmpeg／ffprobe 解析順序：安裝目錄內建 → 環境變數 → PATH。
/// </summary>
/// <remarks>
/// 這條保護「裝好即用」：安裝時若附了 <c>ffmpeg\</c>，即使機器 PATH 上另有版本，
/// 也必須用內建那份。否則版本漂移會重現「連得上卻沒畫面」這類只有 ffmpeg 不同才會
/// 出現、且日誌乾淨的故障。回退 PATH 時刻意保留執行檔名，維持與舊版一致。
/// </remarks>
public class FfmpegLocatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"helivms-ffmpeg-{Guid.NewGuid():N}");

    public FfmpegLocatorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void 安裝目錄的內建檔優先於環境變數與PATH()
    {
        var bundled = WriteBundled("ffmpeg");

        var resolved = FfmpegLocator.Resolve("ffmpeg", _dir, _ => "/custom/ffmpeg");

        Assert.Equal(bundled, resolved);
    }

    [Fact]
    public void 沒有內建檔時採用環境變數()
    {
        var resolved = FfmpegLocator.Resolve("ffmpeg", _dir, key =>
            key == FfmpegLocator.FfmpegVariable ? "/custom/ffmpeg" : null);

        Assert.Equal("/custom/ffmpeg", resolved);
    }

    [Fact]
    public void ffprobe讀取專屬的環境變數而非共用的那個()
    {
        var resolved = FfmpegLocator.Resolve("ffprobe", _dir, key =>
            key == FfmpegLocator.FfprobeVariable ? "/custom/ffprobe" : "/wrong/ffmpeg");

        Assert.Equal("/custom/ffprobe", resolved);
    }

    [Fact]
    public void 都沒有時回退PATH且保留執行檔名()
    {
        var resolved = FfmpegLocator.Resolve("ffmpeg", _dir, _ => null);

        Assert.Equal("ffmpeg", resolved);
    }

    [Fact]
    public void 只存在ffprobe內建檔時不會誤用於ffmpeg()
    {
        WriteBundled("ffprobe");

        var resolved = FfmpegLocator.Resolve("ffmpeg", _dir, _ => null);

        Assert.Equal("ffmpeg", resolved);
    }

    private string WriteBundled(string executable)
    {
        var dir = Path.Combine(_dir, FfmpegLocator.DirectoryName);
        Directory.CreateDirectory(dir);
        var name = OperatingSystem.IsWindows() ? executable + ".exe" : executable;
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, string.Empty);
        return path;
    }
}
