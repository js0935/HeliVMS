using HeliVMS.Storage;
using HeliVMS.WebApi;
using Microsoft.AspNetCore.Builder;

namespace HeliVMS.App.Services;

/// <summary>
/// 桌面程式內嵌的 Web 服務主機（M305）。以同一份 WebApi 程式碼在行程內啟停 Kestrel，
/// 由設定中心「網路」頁控制，不需另外帶一顆執行檔或管理子行程。
///
/// <para>
/// 刻意只綁定 <c>localhost</c>：這是給同機瀏覽器用的管理介面，不是對外服務；
/// 對外要另外做憑證、反向代理與授權，不該用一顆預設無 TLS 的 Kestrel 直接暴露。
/// </para>
/// </summary>
public sealed class WebHost : IDisposable
{
    public const int DefaultPort = 5080;
    public const string EnabledKey = "web.enabled";
    public const string PortKey = "web.port";
    public const string AllowNoKeyKey = "web.allow_no_key";

    private readonly string _dataRoot;
    private readonly string _dbPath;
    private WebApplication? _app;

    public WebHost(string dataRoot, string dbPath)
    {
        _dataRoot = dataRoot;
        _dbPath = dbPath;
    }

    public bool IsRunning => _app is not null;

    public int Port { get; private set; }

    /// <summary>最近一次啟動／套用的錯誤訊息；成功時為 <c>null</c>。</summary>
    public string? LastError { get; private set; }

    /// <summary>對外顯示用的本機網址。</summary>
    public string Url => $"http://localhost:{Port}";

    /// <summary>依設定啟用或停用 Web 服務，回傳狀態訊息。</summary>
    public string ApplySettings(SettingsRepository settings)
    {
        var enabled = settings.Get(EnabledKey) == "1";
        var port = ReadPort(settings);
        var allowNoKey = settings.Get(AllowNoKeyKey) == "1";

        if (!enabled)
        {
            Stop();
            return Localizer.T("Settings.WebStopped");
        }

        LastError = null;
        try
        {
            Start(port, allowNoKey);
            return string.Format(Localizer.T("Settings.WebRunning"), port);
        }
        catch (Exception ex)
        {
            // 連接埠被占用等啟動失敗：確保不留半啟動的主機，否則下次再按「套用」會洩漏一個 Kestrel。
            LastError = ex.Message;
            Stop();
            return string.Format(Localizer.T("Settings.WebStartFailed"), ex.Message);
        }
    }

    private static int ReadPort(SettingsRepository settings)
    {
        var raw = settings.Get(PortKey);
        return int.TryParse(raw, out var port) && port is > 0 and <= 65535 ? port : DefaultPort;
    }

    public void Start(int port, bool allowNoKey)
    {
        if (IsRunning)
        {
            return;
        }

        var args = new List<string>
        {
            $"--urls=http://localhost:{port}",
            // 明確把資料庫與資料根目錄指到桌面程式正在用的那一份：不靠環境變數或預設推導，
            // 否則在某些安裝方式下 Web 端會開起另一個資料庫，設定頁顯示成功但機器沒在錄。
            $"--{DataRootLayout.DatabaseVariable}={_dbPath}",
            $"--{DataRootLayout.DataRootVariable}={_dataRoot}",
        };
        if (allowNoKey)
        {
            args.Add($"--{ApiKeyAuthMiddleware.DisabledConfigKey}=1");
        }

        // 內嵌時不掛背景服務：桌面程序本身已有保留迴圈，重複的 RetentionService 會與它搶同一批檔案。
        var app = WebApiHost.Create([.. args], new WebApiHostOptions { IncludeHostedServices = false }, AppContext.BaseDirectory);
        try
        {
            app.StartAsync().GetAwaiter().GetResult();
        }
        catch
        {
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
            throw;
        }

        _app = app;
        Port = port;
    }

    public void Stop()
    {
        var app = _app;
        _app = null;
        if (app is null)
        {
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            app.StopAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // 停止逾時或已中止：不讓關閉流程卡住，繼續釋放資源。
        }
        finally
        {
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public void Dispose() => Stop();
}
