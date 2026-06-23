using HeliVMS.Models;

namespace HeliVMS.Helpers;

public static class RtspUrlBuilder {
    /// <summary>External brand config (injected after BrandConfigService loads)</summary>
    public static CameraBrandConfig? BrandConfig { get; set; }

    public static string BuildRtspUrl(string ip, int port, string username, string password) {
        var url = !string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password)
            ? $"rtsp://{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(password)}@{ip}:{port}/"
            : $"rtsp://{ip}:{port}/";
        return url;
    }

    public static string BuildRtspUrlWithBrand(string ip, int port, string username, string password, string brand) {
        // 先從外部設定檔查詢
        var configPath = LookupBrandPath(brand, isSub: false);
        if (configPath is not null) {
            return BuildUrl(ip, port, username, password, configPath);
        }

        // 回退到內建硬編碼
        var path = brand?.ToLowerInvariant() switch {
            "hikvision" => "h264",
            "dahua" => "cam/realmonitor",
            "axis" => "axis-media/media.amp",
            "foscam" => "videoMain",
            "vivotek" => "live1s1.sdp",
            "panasonic" => "nphMotionJpeg",
            "sony" => "stream",
            "samsung" or "hanwha" => "profile",
            "tplink" or "bosch" or "pelco" => "stream",
            "amcrest" or "reolink" => "h264",
            "aver" or "avermedia" or "averinformation" => "live_st1",
            "uniview" => "media/video1",
            "acti" => "stream1",
            "geovision" => "CH001.sdp",
            "tiandy" => "h264_stream",
            "honeywell" => "cam/realmonitor",
            "idis" => "onvif/media",
            _ => "stream",
        };

        return BuildUrl(ip, port, username, password, path);
    }

    public static string BuildRtspUrlSubWithBrand(string ip, int port, string username, string password, string brand) {
        // 先從外部設定檔查詢
        var configPath = LookupBrandPath(brand, isSub: true);
        if (configPath is not null) {
            return BuildUrl(ip, port, username, password, configPath);
        }

        // 回退到內建硬編碼

        var path = brand?.ToLowerInvariant() switch {
            "hikvision" => "h264_Sub",
            "dahua" => "cam/realmonitor?channel=1&subtype=1",
            "axis" => "axis-media/media.amp",
            "foscam" => "videoSub",
            "vivotek" => "live1s2.sdp",
            "panasonic" => "nphMotionJpeg",
            "sony" => "stream",
            "samsung" or "hanwha" => "profile",
            "tplink" => "stream2",
            "amcrest" => "h264_Sub",
            "reolink" => "h264_sub",
            "bosch" => "stream2",
            "pelco" => "stream2",
            "aver" or "avermedia" or "averinformation" => "live_st2",
            "uniview" => "media/video2",
            "acti" => "stream2",
            "geovision" => "CH002.sdp",
            "tiandy" => "live3.sdp",
            "honeywell" => "cam/realmonitor?channel=1&subtype=1",
            "idis" => "onvif/media",
            _ => "h264_Sub",
        };

        return BuildUrl(ip, port, username, password, path);
    }

    /// <summary>Lookup brand path from external BrandConfig, returns null if not found</summary>
    private static string? LookupBrandPath(string brand, bool isSub) {
        if (BrandConfig is null) return null;
        var key = brand?.ToLowerInvariant();
        if (string.IsNullOrEmpty(key)) return null;
        BrandEntry? entry = null;
        var brands = BrandConfig.Brands;
        for (var i = 0; i < brands.Count; i++) {
            if (brands[i].Key == key) { entry = brands[i]; break; }
        }
        if (entry is null) return null;
        return isSub
            ? (!string.IsNullOrEmpty(entry.SubPath) ? entry.SubPath : entry.MainPath)
            : entry.MainPath;
    }

    private static string BuildUrl(string ip, int port, string username, string password, string path) {
        return !string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password)
            ? $"rtsp://{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(password)}@{ip}:{port}/{path}"
            : $"rtsp://{ip}:{port}/{path}";
    }

    /// <summary>Derive sub-stream URL from main stream URL (generic rules, brand-independent)</summary>
    public static string? DeriveSubStreamUrl(string? mainRtspUrl) {
        if (string.IsNullOrEmpty(mainRtspUrl)) return null;

        // 0) Aver 風格: live_st1 → live_st2
        var mainUp = mainRtspUrl.TrimEnd('/');
        if (mainUp.EndsWith("live_st1", StringComparison.OrdinalIgnoreCase)) {
            return mainUp.ReplaceLast("live_st1", "live_st2");
        }

        // 1) Dahua 風格: main.cgi?channel=1&subtype=0 → subtype=1
        if (mainRtspUrl.Contains("subtype=0", StringComparison.OrdinalIgnoreCase)) {
            return mainRtspUrl.Replace("subtype=0", "subtype=1", StringComparison.OrdinalIgnoreCase);
        }

        // 2) 有些攝影機用 streamid=0 / streamid=1
        if (mainRtspUrl.Contains("streamid=0", StringComparison.OrdinalIgnoreCase)) {
            return mainRtspUrl.Replace("streamid=0", "streamid=1", StringComparison.OrdinalIgnoreCase);
        }

        // 從 URL 中取出最後的路徑段
        var uri = TryParseRtspUri(mainRtspUrl);
        if (uri is null) return null;

        var lastSegment = uri.Segments.LastOrDefault()?.TrimEnd('/');
        if (string.IsNullOrEmpty(lastSegment)) return null;

        // 3) Hikvision 風格: h264 → h264_Sub, h265 → h265_Sub
        if (lastSegment.Equals("h264", StringComparison.OrdinalIgnoreCase)) {
            return mainRtspUrl.TrimEnd('/') + "_Sub";
        }
        if (lastSegment.Equals("h265", StringComparison.OrdinalIgnoreCase)) {
            return mainRtspUrl.TrimEnd('/') + "_Sub";
        }

        // 4) 一般風格: live → live_sub, stream → stream_sub, videoMain → videoSub
        if (lastSegment.Equals("live", StringComparison.OrdinalIgnoreCase)) {
            return mainRtspUrl.TrimEnd('/') + "_sub";
        }
        if (lastSegment.Equals("stream", StringComparison.OrdinalIgnoreCase)) {
            return mainRtspUrl.TrimEnd('/') + "2";
        }
        if (lastSegment.Equals("videoMain", StringComparison.OrdinalIgnoreCase)) {
            return mainRtspUrl.ReplaceLast("videoMain", "videoSub");
        }
        if (lastSegment.Equals("media.amp", StringComparison.OrdinalIgnoreCase)) {
            return mainRtspUrl.TrimEnd('/') + "?streamtype=sub";
        }

        // 5) 通用回退：路徑後加 _sub
        return mainRtspUrl.TrimEnd('/') + "_sub";
    }

    private static Uri? TryParseRtspUri(string url) {
        try { return new Uri(url); } catch { return null; }
    }
}

internal static class StringExtensions {
    public static string ReplaceLast(this string source, string oldValue, string newValue) {
        var idx = source.LastIndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return source;
        return source[..idx] + newValue + source[(idx + oldValue.Length)..];
    }
}
