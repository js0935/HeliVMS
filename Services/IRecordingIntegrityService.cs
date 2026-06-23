using System;
using System.Threading.Tasks;

namespace HeliVMS.Services;

public interface IRecordingIntegrityService {
    void StartMonitoring();
    void StopMonitoring();
    int TotalSegments { get; }
    int CheckedSegments { get; }
    int CorruptedSegments { get; }
    event Action? StatsChanged;
    /// <summary>立即執行一次完整性檢查（非同步）</summary>
    Task ForceCheck();
}
