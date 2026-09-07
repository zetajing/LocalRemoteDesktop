using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LocalRemoteDesktop.Security
{
    /// <summary>
    /// 为本机生成并保存短格式随机访问码。访问码是预共享密钥，不能记录到日志。
    /// </summary>
    public static class AccessCodeStore
    {
        private const int AccessCodeLength = 8;
        // 去掉 I/O/0/1，避免远程口述或查看时混淆；字符集长度为 32。
        private const string AccessCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        private const string EnabledFileName = "access-code-enabled";
        private static readonly object SyncRoot = new object();

        public static string GetOrCreate()
        {
            lock (SyncRoot)
            {
                var path = GetStoragePath();
                if (File.Exists(path))
                {
                    var existing = File.ReadAllText(path, Encoding.UTF8).Trim();
                    if (IsGeneratedAccessCode(existing))
                        return existing;
                }

                return RegenerateCore(path);
            }
        }

        public static string Regenerate()
        {
            lock (SyncRoot)
            {
                return RegenerateCore(GetStoragePath());
            }
        }

        public static bool IsEnabled()
        {
            lock (SyncRoot)
            {
                var path = GetEnabledStoragePath();
                if (!File.Exists(path))
                    return true;

                var value = File.ReadAllText(path, Encoding.UTF8).Trim();
                if (string.Equals(value, "0", StringComparison.Ordinal) ||
                    string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return true;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            lock (SyncRoot)
            {
                WriteSafely(GetEnabledStoragePath(), enabled ? "1" : "0");
            }
        }

        private static string RegenerateCore(string path)
        {
            var random = new byte[AccessCodeLength];
            using (var rng = new RNGCryptoServiceProvider())
                rng.GetBytes(random);

            try
            {
                var accessCodeChars = new char[AccessCodeLength];
                for (var i = 0; i < accessCodeChars.Length; i++)
                    accessCodeChars[i] = AccessCodeAlphabet[random[i] % AccessCodeAlphabet.Length];

                var accessCode = new string(accessCodeChars);

                WriteSafely(path, accessCode);
                return accessCode;
            }
            finally
            {
                Array.Clear(random, 0, random.Length);
            }
        }

        private static void WriteSafely(string path, string accessCode)
        {
            var directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);

            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, accessCode, new UTF8Encoding(false));

                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(temporaryPath, path, null);
                    }
                    catch (IOException)
                    {
                        File.Copy(temporaryPath, path, true);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Copy(temporaryPath, path, true);
                    }
                }
                else
                {
                    File.Move(temporaryPath, path);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }

        private static string GetStoragePath()
        {
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localData, "LocalRemoteDesktop", "access-code");
        }

        private static string GetEnabledStoragePath()
        {
            var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localData, "LocalRemoteDesktop", EnabledFileName);
        }

        private static bool IsGeneratedAccessCode(string value)
        {
            if (value == null || value.Length != AccessCodeLength)
                return false;

            for (var i = 0; i < value.Length; i++)
            {
                if (AccessCodeAlphabet.IndexOf(value[i]) >= 0)
                    continue;

                return false;
            }

            return true;
        }
    }
}
