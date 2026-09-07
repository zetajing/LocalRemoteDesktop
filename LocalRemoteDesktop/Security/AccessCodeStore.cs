using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace LocalRemoteDesktop.Security
{
    /// <summary>
    /// 为本机生成并保存 256 位随机访问码。访问码是预共享密钥，不能记录到日志。
    /// </summary>
    public static class AccessCodeStore
    {
        private const int AccessCodeBytes = 32;
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

        private static string RegenerateCore(string path)
        {
            var random = new byte[AccessCodeBytes];
            using (var rng = new RNGCryptoServiceProvider())
                rng.GetBytes(random);

            try
            {
                var accessCode = Convert.ToBase64String(random)
                    .TrimEnd('=')
                    .Replace('+', '-')
                    .Replace('/', '_');

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

        private static bool IsGeneratedAccessCode(string value)
        {
            if (value == null || value.Length != 43)
                return false;

            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if ((c >= 'A' && c <= 'Z') ||
                    (c >= 'a' && c <= 'z') ||
                    (c >= '0' && c <= '9') ||
                    c == '-' || c == '_')
                {
                    continue;
                }

                return false;
            }

            return true;
        }
    }
}
