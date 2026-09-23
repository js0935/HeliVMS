namespace HeliVMS.Storage;

/// <summary>本機攝影機設定（.secrets/cameras.local.yaml；git-ignored、僅供本機測試、不入 repo）。</summary>
public sealed record LocalCameraConfig(string Id, string Name, string RtspUrl, string Username, string Password);

/// <summary>讀取 .secrets/cameras.local.yaml 之本機攝影機設定（M149 補充；不連網、純解析）。</summary>
public static class LocalCameraConfigReader
{
    /// <summary>根目錄下 .secrets/cameras.local.yaml 之相對路徑。</summary>
    public const string LocalPath = @".secrets\cameras.local.yaml";

    /// <summary>解析最小 YAML 子集：纏腰 "cameras:" 列表，每項 "- id:/name:/url:/username:/password:"。</summary>
    public static IReadOnlyList<LocalCameraConfig> Parse(string yaml)
    {
        var result = new List<LocalCameraConfig>();
        LocalCameraConfig? current = null;
        var inCameras = false;

        foreach (var raw in yaml.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            if (!inCameras)
            {
                if (line.Trim() == "cameras:")
                {
                    inCameras = true;
                }

                continue;
            }

            if (line.Trim().StartsWith("- id:"))
            {
                if (current is not null)
                {
                    result.Add(current);
                }

                current = new LocalCameraConfig(
                    Scal(line, "- id:"),
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty);
                continue;
            }

            if (current is null)
            {
                continue;
            }

            if (line.TrimStart().StartsWith("name:"))
            {
                current = current with { Name = Scal(line, "name:") };
            }
            else if (line.TrimStart().StartsWith("url:"))
            {
                current = current with { RtspUrl = Scal(line, "url:") };
            }
            else if (line.TrimStart().StartsWith("username:"))
            {
                current = current with { Username = Scal(line, "username:") };
            }
            else if (line.TrimStart().StartsWith("password:"))
            {
                current = current with { Password = Scal(line, "password:") };
            }
        }

        if (current is not null)
        {
            result.Add(current);
        }

        return result;
    }

    /// <summary>由工作目錄載入 ken根目錄下之 .secrets/cameras.local.yaml（不存在回空）。</summary>
    public static IReadOnlyList<LocalCameraConfig> Load(string workingDir)
    {
        var path = Path.Combine(workingDir, LocalPath);
        return File.Exists(path) ? Parse(File.ReadAllText(path)) : Array.Empty<LocalCameraConfig>();
    }

    private static string Scal(string line, string key)
    {
        var idx = line.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0)
        {
            return string.Empty;
        }

        var value = line[(idx + key.Length)..].Trim();
        return value.Trim('"', '\'');
    }
}