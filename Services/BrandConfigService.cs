// ============================================================
// HeliVMS - Intelligent Video Management System
// H.R. Software Development Team / Code Design: Hong Jun-Shi / Version: V1.0.0
// ============================================================

using System.IO;
using System.Net.Http;
using Serilog;
using System.Text.Json;
using HeliVMS.Helpers;
using HeliVMS.Models;

namespace HeliVMS.Services;

public interface IBrandConfigService {
    CameraBrandConfig Config { get; }
    int BrandCount { get; }
    int TotalModelCount { get; }
    void Load();
    void Save();
    Task<(int Added, int UpdatedModels, string[] Errors)> UpdateFromStrixCamDBAsync();
}

public class BrandConfigService : IBrandConfigService {
    private static readonly string ConfigPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "Data", "camera_brands.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly string StrixCamDbApi =
        "https://api.github.com/repos/eduard256/StrixCamDB/contents/brands";

    private static readonly string StrixCamDbRaw =
        "https://raw.githubusercontent.com/eduard256/StrixCamDB/main/brands";

    /// <summary>Fallback brand IDs used when GitHub API is rate-limited or unreachable</summary>
    private static readonly string[] FallbackBrandIds = [
        "hikvision", "dahua", "axis", "foscam", "vivotek",
        "panasonic", "sony", "samsung", "tplink", "bosch",
        "pelco", "amcrest", "reolink", "aver", "uniview", "acti",
        "geovision", "tiandy", "honeywell", "idis", "arecont",
        "avigilon", "mobotix", "wisenet", "jvc", "toshiba",
        "d-link", "cisco", "grandstream", "ubiquiti", "sanyo",
        "lg", "vstarcam", "wanscam", "tenvis", "airlive",
        "hualu", "zxtech", "kjlink", "milesight", "davido",
    ];

    private CameraBrandConfig _config = new();
    private readonly HttpClient _http = new() {
        Timeout = TimeSpan.FromSeconds(15),
    };

    public CameraBrandConfig Config => _config;
    public int BrandCount => _config.Brands.Count;
    public int TotalModelCount {
        get {
            var total = 0;
            var brands = _config.Brands;
            for (var i = 0; i < brands.Count; i++) {
                total += brands[i].ModelCount;
            }
            return total;
        }
    }

    public BrandConfigService() {
        Load();
        // Feed brand config to RtspUrlBuilder for RTSP path resolution
        RtspUrlBuilder.BrandConfig = _config;
    }

    public void Load() {
        try {
            if (!File.Exists(ConfigPath)) {
                _config = CreateDefaultConfig();
                Save();
                return;
            }
            var json = File.ReadAllText(ConfigPath, System.Text.Encoding.UTF8);
            var loaded = JsonSerializer.Deserialize<CameraBrandConfig>(json);
            if (loaded is not null && loaded.Brands.Count > 0) {
                _config = loaded;
            } else {
                _config = CreateDefaultConfig();
            }
        } catch {
            _config = CreateDefaultConfig();
        }

        RtspUrlBuilder.BrandConfig = _config;
    }

    public void Save() {
        try {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (dir is not null && !Directory.Exists(dir)) { Directory.CreateDirectory(dir); }
            var json = JsonSerializer.Serialize(_config, JsonOptions);
            File.WriteAllText(ConfigPath, json, System.Text.Encoding.UTF8);
        } catch { }
    }

    /// <summary>Update from StrixCamDB (github.com/eduard256/StrixCamDB) with RTSP paths</summary>
    /// <remarks>
    /// StrixCamDB is licensed under CC BY-NC 4.0.
    /// Credits to eduard256 for maintaining the database.
    /// Sources include ispyconnect.com.
    /// </remarks>
    public async Task<(int Added, int UpdatedModels, string[] Errors)> UpdateFromStrixCamDBAsync() {
        var errors = new List<string>(4);
        var added = 0;
        var updatedModels = 0;

        try {
            // Step 1: Fetch brand list from StrixCamDB
            var brandList = await FetchStrixCamBrandListAsync().ConfigureAwait(false);
            if (brandList.Count == 0) {
                errors.Add("無法連線 StrixCamDB 伺服器（GitHub API），請檢查網路後重試");
                return (0, 0, errors.ToArray());
            }

            var brandMap = new Dictionary<string, BrandEntry>(
                _config.Brands.Count, StringComparer.OrdinalIgnoreCase);
            for (var bi = 0; bi < _config.Brands.Count; bi++)
                brandMap[_config.Brands[bi].Key] = _config.Brands[bi];

            // Step 2: Download each brand JSON and merge RTSP paths
            foreach (var brandId in brandList) {
                try {
                    var entry = await DownloadAndParseStrixCamBrandAsync(brandId).ConfigureAwait(false);
                    if (entry is null) { continue; }

                    if (brandMap.TryGetValue(brandId, out var existing)) {
                        // Update model count if changed
                        if (existing.ModelCount != entry.ModelCount) {
                            existing.ModelCount = entry.ModelCount;
                            updatedModels++;
                        }
                        if (!string.IsNullOrEmpty(entry.MainPath)) {
                            existing.MainPath = entry.MainPath;
                        }
                        if (!string.IsNullOrEmpty(entry.SubPath)) {
                            existing.SubPath = entry.SubPath;
                        }
                    } else {
                        // add new entry
                        _config.Brands.Add(entry);
                        brandMap[entry.Key] = entry;
                        added++;
                    }
                } catch (Exception ex) {
                    Log.Debug("[HeliVMS] StrixCamDB failed to load: {BrandId}: {Msg}", brandId, ex.Message);
                }
            }

            if (added > 0 || updatedModels > 0) {
                Save();
                RtspUrlBuilder.BrandConfig = _config;
            }

            return (added, updatedModels, errors.ToArray());
        } catch (Exception ex) {
            errors.Add($"Update failed: {ex.Message}");
            return (0, 0, errors.ToArray());
        }
    }

    /// <summary>Fetch brand list from GitHub API with retry + fallback list</summary>
    private async Task<List<string>> FetchStrixCamBrandListAsync() {
        for (var retry = 0; retry < 3; retry++) {
            try {
                var req = new HttpRequestMessage(HttpMethod.Get, StrixCamDbApi);
                req.Headers.UserAgent.ParseAdd("HeliVMS/1.0");
                req.Headers.Accept.ParseAdd("application/vnd.github.v3+json");
                var resp = await _http.SendAsync(req).ConfigureAwait(false);

                if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                    resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests) {
                    var retryAfter = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10);
                    Log.Debug("[HeliVMS] GitHub API rate limited, retrying after {Sec}s", retryAfter.TotalSeconds);
                    await Task.Delay(retryAfter).ConfigureAwait(false);
                    continue;
                }
                if (!resp.IsSuccessStatusCode) { break; }

                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var doc = JsonDocument.Parse(json);
                var brands = new List<string>(doc.RootElement.GetArrayLength());
                foreach (var item in doc.RootElement.EnumerateArray()) {
                    var type = item.TryGetProperty("type", out var t) ? t.GetString() : "";
                    if (type != "file") { continue; }
                    var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (name is not null && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) {
                        var id = Path.GetFileNameWithoutExtension(name);
                        if (!string.IsNullOrEmpty(id)) { brands.Add(id); }
                    }
                }
                Log.Debug("[HeliVMS] Fetched {Count} brands from StrixCamDB API", brands.Count);
                return brands;
            } catch (HttpRequestException) when (retry < 2) {
                await Task.Delay(TimeSpan.FromSeconds(1 << retry)).ConfigureAwait(false);
            }
        }

        Log.Debug("[HeliVMS] GitHub API unavailable, falling back to {Count} known brands", FallbackBrandIds.Length);
        return [.. FallbackBrandIds];
    }

    /// <summary>Download and parse a StrixCam brand JSON, extracting RTSP URL patterns</summary>
    private async Task<BrandEntry?> DownloadAndParseStrixCamBrandAsync(string brandId) {
        var url = $"{StrixCamDbRaw}/{brandId}.json";
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd("HeliVMS/1.0");
        var resp = await _http.SendAsync(req).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) { return null; }

        var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var brandName = root.TryGetProperty("brand", out var bName) ? bName.GetString() ?? brandId : brandId;

        // Extract all RTSP stream URLs with model weights
        var allModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<(string Url, int Weight)> rtspStreams;
        if (root.TryGetProperty("streams", out var streams)) {
            rtspStreams = new List<(string Url, int Weight)>(streams.GetArrayLength());
            foreach (var s in streams.EnumerateArray()) {
                var protocol = s.TryGetProperty("protocol", out var proto) ? proto.GetString() : "";
                if (protocol != "rtsp") { continue; }

                var urlVal = s.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(urlVal)) { continue; }

                // Calculate weight based on model count
                var weight = 0;
                if (s.TryGetProperty("models", out var models)) {
                    foreach (var m in models.EnumerateArray()) {
                        var model = m.GetString();
                        if (model == "*") { weight += 1000; } else if (!string.IsNullOrEmpty(model)) { allModels.Add(model); weight++; }
                    }
                }
                rtspStreams.Add((urlVal, weight));
            }
        } else {
            rtspStreams = [];
        }

        if (rtspStreams.Count == 0) { return null; }

        // Sort by weight descending (most model matches first)
        rtspStreams.Sort((a, b) => b.Weight.CompareTo(a.Weight));

        var mainUrl = CleanRtspPath(rtspStreams[0].Url);
        var subUrl = rtspStreams.Count > 1 ? CleanRtspPath(rtspStreams[1].Url) : "";

        // Build alias list for brand matching
        var aliases = new List<string> { brandId.ToLowerInvariant() };
        var nameLower = brandName.ToLowerInvariant();
        if (!string.Equals(brandName, brandId, StringComparison.OrdinalIgnoreCase)) {
            aliases.Add(nameLower);
        }

        return new BrandEntry {
            Key = brandId.ToLowerInvariant(),
            Aliases = aliases,
            MainPath = mainUrl,
            SubPath = subUrl,
            ModelCount = allModels.Count,
        };
    }

    /// <summary>Clean RTSP path: strip query string, replace placeholders</summary>
    private static string CleanRtspPath(string url) {
        // Strip query string; keep only the path portion
        var qIdx = url.IndexOf('?');
        var path = qIdx >= 0 ? url[..qIdx] : url;

        // Replace common placeholders with defaults
        path = path.Replace("[CHANNEL]", "1")
                   .Replace("[CHANNEL+1]", "1")
                   .Replace("[USERNAME]", "")
                   .Replace("[PASSWORD]", "")
                   .Replace("[USER]", "")
                   .Replace("[PASS]", "")
                   .Replace("[PWD]", "")
                   .Replace("[AUTH]", "")
                   .Replace("[TOKEN]", "");

        path = path.TrimStart('/');
        return path;
    }

    private static CameraBrandConfig CreateDefaultConfig() {
        return new CameraBrandConfig {
            Version = 1,
            Brands =
            [
                new() { Key = "hikvision",  Aliases = ["hikvision"],                         MainPath = "h264",         SubPath = "h264_Sub" },
                new() { Key = "dahua",      Aliases = ["dahua"],                              MainPath = "cam/realmonitor", SubPath = "cam/realmonitor?channel=1&subtype=1" },
                new() { Key = "axis",       Aliases = ["axis"],                              MainPath = "axis-media/media.amp", SubPath = "axis-media/media.amp" },
                new() { Key = "foscam",     Aliases = ["foscam"],                            MainPath = "videoMain",    SubPath = "videoSub" },
                new() { Key = "vivotek",    Aliases = ["vivotek"],                           MainPath = "live1s1.sdp",  SubPath = "live1s2.sdp" },
                new() { Key = "panasonic",  Aliases = ["panasonic"],                         MainPath = "nphMotionJpeg", SubPath = "nphMotionJpeg" },
                new() { Key = "sony",       Aliases = ["sony"],                              MainPath = "stream",       SubPath = "stream" },
                new() { Key = "samsung",    Aliases = ["samsung", "hanwha"],                 MainPath = "profile",      SubPath = "profile" },
                new() { Key = "tplink",     Aliases = ["tplink"],                            MainPath = "stream",       SubPath = "stream2" },
                new() { Key = "bosch",      Aliases = ["bosch"],                             MainPath = "stream",       SubPath = "stream2" },
                new() { Key = "pelco",      Aliases = ["pelco"],                             MainPath = "stream",       SubPath = "stream2" },
                new() { Key = "amcrest",    Aliases = ["amcrest"],                           MainPath = "h264",         SubPath = "h264_Sub" },
                new() { Key = "reolink",    Aliases = ["reolink"],                           MainPath = "h264",         SubPath = "h264_sub" },
                new() { Key = "aver",       Aliases = ["aver", "avermedia", "aver information"], MainPath = "live_st1", SubPath = "live_st2" },

                // Additional brands (non-StrixCam, manually maintained)
                new() { Key = "uniview",    Aliases = ["uniview"],                             MainPath = "media/video1", SubPath = "media/video2" },
                new() { Key = "acti",       Aliases = ["acti"],                               MainPath = "stream1",      SubPath = "stream2" },
                new() { Key = "geovision",  Aliases = ["geovision", "geo vision"],            MainPath = "CH001.sdp",    SubPath = "CH002.sdp" },
                new() { Key = "tiandy",     Aliases = ["tiandy"],                              MainPath = "h264_stream",  SubPath = "live3.sdp" },
                new() { Key = "honeywell",  Aliases = ["honeywell"],                          MainPath = "cam/realmonitor", SubPath = "" },
                new() { Key = "idis",       Aliases = ["idis"],                               MainPath = "onvif/media",  SubPath = "" },
            ]
        };
    }
}
