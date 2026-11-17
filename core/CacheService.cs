using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HelmetDesk.Core
{
    /// <summary>A place holding disposable files: the entries of <see cref="Dir"/> whose name matches <see cref="Pattern"/>
    /// (files, and folders taken whole).</summary>
    public sealed record CacheTarget(string Dir, string Pattern = "*");

    /// <summary>What a clean-up freed. <see cref="Skipped"/> counts files that were in use and stayed.</summary>
    public readonly record struct CacheResult(long FreedBytes, int Deleted, int Skipped);

    /// <summary>
    /// Settings → Dados → "Limpar cache": measures and deletes files the app can rebuild or no longer needs
    /// (link previews, old bug reports, temp leftovers). The caller decides what is cache; user data never goes in a target.
    /// </summary>
    public static class CacheService
    {
        public static long Measure(IEnumerable<CacheTarget> targets) =>
            Files(targets).Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });

        public static CacheResult Clear(IEnumerable<CacheTarget> targets)
        {
            long freed = 0; int deleted = 0, skipped = 0;
            foreach (var t in targets ?? Enumerable.Empty<CacheTarget>())
            {
                foreach (var entry in Entries(t))
                {
                    foreach (var f in Directory.Exists(entry) ? SafeFiles(entry) : new[] { entry })
                    {
                        try
                        {
                            var len = new FileInfo(f).Length;
                            File.SetAttributes(f, FileAttributes.Normal); // read-only files too
                            File.Delete(f);
                            freed += len; deleted++;
                        }
                        catch { skipped++; } // in use (e.g. open in another program): left for next time
                    }
                    if (Directory.Exists(entry)) RemoveEmptyDirs(entry);
                }
            }
            return new CacheResult(freed, deleted, skipped);
        }

        /// <summary>"1,4 MB" style, pt-BR.</summary>
        public static string FormatSize(long bytes)
        {
            var pt = L.Culture;
            if (bytes < 1024) return $"{bytes} bytes";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0", pt) + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("0.0", pt) + " MB";
            return (bytes / (1024.0 * 1024 * 1024)).ToString("0.00", pt) + " GB";
        }

        private static IEnumerable<string> Files(IEnumerable<CacheTarget> targets) =>
            (targets ?? Enumerable.Empty<CacheTarget>())
                .SelectMany(Entries)
                .SelectMany(e => Directory.Exists(e) ? SafeFiles(e) : new[] { e });

        private static IEnumerable<string> Entries(CacheTarget t)
        {
            if (t == null || string.IsNullOrEmpty(t.Dir) || !Directory.Exists(t.Dir)) return Array.Empty<string>();
            try { return Directory.GetFileSystemEntries(t.Dir, string.IsNullOrEmpty(t.Pattern) ? "*" : t.Pattern); }
            catch { return Array.Empty<string>(); }
        }

        private static string[] SafeFiles(string dir)
        {
            try { return Directory.GetFiles(dir, "*", SearchOption.AllDirectories); }
            catch { return Array.Empty<string>(); }
        }

        private static void RemoveEmptyDirs(string dir)
        {
            try
            {
                foreach (var d in Directory.GetDirectories(dir)) RemoveEmptyDirs(d);
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch { }
        }
    }
}
