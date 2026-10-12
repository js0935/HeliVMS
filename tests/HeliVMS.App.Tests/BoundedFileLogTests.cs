using HeliVMS.Shared;

namespace HeliVMS.App.Tests;

/// <summary>
/// 有上限的診斷日誌。
///
/// 守的是一個會慢慢害死整台機器的失效：崩潰記錄是無條件附加的，沒有任何上限。
/// 真正危險的不是檔案大，而是磁碟被灌滿之後錄影就完了——ffmpeg 寫不出分段，
/// 於是「有人寫了一個每天崩潰幾百次的迴圈」會變成「攝影機全都沒在錄」。
/// </summary>
public sealed class BoundedFileLogTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"helivms-log-{Guid.NewGuid():N}.log");

    public void Dispose()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }

    [Fact]
    public void 會建立檔案並寫入內容()
    {
        BoundedFileLog.Append(_path, "第一筆\n", maxBytes: 1024);

        Assert.Equal("第一筆\n", File.ReadAllText(_path));
    }

    /// <summary>
    /// 超出上限時要裁掉舊內容，檔案大小回到上限內。
    ///
    /// 這個測試是整組的核心：如果實作變回無上限的 AppendAllText，
    /// 一個每秒崩潰的迴圈就會在 %TEMP% 產生無上限的檔案。
    /// </summary>
    [Fact]
    public void 超過上限時檔案會被裁回上限內()
    {
        const long Max = 512;
        for (var i = 0; i < 200; i++)
        {
            BoundedFileLog.Append(_path, $"紀錄 {i:D3}：{'x' * 40}\n", Max);
        }

        Assert.True(new FileInfo(_path).Length <= Max, "檔案超過上限，代表沒有裁切。");
    }

    /// <summary>
    /// 裁切後必須保留最近的內容，不是保留最舊的。
    ///
    /// 使用者通常在事發後才去看這個檔案；留下開頭的舊紀錄等於什麼都沒留下。
    /// </summary>
    [Fact]
    public void 裁切後保留的是最近的記錄()
    {
        const long Max = 512;
        for (var i = 0; i < 200; i++)
        {
            BoundedFileLog.Append(_path, $"紀錄 {i:D3}\n", Max);
        }

        var text = File.ReadAllText(_path);
        Assert.Contains("紀錄 199", text, StringComparison.Ordinal);
        Assert.DoesNotContain("紀錄 000", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// 裁切後不能留下半行。
    ///
    /// 從位元組中間切開會切在多位元組字元或某行文字中間，log 的第一行會是亂碼，
    /// 害得下次讀檔的人（或下一次追加）誤以為這就是記錄起點。
    /// </summary>
    [Fact]
    public void 裁切後第一行是完整的記錄()
    {
        const long Max = 300;
        for (var i = 0; i < 100; i++)
        {
            BoundedFileLog.Append(_path, $"紀錄 {i:D3}：內容\n", Max);
        }

        foreach (var line in File.ReadAllLines(_path))
        {
            // 每行都必須完整符合格式；半行會在數字或結尾處斷掉。
            Assert.Matches(@"^紀錄 \d{3}：內容$", line);
        }
    }

    /// <summary>沒超上限時原樣累積，不該動到內容。</summary>
    [Fact]
    public void 沒超過上限時內容原樣累積()
    {
        BoundedFileLog.Append(_path, "第一筆\n", maxBytes: 4096);
        BoundedFileLog.Append(_path, "第二筆\n", maxBytes: 4096);

        Assert.Equal("第一筆\n第二筆\n", File.ReadAllText(_path));
    }

/// <summary>
    /// 寫入失敗不能讓應用程式再崩一次，但要被看見。
    ///
    /// 這是記錄診斷資訊的函式；它自己拋例外會蓋掉原本正要記錄的真正問題
    /// （而且會發生在 AppDomain.UnhandledException 的處理器裡，等於崩潰時再崩一次）。
    /// 用「指向目錄的路徑」確定觸發寫入失敗。
    /// </summary>
    [Fact]
    public void 寫入失敗時回報錯誤而不拋出()
    {
        var errors = new List<Exception>();
        var asDirectory = Path.Combine(Path.GetTempPath(), $"helivms-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(asDirectory);
        try
        {
            // 對目錄做 AppendAllText 在 Windows 上必定失敗。
            BoundedFileLog.Append(asDirectory, "內容\n", maxBytes: 1024, onError: errors.Add);

            Assert.Single(errors);
        }
        finally
        {
            Directory.Delete(asDirectory, recursive: true);
        }
    }
}