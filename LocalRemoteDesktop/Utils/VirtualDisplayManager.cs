using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;

namespace LocalRemoteDesktop.Utils
{
    /// <summary>
    /// IddSampleDriver 虚拟显示器驱动管理
    /// 驱动来自: https://github.com/ge9/IddSampleDriver
    /// </summary>
    public static class VirtualDisplayManager
    {
        private static readonly string DrvDir = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "Drivers", "IddSampleDriver");

        private static readonly string OptionFile = @"C:\IddSampleDriver\option.txt";

        private const string DriverHardwareId = "Root\\iddsampledriver";

        /// <summary>驱动是否已安装（检查设备管理器中的虚拟显示器）</summary>
        public static bool IsInstalled()
        {
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    $"SELECT * FROM Win32_PnPEntity WHERE DeviceID LIKE '{DriverHardwareId}%'"))
                {
                    return searcher.Get().Cast<ManagementObject>().Any();
                }
            }
            catch { return false; }
        }

        /// <summary>安装虚拟显示器驱动（需要管理员权限）</summary>
        public static bool Install()
        {
            try
            {
                // 确保驱动文件已解压
                if (!EnsureDriverExtracted())
                    return false;

                // 用 pnputil 安装驱动
                var psi = new ProcessStartInfo("pnputil")
                {
                    Arguments = $"/add-driver \"{Path.Combine(DrvDir, "IddSampleDriver.inf")}\" /install",
                    UseShellExecute = true,
                    Verb = "runas", // 提权
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                var proc = Process.Start(psi);
                proc?.WaitForExit(30000);
                return proc?.ExitCode == 0;
            }
            catch { return false; }
        }

        /// <summary>设置虚拟显示器分辨率（重启后生效）</summary>
        public static void SetResolution(int width, int height, int refreshRate = 60)
        {
            try
            {
                var dir = Path.GetDirectoryName(OptionFile);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                // IddSampleDriver option.txt 格式: width,height,refreshRate
                File.WriteAllText(OptionFile, $"{width},{height},{refreshRate}");
            }
            catch { }
        }

        /// <summary>卸载虚拟显示器驱动</summary>
        public static bool Uninstall()
        {
            try
            {
                // 找到已安装的驱动包
                string infName = null;
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_PnPSignedDriver WHERE DeviceName LIKE '%IddSample%'"))
                {
                    var obj = searcher.Get().Cast<ManagementObject>().FirstOrDefault();
                    if (obj != null)
                        infName = obj["InfName"]?.ToString();
                }

                if (string.IsNullOrEmpty(infName))
                    return false;

                var psi = new ProcessStartInfo("pnputil")
                {
                    Arguments = $"/delete-driver {infName} /uninstall",
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                var proc = Process.Start(psi);
                proc?.WaitForExit(30000);
                return proc?.ExitCode == 0;
            }
            catch { return false; }
        }

        /// <summary>确保驱动文件已解压到 DrvDir</summary>
        private static bool EnsureDriverExtracted()
        {
            try
            {
                var zipPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "Drivers", "IddSampleDriver.zip");

                if (!File.Exists(zipPath)) return false;
                if (Directory.Exists(DrvDir) &&
                    File.Exists(Path.Combine(DrvDir, "IddSampleDriver.inf")))
                    return true; // 已解压

                if (Directory.Exists(DrvDir))
                    Directory.Delete(DrvDir, true);

                ZipFile.ExtractToDirectory(zipPath, DrvDir);
                return true;
            }
            catch { return false; }
        }
    }
}
