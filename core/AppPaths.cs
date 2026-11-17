using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HelmetDesk.Core
{
    /// <summary>
    /// Every folder and file the app reads or writes, in one place. Nothing lives next to the executable:
    /// the install folder can be read-only (all-users install in Program Files) and is replaced on every update.
    ///
    ///   DataDir  = %AppData%\HelmetDesk          user data (data.json, attachments, link cache) — goes in backups
    ///   LocalDir = %LocalAppData%\HelmetDesk.Local machine-local files (logs, safety backups, bug reports, crash marker)
    ///
    /// LocalDir used to be %LocalAppData%\HelmetDesk, which is exactly where the installer (Velopack, packId
    /// HelmetDesk) puts the app — and that folder is deleted on uninstall. <see cref="MigrateLocal"/> copies the
    /// old contents over once.
    ///
    /// HELMETDESK_DATA (tests / automation) points DataDir elsewhere; LocalDir then becomes "&lt;that&gt;-local".
    /// </summary>
    public static class AppPaths
    {
        public const string AppFolder = "HelmetDesk";
        public const string LocalFolder = "HelmetDesk.Local";

        private static readonly string Override = Environment.GetEnvironmentVariable("HELMETDESK_DATA") is { Length: > 0 } v ? v : null;

        public static readonly string DataDir = Override
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolder);

        public static readonly string LocalDir = Override != null
            ? Override.TrimEnd('\\', '/') + "-local"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), LocalFolder);

        /// <summary>Where LocalDir was before 1.0 (now the install folder). Null when HELMETDESK_DATA is set.</summary>
        public static readonly string LegacyLocalDir = Override != null
            ? null
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolder);

        // ----- user data (roaming) -----
        public static string DataFile => Path.Combine(DataDir, "data.json");
        public static string AttachmentsDir => Path.Combine(DataDir, "attachments");
        public static string LinksCacheDir => Path.Combine(DataDir, "links");

        // ----- machine-local -----
        public static string LogDir => Path.Combine(LocalDir, "logs");
        public static string SafetyBackupDir => Path.Combine(LocalDir, "backups");
        public static string ReportsDir => Path.Combine(LocalDir, "relatorios");
        public static string CrashMarker => Path.Combine(LocalDir, "last-crash.txt");

        /// <summary>What used to live in the old LocalDir and is copied over. Anything else there (installer files) is left alone.</summary>
        internal static readonly string[] LocalEntries = { "logs", "backups", "relatorios", "last-crash.txt" };

        /// <summary>Creates DataDir and LocalDir. Call before anything writes.</summary>
        public static void EnsureCreated()
        {
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(LocalDir);
        }

        /// <summary>One-time copy of the old local folder into the new one. Returns what happened (for the log), or null if nothing to do.</summary>
        public static string MigrateLocal() => LegacyLocalDir == null ? null : MigrateLocal(LegacyLocalDir, LocalDir);

        /// <summary>
        /// Copies the known entries from <paramref name="oldDir"/> to <paramref name="newDir"/> when the old one has any of them
        /// and the new one is empty or missing. Never deletes or overwrites anything. Returns a log line, or null if nothing was done.
        /// </summary>
        public static string MigrateLocal(string oldDir, string newDir)
        {
            if (!Directory.Exists(oldDir)) return null;
            var present = LocalEntries.Where(e => File.Exists(Path.Combine(oldDir, e)) || Directory.Exists(Path.Combine(oldDir, e))).ToList();
            if (present.Count == 0) return null;
            if (Directory.Exists(newDir) && Directory.EnumerateFileSystemEntries(newDir).Any()) return null;

            Directory.CreateDirectory(newDir);
            int files = 0;
            var failed = new List<string>();
            foreach (var e in present)
            {
                var src = Path.Combine(oldDir, e);
                var dst = Path.Combine(newDir, e);
                try
                {
                    if (File.Exists(src)) { File.Copy(src, dst, false); files++; }
                    else files += CopyTree(src, dst);
                }
                catch (Exception ex) { failed.Add($"{e} ({ex.GetType().Name})"); }
            }
            return $"Pasta local migrada de {oldDir} para {newDir}: {files} arquivo(s) copiado(s)"
                   + (failed.Count > 0 ? "; falhas: " + string.Join(", ", failed) : "") + ". Originais mantidos.";
        }

        private static int CopyTree(string src, string dst)
        {
            int n = 0;
            Directory.CreateDirectory(dst);
            foreach (var d in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(dst, Path.GetRelativePath(src, d)));
            foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(dst, Path.GetRelativePath(src, f));
                if (File.Exists(target)) continue;
                // logs may be open by a running instance: share read/write so the copy still works
                using (var i = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var o = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                    i.CopyTo(o);
                File.SetLastWriteTime(target, File.GetLastWriteTime(f));
                n++;
            }
            return n;
        }
    }
}
