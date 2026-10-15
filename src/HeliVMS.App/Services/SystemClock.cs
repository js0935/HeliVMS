using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace HeliVMS.App.Services;

/// <summary>
/// 校正 Windows 系統時鐘（M305，搭配 M68 的 SNTP 用戶端）。
///
/// <para>
/// 錄影時間戳一律取系統 UTC 牆鐘，所以「NTP 對時」若不真的調整系統時間，對證據時序就沒有意義。
/// 但 <c>SetSystemTime</c> 需要 <c>SeSystemtimePrivilege</c>，一般使用者權杖預設沒有這個權限，
/// 因此這裡是盡力而為：得不到權限時回傳失敗與原因（通常是需要以系統管理員身分執行），
/// 由呼叫端把偏移量顯示給操作員，而不是靜默地什麼都沒發生。
/// </para>
/// <para>
/// 用 UTC 的 <c>SetSystemTime</c>（不是設定本地時間的 <c>SetLocalTime</c>）：本程式內部時序全部是 UTC。
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
internal static class SystemClock
{
    private const uint TokenAdjustPrivileges = 0x0020;
    private const uint TokenQuery = 0x0008;
    private const uint SePrivilegeEnabled = 0x0002;
    private const string SeSystemtimePrivilege = "SeSystemtimePrivilege";

    /// <summary>把系統 UTC 時間設為 <paramref name="utc"/>；失敗時回傳 false 並帶出原因。</summary>
    public static bool TrySetUtcNow(DateTime utc, out string? error)
    {
        error = null;
        try
        {
            // 權杖預設「持有但停用」此權限，必須先啟用；取得權限失敗就讓 SetSystemTime 回報真正原因。
            EnablePrivilege(SeSystemtimePrivilege);

            var st = ToSystemTime(utc);
            if (!SetSystemTime(ref st))
            {
                error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token))
        {
            return;
        }

        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid))
            {
                return;
            }

            var tp = new TokenPrivileges
            {
                PrivilegeCount = 1,
                Privileges = new LuidAndAttributes { Luid = luid, Attributes = SePrivilegeEnabled },
            };
            AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            CloseHandle(token);
        }
    }

    private static SystemTime ToSystemTime(DateTime utc) => new()
    {
        Year = (ushort)utc.Year,
        Month = (ushort)utc.Month,
        DayOfWeek = (ushort)utc.DayOfWeek,
        Hour = (ushort)utc.Hour,
        Minute = (ushort)utc.Minute,
        Second = (ushort)utc.Second,
        Milliseconds = (ushort)utc.Millisecond,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemTime
    {
        public ushort Year;
        public ushort Month;
        public ushort DayOfWeek;
        public ushort Hour;
        public ushort Minute;
        public ushort Second;
        public ushort Milliseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes
    {
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint PrivilegeCount;
        public LuidAndAttributes Privileges;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSystemTime(ref SystemTime time);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr token,
        [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivileges newState,
        uint bufferLength,
        IntPtr previousState,
        IntPtr returnLength);
}
