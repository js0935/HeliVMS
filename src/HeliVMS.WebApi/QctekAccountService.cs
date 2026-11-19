using System.Globalization;
using System.Text;
using HeliVMS.Qctek.Nemo;
using HeliVMS.Storage;

namespace HeliVMS.WebApi;

/// <summary>帳號寫入端點的處理結果（對應真機的 200／400／401／500）。</summary>
public enum QctekAccountStatus
{
    Ok = 200,
    BadRequest = 400,
    Unauthorized = 401,
    Failed = 500,
}

/// <summary>
/// <see cref="QctekAccountService"/> 的結果。<paramref name="Body"/> 只有 update 成功時有值
/// （帳號序號的十進位文字）；<paramref name="Detail"/> 只進診斷日誌，絕不含密碼。
/// </summary>
public sealed record QctekAccountResult(
    QctekAccountStatus Status,
    string? Body = null,
    string ContentType = "text/html",
    string AuthSource = "none",
    string? Detail = null);

/// <summary>
/// QCC 遠端設定頁的帳號寫入：刪除／新增修改／複製權限。
/// </summary>
/// <remarks>
/// <para>
/// 線路格式在 <see cref="QctekAccountEndpoint"/>（純函式）；這裡負責「對到 HeliVMS 的
/// <see cref="UserRepository"/>」與授權。真機的順序是<b>先授權（401）再解析（400）</b>，
/// 最後才是資料層（500）——這個順序要保留，否則打錯密碼時 QCC 收到的會是「格式錯誤」，
/// 它會改送另一種格式而不是提示使用者改密碼。
/// </para>
/// <para>
/// <b>授權與登入分開。</b>這裡用 <see cref="PasswordHasher.Verify"/> 自行比對，不呼叫
/// <see cref="AuthService.Authenticate"/>：QCC 的操作不是「登入」，走 <c>Authenticate</c>
/// 會累計失敗次數並把整個帳號鎖住（連桌面登入都一起被鎖），而且成功時會改寫
/// <c>last_login</c>。授權失敗只寫診斷日誌。
/// </para>
/// <para>
/// <b>QCC 的密碼雜湊不可用。</b>它的 32-byte 雜湊是 MD5 加 <c>rand()</c> 遮罩與時間，
/// 無法轉成 HeliVMS 的 PBKDF2，所以帳號寫入只映射名稱／權限／啟用；新增帳號用隨機密碼
/// 建立，實際密碼由操作員在 HeliVMS 設定（日誌會寫明這件事，但不寫密碼本身）。
/// </para>
/// </remarks>
public sealed class QctekAccountService
{
    private readonly UserRepository _users;
    private readonly AuthService _auth;

    public QctekAccountService(UserRepository users, AuthService auth)
    {
        _users = users;
        _auth = auth;
    }

    /// <summary>處理一條帳號寫入請求；不認識的路徑回 400（呼叫端不該走到這裡）。</summary>
    public QctekAccountResult Handle(string? path, string? queryString, string? authorization)
    {
        var action = QctekAccountEndpoint.Classify(path);
        if (action == QctekAccountAction.None)
        {
            return new QctekAccountResult(QctekAccountStatus.BadRequest, Detail: $"unknown-path {path}");
        }

        var payload = QctekAccountEndpoint.Payload(queryString);
        var auth = Authorize(authorization, payload);
        if (!auth.Allowed)
        {
            return new QctekAccountResult(
                QctekAccountStatus.Unauthorized,
                AuthSource: auth.Source,
                Detail: $"{action} 授權失敗");
        }

        var result = action switch
        {
            QctekAccountAction.Remove => Remove(payload, auth.Actor),
            QctekAccountAction.Update => Update(payload, auth.Actor),
            QctekAccountAction.Copy => Copy(payload),
            _ => new QctekAccountResult(QctekAccountStatus.BadRequest),
        };

        return result with { AuthSource = auth.Source };
    }

    /// <summary>
    /// 刪除一個帳號。<c>usr</c> 讀不出來回 400；序號 ≤ 0、不存在、目前登入者、
    /// 最後一個啟用中 admin 一律回 500（較真機嚴格的那幾條是刻意加上的，見類別說明）。
    /// </summary>
    private QctekAccountResult Remove(string? payload, string? actor)
    {
        if (!QctekAccountEndpoint.TryReadRemoveSequence(payload, out var sequence))
        {
            return new QctekAccountResult(QctekAccountStatus.BadRequest, Detail: "remove 缺少 usr");
        }

        if (sequence <= 0)
        {
            return new QctekAccountResult(QctekAccountStatus.Failed, Detail: $"remove 序號無效 {sequence}");
        }

        var target = _users.GetById(sequence);
        if (target is null)
        {
            return new QctekAccountResult(QctekAccountStatus.Failed, Detail: $"remove 找不到 {sequence}");
        }

        if (actor is { Length: > 0 } &&
            string.Equals(target.Username, actor, StringComparison.OrdinalIgnoreCase))
        {
            return new QctekAccountResult(
                QctekAccountStatus.Failed, Detail: $"remove 拒絕刪除目前登入者 {target.Username}");
        }

        if (IsLastEnabledAdmin(target))
        {
            return new QctekAccountResult(
                QctekAccountStatus.Failed, Detail: $"remove 拒絕刪除最後一個 admin {target.Username}");
        }

        _users.DeleteUser(target.Id, actor);
        return new QctekAccountResult(QctekAccountStatus.Ok, Detail: $"remove {target.Username}");
    }

    /// <summary>
    /// 新增（序號 &lt; 0）或修改（序號 ≥ 0）一個帳號。成功回帳號序號的十進位文字，
    /// 這是 QCC 之後指名同一個帳號的依據。
    /// </summary>
    private QctekAccountResult Update(string? payload, string? actor)
    {
        var bytes = QctekAccountEndpoint.DecodePayload(
            payload, QctekAccountEndpoint.UpdatePayloadLength);
        if (bytes is null)
        {
            return new QctekAccountResult(QctekAccountStatus.BadRequest, Detail: "update payload 長度或格式不符");
        }

        var row = QctekAccountEndpoint.ReadUserRow(bytes);
        return row.Sequence < 0
            ? Create(row, actor)
            : Modify(row, actor);
    }

    private QctekAccountResult Create(QctekUserRow row, string? actor)
    {
        var name = row.Username.Trim();
        if (name.Length == 0)
        {
            // 真機允許沒有名字的帳號；HeliVMS 的 username 是登入識別，建一個空名稱
            // 等於一個沒人能登入、也分不出是誰的帳號。回 500，讓 QCC 顯示「失敗」。
            return new QctekAccountResult(QctekAccountStatus.Failed, Detail: "update 拒絕建立無名稱帳號");
        }

        if (_users.GetByUsername(name) is not null)
        {
            return new QctekAccountResult(QctekAccountStatus.Failed, Detail: $"update 名稱重複 {name}");
        }

        var role = QctekAccountEndpoint.IsAdministrator(row.Permission) ? "admin" : "viewer";
        var enabled = QctekAccountEndpoint.IsEnabled(row.Permission);

        int id;
        try
        {
            // 密碼是隨機的：QCC 送來的雜湊無法轉成 PBKDF2（見類別說明），
            // 所以帳號建立後一定是「在這裡不能用」，直到操作員在 HeliVMS 設定密碼。
            id = _users.CreateUser(name, PasswordHasher.Hash(Guid.NewGuid().ToString("N")), role, actor: actor);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            return new QctekAccountResult(QctekAccountStatus.Failed, Detail: $"update 建立失敗 {ex.Message}");
        }

        if (!enabled)
        {
            _users.SetEnabled(id, false, actor);
        }

        return new QctekAccountResult(
            QctekAccountStatus.Ok,
            id.ToString(CultureInfo.InvariantCulture),
            Detail: $"update 建立 {name} id={id}；密碼請在 HeliVMS 設定");
    }

    private QctekAccountResult Modify(QctekUserRow row, string? actor)
    {
        var target = _users.GetById(row.Sequence);
        if (target is null)
        {
            return new QctekAccountResult(QctekAccountStatus.Failed, Detail: $"update 找不到 {row.Sequence}");
        }

        var role = QctekAccountEndpoint.IsAdministrator(row.Permission) ? "admin" : "viewer";
        var enabled = QctekAccountEndpoint.IsEnabled(row.Permission);

        // 最後一個啟用中 admin 不得被降級或停用——QCC 上勾錯一格就會把所有人鎖在門外，
        // 而真機沒有這道保護（它允許把自己停用）。這條不看 auth.enabled：桌面版的
        // 「最後一個 admin」規則同樣是無條件保護。
        if (target.Enabled && target.Role == "admin" &&
            (!enabled || role != "admin") && IsLastEnabledAdmin(target))
        {
            return new QctekAccountResult(
                QctekAccountStatus.Failed, Detail: $"update 拒絕降級最後一個 admin {target.Username}");
        }

        var name = row.Username.Trim();
        if (name.Length > 0 && !string.Equals(name, target.Username, StringComparison.Ordinal))
        {
            var clash = _users.GetByUsername(name);
            if (clash is not null && clash.Id != target.Id)
            {
                return new QctekAccountResult(QctekAccountStatus.Failed, Detail: $"update 名稱重複 {name}");
            }

            try
            {
                _users.SetUsername(target.Id, name, actor);
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                return new QctekAccountResult(QctekAccountStatus.Failed, Detail: $"update 改名失敗 {ex.Message}");
            }
        }

        if (!string.Equals(target.Role, role, StringComparison.Ordinal))
        {
            _users.SetRole(target.Id, role, actor);
        }

        if (target.Enabled != enabled)
        {
            _users.SetEnabled(target.Id, enabled, actor);
        }

        return new QctekAccountResult(
            QctekAccountStatus.Ok,
            target.Id.ToString(CultureInfo.InvariantCulture),
            Detail: $"update 修改 {target.Username} id={target.Id} role={role} enabled={enabled}");
    }

    /// <summary>
    /// 複製權限。真機要求來源是「一般帳號」（<c>(perm &amp; 0x1c) == 8</c>），否則回 500；
    /// 只把每個被勾選目標的權限 bit0 換成來源的 bit0，名稱與密碼雜湊不動。
    /// </summary>
    private static QctekAccountResult Copy(string? payload)
    {
        var bytes = QctekAccountEndpoint.DecodePayload(payload, QctekAccountEndpoint.CopyPayloadLength);
        if (bytes is null)
        {
            return new QctekAccountResult(QctekAccountStatus.BadRequest, Detail: "copy payload 長度或格式不符");
        }

        var source = QctekAccountEndpoint.ReadUserRow(bytes);
        if (!QctekAccountEndpoint.IsViewerClass(source.Permission))
        {
            return new QctekAccountResult(
                QctekAccountStatus.Failed, Detail: $"copy 來源不是一般帳號 perm=0x{source.Permission:x}");
        }

        var selection = QctekAccountEndpoint.ReadSelection(bytes);
        var selected = selection.Count(static s => s);

        // HeliVMS 的權限只有 0／8／0x1c（bit0 一律 0），所以「只換 bit0」對我們是 no-op。
        // 刻意不做「批次停用」：把 bit0 誤讀成啟用會一次停掉所有被勾選的帳號，
        // 而真機的 DefaultAccountSetting 給一般帳號的也是 8（bit0 = 0）。
        return new QctekAccountResult(
            QctekAccountStatus.Ok,
            Detail: $"copy 驗證通過（來源 perm=0x{source.Permission:x}，選取 {selected} 個，無變更）");
    }

    /// <summary>
    /// 授權：<c>auth.enabled</c> 未啟用時比照桌面版放行；啟用時要求憑證且該帳號是
    /// <b>啟用中的 admin</b>。憑證來源是 <c>Authorization: Basic</c> 或查詢的
    /// <c>hBase64=</c>（真機兩種都接受）。失敗一律不透露原因（帳號存在與否不區分）。
    /// </summary>
    private (bool Allowed, string? Actor, string Source) Authorize(string? authorization, string? payload)
    {
        if (!_auth.IsAuthEnabled)
        {
            return (true, BasicUsername(authorization), "disabled");
        }

        var credentials = ReadCredentials(authorization, payload);
        if (credentials is not { } c)
        {
            return (false, null, "missing");
        }

        var user = _users.GetByUsername(c.Username);
        if (user is null || !user.Enabled || user.Role != "admin" ||
            !PasswordHasher.Verify(c.Password, user.PasswordHash))
        {
            return (false, null, c.Source);
        }

        return (true, user.Username, c.Source);
    }

    private static (string Username, string Password, string Source)? ReadCredentials(
        string? authorization,
        string? payload)
    {
        const string prefix = "Basic ";
        if (authorization is not null &&
            authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var text = Encoding.UTF8.GetString(
                    Convert.FromBase64String(authorization[prefix.Length..].Trim()));
                var colon = text.IndexOf(':');
                if (colon > 0)
                {
                    return (text[..colon], text[(colon + 1)..], "basic");
                }
            }
            catch (FormatException)
            {
                // 落到 hBase64 再試一次：真機的裝置精靈有時只帶查詢憑證。
            }
        }

        return QctekAccountEndpoint.TryReadQueryCredentials(payload, out var username, out var password)
            ? (username, password, "query")
            : null;
    }

    /// <summary>取 <c>Authorization: Basic</c> 的使用者名稱（只給稽核的 actor 用）。</summary>
    private static string? BasicUsername(string? authorization) =>
        ReadCredentials(authorization, null) is { } c ? c.Username : null;

    /// <summary>
    /// 這個帳號是不是「唯一一個啟用中的 admin」。刻意與
    /// <see cref="AuthService.WouldRemoveLastEnabledAdmin"/> 分開：那一支在
    /// <c>auth.enabled = 0</c> 時回 false（登入未啟用＝鎖不死自己），但 QCC 的帳號
    /// 寫入是無條件保護——把唯一的 admin 降成 viewer 之後，就算登入未啟用，
    /// 也沒有人能用管理權限改回來。
    /// </summary>
    private bool IsLastEnabledAdmin(UserRecord user)
    {
        if (!user.Enabled || user.Role != "admin")
        {
            return false;
        }

        return _users.ListUsers().Count(static u => u.Enabled && u.Role == "admin") <= 1;
    }
}
