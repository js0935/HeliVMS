namespace HeliVMS.Storage.Tests;

/// <summary>
/// 錄影心跳檔（M320，§11.1）。
///
/// <para>
/// 心跳是「錄影行程還活著」的唯一外部訊號。這裡測的是它自己的可靠性：寫了讀得回來、
/// 過期能判出來、壞掉的內容不會讓看門狗擲例外。內容壞掉若擲例外，看門狗會在每分鐘一輪的
/// 檢查中不斷崩潰，反而製造出它要防的那種「無人察覺的停擺」。
/// </para>
/// </summary>
public sealed class RecordingHeartbeatTests : IDisposable
{
    private readonly string _root;

    public RecordingHeartbeatTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"helivms-hb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    /// <summary>寫入後讀得回同樣的內容（時間、行程、路數）。</summary>
    [Fact]
    public void 寫入後可讀回同樣內容()
    {
        var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

        RecordingHeartbeat.Write(_root, now, activeChannels: 7, processId: 4242);

        var read = RecordingHeartbeat.TryRead(_root);
        Assert.NotNull(read);
        Assert.Equal(now, read!.UpdatedUtc, TimeSpan.FromMilliseconds(1));
        Assert.Equal(7, read.ActiveChannels);
        Assert.Equal(4242, read.ProcessId);
    }

    /// <summary>寫入是原子替換：不留下半成品 <c>.tmp</c>，看門狗不會讀到寫一半的內容。</summary>
    [Fact]
    public void 寫入後不留下暫存檔()
    {
        RecordingHeartbeat.Write(_root, DateTime.UtcNow, 1, 1);

        Assert.False(File.Exists(RecordingHeartbeat.PathFor(_root) + ".tmp"));
        Assert.True(File.Exists(RecordingHeartbeat.PathFor(_root)));
    }

    /// <summary>資料根目錄不存在時會自動建立，不因目錄未建而寫不進去。</summary>
    [Fact]
    public void 資料根目錄不存在時會建立()
    {
        var nested = Path.Combine(_root, "a", "b");

        RecordingHeartbeat.Write(nested, DateTime.UtcNow, 0, 1);

        Assert.True(File.Exists(RecordingHeartbeat.PathFor(nested)));
    }

    /// <summary>沒有心跳檔時回 null（呼叫端據此判定 Missing）。</summary>
    [Fact]
    public void 沒有心跳檔時回傳空()
        => Assert.Null(RecordingHeartbeat.TryRead(_root));

    /// <summary>內容損壞時回 null，而不是擲例外讓看門狗每輪崩潰。</summary>
    [Fact]
    public void 內容損壞時回傳空而非擲例外()
    {
        File.WriteAllText(RecordingHeartbeat.PathFor(_root), "{ 這不是合法 JSON");

        Assert.Null(RecordingHeartbeat.TryRead(_root));
    }

    /// <summary>在容許時距內為 Fresh。</summary>
    [Fact]
    public void 容許時距內為新鮮()
    {
        var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var record = new RecordingHeartbeatRecord(now.AddSeconds(-30), 1, 2);

        Assert.Equal(
            RecordingHeartbeatStatus.Fresh,
            RecordingHeartbeat.Evaluate(record, now, TimeSpan.FromMinutes(2)));
    }

    /// <summary>超過容許時距為 Stale。</summary>
    [Fact]
    public void 超過容許時距為過期()
    {
        var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var record = new RecordingHeartbeatRecord(now.AddMinutes(-5), 1, 2);

        Assert.Equal(
            RecordingHeartbeatStatus.Stale,
            RecordingHeartbeat.Evaluate(record, now, TimeSpan.FromMinutes(2)));
    }

    /// <summary>沒有任何心跳紀錄時為 Missing（與 Stale 區分：從未啟動 vs 曾啟動後停住）。</summary>
    [Fact]
    public void 沒有紀錄時為缺少()
        => Assert.Equal(
            RecordingHeartbeatStatus.Missing,
            RecordingHeartbeat.Evaluate(null, DateTime.UtcNow, TimeSpan.FromMinutes(2)));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
