using System.Globalization;
using System.Text;

namespace HeliVMS.Recording;

/// <summary>矯正投影格式（輸入限 fisheye/dfisheye/equirect；輸出限 flat/equirect/c3x2）。</summary>
public enum DewarpProjection
{
    Fisheye,
    DualFisheye,
    Equirect,
    Flat,
    Cubemap3x2,
}

/// <summary>魚眼矯正參數（角度單位為度）。</summary>
public sealed record DewarpSettings
{
    public DewarpProjection Input { get; init; } = DewarpProjection.Fisheye;

    public DewarpProjection Output { get; init; } = DewarpProjection.Flat;

    public double InputHFov { get; init; } = 180;

    public double InputVFov { get; init; } = 180;

    public double HFov { get; init; } = 90;

    public double VFov { get; init; } = 90;

    public double Yaw { get; init; }

    public double Pitch { get; init; }

    public double Roll { get; init; }

    public int Width { get; init; } = 1280;

    public int Height { get; init; } = 720;

    public static DewarpSettings Default { get; } = new();
}

/// <summary>
/// 產生 ffmpeg 魚眼矯正 filter（§14.7 #2 Dewarping）：以內建 <c>v360</c> 將魚眼/全景
/// 投影轉為平面或全景輸出。純字串建構，測試不需 ffmpeg。
/// </summary>
public static class DewarpFilter
{
    public static string Build(DewarpSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var input = InputToken(settings.Input);
        var output = OutputToken(settings.Output);

        ValidateFov(settings.InputHFov, nameof(settings.InputHFov));
        ValidateFov(settings.InputVFov, nameof(settings.InputVFov));
        ValidateFov(settings.HFov, nameof(settings.HFov));
        ValidateFov(settings.VFov, nameof(settings.VFov));
        ValidateAngle(settings.Yaw, nameof(settings.Yaw));
        ValidateAngle(settings.Pitch, nameof(settings.Pitch));
        ValidateAngle(settings.Roll, nameof(settings.Roll));

        if (settings.Output == DewarpProjection.Flat && (settings.HFov <= 0 || settings.VFov <= 0))
        {
            throw new InvalidOperationException("輸出投影為 flat 時，水平與垂直視角必須大於 0。");
        }

        if (settings.Width < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings.Width), "輸出寬度不可為負。");
        }

        if (settings.Height < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(settings.Height), "輸出高度不可為負。");
        }

        var sb = new StringBuilder("v360=input=");
        sb.Append(input).Append(":output=").Append(output);
        sb.Append(":ih_fov=").Append(Num(settings.InputHFov));
        sb.Append(":iv_fov=").Append(Num(settings.InputVFov));
        sb.Append(":h_fov=").Append(Num(settings.HFov));
        sb.Append(":v_fov=").Append(Num(settings.VFov));
        sb.Append(":yaw=").Append(Num(settings.Yaw));
        sb.Append(":pitch=").Append(Num(settings.Pitch));
        sb.Append(":roll=").Append(Num(settings.Roll));

        if (settings.Width > 0 && settings.Height > 0)
        {
            sb.Append(":w=").Append(settings.Width);
            sb.Append(":h=").Append(settings.Height);
        }

        return sb.ToString();
    }

    public static string ToToken(DewarpProjection projection) => projection switch
    {
        DewarpProjection.Fisheye => "fisheye",
        DewarpProjection.DualFisheye => "dfisheye",
        DewarpProjection.Equirect => "equirect",
        DewarpProjection.Flat => "flat",
        DewarpProjection.Cubemap3x2 => "c3x2",
        _ => throw new ArgumentOutOfRangeException(nameof(projection), "未知的投影格式。"),
    };

    private static string InputToken(DewarpProjection projection) => projection switch
    {
        DewarpProjection.Fisheye or DewarpProjection.DualFisheye or DewarpProjection.Equirect =>
            ToToken(projection),
        _ => throw new ArgumentException("輸入投影僅支援 fisheye、dfisheye 或 equirect。", "Input"),
    };

    private static string OutputToken(DewarpProjection projection) => projection switch
    {
        DewarpProjection.Flat or DewarpProjection.Equirect or DewarpProjection.Cubemap3x2 =>
            ToToken(projection),
        _ => throw new ArgumentException("輸出投影僅支援 flat、equirect 或 c3x2。", "Output"),
    };

    private static void ValidateFov(double value, string name)
    {
        if (double.IsNaN(value) || value < 0 || value > 360)
        {
            throw new ArgumentOutOfRangeException(name, "視角需介於 0 至 360 度。");
        }
    }

    private static void ValidateAngle(double value, string name)
    {
        if (double.IsNaN(value) || value < -180 || value > 180)
        {
            throw new ArgumentOutOfRangeException(name, "角度需介於 -180 至 180 度。");
        }
    }

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
