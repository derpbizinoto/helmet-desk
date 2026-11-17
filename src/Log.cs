using System;
using System.IO;
using Serilog; // WriteTo.File extension

namespace HelmetDesk
{
    /// <summary>
    /// Technical log (Serilog): %LocalAppData%\HelmetDesk.Local\logs (AppPaths.LogDir), one file per day, 1 MB each, the last 5 kept.
    /// Kept out of the data folder so it never travels in backups and never holds a file open there.
    /// </summary>
    public static class Log
    {
        private static string Dir => AppPaths.LogDir;
        private static bool _ready;

        public static void Init()
        {
            if (_ready) return;
            try
            {
                Directory.CreateDirectory(Dir);
                Serilog.Log.Logger = new Serilog.LoggerConfiguration()
                    .MinimumLevel.Information()
                    .WriteTo.File(Path.Combine(Dir, "helmetdesk-.log"),
                        rollingInterval: Serilog.RollingInterval.Day,
                        fileSizeLimitBytes: 1024 * 1024,
                        rollOnFileSizeLimit: true,
                        retainedFileCountLimit: 5,
                        shared: true, // the bug report reads it while it is open
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
                    .CreateLogger();
                _ready = true;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("log init failed: " + ex.Message); }
        }

        public static void Info(string message) { System.Diagnostics.Debug.WriteLine(message); if (_ready) Serilog.Log.Information(message); }
        public static void Warn(string message, Exception ex = null) { System.Diagnostics.Debug.WriteLine(message); if (_ready) Serilog.Log.Warning(ex, message); }

        public static void Error(string message, Exception ex = null)
        {
            System.Diagnostics.Debug.WriteLine($"{message}{(ex != null ? ": " + ex.Message : "")}");
            if (_ready) Serilog.Log.Error(ex, message);
        }

        public static void Fatal(string message, Exception ex)
        {
            if (_ready) Serilog.Log.Fatal(ex, message);
            Flush();
        }

        /// <summary>Writes everything out (on exit, or when the process is about to die).</summary>
        public static void Flush() { try { _ready = false; Serilog.Log.CloseAndFlush(); } catch { } }
    }
}
