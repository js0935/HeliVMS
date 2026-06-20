using System.IO;
using System.Text.Json;

namespace HeliVMS.Models;

public class LoginConfig
{
    public string Username { get; set; } = "";
    public string PasswordObfuscated { get; set; } = "";
    public bool RememberPassword { get; set; }
    public bool AutoLogin { get; set; }

    private static readonly string _configPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "Data", "login.json");

    /// <summary>Obfuscates a string value using XOR with 0x5A and Base64 encoding</summary>
    public static string Obfuscate(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] ^= 0x5A;
        return Convert.ToBase64String(bytes);
    }

    public static string Deobfuscate(string value)
    {
        try
        {
            var bytes = Convert.FromBase64String(value);
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] ^= 0x5A;
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }

    public static LoginConfig? Load()
    {
        try
        {
            if (!File.Exists(_configPath)) return null;
            var json = File.ReadAllText(_configPath);
            return JsonSerializer.Deserialize<LoginConfig>(json);
        }
        catch
        {
            return null;
        }
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_configPath);
            if (dir is not null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configPath, json);
        }
        catch { }
    }

    public static void Clear()
    {
        try { if (File.Exists(_configPath)) File.Delete(_configPath); } catch { }
    }
}
