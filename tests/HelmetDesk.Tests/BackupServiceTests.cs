using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using HelmetDesk.Core;
using Xunit;

namespace HelmetDesk.Tests
{
    public sealed class FakeClock : IClock
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 12, 15, 30, 0, TimeSpan.FromHours(-3));
    }

    /// <summary>A throwaway folder layout: Root/HelmetDesk (data), Root/safety, Root/out.</summary>
    public sealed class Sandbox : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "hd-tests-" + Guid.NewGuid().ToString("N"));
        public string DataDir => Path.Combine(Root, "HelmetDesk");
        public string SafetyDir => Path.Combine(Root, "safety");
        public string OutDir => Path.Combine(Root, "out");
        public string DataFile => Path.Combine(DataDir, "data.json");
        public FakeClock Clock { get; } = new();

        public Sandbox()
        {
            Directory.CreateDirectory(DataDir);
            Directory.CreateDirectory(OutDir);
        }

        public BackupService Service(Action<BackupOptions> tweak = null)
        {
            var o = new BackupOptions { DataDir = DataDir, SafetyBackupDir = SafetyDir, AppVersion = "1.0.0", CurrentSchema = 1 };
            tweak?.Invoke(o);
            return new BackupService(o, new SemaphoreGate(), Clock);
        }

        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }

    public class BackupServiceTests : IDisposable
    {
        private readonly Sandbox _s = new();
        public void Dispose() => _s.Dispose();

        private const string Original = "{\"Version\":1,\"Items\":[{\"Title\":\"Nota original — ção\"}]}";

        /// <summary>A hand-made backup .zip: manifest + files, optionally with a wrong hash.</summary>
        private string MakeZip(string name, int schema = 1, string app = BackupManifest.AppName, params (string path, string content, string hashOverride)[] files)
        {
            var path = Path.Combine(_s.OutDir, name);
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            var manifest = new BackupManifest { App = app, AppVersion = "9.9.9", SchemaVersion = schema, ExportedAtUtc = DateTime.UtcNow };
            foreach (var (p, content, hashOverride) in files)
            {
                var bytes = Encoding.UTF8.GetBytes(content);
                using (var s = zip.CreateEntry(p).Open()) s.Write(bytes);
                manifest.Files.Add(new BackupFileEntry { Path = p, Size = bytes.Length, Sha256 = hashOverride ?? BackupService.Hash(bytes) });
            }
            using (var s = zip.CreateEntry(BackupManifest.FileName).Open()) JsonSerializer.Serialize(s, manifest);
            return path;
        }

        [Fact]
        public async Task Export_then_import_gives_identical_data()
        {
            File.WriteAllText(_s.DataFile, Original, Encoding.UTF8);
            Directory.CreateDirectory(Path.Combine(_s.DataDir, "backups"));
            File.WriteAllText(Path.Combine(_s.DataDir, "backups", "antigo.json"), "x");
            var originalBytes = File.ReadAllBytes(_s.DataFile);
            var svc = _s.Service();

            var zip = Path.Combine(_s.OutDir, svc.SuggestedFileName(_s.Clock.Now));
            Assert.Equal("HelmetDesk-backup-2026-11-12-1530.zip", Path.GetFileName(zip));
            var m = await svc.ExportAsync(zip);
            Assert.Equal(BackupManifest.AppName, m.App);
            Assert.Single(m.Files);
            Assert.Equal(BackupService.Hash(originalBytes), m.Files[0].Sha256);

            File.WriteAllText(_s.DataFile, "{\"Version\":1,\"Items\":[]}"); // the data changes after the export
            var inspected = await svc.InspectAsync(zip);
            Assert.Equal(m.ExportedAtUtc, inspected.ExportedAtUtc);

            var safety = await svc.ImportAsync(zip);

            Assert.Equal(originalBytes, File.ReadAllBytes(_s.DataFile));
            Assert.True(File.Exists(Path.Combine(_s.DataDir, "backups", "antigo.json")), "non-data files are kept");
            Assert.True(File.Exists(safety), "a safety copy is made before replacing");
            Assert.False(Directory.Exists(_s.DataDir + ".old"));
            Assert.False(Directory.Exists(_s.DataDir + ".import-tmp"));
            using var sz = ZipFile.OpenRead(safety);
            Assert.NotNull(sz.GetEntry("data.json"));
        }

        [Fact]
        public async Task Export_includes_only_data_and_manifest()
        {
            File.WriteAllText(_s.DataFile, Original);
            File.WriteAllText(Path.Combine(_s.DataDir, "data.bak.json"), "bak");
            File.WriteAllText(Path.Combine(_s.DataDir, "log.txt"), "log");
            var zip = Path.Combine(_s.OutDir, "b.zip");
            await _s.Service().ExportAsync(zip);
            using var z = ZipFile.OpenRead(zip);
            Assert.Equal(new[] { "data.json", "manifest.json" }, z.Entries.Select(e => e.FullName).OrderBy(x => x));
        }

        [Fact]
        public async Task Corrupt_zip_is_rejected_and_data_untouched()
        {
            File.WriteAllText(_s.DataFile, Original);
            var bad = Path.Combine(_s.OutDir, "bad.zip");
            File.WriteAllBytes(bad, Enumerable.Range(0, 500).Select(i => (byte)(i * 7)).ToArray());
            var ex = await Assert.ThrowsAsync<BackupException>(() => _s.Service().ImportAsync(bad));
            Assert.Equal(BackupError.Corrupt, ex.Kind);
            Assert.Equal(Original, File.ReadAllText(_s.DataFile));
            Assert.False(Directory.Exists(_s.SafetyDir), "validation happens before any copy");
        }

        [Fact]
        public async Task Zip_without_manifest_is_not_a_backup()
        {
            var path = Path.Combine(_s.OutDir, "nomanifest.zip");
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var s = new StreamWriter(zip.CreateEntry("data.json").Open())) s.Write("{}");
            var ex = await Assert.ThrowsAsync<BackupException>(() => _s.Service().InspectAsync(path));
            Assert.Equal(BackupError.NotHelmetDesk, ex.Kind);
        }

        [Fact]
        public async Task Other_app_is_rejected()
        {
            var zip = MakeZip("other.zip", app: "Outro App", files: ("data.json", "{}", null));
            var ex = await Assert.ThrowsAsync<BackupException>(() => _s.Service().InspectAsync(zip));
            Assert.Equal(BackupError.NotHelmetDesk, ex.Kind);
        }

        [Theory]
        [InlineData("../evil.json")]
        [InlineData("..\\..\\evil.json")]
        [InlineData("dados/../../evil.json")]
        [InlineData("C:/Windows/evil.json")]
        [InlineData("/etc/evil.json")]
        public async Task Zip_slip_is_rejected(string entry)
        {
            File.WriteAllText(_s.DataFile, Original);
            var zip = MakeZip("slip.zip", files: new[] { ("data.json", "{}", (string)null), (entry, "pwned", null) });
            var ex = await Assert.ThrowsAsync<BackupException>(() => _s.Service().ImportAsync(zip));
            Assert.Equal(BackupError.UnsafePath, ex.Kind);
            Assert.Equal(Original, File.ReadAllText(_s.DataFile));
            Assert.False(File.Exists(Path.Combine(_s.Root, "evil.json")));
        }

        [Fact]
        public async Task Newer_schema_asks_to_update()
        {
            var zip = MakeZip("newer.zip", schema: 2, files: ("data.json", "{}", null));
            var ex = await Assert.ThrowsAsync<BackupException>(() => _s.Service().InspectAsync(zip));
            Assert.Equal(BackupError.NewerSchema, ex.Kind);
            Assert.Contains("Atualize o Helmet Desk", ex.Message);
        }

        [Fact]
        public async Task Older_schema_goes_through_migration()
        {
            File.WriteAllText(_s.DataFile, Original);
            var zip = MakeZip("older.zip", schema: 1, files: ("data.json", "{\"Version\":1}", null));
            int migratedFrom = 0;
            var svc = _s.Service(o => { o.CurrentSchema = 2; o.Migrate = (dir, from) => { migratedFrom = from; File.WriteAllText(Path.Combine(dir, "data.json"), "{\"Version\":2}"); }; });
            await svc.ImportAsync(zip);
            Assert.Equal(1, migratedFrom);
            Assert.Equal("{\"Version\":2}", File.ReadAllText(_s.DataFile));
        }

        [Fact]
        public async Task Hash_mismatch_is_rejected()
        {
            File.WriteAllText(_s.DataFile, Original);
            var zip = MakeZip("hash.zip", files: ("data.json", "{\"Items\":[]}", new string('0', 64)));
            var ex = await Assert.ThrowsAsync<BackupException>(() => _s.Service().ImportAsync(zip));
            Assert.Equal(BackupError.HashMismatch, ex.Kind);
            Assert.Equal(Original, File.ReadAllText(_s.DataFile));
        }

        [Fact]
        public async Task Unlisted_file_is_rejected()
        {
            var path = MakeZip("extra.zip", files: ("data.json", "{}", null));
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Update))
            using (var s = new StreamWriter(zip.CreateEntry("extra.exe").Open())) s.Write("MZ");
            var ex = await Assert.ThrowsAsync<BackupException>(() => _s.Service().InspectAsync(path));
            Assert.Equal(BackupError.HashMismatch, ex.Kind);
        }

        [Fact]
        public async Task Zip_bomb_is_rejected_by_total_size()
        {
            var zip = MakeZip("bomb.zip", files: ("data.json", new string('a', 50_000), null));
            var ex = await Assert.ThrowsAsync<BackupException>(() => _s.Service(o => o.MaxUncompressedBytes = 10_000).InspectAsync(zip));
            Assert.Equal(BackupError.TooLarge, ex.Kind);
        }

        [Theory]
        [InlineData(ImportStage.Extracted)]
        [InlineData(ImportStage.Migrated)]
        [InlineData(ImportStage.OldMovedAside)]
        [InlineData(ImportStage.NewMovedIn)]
        public async Task Failure_midway_rolls_back(ImportStage stage)
        {
            File.WriteAllText(_s.DataFile, Original);
            Directory.CreateDirectory(Path.Combine(_s.DataDir, "backups"));
            var zip = MakeZip("good.zip", files: ("data.json", "{\"Version\":1,\"Items\":[\"novo\"]}", null));
            var svc = _s.Service(o => o.FaultInjector = s => { if (s == stage) throw new IOException("falha simulada"); });

            var ex = await Assert.ThrowsAsync<BackupException>(() => svc.ImportAsync(zip));
            Assert.Equal(BackupError.Io, ex.Kind);
            Assert.Contains("falha simulada", ex.Message);
            Assert.Equal(Original, File.ReadAllText(_s.DataFile));
            Assert.True(Directory.Exists(Path.Combine(_s.DataDir, "backups")));
            Assert.False(Directory.Exists(_s.DataDir + ".old"), "the .old folder went back into place");
            Assert.False(Directory.Exists(_s.DataDir + ".import-tmp"));
        }

        [Fact]
        public async Task Keeps_only_the_last_five_safety_backups()
        {
            File.WriteAllText(_s.DataFile, Original);
            var zip = Path.Combine(_s.OutDir, "b.zip");
            var svc = _s.Service();
            await svc.ExportAsync(zip);
            for (int i = 0; i < 7; i++)
            {
                _s.Clock.Now = _s.Clock.Now.AddMinutes(1);
                await svc.ImportAsync(zip);
            }
            var left = Directory.GetFiles(_s.SafetyDir, "pre-import-*.zip").Select(Path.GetFileName).OrderBy(x => x).ToList();
            Assert.Equal(5, left.Count);
            Assert.Equal("pre-import-2026-11-12-153700.zip", left.Last());
        }

        [Fact]
        public async Task Export_waits_for_the_data_gate()
        {
            File.WriteAllText(_s.DataFile, Original);
            var gate = new SemaphoreGate();
            var svc = new BackupService(new BackupOptions { DataDir = _s.DataDir, SafetyBackupDir = _s.SafetyDir }, gate, _s.Clock);
            Task export;
            using (gate.Enter())
            {
                export = svc.ExportAsync(Path.Combine(_s.OutDir, "g.zip"));
                await Task.Delay(150);
                Assert.False(export.IsCompleted, "export must not read while a save is in progress");
            }
            await export;
        }

        // ======================= attachments folder =======================

        private BackupService WithAttachments(Action<BackupOptions> tweak = null) =>
            _s.Service(o => { o.DataFolders = new[] { "attachments" }; tweak?.Invoke(o); });

        private static byte[] Noise(int size, int seed)
        {
            var b = new byte[size];
            new Random(seed).NextBytes(b);
            return b;
        }

        [Fact]
        public async Task Attachments_go_into_the_backup_and_come_back_on_import()
        {
            File.WriteAllText(_s.DataFile, Original);
            var noteDir = Path.Combine(_s.DataDir, "attachments", "nota1");
            Directory.CreateDirectory(noteDir);
            var photo = Noise(3 * 1024 * 1024, 1);
            File.WriteAllBytes(Path.Combine(noteDir, "foto.png"), photo);
            File.WriteAllText(Path.Combine(noteDir, "lista ção.txt"), "remédios");
            Directory.CreateDirectory(Path.Combine(_s.DataDir, "links"));
            File.WriteAllText(Path.Combine(_s.DataDir, "links", "index.json"), "{}");
            var svc = WithAttachments();

            var zip = Path.Combine(_s.OutDir, "anexos.zip");
            var m = await svc.ExportAsync(zip);
            Assert.Equal(new[] { "attachments/nota1/foto.png", "attachments/nota1/lista ção.txt", "data.json" }, m.Files.Select(f => f.Path).OrderBy(x => x, StringComparer.Ordinal));
            using (var z = ZipFile.OpenRead(zip))
            {
                var e = z.GetEntry("attachments/nota1/foto.png");
                Assert.True(e.CompressedLength >= e.Length, "pictures are stored, not deflated again");
                Assert.Null(z.GetEntry("links/index.json")); // cache, not user data
            }

            // the folder changes after the export
            Directory.Delete(Path.Combine(_s.DataDir, "attachments"), true);
            Directory.CreateDirectory(Path.Combine(_s.DataDir, "attachments", "outra"));
            File.WriteAllText(Path.Combine(_s.DataDir, "attachments", "outra", "nova.txt"), "x");

            await svc.ImportAsync(zip);
            Assert.Equal(photo, File.ReadAllBytes(Path.Combine(noteDir, "foto.png")));
            Assert.Equal("remédios", File.ReadAllText(Path.Combine(noteDir, "lista ção.txt")));
            Assert.False(Directory.Exists(Path.Combine(_s.DataDir, "attachments", "outra")), "attachments are exactly the backup's");
            Assert.True(File.Exists(Path.Combine(_s.DataDir, "links", "index.json")), "other folders are kept");
        }

        [Fact]
        public async Task Big_attachments_are_not_limited_by_the_data_size_cap()
        {
            File.WriteAllText(_s.DataFile, Original);
            Directory.CreateDirectory(Path.Combine(_s.DataDir, "attachments", "n"));
            File.WriteAllBytes(Path.Combine(_s.DataDir, "attachments", "n", "video.mp4"), Noise(2 * 1024 * 1024, 2));
            var svc = WithAttachments(o => o.MaxUncompressedBytes = 10_000);
            var zip = Path.Combine(_s.OutDir, "big.zip");
            await svc.ExportAsync(zip);
            var m = await svc.InspectAsync(zip);
            Assert.Equal(2, m.Files.Count);
        }

        [Fact]
        public async Task Suspiciously_compressed_attachment_is_refused()
        {
            var path = Path.Combine(_s.OutDir, "bomb.zip");
            var zeros = new byte[4 * 1024 * 1024];
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                var manifest = new BackupManifest { App = BackupManifest.AppName, AppVersion = "1", SchemaVersion = 1, ExportedAtUtc = DateTime.UtcNow };
                var data = Encoding.UTF8.GetBytes(Original);
                using (var s = zip.CreateEntry("data.json").Open()) s.Write(data);
                using (var s = zip.CreateEntry("attachments/n/zeros.bin", CompressionLevel.Optimal).Open()) s.Write(zeros);
                manifest.Files.Add(new BackupFileEntry { Path = "data.json", Size = data.Length, Sha256 = BackupService.Hash(data) });
                manifest.Files.Add(new BackupFileEntry { Path = "attachments/n/zeros.bin", Size = zeros.Length, Sha256 = BackupService.Hash(zeros) });
                using (var s = zip.CreateEntry(BackupManifest.FileName).Open()) JsonSerializer.Serialize(s, manifest);
            }
            var ex = await Assert.ThrowsAsync<BackupException>(() => WithAttachments(o => o.MaxCompressionRatio = 50).InspectAsync(path));
            Assert.Equal(BackupError.TooLarge, ex.Kind);
            Assert.Contains("zeros.bin", ex.Message);
        }

        [Fact]
        public async Task Old_backup_without_attachments_still_imports()
        {
            File.WriteAllText(_s.DataFile, Original);
            var zip = MakeZip("antigo.zip", 1, BackupManifest.AppName, ("data.json", Original, null));
            Directory.CreateDirectory(Path.Combine(_s.DataDir, "attachments", "n"));
            File.WriteAllText(Path.Combine(_s.DataDir, "attachments", "n", "a.txt"), "a");
            await WithAttachments().ImportAsync(zip);
            Assert.Equal(Original, File.ReadAllText(_s.DataFile));
            Assert.False(Directory.Exists(Path.Combine(_s.DataDir, "attachments")), "the old data has no attachments, so none are left");
            var safety = Directory.GetFiles(_s.SafetyDir).Single();
            using var z = ZipFile.OpenRead(safety);
            Assert.NotNull(z.GetEntry("attachments/n/a.txt")); // but the safety copy still has them
        }

        [Fact]
        public async Task Safety_copies_are_pruned_by_total_size_keeping_the_newest()
        {
            File.WriteAllText(_s.DataFile, Original);
            Directory.CreateDirectory(Path.Combine(_s.DataDir, "attachments", "n"));
            File.WriteAllBytes(Path.Combine(_s.DataDir, "attachments", "n", "f.bin"), Noise(200_000, 3));
            var svc = WithAttachments(o => o.MaxSafetyBytes = 250_000);
            var zip = Path.Combine(_s.OutDir, "b.zip");
            await svc.ExportAsync(zip);
            for (int i = 0; i < 3; i++)
            {
                _s.Clock.Now = _s.Clock.Now.AddMinutes(1);
                await svc.ImportAsync(zip);
            }
            var left = Directory.GetFiles(_s.SafetyDir, "pre-import-*.zip").Select(Path.GetFileName).ToList();
            Assert.Equal(new[] { "pre-import-2026-11-12-153300.zip" }, left);
        }
    }
}
