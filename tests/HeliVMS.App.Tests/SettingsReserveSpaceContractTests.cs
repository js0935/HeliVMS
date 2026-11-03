namespace HeliVMS.App.Tests;

/// <summary>
/// 設定中心「儲存」頁的空間治理語意：保留空間（磁碟至少留多少可用），不是錄影上限（配額）。
///
/// <para>
/// 背景：這個頁面原本是「錄影保留上限（GB）」，而 <c>RetentionService.Apply</c> 的迴圈條件是
/// <c>while (usage &gt; quotaBytes)</c>。把配額改成 0（或讓它小於一段錄影）就等於「把所有錄影刪光」，
/// 操作員看到的卻是「已套用」。改成保留空間後，真正決定汰除的是
/// <c>recording.min_free_bytes</c> 的壓力汰除，配額路徑停用。
/// </para>
/// <para>
/// 這個契約守的是「按鈕按下去寫的是哪個鍵」——接錯鍵的症狀是：套用了、畫面顯示成功、
/// 磁碟照樣寫爆，而保留空間從來沒生效過。
/// </para>
/// </summary>
public sealed class SettingsReserveSpaceContractTests
{
    private const string Code = "src/HeliVMS.App/SettingsWindow.xaml.cs";
    private const string Xaml = "src/HeliVMS.App/SettingsWindow.xaml";
    private const string I18n = "src/HeliVMS.Storage/I18n.cs";

    [Fact]
    public void 套用保留空間會以bytes寫入min_free設定鍵()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Code), "OnApplyReserveClicked");

        Assert.Contains("ReserveGbSettingKey", body);
        Assert.Contains("_settings.Set(", body);
        // 輸入是 GB，儲存是 bytes：寫錯單位會讓門檻差 10 億倍（2 bytes 或 2 PB）。
        Assert.Contains("1024d * 1024 * 1024", body);
        // 舊的總量配額已從設定面移除，不得再被這個按鈕寫入。
        Assert.DoesNotContain("quota_gb", body);
    }

    [Fact]
    public void 保留空間以同一組鍵讀回並換算成GB顯示()
    {
        var source = SourceContract.Read(Code);
        var read = SourceContract.BodyOf(source, "ReserveGbFromStore");

        Assert.Contains("ReserveGbSettingKey", read);
        Assert.Contains("1024d * 1024 * 1024", read);
        // 設定頁顯示的數字必須來自資料庫那一份，不能另外存一份而漂移。
        Assert.Contains("DefaultReserveGb", read);
        Assert.Contains("ReserveTextBox.Text = ", SourceContract.BodyOf(source, "ReloadReserve"));
    }

    [Fact]
    public void 立即清理走保留空間的壓力汰除而不是總量配額()
    {
        var body = SourceContract.BodyOf(SourceContract.Read(Code), "OnCleanupClicked");

        Assert.Contains("ApplyPressure", body);
        Assert.Contains("ReserveGbFromStore", body);
        // Apply(...) 是「總量配額」那條路；按鈕改走壓力汰除後不該再呼叫它。
        Assert.DoesNotContain("service.Apply(", body);
    }

    [Fact]
    public void 儲存頁的保留空間說明三語都在且不再提錄影上限()
    {
        var i18n = SourceContract.Read(I18n);
        var lines = i18n.Split('\n')
            .Where(line => line.Contains("\"Settings.ReserveLabel\"", StringComparison.Ordinal))
            .ToList();

        // 三語各一行。少了某一語，該語系的操作員會看到空白的標籤。
        Assert.Equal(3, lines.Count);
        Assert.All(lines, line => Assert.DoesNotContain("quota", line, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(lines, line => line.Contains("keep free", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(lines, line => line.Contains("保留空間", StringComparison.Ordinal));

        // 舊的配額字串已改名，不能留著讓程式碼又接回去。
        Assert.DoesNotContain("\"Settings.QuotaLabel\"", i18n);
        Assert.DoesNotContain("\"Settings.QuotaApplied\"", i18n);
        Assert.DoesNotContain("\"Settings.ApplyQuotaTip\"", i18n);
    }

    [Fact]
    public void 儲存頁的保留空間控件在而且舊配額控件已移除()
    {
        var xaml = SourceContract.Read(Xaml);

        Assert.Contains("x:Name=\"ReserveTextBox\"", xaml);
        Assert.Contains("x:Name=\"ApplyReserveButton\"", xaml);
        Assert.Contains("Click=\"OnApplyReserveClicked\"", xaml);
        Assert.DoesNotContain("QuotaTextBox", xaml);
        Assert.DoesNotContain("ApplyQuotaButton", xaml);
    }
}
