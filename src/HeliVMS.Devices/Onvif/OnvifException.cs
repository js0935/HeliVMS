namespace HeliVMS.Devices.Onvif;

/// <summary>ONVIF 呼叫失敗之分類（供 UI 與重試策略判斷）。</summary>
public enum OnvifErrorCode
{
    /// <summary>未知／未分類錯誤。</summary>
    Unknown,

    /// <summary>認證失敗（HTTP 401/403 或 WS-Security 認證錯誤）。</summary>
    Unauthorized,

    /// <summary>設備不支援該動作（SOAP Fault ter:ActionNotSupported 等）。</summary>
    NotSupported,

    /// <summary>連線逾時。</summary>
    Timeout,

    /// <summary>無法連線（連線被拒、位址無效、連線重設）。</summary>
    Connection,

    /// <summary>設備回報 SOAP Fault。</summary>
    DeviceFault,

    /// <summary>回應格式無法解析（非 SOAP／非 XML）。</summary>
    BadResponse,

    /// <summary>使用者或呼叫端取消。</summary>
    Cancelled,
}

/// <summary>
/// ONVIF 呼叫失敗之型別化例外。訊息為可直接顯示於 UI 之中文說明，
/// <see cref="Code"/> 提供程式化判斷依據（重試、切換認證模式、略過該 Profile 等）。
/// </summary>
public sealed class OnvifException : Exception
{
    public OnvifException(OnvifErrorCode code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public OnvifErrorCode Code { get; }

    /// <summary>設備回報之 fault code（如 ter:NotAuthorized、wsse:FailedAuthentication）。</summary>
    public string? FaultCode { get; init; }

    /// <summary>是否為可重試之暫時性錯誤（逾時或連線問題）。</summary>
    public bool IsTransient => Code is OnvifErrorCode.Timeout or OnvifErrorCode.Connection;

    internal static OnvifException Unauthorized(string message, string? faultCode = null) =>
        new(OnvifErrorCode.Unauthorized, message) { FaultCode = faultCode };

    internal static OnvifException NotSupported(string message, string? faultCode = null) =>
        new(OnvifErrorCode.NotSupported, message) { FaultCode = faultCode };
}
