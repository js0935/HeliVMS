using System.Security.Cryptography;
using System.Text;

namespace HeliVMS.Helpers;

public static class TotpHelper {
    private const int StepSeconds = 30;
    private const int CodeLength = 6;

    public static string GenerateSecret() {
        var bytes = RandomNumberGenerator.GetBytes(20);
        return Convert.ToBase64String(bytes);
    }

    public static string GenerateCode(string secret) {
        return GenerateCode(secret, GetCurrentTimeStep());
    }

    public static bool VerifyCode(string secret, string code) {
        if (string.IsNullOrEmpty(code) || code.Length != CodeLength) return false;
        if (!int.TryParse(code, out _)) return false;
        var expected = GenerateCode(secret, GetCurrentTimeStep());
        if (expected == code) return true;
        expected = GenerateCode(secret, GetCurrentTimeStep() - 1);
        if (expected == code) return true;
        expected = GenerateCode(secret, GetCurrentTimeStep() + 1);
        return expected == code;
    }

    public static string GenerateQrCodeUri(string username, string secretBase64) {
        var bytes = Convert.FromBase64String(secretBase64);
        var base32 = Base32Encode(bytes);
        return $"otpauth://totp/HeliVMS:{Uri.EscapeDataString(username)}?secret={base32}&issuer=HeliVMS";
    }

    public static string Base32Encode(byte[] data) {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var result = new StringBuilder((data.Length + 4) / 5 * 8);
        var index = 0;
        while (index < data.Length) {
            long buffer = 0;
            var bits = 0;
            for (var i = 0; i < 5 && index < data.Length; i++, index++) {
                buffer = (buffer << 8) | data[index];
                bits += 8;
            }
            while (bits > 0) {
                bits -= 5;
                var charIndex = (int)((buffer >> bits) & 0x1F);
                result.Append(alphabet[charIndex]);
            }
        }
        return result.ToString();
    }

    private static long GetCurrentTimeStep() {
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds() / StepSeconds;
    }

    private static string GenerateCode(string secret, long timeStep) {
        var key = Convert.FromBase64String(secret);
        var timeBytes = BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(timeStep));
        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(timeBytes);
        var offset = hash[^1] & 0x0F;
        var binary = (hash[offset] & 0x7F) << 24
                   | (hash[offset + 1] & 0xFF) << 16
                   | (hash[offset + 2] & 0xFF) << 8
                   | (hash[offset + 3] & 0xFF);
        var code = binary % 1000000;
        return code.ToString($"D{CodeLength}");
    }
}
