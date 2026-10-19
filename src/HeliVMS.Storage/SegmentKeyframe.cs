namespace HeliVMS.Storage;

/// <summary>
/// 一個關鍵幀（I-frame）在區段內的位置（§3.4 關鍵幀吸附）。
/// </summary>
/// <remarks>
/// <para>
/// <paramref name="TimeSec"/> 是距區段起點的秒數（PTS），供時間軸刻度與「T 前最近的關鍵幀」查表；
/// <paramref name="MoofOffset"/> 是該關鍵幀封包在檔案中的位元組位置——錄影檔是 fMP4
/// （<c>frag_keyframe</c>），每個 moof 都由關鍵幀開頭，這個位址就是可直接 seek 到的位置。
/// </para>
/// </remarks>
public readonly record struct SegmentKeyframe(double TimeSec, long MoofOffset);
