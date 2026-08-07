using System;
using System.Security.Cryptography;
using System.Text;

namespace MaxChemical.DtuServer.Services
{
    /// <summary>密码/密钥相关的安全工具:PBKDF2 哈希、随机凭证、设备标识码生成。</summary>
    public static class SecurityUtil
    {
        private const int Pbkdf2Iterations = 100_000;
        private const int SaltBytes = 16;
        private const int HashBytes = 32;

        // ---- PBKDF2 哈希(用于登录密码 + API Secret)----

        public static (string hash, string salt) Hash(string secret)
        {
            byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
            byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(secret), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, HashBytes);
            return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
        }

        public static bool Verify(string secret, string hashB64, string saltB64)
        {
            if (string.IsNullOrEmpty(hashB64) || string.IsNullOrEmpty(saltB64)) return false;
            try
            {
                byte[] salt = Convert.FromBase64String(saltB64);
                byte[] expected = Convert.FromBase64String(hashB64);
                byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
                    Encoding.UTF8.GetBytes(secret), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, expected.Length);
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            catch { return false; }
        }

        // ---- 随机凭证 ----

        /// <summary>API Key:公开标识,前缀 mk_ 便于识别。</summary>
        public static string NewApiKey() => "mk_" + RandomHex(16);

        /// <summary>API Secret:只在创建时返回一次,库里只存哈希。前缀 ms_。</summary>
        public static string NewApiSecret() => "ms_" + RandomHex(24);

        private static string RandomHex(int bytes) =>
            Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();

        // ---- 设备标识码 ----

        // 去掉易混淆字符(无 I O 0 1),贴在设备上人也能手输。
        private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        /// <summary>生成设备标识码,形如 MX-7K3Q-9F2A。</summary>
        public static string NewDeviceCode()
        {
            char[] g1 = RandomChars(4);
            char[] g2 = RandomChars(4);
            return $"MX-{new string(g1)}-{new string(g2)}";
        }

        /// <summary>
        /// 生成 DTU 登录包序列号:无分隔符、无易混淆字符,方便手填进 DTU。
        /// 形如 HT8KP3Q9F2A6(前缀 HT + 10 位)。
        /// </summary>
        public static string NewDtuSerial()
        {
            return "HT" + new string(RandomChars(10));
        }

        private static char[] RandomChars(int n)
        {
            var result = new char[n];
            for (int i = 0; i < n; i++)
                result[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
            return result;
        }
    }
}
