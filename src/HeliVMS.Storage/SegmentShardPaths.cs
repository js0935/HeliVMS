using System.Globalization;

namespace HeliVMS.Storage;

public static class SegmentShardPaths
{
    public const string ShardPrefix = "index-";
    public const string ShardSuffix = ".db";
    public static string GetShardFileName(DateTime monthUtc) => $"{ShardPrefix}{monthUtc.Year:D4}{monthUtc.Month:D2}{ShardSuffix}";
    public static string GetShardFileName(int year,int month) => $"{ShardPrefix}{year:D4}{month:D2}{ShardSuffix}";
    public static (int Year,int Month)? TryParseShardFileName(string fileName)
    {
        if(string.IsNullOrWhiteSpace(fileName)) return null;
        if(!fileName.StartsWith(ShardPrefix,StringComparison.OrdinalIgnoreCase)) return null;
        if(!fileName.EndsWith(ShardSuffix,StringComparison.OrdinalIgnoreCase)) return null;
        var core=fileName.Substring(ShardPrefix.Length,fileName.Length-ShardPrefix.Length-ShardSuffix.Length);
        if(core.Length!=6) return null;
        if(int.TryParse(core.Substring(0,4),NumberStyles.None,CultureInfo.InvariantCulture,out var y)&&int.TryParse(core.Substring(4,2),NumberStyles.None,CultureInfo.InvariantCulture,out var m)&&m>=1&&m<=12) return (y,m);
        return null;
    }
    public static DateTime GetMonthStartUtc(DateTime utc)=>new DateTime(utc.Year,utc.Month,1,0,0,0,DateTimeKind.Utc);
    public static DateTime GetMonthEndUtc(DateTime utc){var s=GetMonthStartUtc(utc);return s.AddMonths(1);}
}
