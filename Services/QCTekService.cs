using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using HeliVMS.SDK;
using Serilog;

namespace HeliVMS.Services;

/// <summary>
/// QCTek SDK service — fallback query engine when ONVIF WS fails.
/// Wraps QC_Onvif.dll via LoadLibrary + P/Invoke, provides OnvifQueryRtspUrl fallback.
/// </summary>
public sealed class QCTekService : IDisposable
{
    private bool _isInitialized;
    private bool _disposed;
    private readonly object _lock = new();
    private readonly Dictionary<string, IntPtr> _dllHandles = new();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);

    /// <summary>Whether the critical QC DLL was loaded successfully</summary>
    public bool IsInitialized => _isInitialized;

    /// <summary>Initialize QCTek SDK — attempt to load QC_Onvif.dll (and its dependencies)</summary>
    /// <returns>true if at least QC_Onvif.dll was loaded successfully</returns>
    public bool Initialize()
    {
        lock (_lock)
        {
            if (_isInitialized) { return true; }

            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var dllDir = Path.Combine(baseDir, "DLL");

                if (!Directory.Exists(dllDir))
                {
                    Log.Debug("[HeliVMS] QCTek DLL directory not found: {DllDir}", dllDir);
                    return false;
                }

                // QC_Onvif.dll 依賴 vcruntime140.dll + vcruntime140_1.dll（Visual C++ runtime）
                // 這些 runtime DLL 通常已在系統 PATH 或與應用程式同目錄
                string[] criticalDlls = { "QC_Onvif.dll" };
                int loaded = 0;
                var errors = new List<string>(criticalDlls.Length);

                foreach (var dll in criticalDlls)
                {
                    var path = Path.Combine(dllDir, dll);
                    if (!File.Exists(path))
                    {
                        errors.Add($"{dll}: file not found");
                        continue;
                    }

                    var handle = LoadLibrary(path);
                    if (handle != IntPtr.Zero)
                    {
                        loaded++;
                        _dllHandles[dll] = handle;
                        Log.Debug("[HeliVMS] Loaded {Dll}", dll);
                    }
                    else
                    {
                        var err = Marshal.GetLastWin32Error();
                        errors.Add($"{dll}: LoadLibrary failed (error {err})");
                        Log.Debug("[HeliVMS] Failed to load {Dll}, error code: {ErrCode}", dll, err);
                    }
                }

                if (loaded > 0)
                {
                    _isInitialized = true;
                    Log.Debug("[HeliVMS] QCTek SDK initialized: {Loaded}/{Total} DLLs loaded", loaded, criticalDlls.Length);
                    return true;
                }

                Log.Debug("[HeliVMS] QCTek SDK initialization failed: {Errors}", string.Join("; ", errors));
                return false;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[HeliVMS] QCTek SDK initialization error");
                return false;
            }
        }
    }

    /// <summary>Release all loaded QC DLLs</summary>
    public void Deinitialize()
    {
        lock (_lock)
        {
            if (!_isInitialized) { return; }

            foreach (var (name, handle) in _dllHandles)
            {
                if (handle != IntPtr.Zero)
                {
                    FreeLibrary(handle);
                    Log.Debug("[HeliVMS] Unloaded {Dll}", name);
                }
            }
            _dllHandles.Clear();
            _isInitialized = false;
        }
    }

    /// <summary>Query RTSP URL via QC_Onvif.dll (ONVIF WS fallback)</summary>
    /// <param name="deviceIp">Device IP address</param>
    /// <param name="username">Username</param>
    /// <param name="password">Password</param>
    /// <returns>RTSP URL, or empty string on failure</returns>
    public string OnvifQueryRtspUrl(string deviceIp, string username, string password)
    {
        if (!EnsureInitialized()) return string.Empty;

        try
        {
            var url = new StringBuilder(512);
            var result = QCTekWrapper.OnvifQueryRtspUrl(
                deviceIp, username ?? "", password ?? "",
                url, url.Capacity);

            return result == 0 ? url.ToString() : string.Empty;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[HeliVMS] QCTek OnvifQueryRtspUrl failed for {Ip}", deviceIp);
            return string.Empty;
        }
    }

    /// <summary>Check ONVIF authentication via QC_Onvif.dll</summary>
    public bool OnvifCheckAuth(string deviceIp, string username, string password)
    {
        if (!EnsureInitialized()) return false;

        try
        {
            return QCTekWrapper.OnvifCheckAuth(deviceIp, username ?? "", password ?? "") == 0;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[HeliVMS] QCTek OnvifCheckAuth failed for {Ip}", deviceIp);
            return false;
        }
    }

    private bool EnsureInitialized()
    {
        if (!_isInitialized)
        {
            Log.Debug("[HeliVMS] QCTekService not initialized, attempting auto-init");
            return Initialize();
        }
        return true;
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        Deinitialize();
        _disposed = true;
    }
}
