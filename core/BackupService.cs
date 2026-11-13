using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace HelmetDesk.Core
{
    public sealed class BackupFileEntry
    {
        [JsonPropertyName("path")] public string Path { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("sha256")] public string Sha256 { get; set; }
    }

    public sealed class BackupManifest
    {
        public const string FileName = "manifest.json";
        public const string AppName = "Helmet Desk";

        [JsonPropertyName("app")] public string App { get; set; }
        [JsonPropertyName("appVersion")] public string AppVersion { get; set; }
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonPropertyName("exportedAtUtc")] public DateTime ExportedAtUtc { get; set; }
        [JsonPropertyName("files")] public List<BackupFileEntry> Files { get; set; } = new();
    }

    public enum BackupError { Corrupt, NotHelmetDesk, UnsafePath, TooLarge, HashMismatch, NewerSchema, Io }

    /// <summary>A backup that can't be used, with a message ready for the user (pt-BR).</summary>
    public sealed class BackupException : Exception
    {
        public BackupError Kind { get; }
        public BackupException(BackupError kind, string message, Exception inner = null) : base(message, inner) => Kind = kind;
    }

    /// <summary>Points in the import where a test can make it fail on purpose.</summary>
    public enum ImportStage { Extracted, Migrated, OldMovedAside, NewMovedIn }

    public sealed class BackupOptions
    {
        /// <summary>The folder with the user's data (it is swapped as a whole on import).</summary>
        public string DataDir { get; set; }
        /// <summary>Where the automatic "pre-import" copies go. Must be outside <see cref="DataDir"/>.</summary>
        public string SafetyBackupDir { get; set; }
        /// <summary>Files (relative to DataDir) that make up the user's data.</summary>
        public IReadOnlyList<string> DataFiles { get; set; } = new[] { "data.json" };
        public string AppVersion { get; set; } = "0.0.0";
        public int CurrentSchema { get; set; } = 1;
        /// <summary>The data today is a few KB; 100 MB leaves room for years of notes and still stops a zip bomb.</summary>
        public long MaxUncompressedBytes { get; set; } = 100L * 1024 * 1024;
        public int MaxEntries { get; set; } = 1000;
        public int KeepSafetyBackups { get; set; } = 5;
        /// <summary>Brings an older backup (already extracted to the given folder) up to the current schema.</summary>
        public Action<string, int> Migrate { get; set; }
        /// <summary>Test hook: called at each stage of the import; throwing simulates a failure there.</summary>
        public Action<ImportStage> FaultInjector { get; set; }
    }

    public interface IBackupService
    {
        string SuggestedFileName(DateTimeOffset now);
        Task<BackupManifest> ExportAsync(string zipPath, IProgress<OperationProgress> progress = null, CancellationToken ct = default);
        /// <summary>Checks a backup without touching the current data. Throws <see cref="BackupException"/>.</summary>
        Task<BackupManifest> InspectAsync(string zipPath, IProgress<OperationProgress> progress = null, CancellationToken ct = default);
        /// <summary>Replaces the current data with the backup. Returns the path of the safety copy made first.</summary>
        Task<string> ImportAsync(string zipPath, IProgress<OperationProgress> progress = null, CancellationToken ct = default);
    }

    public sealed class BackupService : IBackupService
    {
        private readonly BackupOptions _o;
        private readonly IDataGate _gate;
        private readonly IClock _clock;
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        public BackupService(BackupOptions options, IDataGate gate = null, IClock clock = null)
        {
            _o = options ?? throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrEmpty(_o.DataDir)) throw new ArgumentException("DataDir");
            _gate = gate ?? new SemaphoreGate();
            _clock = clock ?? SystemClock.Instance;
        }

        public string SuggestedFileName(DateTimeOffset now) => $"HelmetDesk-backup-{now:yyyy-MM-dd-HHmm}.zip";

        // ======================= export =======================

        public Task<BackupManifest> ExportAsync(string zipPath, IProgress<OperationProgress> progress = null, CancellationToken ct = default) =>
            Task.Run(() => Export(zipPath, progress, ct), ct);

        private BackupManifest Export(string zipPath, IProgress<OperationProgress> progress, CancellationToken ct)
        {
            progress?.Report(new(5, "Lendo os dados…"));
            // a consistent snapshot: copy the files into memory while the app is not writing them
            var files = new List<(string rel, byte[] bytes)>();
            using (_gate.Enter())
            {
                foreach (var rel in _o.DataFiles)
                {
                    var full = Path.Combine(_o.DataDir, rel);
                    if (File.Exists(full)) files.Add((Normalize(rel), File.ReadAllBytes(full)));
                }
            }
            if (files.Count == 0) throw new BackupException(BackupError.Io, "Não há dados para exportar.");
            ct.ThrowIfCancellationRequested();

            var manifest = new BackupManifest
            {
                App = BackupManifest.AppName,
                AppVersion = _o.AppVersion,
                SchemaVersion = _o.CurrentSchema,
                ExportedAtUtc = _clock.Now.UtcDateTime,
                Files = files.Select(f => new BackupFileEntry { Path = f.rel, Size = f.bytes.LongLength, Sha256 = Hash(f.bytes) }).ToList()
            };

            progress?.Report(new(40, "Compactando…"));
            var dir = Path.GetDirectoryName(Path.GetFullPath(zipPath));
            Directory.CreateDirectory(dir);
            // write next to the target and move into place: a cancelled/failed export never leaves half a zip
            var tmp = Path.Combine(dir, "." + Path.GetFileName(zipPath) + ".tmp");
            try
            {
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    foreach (var (rel, bytes) in files)
                    {
                        var e = zip.CreateEntry(rel, CompressionLevel.Optimal);
                        using var s = e.Open();
                        s.Write(bytes, 0, bytes.Length);
                    }
                    var m = zip.CreateEntry(BackupManifest.FileName, CompressionLevel.Optimal);
                    using (var s = m.Open()) JsonSerializer.Serialize(s, manifest, Json);
                }
                ct.ThrowIfCancellationRequested();
                File.Move(tmp, zipPath, true);
            }
            catch
            {
                TryDelete(tmp);
                throw;
            }
            progress?.Report(new(100, "Backup exportado."));
            return manifest;
        }

        // ======================= validation =======================

        public Task<BackupManifest> InspectAsync(string zipPath, IProgress<OperationProgress> progress = null, CancellationToken ct = default) =>
            Task.Run(() => Validate(zipPath, progress, ct), ct);

        private BackupManifest Validate(string zipPath, IProgress<OperationProgress> progress, CancellationToken ct)
        {
            progress?.Report(new(5, "Verificando o backup…"));
            ZipArchive zip;
            try { zip = ZipFile.OpenRead(zipPath); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                throw new BackupException(BackupError.Corrupt, "O arquivo não é um .zip válido ou está corrompido.", ex);
            }
            using (zip)
            {
                // ---- size and paths, before reading anything ----
                IReadOnlyCollection<ZipArchiveEntry> entries;
                try { entries = zip.Entries; }
                catch (InvalidDataException ex) { throw new BackupException(BackupError.Corrupt, "O arquivo .zip está corrompido.", ex); }
                if (entries.Count > _o.MaxEntries)
                    throw new BackupException(BackupError.TooLarge, "O backup tem arquivos demais para ser um backup do Helmet Desk.");
                long total = 0;
                foreach (var e in entries)
                {
                    total += e.Length;
                    if (total > _o.MaxUncompressedBytes)
                        throw new BackupException(BackupError.TooLarge, $"O backup é grande demais (limite de {_o.MaxUncompressedBytes / (1024 * 1024)} MB descompactado).");
                    CheckSafePath(e.FullName);
                }

                // ---- manifest ----
                var me = zip.GetEntry(BackupManifest.FileName)
                    ?? throw new BackupException(BackupError.NotHelmetDesk, "Este .zip não é um backup do Helmet Desk (falta o manifest.json).");
                BackupManifest manifest;
                try
                {
                    using var s = me.Open();
                    manifest = JsonSerializer.Deserialize<BackupManifest>(ReadLimited(s, 1024 * 1024));
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or BackupException)
                {
                    throw new BackupException(BackupError.Corrupt, "O manifest.json do backup está ilegível.", ex);
                }
                if (manifest == null || manifest.App != BackupManifest.AppName)
                    throw new BackupException(BackupError.NotHelmetDesk, "Este .zip não é um backup do Helmet Desk.");
                if (manifest.SchemaVersion > _o.CurrentSchema)
                    throw new BackupException(BackupError.NewerSchema,
                        $"Este backup foi criado por uma versão mais nova do Helmet Desk ({manifest.AppVersion}). Atualize o Helmet Desk para importá-lo.");
                if (manifest.SchemaVersion < 1 || manifest.Files == null || manifest.Files.Count == 0)
                    throw new BackupException(BackupError.Corrupt, "O manifest.json do backup está incompleto.");

                // ---- every listed file is there, nothing else is, and the hashes match ----
                var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in manifest.Files)
                {
                    CheckSafePath(f.Path);
                    if (!listed.Add(Normalize(f.Path)))
                        throw new BackupException(BackupError.Corrupt, "O manifest.json lista o mesmo arquivo duas vezes.");
                }
                foreach (var e in entries)
                {
                    if (e.FullName.EndsWith("/") && e.Length == 0) continue; // folder entry
                    if (e.FullName == BackupManifest.FileName) continue;
                    if (!listed.Contains(Normalize(e.FullName)))
                        throw new BackupException(BackupError.HashMismatch, $"O backup tem um arquivo que não está no manifesto: {e.FullName}");
                }
                int i = 0;
                foreach (var f in manifest.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new(10 + 60 * i++ / manifest.Files.Count, "Conferindo a integridade…"));
                    var e = zip.GetEntry(Normalize(f.Path))
                        ?? throw new BackupException(BackupError.HashMismatch, $"Falta no backup o arquivo {f.Path}.");
                    string h;
                    try { using var s = e.Open(); h = Hash(s, f.Size); }
                    catch (InvalidDataException ex) { throw new BackupException(BackupError.Corrupt, $"O arquivo {f.Path} dentro do backup está corrompido.", ex); }
                    if (!string.Equals(h, f.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new BackupException(BackupError.HashMismatch, $"O arquivo {f.Path} não confere com o manifesto (SHA-256 diferente). O backup pode ter sido alterado ou danificado.");
                }
                progress?.Report(new(70, "Backup verificado."));
                return manifest;
            }
        }

        /// <summary>Zip-slip guard: relative, no "..", and resolving inside a destination folder.</summary>
        internal static void CheckSafePath(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Contains('\0'))
                throw new BackupException(BackupError.UnsafePath, "O backup tem um nome de arquivo inválido.");
            if (Path.IsPathRooted(name) || name.StartsWith("/") || name.StartsWith("\\") || name.Contains(':'))
                throw new BackupException(BackupError.UnsafePath, $"O backup tem um caminho absoluto, recusado por segurança: {name}");
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "hd-zip-check")) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(root, name));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new BackupException(BackupError.UnsafePath, $"O backup tenta gravar fora da pasta de dados, recusado por segurança: {name}");
        }

        // ======================= import =======================

        public Task<string> ImportAsync(string zipPath, IProgress<OperationProgress> progress = null, CancellationToken ct = default) =>
            Task.Run(() => Import(zipPath, progress, ct), ct);

        private string Import(string zipPath, IProgress<OperationProgress> progress, CancellationToken ct)
        {
            var manifest = Validate(zipPath, progress, ct);   // nothing has been touched yet

            var dataDir = Path.GetFullPath(_o.DataDir).TrimEnd(Path.DirectorySeparatorChar);
            var tmp = dataDir + ".import-tmp";
            var old = dataDir + ".old";

            // 1. safety copy of what is there now
            progress?.Report(new(72, "Guardando uma cópia dos dados atuais…"));
            string safety = null;
            if (_o.DataFiles.Any(f => File.Exists(Path.Combine(dataDir, f))))
            {
                Directory.CreateDirectory(_o.SafetyBackupDir);
                safety = Path.Combine(_o.SafetyBackupDir, $"pre-import-{_clock.Now:yyyy-MM-dd-HHmmss}.zip");
                Export(safety, null, CancellationToken.None);
                PruneSafetyBackups();
            }

            bool swapped = false;
            try
            {
                // 2. extract into a sibling folder (same volume, so the swap is a rename)
                progress?.Report(new(80, "Extraindo…"));
                if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                Directory.CreateDirectory(tmp);
                var tmpRoot = tmp + Path.DirectorySeparatorChar;
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    foreach (var f in manifest.Files)
                    {
                        var target = Path.GetFullPath(Path.Combine(tmp, Normalize(f.Path)));
                        if (!target.StartsWith(tmpRoot, StringComparison.OrdinalIgnoreCase))
                            throw new BackupException(BackupError.UnsafePath, $"Caminho recusado: {f.Path}");
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        using (var src = zip.GetEntry(Normalize(f.Path)).Open())
                        using (var dst = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                            CopyLimited(src, dst, f.Size);
                        // validate what actually landed on disk
                        using var check = File.OpenRead(target);
                        if (!string.Equals(Hash(check, f.Size), f.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new BackupException(BackupError.HashMismatch, $"O arquivo {f.Path} mudou ao ser extraído.");
                    }
                }
                _o.FaultInjector?.Invoke(ImportStage.Extracted);

                // 3. older schema → current
                if (manifest.SchemaVersion < _o.CurrentSchema) _o.Migrate?.Invoke(tmp, manifest.SchemaVersion);
                _o.FaultInjector?.Invoke(ImportStage.Migrated);

                // 4. keep everything in the data folder that is not user data (old backups, the startup .bak, …)
                if (Directory.Exists(dataDir))
                {
                    var dataNames = new HashSet<string>(manifest.Files.Select(f => Normalize(f.Path)).Concat(_o.DataFiles.Select(Normalize)), StringComparer.OrdinalIgnoreCase);
                    foreach (var path in Directory.EnumerateFileSystemEntries(dataDir))
                    {
                        var name = Path.GetFileName(path);
                        if (dataNames.Contains(name)) continue;
                        var dest = Path.Combine(tmp, name);
                        if (Directory.Exists(path)) CopyDir(path, dest);
                        else File.Copy(path, dest, true);
                    }
                }

                // 5. swap: current → .old, new → current
                progress?.Report(new(92, "Substituindo os dados…"));
                if (Directory.Exists(old)) Directory.Delete(old, true);
                bool hadData = Directory.Exists(dataDir);
                if (hadData) Directory.Move(dataDir, old);
                swapped = hadData;
                _o.FaultInjector?.Invoke(ImportStage.OldMovedAside);
                Directory.Move(tmp, dataDir);
                _o.FaultInjector?.Invoke(ImportStage.NewMovedIn);
            }
            catch (Exception ex)
            {
                // roll back: put the original folder back exactly as it was
                try
                {
                    if (swapped)
                    {
                        if (Directory.Exists(dataDir))
                        {
                            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                            Directory.Move(dataDir, tmp);
                        }
                        Directory.Move(old, dataDir);
                    }
                }
                catch (Exception rex)
                {
                    throw new BackupException(BackupError.Io,
                        $"A importação falhou e não foi possível restaurar a pasta automaticamente. Seus dados originais estão em \"{old}\" e a cópia de segurança em \"{safety}\".", new AggregateException(ex, rex));
                }
                finally { TryDeleteDir(tmp); }
                if (ex is BackupException) throw;
                throw new BackupException(BackupError.Io, "A importação falhou e os dados atuais foram mantidos: " + ex.Message, ex);
            }

            TryDeleteDir(old); // only after success
            progress?.Report(new(100, "Backup importado."));
            return safety;
        }

        private void PruneSafetyBackups()
        {
            try
            {
                foreach (var f in new DirectoryInfo(_o.SafetyBackupDir).GetFiles("pre-import-*.zip")
                             .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name).Skip(_o.KeepSafetyBackups))
                    f.Delete();
            }
            catch { /* best effort */ }
        }

        // ======================= helpers =======================

        private static string Normalize(string rel) => rel.Replace('\\', '/').TrimStart('/');

        internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        /// <summary>Hashes a stream, refusing to read more than the size the manifest declared (zip-bomb guard).</summary>
        private static string Hash(Stream s, long declared)
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[81920];
            long total = 0;
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
            {
                total += n;
                if (total > declared) throw new BackupException(BackupError.HashMismatch, "Um arquivo do backup é maior do que o manifesto diz.");
                sha.AppendData(buf, 0, n);
            }
            return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        }

        private static void CopyLimited(Stream src, Stream dst, long max)
        {
            var buf = new byte[81920];
            long total = 0;
            int n;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            {
                total += n;
                if (total > max) throw new BackupException(BackupError.HashMismatch, "Um arquivo do backup é maior do que o manifesto diz.");
                dst.Write(buf, 0, n);
            }
        }

        private static byte[] ReadLimited(Stream s, int max)
        {
            using var ms = new MemoryStream();
            CopyLimited(s, ms, max);
            return ms.ToArray();
        }

        private static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        private static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }
        private static void TryDeleteDir(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { } }
    }
}
