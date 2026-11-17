using System;
using System.IO;
using HelmetDesk.Core;
using Xunit;

namespace HelmetDesk.Tests
{
    public class AppPathsTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "hd-paths-tests-" + Guid.NewGuid().ToString("N"));
        private string Old => Path.Combine(_root, "HelmetDesk");
        private string New => Path.Combine(_root, "HelmetDesk.Local");

        public AppPathsTests() => Directory.CreateDirectory(_root);
        public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

        private void Put(string dir, string rel, string text = "x")
        {
            var p = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllText(p, text);
        }

        [Fact]
        public void Copies_known_entries_keeps_originals_and_skips_installer_files()
        {
            Put(Old, "logs/helmetdesk-20261101.log", "log");
            Put(Old, "backups/pre-import-1.zip");
            Put(Old, "relatorios/r.zip");
            Put(Old, "last-crash.txt", "crash");
            Put(Old, "current/HelmetDesk.exe");   // Velopack install files: not ours
            Put(Old, "Update.exe");

            var msg = AppPaths.MigrateLocal(Old, New);

            Assert.NotNull(msg);
            Assert.Equal("log", File.ReadAllText(Path.Combine(New, "logs", "helmetdesk-20261101.log")));
            Assert.True(File.Exists(Path.Combine(New, "backups", "pre-import-1.zip")));
            Assert.True(File.Exists(Path.Combine(New, "relatorios", "r.zip")));
            Assert.Equal("crash", File.ReadAllText(Path.Combine(New, "last-crash.txt")));
            Assert.False(Directory.Exists(Path.Combine(New, "current")));
            Assert.False(File.Exists(Path.Combine(New, "Update.exe")));
            // originals untouched
            Assert.True(File.Exists(Path.Combine(Old, "logs", "helmetdesk-20261101.log")));
            Assert.True(File.Exists(Path.Combine(Old, "last-crash.txt")));
        }

        [Fact]
        public void Does_nothing_when_new_folder_already_has_content()
        {
            Put(Old, "logs/a.log", "old");
            Put(New, "logs/a.log", "new");

            Assert.Null(AppPaths.MigrateLocal(Old, New));
            Assert.Equal("new", File.ReadAllText(Path.Combine(New, "logs", "a.log")));
        }

        [Fact]
        public void Does_nothing_without_old_data()
        {
            Assert.Null(AppPaths.MigrateLocal(Old, New));       // old folder missing
            Put(Old, "current/HelmetDesk.exe");                  // only installer files
            Assert.Null(AppPaths.MigrateLocal(Old, New));
            Assert.False(Directory.Exists(New));
        }

        [Fact]
        public void Runs_once_second_call_is_a_no_op()
        {
            Put(Old, "logs/a.log");
            Assert.NotNull(AppPaths.MigrateLocal(Old, New));
            Put(Old, "logs/b.log");
            Assert.Null(AppPaths.MigrateLocal(Old, New));
            Assert.False(File.Exists(Path.Combine(New, "logs", "b.log")));
        }

        [Fact]
        public void Copies_a_log_that_is_open_for_writing()
        {
            Put(Old, "logs/open.log", "busy");
            using (new FileStream(Path.Combine(Old, "logs", "open.log"), FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                AppPaths.MigrateLocal(Old, New);
            Assert.Equal("busy", File.ReadAllText(Path.Combine(New, "logs", "open.log")));
        }

        [Fact]
        public void Paths_never_point_at_the_executable_folder()
        {
            var exeDir = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\');
            foreach (var p in new[] { AppPaths.DataDir, AppPaths.LocalDir, AppPaths.LogDir, AppPaths.SafetyBackupDir, AppPaths.ReportsDir })
                Assert.False(Path.GetFullPath(p).StartsWith(exeDir, StringComparison.OrdinalIgnoreCase), p);
            Assert.NotEqual(AppPaths.LegacyLocalDir, AppPaths.LocalDir);
        }
    }
}
