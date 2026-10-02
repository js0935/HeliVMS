using HeliVMS.Rtc;

namespace HeliVMS.Rtc.Tests;

/// <summary>
/// 憑證遮蔽的守衛測試（R244）。
/// <para>
/// 這條紅線在 repo 裡出現過三次（M235 錄影、M239 遠端回放、M244 即時串流），
/// 每次的理由都一樣：ffmpeg／RTSP 的位址含有帳密，而它會出現在錯誤訊息裡，
/// 錯誤訊息會進 log、會回給瀏覽器、會被複製到工單。
/// </para>
/// </summary>
public class RedactingErrorBufferTests
{
    [Fact]
    public void 遮蔽RTS位址中的帳密()
    {
        var buffer = new RedactingErrorBuffer();
        buffer.Append("Input #0, rtsp, from 'rtsp://admin:s3cret@192.0.2.1:554/live'");

        var text = buffer.Text;

        Assert.DoesNotContain("s3cret", text, StringComparison.Ordinal);
        Assert.DoesNotContain("admin", text, StringComparison.Ordinal);
        Assert.Contains("192.0.2.1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 多行輸出中的每一行都被遮蔽()
    {
        var buffer = new RedactingErrorBuffer();
        buffer.Append("Input #0, rtsp, from 'rtsp://user:pw1@10.0.0.1/stream'");
        buffer.Append("Output #0, rte, to 'rtp://127.0.0.1:5004'");
        buffer.Append("[rtsp @ 0x1] method: DESCRIBE, url: rtsp://user:pw2@10.0.0.1/stream");

        var text = buffer.Text;

        Assert.DoesNotContain("pw1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("pw2", text, StringComparison.Ordinal);
        Assert.Contains("rtp://127.0.0.1:5004", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 沒有帳碼的輸出保持原樣()
    {
        var buffer = new RedactingErrorBuffer();
        buffer.Append("[h264 @ 0x1] frame= 120 fps= 30 q=28.0 size= 4096kB");

        Assert.Contains("frame= 120", buffer.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void 只保留最後幾行避免無限成長()
    {
        var buffer = new RedactingErrorBuffer();
        for (var i = 0; i < RedactingErrorBuffer.MaxLines + 10; i++)
        {
            buffer.Append($"line {i}");
        }

        var text = buffer.Text;
        var lines = text.Split('\n');

        Assert.Equal(RedactingErrorBuffer.MaxLines, lines.Length);
        Assert.DoesNotContain("line 0\n", text, StringComparison.Ordinal);
        Assert.Contains($"line {RedactingErrorBuffer.MaxLines + 9}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 超長行被截斷但遮蔽仍先發生()
    {
        // 順序很重要：若先截斷再遮蔽，被截掉的尾巴可能正好是密碼。
        var buffer = new RedactingErrorBuffer();
        var padding = new string('x', 4000);
        buffer.Append($"Input #0, rtsp, from 'rtsp://user:topsecret@{padding}'");

        var text = buffer.Text;

        Assert.DoesNotContain("topsecret", text, StringComparison.Ordinal);
        Assert.EndsWith("…", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 清空後沒有殘留()
    {
        var buffer = new RedactingErrorBuffer();
        buffer.Append("rtsp://user:pw@10.0.0.1/stream");
        buffer.Clear();

        Assert.Equal(string.Empty, buffer.Text);
    }

    [Fact]
    public void 忽略null行()
    {
        var buffer = new RedactingErrorBuffer();
        buffer.Append(null);

        Assert.Equal(string.Empty, buffer.Text);
    }

    [Fact]
    public void 空內容取用後為空字串()
    {
        Assert.Equal(string.Empty, new RedactingErrorBuffer().Text);
    }

    [Fact]
    public async Task 並發寫入不會遺失遮蔽()
    {
        var buffer = new RedactingErrorBuffer();

        await Parallel.ForEachAsync(Enumerable.Range(0, 200), async (i, ct) =>
        {
            buffer.Append($"rtsp://user:pass{i}@192.0.2.{i % 255}/stream");
            await Task.CompletedTask;
        });

        var text = buffer.Text;

        Assert.DoesNotContain("pass0 ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("pass1\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("@192.0.2.", text[..Math.Min(text.Length, 4000)].Replace("rtsp://", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }
}