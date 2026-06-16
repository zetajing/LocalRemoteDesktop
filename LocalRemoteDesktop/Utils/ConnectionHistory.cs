using System;
using System.Collections.Generic;
using System.IO;

namespace LocalRemoteDesktop.Utils
{
    /// <summary>
    /// 历史连接记录 — 格式: ip:port|label|timestampTicks 每行一条
    /// </summary>
    public class ConnectionHistory
    {
        public string Ip { get; set; }
        public int Port { get; set; }
        public string Label { get; set; }
        public DateTime LastConnected { get; set; }

        private const int MaxEntries = 20;
        private static readonly string StorePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "connections.txt");

        public static List<ConnectionHistory> Load()
        {
            var list = new List<ConnectionHistory>();
            try
            {
                if (File.Exists(StorePath))
                {
                    foreach (var line in File.ReadAllLines(StorePath))
                    {
                        var parts = line.Split('|');
                        // 格式: ip:port|ticks|label
                        if (parts.Length >= 3 &&
                            long.TryParse(parts[1], out var ticks))
                        {
                            var addr = parts[0].Split(':');
                            if (addr.Length == 2 && int.TryParse(addr[1], out var port))
                            {
                                list.Add(new ConnectionHistory
                                {
                                    Ip = addr[0],
                                    Port = port,
                                    Label = parts[2],
                                    LastConnected = new DateTime(ticks, DateTimeKind.Utc)
                                });
                            }
                        }
                    }
                }
            }
            catch { }
            return list;
        }

        public static void Save(List<ConnectionHistory> history)
        {
            try
            {
                var dir = Path.GetDirectoryName(StorePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                using (var w = new StreamWriter(StorePath, false))
                {
                    int count = 0;
                    foreach (var h in history)
                    {
                        if (count++ >= MaxEntries) break;
                        w.WriteLine($"{h.Ip}:{h.Port}|{h.LastConnected.Ticks}|{h.Label}");
                    }
                }
            }
            catch { }
        }

        public static void Add(string ip, int port)
        {
            var history = Load();
            history.RemoveAll(h => h.Ip == ip && h.Port == port);
            history.Insert(0, new ConnectionHistory
            {
                Ip = ip,
                Port = port,
                Label = ip,
                LastConnected = DateTime.UtcNow
            });
            if (history.Count > MaxEntries)
                history.RemoveRange(MaxEntries, history.Count - MaxEntries);
            Save(history);
        }
    }
}
