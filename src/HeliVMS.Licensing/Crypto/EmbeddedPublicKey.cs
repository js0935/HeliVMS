namespace HeliVMS.Licensing.Crypto;

/// <summary>
/// 內嵌於產品端之授權驗證公鑰（§19：私鑰僅存廠商，產品只內嵌公鑰）。
/// </summary>
public static class EmbeddedPublicKey
{
    /// <summary>開發測試用公鑰（正式版以新金鑰對替換，採輪替機制）。</summary>
    public const string Value =
        """
        -----BEGIN PUBLIC KEY-----
        MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA1O2zIs+hplySI0kZecAr
        LH4g0+krXo5SbDAD5OMJhGAcFEeO+NCrzi2jz/sou4bwvKwVlFZ8cRUM21xdsgFE
        Bi3WdsAWBZuvkZKt13vTHinX/3MOGF19WPKzm0wUqFy4g5Q7c/BVIY3VJjCFFnxk
        TGkWF/idvBUxsNqYBrWa6Llf6bk1xv3lo9fZHilCo6n01BDfL5PcxOMKbAaCLPi4
        tf1/2RL+ay93bvRQxwhm4dpJJHIoYI8Gka59hUVhLnG/PJXeacoOMU4hu1xzHBZD
        kHNdTGo3L4dgI8E61NP6UdxRSEjkUFfUVdFlopxawyJLfVINUJvGIDJeD9dziFqJ
        JQIDAQAB
        -----END PUBLIC KEY-----
        """;
}