using System.Text.RegularExpressions;

namespace HeliVMS.App.Tests;

/// <summary>
/// 保證「沒有任何 <c>.csproj</c> 落在 CI 的編譯範圍之外」。
///
/// 這個 repository 曾經有兩個都叫 <c>HeliVMS.Decoder</c> 的專案，而且被建置的那個是空的：
/// <c>src/HeliVMS.Decoder</c> 只有 csproj 與 lock file、一行原始碼都沒有，卻被
/// <c>App</c> 與 <c>Media</c> 以 ProjectReference 引用；真正有程式碼的
/// <c>Tools/HeliVMS.Decoder</c>（命名管線、硬體解碼自動偵測、影格轉換）不在 slnx 裡，
/// 所以從初始提交起就沒有任何建置編譯過它。它壞掉時是一個 CS1587 ——
/// XML 註解掛在 top-level statements 檔案的 local function 上。
///
/// 症狀是最惡劣的那種：<c>dotnet build HeliVMS.slnx</c> 全綠，真實程式碼從未編譯。
/// 六個檔案可以任意腐化而沒有任何回饋。這份契約把「記得把新專案加進 slnx」
/// 從人的記憶變成 CI 會擋下的錯誤。
/// </summary>
public sealed class SolutionCoverageTests
{
    /// <summary>刻意不納入建置的目錄前綴。底線開頭是這份 repo 標示「已棄用」的既有慣例。</summary>
    private static readonly string[] ExemptPrefixes = ["Tools/_deprecated/", "Tools/_archived/"];

    /// <summary>
    /// 沒進解決方案的專案等於沒有任何東西會編譯它。
    ///
    /// 現場症狀：CI 全綠，但該專案的原始碼可能壞了好幾年。
    /// 操作員看到的是「功能沒反應」，日誌裡連一行編譯錯誤都沒有——
    /// 因為那段碼根本沒被編譯過。
    /// </summary>
    [Fact]
    public void 每個專案都在解決方案內被編譯()
    {
        var root = SourceContract.RepoRoot();
        var declared = DeclaredProjects(root);
        var undeclared = new List<string>();

        foreach (var project in AllProjectsOnDisk(root))
        {
            var relative = Relative(root, project);
            if (ExemptPrefixes.Any(relative.StartsWith))
            {
                continue;
            }

            if (!declared.Contains(relative))
            {
                undeclared.Add(relative);
            }
        }

        Assert.True(
            undeclared.Count == 0,
            "下列專案不在 HeliVMS.slnx 內，不會被任何建置編譯："
            + string.Join("、", undeclared)
            + "。請加入 slnx（Tools 類請放 /Tools 資料夾），"
            + "或若確定棄用，請移到 Tools/_deprecated/ 以表明意圖。");
    }

    /// <summary>
    /// 解決方案宣告的專案必須真的存在，否則建置會整個失敗而不是靜默漏掉。
    ///
    /// 這一條是給「刪了專案卻忘記移除 slnx 條目」的情境：
    /// 那會讓所有人在本機與 CI 都看到紅燈，屬於容易發現的錯誤，
    /// 但擋在前面仍然比事後回頭找便宜。
    /// </summary>
    [Fact]
    public void 解決方案內的專案都真的存在()
    {
        var root = SourceContract.RepoRoot();
        var missing = DeclaredProjects(root)
            .Where(relative => !File.Exists(Path.Combine(root, relative)))
            .ToList();

        Assert.True(
            missing.Count == 0,
            "HeliVMS.slnx 宣告了不存在的專案："
            + string.Join("、", missing)
            + "。請移除 slnx 條目或還原該專案目錄。");
    }

    /// <summary>
    /// 每個宣告進 slnx 的專案都必須有 <c>packages.lock.json</c>。
    ///
    /// CI 執行 <c>dotnet restore --locked-mode</c>（.github/workflows/ci.yml），
    /// 而 <c>RestorePackagesWithLockFile=true</c> 是 Directory.Build.props 的全 repo 設定。
    /// 少了 lock file 會出現最難查的方向錯誤：本機 <c>dotnet build</c> 綠，
    /// CI 紅（NU1004）。「本機過、CI 爆」通常會被誤判成 runner 或網路問題，
    /// 實際上是版權設定沒跟上。
    /// </summary>
    [Fact]
    public void 解決方案內的專案都有鎖定相依版本()
    {
        var root = SourceContract.RepoRoot();
        var missing = DeclaredProjects(root)
            .Where(relative => !File.Exists(
                Path.Combine(root, Path.GetDirectoryName(relative)!, "packages.lock.json")))
            .ToList();

        Assert.True(
            missing.Count == 0,
            "下列專案缺 packages.lock.json，CI 的 --locked-mode restore 會失敗："
            + string.Join("、", missing)
            + "。請執行 `dotnet restore " + "HeliVMS.slnx" + "` 後一併提交該檔。");
    }

    private static readonly Regex ProjectPath =
        new("Project\\s+Path\\s*=\\s*\"([^\"]+)\"", RegexOptions.Compiled);

    /// <summary>slnx 宣告的專案路徑，統一成 <c>/</c> 分隔並正規化大小寫以利比對。</summary>
    private static HashSet<string> DeclaredProjects(string root)
    {
        var slnx = File.ReadAllText(Path.Combine(root, "HeliVMS.slnx"));
        return ProjectPath.Matches(slnx)
            .Select(m => m.Groups[1].Value.Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 磁碟上所有 <c>.csproj</c>。<c>bin</c>/<c>obj</c> 必須排除：
    /// 組建產物裡的 <c>.AssemblyReference.cache</c> 與轉發的 csproj 副本會讓清單膨脹。
    /// </summary>
    private static IEnumerable<string> AllProjectsOnDisk(string root)
    {
        foreach (var dir in new[] { "src", "Tools", "tests" })
        {
            var full = Path.Combine(root, dir);
            if (!Directory.Exists(full))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(full, "*.csproj", SearchOption.AllDirectories))
            {
                if (file.Replace('\\', '/').Contains("/bin/", StringComparison.Ordinal)
                    || file.Replace('\\', '/').Contains("/obj/", StringComparison.Ordinal))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');
}