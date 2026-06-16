using System.Collections.Generic;

namespace LocalRemoteDesktop.Utils
{
    public class ArgumentParser
    {
        private readonly Dictionary<string, string> _args = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);

        private ArgumentParser() { }

        public bool ContainsKey(string key) => _args.ContainsKey(key);

        public string GetValue(string key) => _args.TryGetValue(key, out var val) ? val : null;

        public static ArgumentParser Parse(string[] args)
        {
            var parser = new ArgumentParser();
            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (arg.StartsWith("/") || arg.StartsWith("-"))
                {
                    var key = arg.TrimStart('/', '-').ToLowerInvariant();
                    // look ahead for a value (not starting with / or -)
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("/") && !args[i + 1].StartsWith("-"))
                    {
                        parser._args[key] = args[++i];
                    }
                    else
                    {
                        parser._args[key] = string.Empty; // flag only
                    }
                }
            }
            return parser;
        }
    }
}
