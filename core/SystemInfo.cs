using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace HelmetDesk.Core
{
    /// <summary>Technical details that go into a bug report.</summary>
    public sealed class SystemInfo
    {
        public string AppVersion { get; init; }
        public string WindowsName { get; init; }
        public string OsVersion { get; init; }
        public string OsDescription { get; init; }
        public string Architecture { get; init; }
        public string ProcessArchitecture { get; init; }
        public string Framework { get; init; }
        public string Culture { get; init; }
        public string UiCulture { get; init; }
        public DateTimeOffset Timestamp { get; init; }

        public static SystemInfo Collect(string appVersion, IClock clock = null)
        {
            var v = Environment.OSVersion.Version;
            return new SystemInfo
            {
                AppVersion = appVersion,
                WindowsName = WindowsNameFor(v),
                OsVersion = v.ToString(),
                OsDescription = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.OSArchitecture.ToString(),
                ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Framework = RuntimeInformation.FrameworkDescription,
                Culture = CultureInfo.CurrentCulture.Name,
                UiCulture = CultureInfo.CurrentUICulture.Name,
                Timestamp = (clock ?? SystemClock.Instance).Now,
            };
        }

        /// <summary>Windows 11 still reports itself as 10.0; the build number (22000+) tells them apart.</summary>
        public static string WindowsNameFor(Version v)
        {
            if (v == null) return T("Desconhecido");
            if (v.Major == 10 && v.Build >= 22000) return "Windows 11";
            if (v.Major == 10) return "Windows 10";
            if (v.Major == 6 && v.Minor == 3) return "Windows 8.1";
            if (v.Major == 6 && v.Minor == 2) return "Windows 8";
            if (v.Major == 6 && v.Minor == 1) return "Windows 7";
            return $"Windows {v.Major}.{v.Minor}";
        }

        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine(TF("Versão do app:   {0}", AppVersion));
            sb.AppendLine(TF("Windows:         {0} (build {1})", WindowsName, OsVersion));
            sb.AppendLine(TF("Sistema:         {0}", OsDescription));
            sb.AppendLine(TF("Arquitetura:     {0} (processo {1})", Architecture, ProcessArchitecture));
            sb.AppendLine($".NET:            {Framework}");
            sb.AppendLine(TF("Idioma:          {0} (interface {1})", Culture, UiCulture));
            sb.AppendLine(TF("Data e hora:     {0:yyyy-MM-dd HH:mm:ss zzz}", Timestamp));
            return sb.ToString();
        }
    }

    public static class LogTail
    {
        /// <summary>
        /// The last <paramref name="count"/> lines across the newest *.log files in <paramref name="dir"/>,
        /// read while the logger may still be writing to them.
        /// </summary>
        public static IReadOnlyList<string> Read(string dir, int count = 200)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir) || count <= 0) return result;
            var files = new DirectoryInfo(dir).GetFiles("*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name).ToList();
            foreach (var f in files)
            {
                List<string> lines;
                try
                {
                    using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var r = new StreamReader(fs, Encoding.UTF8);
                    lines = new List<string>();
                    string line;
                    while ((line = r.ReadLine()) != null) lines.Add(line);
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                int need = count - result.Count;
                result.InsertRange(0, lines.Skip(Math.Max(0, lines.Count - need)));
                if (result.Count >= count) break;
            }
            return result;
        }
    }
}
