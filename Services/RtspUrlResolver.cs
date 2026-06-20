using System.Diagnostics;
using System.IO;
using Serilog;

namespace HeliVMS.Services;

/// <summary>
/// Derives RTSP substream URLs from main stream URLs.
/// Falls back to pattern-based rules when ONVIF GetStreamUri is unavailable.
/// Based on original HeliNVR Services/RtspUrlResolver.cs.
/// </summary>
public class RtspUrlResolver
{
    private readonly List<SubstreamRule> _rules = new();
    private readonly Dictionary<string, List<string>> _brandAliases;

    public RtspUrlResolver()
    {
        LoadBuiltinRules();
        _brandAliases = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            { "hikvision", new List<string> { "hikvision", "hichip", "ezviz" } },
            { "dahua", new List<string> { "dahua", "dahuatechnology", "imou", "lopower" } },
            { "reolink", new List<string> { "reolink" } },
            { "uniview", new List<string> { "uniview", "univiewtechnology" } },
            { "aver", new List<string> { "aver", "avermedia", "averinformation" } },
        };

        // Load additional rules from JSON at Data/rtsp_substream_rules.json
        try
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var rulesPath = Path.Combine(appDir, "Data", "rtsp_substream_rules.json");
            if (File.Exists(rulesPath))
            {
                var json = File.ReadAllText(rulesPath);
                var doc = System.Text.Json.JsonSerializer.Deserialize<RulesDocument>(json);
                if (doc?.Rules is not null && doc.Rules.Count > 0)
                {
                    _rules.Clear();
                    if (_rules.Capacity < doc.Rules.Count)
                        _rules.Capacity = doc.Rules.Count;
                    for (int ri = 0; ri < doc.Rules.Count; ri++)
                    {
                        var r = doc.Rules[ri];
                        var find = r.Find ?? "";
                        var replace = r.Replace ?? "";
                        if (string.IsNullOrEmpty(find) || string.IsNullOrEmpty(replace))
                            continue;
                        _rules.Add(new SubstreamRule
                        {
                            Id = r.Id ?? "",
                            Find = find,
                            Replace = replace,
                            Brands = r.Brands ?? new List<string>()
                        });
                    }
                    Log.Debug("RtspUrlResolver: loaded {Count} rules from external JSON", _rules.Count);
                }
                if (doc?.Brands is not null)
                {
                    foreach (var kvp in doc.Brands)
                    {
                        _brandAliases[kvp.Key] = kvp.Value.Aliases ?? new List<string>();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "RtspUrlResolver: failed to load external rules");
        }
    }

    /// <summary>Derive substream URL from the main stream URL</summary>
    /// <param name="mainUrl">Main stream RTSP URL</param>
    /// <param name="manufacturer">Camera manufacturer (from ONVIF GetDeviceInformation)</param>
    /// <returns>Derived substream URL, or null if no rule matches</returns>
    public string? TryDeriveSubStreamUrl(string mainUrl, string? manufacturer = null)
    {
        if (string.IsNullOrWhiteSpace(mainUrl))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(manufacturer))
        {
            var matchedBrand = FindMatchingBrand(manufacturer);
            if (matchedBrand is not null)
            {
                var brandRules = new List<SubstreamRule>();
                for (int i = 0; i < _rules.Count; i++)
                {
                    var r = _rules[i];
                    if (r.Brands.Count == 0) { continue; }
                    bool brandMatch = false;
                    for (int j = 0; j < r.Brands.Count; j++)
                    {
                        if (string.Equals(r.Brands[j], matchedBrand, StringComparison.OrdinalIgnoreCase))
                        {
                            brandMatch = true;
                            break;
                        }
                    }
                    if (brandMatch) { brandRules.Add(r); }
                }

                foreach (var rule in brandRules)
                {
                    var result = TryApplyRule(mainUrl, rule);
                    if (result is not null)
                    {
                        Log.Debug("RtspUrlResolver: brand rule '{Id}' matched {Url} -> {Result}", rule.Id, mainUrl, result);
                        return result;
                    }
                }
            }
        }

        var genericRules = new List<SubstreamRule>(_rules.Count);
        for (int i = 0; i < _rules.Count; i++)
        {
            if (_rules[i].Brands.Count == 0)
            {
                genericRules.Add(_rules[i]);
            }
        }
        foreach (var rule in genericRules)
        {
            var result = TryApplyRule(mainUrl, rule);
            if (result is not null)
            {
                Log.Debug("RtspUrlResolver: generic rule '{Id}' matched {Url} -> {Result}", rule.Id, mainUrl, result);
                return result;
            }
        }

        foreach (var rule in _rules)
        {
            if (rule.Brands.Count == 0) { continue; }
                if (!string.IsNullOrWhiteSpace(manufacturer))
                {
                    bool brandMatch = false;
                    for (int i = 0; i < rule.Brands.Count; i++)
                    {
                        if (string.Equals(rule.Brands[i], FindMatchingBrand(manufacturer), StringComparison.OrdinalIgnoreCase))
                        {
                            brandMatch = true;
                            break;
                        }
                    }
                    if (brandMatch) { continue; }
                }

            var result = TryApplyRule(mainUrl, rule);
            if (result is not null)
            {
                Log.Debug("RtspUrlResolver: fallback rule '{Id}' matched {Url} -> {Result}", rule.Id, mainUrl, result);
                return result;
            }
        }

        Log.Debug("RtspUrlResolver: no rule matched {Url}", mainUrl);
        return null;
    }

    /// <summary>Resolve URL pair using ONVIF + fallback + resolver</summary>
    public (string MainUrl, string SubUrl) ResolvePair(
        string onvifMainUrl,
        string? onvifSubUrl,
        string fallbackMainUrl,
        string? fallbackSubUrl,
        string? manufacturer = null)
    {
        var mainUrl = !string.IsNullOrWhiteSpace(onvifMainUrl) ? onvifMainUrl : fallbackMainUrl;
        var subUrl = onvifSubUrl;

        if (string.IsNullOrWhiteSpace(subUrl) ||
            string.Equals(subUrl, mainUrl, StringComparison.OrdinalIgnoreCase))
        {
            var derived = TryDeriveSubStreamUrl(mainUrl, manufacturer)
                          ?? TryDeriveSubStreamUrl(fallbackMainUrl, manufacturer);
            if (derived is not null)
            {
                Log.Debug("RtspUrlResolver: derived substream: {Main} -> {Sub}", mainUrl, derived);
                subUrl = derived;
            }
        }

        if (string.IsNullOrWhiteSpace(subUrl))
        {
            subUrl = !string.IsNullOrWhiteSpace(fallbackSubUrl)
                ? fallbackSubUrl
                : mainUrl;
        }

        return (mainUrl, subUrl);
    }

    private static string? TryApplyRule(string url, SubstreamRule rule)
    {
        int idx = url.IndexOf(rule.Find, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) { return null; }

        if (rule.Find.StartsWith("/") && !rule.Find.EndsWith("/"))
        {
            int endIdx = idx + rule.Find.Length;
            if (endIdx < url.Length)
            {
                char next = url[endIdx];
                    if (next != '/' && next != '?' && next != '#')
                    {
                        return null;
                    }
            }
        }

        var result = url.Replace(rule.Find, rule.Replace, StringComparison.OrdinalIgnoreCase);
        return !string.Equals(result, url, StringComparison.OrdinalIgnoreCase) ? result : null;
    }

    private string? FindMatchingBrand(string manufacturer)
    {
        if (string.IsNullOrWhiteSpace(manufacturer)) return null;

        foreach (var (brandName, aliases) in _brandAliases)
        {
            if (string.Equals(manufacturer, brandName, StringComparison.OrdinalIgnoreCase))
            {
                return brandName;
            }

            bool aliasMatch = false;
            if (aliases is not null)
            {
                for (int i = 0; i < aliases.Count; i++)
                {
                    if (manufacturer.IndexOf(aliases[i], StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        aliasMatch = true;
                        break;
                    }
                }
            }
            if (aliasMatch)
            {
                return brandName;
            }
        }

        foreach (var rule in _rules)
        {
            foreach (var brand in rule.Brands)
            {
                if (manufacturer.IndexOf(brand, StringComparison.OrdinalIgnoreCase) >= 0)
                    return brand;
            }
        }

        return null;
    }

    private void LoadBuiltinRules()
    {
        Log.Debug("RtspUrlResolver: loading built-in rules");

        _rules.Add(new SubstreamRule { Id = "hik-live1s", Find = "live1s1", Replace = "live1s2", Brands = new List<string> { "hikvision" } });
        _rules.Add(new SubstreamRule { Id = "hik-channels-101-102", Find = "/Streaming/Channels/101", Replace = "/Streaming/Channels/102", Brands = new List<string> { "hikvision" } });
        _rules.Add(new SubstreamRule { Id = "hik-channels-1-2", Find = "/Streaming/Channels/1", Replace = "/Streaming/Channels/2", Brands = new List<string> { "hikvision" } });
        _rules.Add(new SubstreamRule { Id = "hik-main-sub-av", Find = "/main/", Replace = "/sub/", Brands = new List<string> { "hikvision" } });
        _rules.Add(new SubstreamRule { Id = "hik-h264-main-sub", Find = "/h264/ch1/main/", Replace = "/h264/ch1/sub/", Brands = new List<string> { "hikvision" } });
        _rules.Add(new SubstreamRule { Id = "hik-ch0-0-ch0-1", Find = "ch0_0.h264", Replace = "ch0_1.h264", Brands = new List<string> { "hikvision" } });
        _rules.Add(new SubstreamRule { Id = "dahua-subtype-00-01", Find = "subtype=00", Replace = "subtype=01", Brands = new List<string> { "dahua" } });
        _rules.Add(new SubstreamRule { Id = "dahua-subtype-0-1", Find = "subtype=0", Replace = "subtype=1", Brands = new List<string> { "dahua" } });
        _rules.Add(new SubstreamRule { Id = "reolink-main-sub", Find = "_main", Replace = "_sub", Brands = new List<string> { "reolink" } });
        _rules.Add(new SubstreamRule { Id = "reolink-stream-0-1", Find = "stream=0", Replace = "stream=1", Brands = new List<string> { "reolink" } });
        _rules.Add(new SubstreamRule { Id = "uniview-video1-video2", Find = "/media/video1", Replace = "/media/video2", Brands = new List<string> { "uniview" } });
        _rules.Add(new SubstreamRule { Id = "uniview-s0-s1", Find = "/s0/live", Replace = "/s1/live", Brands = new List<string> { "uniview" } });
        _rules.Add(new SubstreamRule { Id = "aver-live-st1-st2", Find = "live_st1", Replace = "live_st2", Brands = new List<string> { "aver" } });
        _rules.Add(new SubstreamRule { Id = "generic-stream1-stream2", Find = "stream1", Replace = "stream2", Brands = new List<string>() });
        _rules.Add(new SubstreamRule { Id = "generic-stream-main-sub", Find = "stream-main", Replace = "stream-sub", Brands = new List<string>() });
        _rules.Add(new SubstreamRule { Id = "generic-profile1-profile2", Find = "profile1", Replace = "profile2", Brands = new List<string>() });
        _rules.Add(new SubstreamRule { Id = "generic-onvif1-onvif2", Find = "onvif1", Replace = "onvif2", Brands = new List<string>() });
        _rules.Add(new SubstreamRule { Id = "generic-ch1-ch2", Find = "/ch1/", Replace = "/ch2/", Brands = new List<string>() });
        _rules.Add(new SubstreamRule { Id = "generic-channel1-channel2", Find = "channel=1", Replace = "channel=2", Brands = new List<string>() });
        _rules.Add(new SubstreamRule { Id = "generic-video1-video2", Find = "video1", Replace = "video2", Brands = new List<string>() });
        _rules.Add(new SubstreamRule { Id = "generic-av0-av1", Find = "av0", Replace = "av1", Brands = new List<string>() });
        _rules.Add(new SubstreamRule { Id = "generic-ucast-1-2", Find = "ucast/1", Replace = "ucast/2", Brands = new List<string>() });
        _rules.Add(new SubstreamRule { Id = "generic-main-sub", Find = "/main", Replace = "/sub", Brands = new List<string>() });
        _rules.Add(new SubstreamRule { Id = "generic-live1-live2", Find = "/live1", Replace = "/live2", Brands = new List<string>() });
        _rules.Add(new SubstreamRule { Id = "generic-high-low-res", Find = "HighResolutionVideo", Replace = "LowResolutionVideo", Brands = new List<string>() });

        Log.Debug("RtspUrlResolver: loaded {Count} built-in rules", _rules.Count);
    }

    private sealed class SubstreamRule
    {
        public string Id { get; set; } = "";
        public string Find { get; set; } = "";
        public string Replace { get; set; } = "";
        public List<string> Brands { get; set; } = new();
    }

#pragma warning disable CS0649
    private sealed class RulesDocument
    {
        public int Version { get; set; }
        public string? Description { get; set; }
        public List<RuleEntry>? Rules { get; set; }
        public Dictionary<string, BrandEntry>? Brands { get; set; }
    }

    private sealed class RuleEntry
    {
        public string? Id { get; set; }
        public string? Description { get; set; }
        public string? Find { get; set; }
        public string? Replace { get; set; }
        public List<string>? Brands { get; set; }
    }

    private sealed class BrandEntry
    {
        public List<string>? Aliases { get; set; }
    }
#pragma warning restore CS0649
}
