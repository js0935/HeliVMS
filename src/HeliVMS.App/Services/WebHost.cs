using System.IO;
using HeliVMS.Qctek.Nemo;
using HeliVMS.Shared;
using HeliVMS.Storage;
using HeliVMS.WebApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace HeliVMS.App.Services;

/// <summary>
/// 桌面程式內嵌的 Web 服務主機（M305）。以同一份 WebApi 程式碼在行程內啟停 Kestrel，
/// 由設定中心「網路」頁控制，不需另外帶一顆執行檔或管理子行程。
///
/// <para>
/// 刻意只綁定 <c>localhost</c>：這是給同機瀏覽器用的管理介面，不是對外服務；
/// 對外要另外做憑證、反向代理與授權，不該用一顆預設無 TLS 的 Kestrel 直接暴露。
/// </para>
/// <para>
/// 「共用埠」模式：當 QCTek 相容服務也設在同一個埠時，公開埠由
/// <see cref="QctekPortMux"/> 佔用，這裡改綁系統指派的臨時埠並把公開埠以
/// <see cref="QctekHttpResponder.PublicPortConfigKey"/> 告知 Kestrel——否則
/// <c>RemoteConsolePort</c> 會回報只有本行程才知道的臨時埠。
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
    private bool _allowNoKey;

    public WebHost(string dataRoot, string dbPath)
    {
        _dataRoot = dataRoot;
        _dbPath = dbPath;
    }

    public bool IsRunning => _app is not null;

    /// <summary>對外（操作員設定、CMS 使用）的埠。</summary>
    public int Port { get; private set; }

    /// <summary>Kestrel 實際綁定的埠；共用埠時是系統指派的臨時埠。</summary>
    public int InternalPort { get; private set; }

    /// <summary>是否由前置多工器擁有公開埠（Kestrel 改聽臨時埠）。</summary>
    public bool IsSharing { get; private set; }

    /// <summary>最近一次啟動／套用的錯誤訊息；成功時為 <c>null</c>。</summary>
    public string? LastError { get; private set; }

    /// <summary>對外顯示用的本機網址。</summary>
    public string Url => $"http://localhost:{Port}";

    /// <summary>依設定啟用或停用 Web 服務（不共用埠），回傳狀態訊息。</summary>
    public string ApplySettings(SettingsRepository settings) => ApplyLocal(settings, sharePort: false);

    /// <summary>
    /// 依設定啟用或停用 Web 服務，回傳狀態訊息。
    /// </summary>
    /// <param name="settings">共用設定。</param>
    /// <param name="sharePort">
    /// <c>true</c>＝公開埠由 <see cref="QctekPortMux"/> 擁有，Kestrel 改綁臨時埠。
    /// 呼叫端必須先確定多工器已停止（不共用時），或將在本次呼叫後才啟動多工器（共用時）。
    /// </param>
    public string ApplyLocal(SettingsRepository settings, bool sharePort)
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
            Start(port, allowNoKey, sharePort);
            WebDiag(
                $"Web 已啟動：公開埠 {port}，實際 {InternalPort}，共用={sharePort}");
            return string.Format(Localizer.T("Settings.WebRunning"), port);
        }
        catch (Exception ex)
        {
            // 連接埠被占用等啟動失敗：確保不留半啟動的主機，否則下次再按「套用」會洩漏一個 Kestrel。
            // 一定要寫進日誌：這個失敗在畫面上只是一句「Web 服務：已停止」，
            // 而它的後果是「QCC 連上 Web 埠時沒有多工器可用」——看起來像相容服務壞了。
            WebDiag($"Web 啟動失敗：{ex.GetType().Name} {ex.Message}");
            LastError = ex.Message;
            Stop();
            return string.Format(Localizer.T("Settings.WebStartFailed"), ex.Message);
        }
    }

    /// <summary>Web 主機的診斷日誌（<c>%TEMP%\helivms-web.log</c>，上限 512 KB）。</summary>
    private static void WebDiag(string message)
        => BoundedFileLog.Append(
            Path.Combine(Path.GetTempPath(), "helivms-web.log"),
            $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}",
            maxBytes: 512 * 1024);

    private static int ReadPort(SettingsRepository settings)
    {
        var raw = settings.Get(PortKey);
        return int.TryParse(raw, out var port) && port is > 0 and <= 65535 ? port : DefaultPort;
    }

    public void Start(int port, bool allowNoKey, bool sharePort = false)
    {
        // 同一組參數就不要再拆掉一次：共用埠模式下重啟 Kestrel 會換一個臨時埠，
        // 多工器也得跟著重指，等於每次按「套用」都斷一次 HTTP。
        if (IsRunning && Port == port && IsSharing == sharePort && _allowNoKey == allowNoKey)
        {
            return;
        }

        Stop();

        var args = new List<string>
        {
            // 共用埠時讓系統指派：公開埠必須留給多工器。
            // 這裡一定要用明確的迴環位址——Kestrel 不接受「在 localhost 上綁連接埠 0」，
            // 見 WebListenUrl。
            $"--urls={WebListenUrl.For(port, sharePort)}",
            // 明確把資料庫與資料根目錄指到桌面程式正在用的那一份：不靠環境變數或預設推導，
            // 否則在某些安裝方式下 Web 端會開起另一個資料庫，設定頁顯示成功但機器沒在錄。
            $"--{DataRootLayout.DatabaseVariable}={_dbPath}",
            $"--{DataRootLayout.DataRootVariable}={_dataRoot}",
        };
        if (sharePort)
        {
            // RemoteConsolePort 要回報對外的公開埠，而不是這個臨時埠。
            args.Add($"--{QctekHttpResponder.PublicPortConfigKey}={port}");
        }

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
        InternalPort = sharePort ? ReadBoundPort(app) : port;
        IsSharing = sharePort;
        _allowNoKey = allowNoKey;
    }

    /// <summary>
    /// 讀 Kestrel 實際綁定的埠。監聽網址的埠是 0 時（<see cref="WebListenUrl.DynamicLoopback"/>），
    /// 真正的那一個是啟動後才由系統指派，
    /// 只有 <see cref="IServerAddressesFeature"/> 會拿到真正的那一個。
    /// </summary>
    private static int ReadBoundPort(WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses;
        foreach (var address in addresses ?? [])
        {
            if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Port is > 0 and <= 65535)
            {
                return uri.Port;
            }
        }

        throw new InvalidOperationException("Kestrel 已啟動但沒有回報實際連接埠。");
    }

    public void Stop()
    {
        var app = _app;
        _app = null;
        IsSharing = false;
        InternalPort = 0;
        _allowNoKey = false;
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
