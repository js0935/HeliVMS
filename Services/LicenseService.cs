// ============================================================
// HeliVMS - 智慧影像管理系統     禾秝軟體開發團隊 / 代碼設計：洪俊士 / 版本：V1.0.0
// ============================================================

using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using System.Management;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HeliVMS.Models;

namespace HeliVMS.Services;

public sealed class LicenseService : ILicenseService {
    private LicenseInfo _license = new();
    private readonly IEventService _eventLog;

    private static readonly string SystemDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "HeliVMS");

    private static readonly string LicenseFilePath = Path.Combine(SystemDataDir, "license.dat");

    private static readonly string LegacyLicensePath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "license.lic");

    // HMAC secret MUST match LicenseKeyGen ??keep in sync
    private static readonly byte[] HmacSecret =
        [0x1A, 0x2B, 0x3C, 0x4D, 0x5E, 0x6F, 0x70, 0x81,
         0x92, 0xA3, 0xB4, 0xC5, 0xD6, 0xE7, 0xF8, 0x09];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly string[] LicenseTypes =
        ["Trial", "Basic", "Professional", "Enterprise", "Ultimate", "Custom"];

    private static readonly string[] Licensees =
        ["HeliVMS", "內部測試"];

    // MachineId is expensive; Load() defers WMI query by 3-5 seconds
    private static string? _cachedMachineId;
    private static readonly Lock _machineIdLock = new();

    public LicenseInfo CurrentLicense => _license;

    public LicenseService(IEventService eventLog) {
        _eventLog = eventLog;
        Load();
    }

    public void Load() {
        try {
            if (File.Exists(LicenseFilePath)) {
                var json = File.ReadAllText(LicenseFilePath, Encoding.UTF8);
                var loaded = JsonSerializer.Deserialize<LicenseInfo>(json);
                if (loaded is not null && !string.IsNullOrEmpty(loaded.LicenseKey)) {
                    _license = loaded;

                    // MachineId comparison deferred: WMI query takes 3-5 seconds in background
                    if (!string.IsNullOrEmpty(loaded.MachineId)) {
                        var savedMachineId = loaded.MachineId;
                        _ = Task.Run(() => {
                            try {
                                var currentMachineId = ComputeMachineId();
                                if (currentMachineId != savedMachineId) {
                                    _license.InvalidationReason = "機器ID已變更";
                                    _eventLog.LogWarning(EventCategories.Setting, "LicenseService",
                                        "已儲存機器ID不符", $"已儲存: {savedMachineId} / 目前: {currentMachineId}");
                                }
                            } catch (Exception ex) {
                                Log.Debug("[HeliVMS] Deferred machine ID check error: {Msg}", ex.Message);
                            }
                        });
                    }

                    Log.Debug("[HeliVMS] License loaded from {LicenseFilePath}", LicenseFilePath);
                    return;
                }
            }

            if (File.Exists(LegacyLicensePath)) {
                MigrateFromLegacy();
                return;
            }
        } catch (Exception ex) {
            Log.Debug("[HeliVMS] License load error: {Msg}", ex.Message);
        }

        _license = new LicenseInfo();
    }

    public bool Activate(string licenseKey) {
        try {
            var key = licenseKey.Trim();
            if (string.IsNullOrEmpty(key)) {
                return false;
            }

            var parts = key.Split('-');
            if (parts.Length != 4) {
                return false;
            }

            var invalid = false;
            for (var i = 0; i < parts.Length; i++) {
                var p = parts[i];
                var allHex = true;
                for (var ci = 0; ci < p.Length; ci++) { if (!Uri.IsHexDigit(p[ci])) { allHex = false; break; } }
                if (p.Length != 4 || !allHex) { invalid = true; break; }
            }
            if (invalid) { return false; }

            var aaaaBbbb = parts[0] + parts[1];
            var ccccDddd = parts[2] + parts[3];

            var decoded = DecodeKeyParams(aaaaBbbb, ccccDddd);
            if (decoded is null) {
                return false;
            }

            var (maxCameras, typeId, licenseeId, expiryYears, month, day) = decoded.Value;

            var licenseType = typeId < LicenseTypes.Length ? LicenseTypes[typeId] : "Custom";
            var licensee = licenseeId < Licensees.Length ? Licensees[licenseeId] : "";

            string? expiryDate = null;
            if (expiryYears > 0 && month > 0 && day > 0) {
                try {
                    expiryDate = new DateOnly(2025 + expiryYears, month, day)
                        .ToString("yyyy-MM-dd");
                } catch {
                    return false;
                }
            }
            var currentMachineId = ComputeMachineId();
            var storedPrefix = Convert.ToHexString(
                Convert.FromHexString(aaaaBbbb + ccccDddd)[6..8]);
            if (currentMachineId.Length < 4 || storedPrefix != currentMachineId[..4]) {
                _eventLog.LogWarning(EventCategories.Setting, "LicenseService",
                    "機器ID比對失敗，授權檔案可能被複製",
                    $"已儲存: {storedPrefix} / 目前: {(currentMachineId.Length >= 4 ? currentMachineId[..4] : currentMachineId)}");
                return false;
            }

            _license = new LicenseInfo {
                LicenseKey = key,
                ProductName = "HeliVMS Professional",
                Licensee = licensee,
                LicenseType = licenseType,
                MaxCameras = maxCameras,
                ExpiryDate = expiryDate,
                MachineId = currentMachineId,
                ActivatedAt = DateTime.Now
            };

            Save();
            _eventLog.LogInfo(EventCategories.Setting, "LicenseService",
                $"License imported: type={licenseType}, max={maxCameras}, expiry={expiryDate ?? "N/A"}");
            return true;
        } catch (Exception ex) {
            Log.Debug("[HeliVMS] License activate error: {Msg}", ex.Message);
            return false;
        }
    }

    public void Remove() {
        try {
            if (File.Exists(LicenseFilePath)) {
                File.Delete(LicenseFilePath);
                _eventLog.LogWarning(EventCategories.Setting, "LicenseService", "License removed");
            }
        } catch (Exception ex) {
            Log.Debug("[HeliVMS] License remove error: {Msg}", ex.Message);
        }

        _license = new LicenseInfo();
    }

    public int GetMaxCameras() {
        return _license.IsValid ? _license.MaxCameras : 4;
    }

    public string ExportLicense() {
        var json = JsonSerializer.Serialize(_license, JsonOptions);
        return json;
    }

    public bool ImportLicense(string filePath) {
        try {
            if (!File.Exists(filePath)) { return false; }

            var json = File.ReadAllText(filePath, Encoding.UTF8);
            var imported = JsonSerializer.Deserialize<LicenseInfo>(json);
            if (imported is null || string.IsNullOrEmpty(imported.LicenseKey)) {
                return false;
            }

            imported.MachineId = ComputeMachineId();
            imported.InvalidationReason = null;

            _license = imported;
            Save();
            _eventLog.LogInfo(EventCategories.Setting, "LicenseService",
                $"License imported from: {filePath}");
            return true;
        } catch (Exception ex) {
            Log.Debug("[HeliVMS] License import error: {Msg}", ex.Message);
            return false;
        }
    }

    private void Save() {
        try {
            if (!Directory.Exists(SystemDataDir)) {
                Directory.CreateDirectory(SystemDataDir);
            }

            var json = JsonSerializer.Serialize(_license, JsonOptions);
            File.WriteAllText(LicenseFilePath, json, Encoding.UTF8);
            Log.Debug("[HeliVMS] License saved to {Path}", LicenseFilePath);
        } catch (Exception ex) {
            Log.Debug("[HeliVMS] License save error: {Msg}", ex.Message);
        }
    }

    private void MigrateFromLegacy() {
        try {
            var content = File.ReadAllText(LegacyLicensePath, Encoding.UTF8);
            var parts = content.Split('|');
            var machineId = ComputeMachineId();

            _license = new LicenseInfo {
                LicenseKey = parts.Length >= 6 ? parts[5] : "legacy",
                ProductName = parts.Length >= 1 ? parts[0] : "HeliVMS",
                Licensee = parts.Length >= 2 ? parts[1] : "",
                LicenseType = parts.Length >= 3 ? parts[2] : "",
                ExpiryDate = parts.Length >= 4 ? parts[3] : null,
                MaxCameras = parts.Length >= 5 && int.TryParse(parts[4], out var max) ? max : 4,
                MachineId = machineId,
                ActivatedAt = DateTime.Now
            };

            Save();
            Log.Debug("[HeliVMS] License migrated from legacy file");
        } catch (Exception ex) {
            Log.Debug("[HeliVMS] License migration error: {Msg}", ex.Message);
            _license = new LicenseInfo();
        }
    }

    /// <summary>
    /// Decode 16 hex chars (8 bytes = AAAA-BBBB-CCCC-DDDD) ??license parameters.
    /// Byte layout:
    ///   [0] maxCameras (0-255, 0=invalid)
    ///   [1] typeId (bits 7-4) | licenseeId (bits 3-0)
    ///   [2] yearsSince2025 (0=no expiry, 1-255)
    ///   [3] month (1-12; ignored when [2]=0)
    ///   [4] day (1-31; ignored when [2]=0)
    ///   [5] HMAC-SHA256(data[0..4] + ASCII(prefix[0..4]))[0]
    ///   [6..7] devicePrefix hex[0..3] as raw bytes
    /// </summary>
    internal static (int maxCameras, int typeId, int licenseeId, int expiryYears, int month, int day)? DecodeKeyParams(
        string aaaaBbbb, string ccccDddd) {
        try {
            var raw = Convert.FromHexString(aaaaBbbb + ccccDddd);
            if (raw.Length != 8) { return null; }

            var storedPrefix = Convert.ToHexString(raw[6..8]);
            var suffix = Encoding.ASCII.GetBytes(storedPrefix);
            var hmacInput = new byte[5 + suffix.Length];
            Buffer.BlockCopy(raw, 0, hmacInput, 0, 5);
            Buffer.BlockCopy(suffix, 0, hmacInput, 5, suffix.Length);
            using var hmac = new HMACSHA256(HmacSecret);
            var expected = hmac.ComputeHash(hmacInput)[0];
            if (raw[5] != expected) {
                return null;
            }

            var maxCameras = raw[0];
            var typeId = (raw[1] >> 4) & 0x0F;
            var licenseeId = raw[1] & 0x0F;
            var expiryYears = raw[2];
            var month = raw[3];
            var day = raw[4];

            if (maxCameras == 0) { return null; }

            return (maxCameras, typeId, licenseeId, expiryYears, month, day);
        } catch {
            return null;
        }
    }

    internal static string ComputeMachineId() {
        if (_cachedMachineId is not null) { return _cachedMachineId; }
        lock (_machineIdLock) {
            if (_cachedMachineId is not null) { return _cachedMachineId; }
            _cachedMachineId = ComputeMachineIdInternal();
            return _cachedMachineId;
        }
    }

    private static string ComputeMachineIdInternal() {
        try {
            var parts = new List<string>(3);

            try {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT ProcessorId FROM Win32_Processor");
                foreach (var obj in searcher.Get()) {
                    var val = obj["ProcessorId"]?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(val)) {
                        parts.Add(val);
                    }
                }
            } catch { }

            try {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT SerialNumber FROM Win32_BaseBoard");
                foreach (var obj in searcher.Get()) {
                    var val = obj["SerialNumber"]?.ToString()?.Trim();
                    if (!string.IsNullOrEmpty(val) &&
                        !val.Equals("To be filled by O.E.M.", StringComparison.OrdinalIgnoreCase) &&
                        !val.Equals("Default string", StringComparison.OrdinalIgnoreCase)) {
                        parts.Add(val);
                    }
                }
            } catch { }

            if (parts.Count < 2) {
                try {
                    var allDrives = DriveInfo.GetDrives();
                    DriveInfo? drive = null;
                    for (var di = 0; di < allDrives.Length; di++) {
                        var d = allDrives[di];
                        if (d.IsReady && d.DriveType == DriveType.Fixed) { drive = d; break; }
                    }
                    if (drive is not null) {
                        var volumeId = drive.RootDirectory.FullName;
                        var hash = Convert.ToHexString(
                            SHA256.HashData(Encoding.UTF8.GetBytes(volumeId)));
                        parts.Add(hash[..16]);
                    }
                } catch { }
            }

            if (parts.Count == 0) {
                return "UNKNOWN";
            }

            var raw = string.Join("|", parts);
            var finalHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
            return finalHash[..32];
        } catch {
            return "UNKNOWN";
        }
    }
}
