using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 原始碼契約測試共用的工具（找 repo 根、讀檔、取方法主體、外層方法）。
/// 桌面專案沒有 WPF 執行期測試環境，授權／角色契約只能掃原始碼；
/// 這幾支工具被多份契約共用，抽成一份才不會各自複製一份而漂移。
/// </summary>
internal static class SourceContract
{
    /// <summary>
    /// 方法簽章：修飾子後接一串「不含 <c>=</c>／<c>;</c> 的字元」，再接識別字與左括號。
    /// 字元集刻意不含 <c>=</c> 與 <c>;</c>，欄位初始值（<c>private readonly X _y = new(</c>）
    /// 才不會被誤認成方法；集合內含 <c>&lt;&gt;</c>，讓 <c>public async Task&lt;Foo()</c>
    /// 抓到的是 <c>Foo</c> 而不是前一個識別字。
    /// </summary>
    private static readonly Regex Signature =
        new(@"(?:private|public|internal|protected|static)[\s\w<>\[\],\.\?]*\b(\w+)\s*\(");

    /// <summary>
    /// 往上找解決方案檔當作 repo 根目錄；找不到就丟出——寧可測試紅，
    /// 也不要靜默跳過整份契約（跳過等於測試看起來有跑、實際什麼都沒驗）。
    /// </summary>
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HeliVMS.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException(
                $"自 {AppContext.BaseDirectory} 往上找不到 HeliVMS.slnx，無法驗證原始碼契約。");
    }

    public static string Read(string relativePath) =>
        FileCache.GetOrAdd(relativePath, path => File.ReadAllText(Path.Combine(RepoRoot(), path)));

    /// <summary>方法本體（含大括號）或運算式本體；找不到回空字串。</summary>
    public static string BodyOf(string source, string method)
    {
        var signature = Regex.Match(
            source,
            $@"(?:private|public|internal|protected|static)[\s\w<>\[\],\.\?]*\b{Regex.Escape(method)}\s*\(");

        if (!signature.Success)
        {
            return string.Empty;
        }

        var i = signature.Index + signature.Length - 1;
        var depth = 0;
        for (; i < source.Length; i++)
        {
            if (source[i] == '(')
            {
                depth++;
            }
            else if (source[i] == ')' && --depth == 0)
            {
                i++;
                break;
            }
        }

        while (i < source.Length && char.IsWhiteSpace(source[i]))
        {
            i++;
        }

        if (i + 1 < source.Length && source[i] == '=' && source[i + 1] == '>')
        {
            var end = source.IndexOf(';', i);
            return end < 0 ? string.Empty : source[i..end];
        }

        if (i >= source.Length || source[i] != '{')
        {
            return string.Empty;
        }

        var open = i;
        depth = 0;
        for (; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}' && --depth == 0)
            {
                return source[open..(i + 1)];
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 某個位置所屬的外層方法名稱（往前找最近的一個方法簽章）。
    /// 契約測試用它回答「這個建立點在誰的底下」——反向查詢，
    /// <see cref="BodyOf"/> 是給定方法名找本體，方向相反但共用同一個簽章樣式。
    /// </summary>
    public static string EnclosingMethod(string source, int index)
    {
        var found = string.Empty;
        for (var match = Signature.Match(source, 0, index); match.Success; match = match.NextMatch())
        {
            found = match.Groups[1].Value;
        }

        return found;
    }

    private static readonly ConcurrentDictionary<string, string> FileCache = new(StringComparer.Ordinal);
}
