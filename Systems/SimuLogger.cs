using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace SimuFarmer.Systems
{
    public static class SimuLogger
    {
        private static readonly ConcurrentQueue<string> _inMemoryLogs = new();
        private static string _logFilePath = "";
        private const int MaxInMemoryEntries = 50;

        public static void Initialize(string baseDirectory)
        {
            try
            {
                var logsDir = Path.Combine(baseDirectory, "Logs");
                if (!Directory.Exists(logsDir))
                {
                    Directory.CreateDirectory(logsDir);
                }
                _logFilePath = Path.Combine(logsDir, "SimuFarmer.log");
                Log("=== SimuFarmer Logger Initialized ===");
            }
            catch { }
        }

        public static void Log(string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";

            _inMemoryLogs.Enqueue(line);
            while (_inMemoryLogs.Count > MaxInMemoryEntries)
            {
                _inMemoryLogs.TryDequeue(out _);
            }

            if (!string.IsNullOrEmpty(_logFilePath))
            {
                try
                {
                    File.AppendAllText(_logFilePath, line + Environment.NewLine);
                }
                catch { }
            }
        }

        public static IReadOnlyList<string> GetRecentLogs()
        {
            return _inMemoryLogs.ToArray();
        }
    }
}
