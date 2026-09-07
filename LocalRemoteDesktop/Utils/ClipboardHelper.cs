using System;
using System.Threading;

namespace LocalRemoteDesktop.Utils
{
    internal static class ClipboardHelper
    {
        private static readonly object SyncRoot = new object();

        public static bool TrySetText(string text, int attempts = 8, int delayMilliseconds = 40)
        {
            if (string.IsNullOrEmpty(text))
                return false;

            lock (SyncRoot)
            {
                for (var attempt = 0; attempt < attempts; attempt++)
                {
                    try
                    {
                        System.Windows.Forms.Clipboard.SetText(
                            text, System.Windows.Forms.TextDataFormat.UnicodeText);
                        return true;
                    }
                    catch (System.Runtime.InteropServices.ExternalException)
                    {
                        // 其他进程暂时持有剪贴板，稍后重试。
                    }
                    catch (InvalidOperationException)
                    {
                        // 剪贴板当前不可用，稍后重试。
                    }

                    if (attempt + 1 < attempts)
                        Thread.Sleep(delayMilliseconds);
                }
            }

            return false;
        }

        public static bool TryGetText(out string text, int attempts = 4, int delayMilliseconds = 30)
        {
            text = null;

            lock (SyncRoot)
            {
                for (var attempt = 0; attempt < attempts; attempt++)
                {
                    try
                    {
                        if (!System.Windows.Forms.Clipboard.ContainsText())
                            return false;

                        text = System.Windows.Forms.Clipboard.GetText(
                            System.Windows.Forms.TextDataFormat.UnicodeText);
                        return true;
                    }
                    catch (System.Runtime.InteropServices.ExternalException)
                    {
                    }
                    catch (InvalidOperationException)
                    {
                    }

                    if (attempt + 1 < attempts)
                        Thread.Sleep(delayMilliseconds);
                }
            }

            return false;
        }
    }
}
