using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
        [JsonPropertyName("path")] public string? Path { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    }

    public sealed class BackupManifest
    {
        public const string FileName = "manifest.json";
        public const string AppName = "Helmet Desk";

        [JsonPropertyName("app")] public string? App { get; set; }
        [JsonPropertyName("appVersion")] public string? AppVersion { get; set; }
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonPropertyName("exportedAtUtc")] public DateTime ExportedAtUtc { get; set; }
        [JsonPropertyName("files")] public List<BackupFileEntry> Files { get; set; } = new();
    }

    public enum BackupError { Corrupt, NotHelmetDesk, UnsafePath, TooLarge, HashMismatch, NewerSchema, Io }

    /// <summary>A backup that can't be used, with a message ready for the user (pt-BR).</summary>
    public sealed class BackupException : Exception
    {
        public BackupError Kind { get; }
        public BackupException(BackupError kind, string message, Exception? inner = null) : base(message, inner) => Kind = kind;
    }

    /// <summary>Points in the import where a test can make it fail on purpose.</summary>
    public enum ImportStage { Extracted, Migrated, OldMovedAside, NewMovedIn }

    public sealed class BackupOptions
    {
        /// <summary>The folder with the user's data (it is swapped as a whole on import).</summary>
        public required string DataDir { get; set; }
        /// <summary>Where the automatic "pre-import" copies go. Must be outside <see cref="DataDir"/>.</summary>
        public required string SafetyBackupDir { get; set; }
        /// <summary>Files (relative to DataDir) that make up the user's data.</summary>
        public IReadOnlyList<string> DataFiles { get; set; } = new[] { "data.json" };
        /// <summary>Folders (relative to DataDir) copied whole, e.g. "attachments" (note media: can be big, streamed from disk).</summary>
        public IReadOnlyList<string> DataFolders { get; set; } = Array.Empty<string>();
        public string AppVersion { get; set; } = "0.0.0";
        public int CurrentSchema { get; set; } = 1;
        /// <summary>For the data files (JSON): a few KB today; 100 MB leaves room for years of notes and still stops a zip bomb.</summary>
        public long MaxUncompressedBytes { get; set; } = 100L * 1024 * 1024;
        public int MaxEntries { get; set; } = 1000;
        /// <summary>Files inside <see cref="DataFolders"/> (attachments have no size limit; free disk space is checked on import).</summary>
        public int MaxFolderEntries { get; set; } = 200_000;
        /// <summary>Zip-bomb guard for folder files: anything over 1 MB that shrank more than this is refused (real media barely compresses).</summary>
        public double MaxCompressionRatio { get; set; } = 1000;
        /// <summary>Safety copies are pruned to this total size (always keeping the newest): with media they can be big.</summary>
        public long MaxSafetyBytes { get; set; } = 2L * 1024 * 1024 * 1024;
        public int KeepSafetyBackups { get; set; } = 5;
        /// <summary>Brings an older backup (already extracted to the given folder) up to the current schema.</summary>
        public Action<string, int>? Migrate { get; set; }
        /// <summary>Test hook: called at each stage of the import; throwing simulates a failure there.</summary>
        public Action<ImportStage>? FaultInjector { get; set; }
    }

    public interface IBackupService
    {
        string SuggestedFileName(DateTimeOffset now);
        Task<BackupManifest> ExportAsync(string zipPath, IProgress<OperationProgress>? progress = null, CancellationToken ct = default);
        /// <summary>Checks a backup without touching the current data. Throws <see cref="BackupException"/>.</summary>
        Task<BackupManifest> InspectAsync(string zipPath, IProgress<OperationProgress>? progress = null, CancellationToken ct = default);
        /// <summary>Replaces the current data with the backup. Returns the path of the safety copy made first (null when there was no data to copy).</summary>
        Task<string?> ImportAsync(string zipPath, IProgress<OperationProgress>? progress = null, CancellationToken ct = default);
    }

    public sealed class BackupService : IBackupService
    {
        private readonly BackupOptions _o;
        private readonly IDataGate _gate;
        private readonly IClock _clock;
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        public BackupService(BackupOptions options, IDataGate? gate = null, IClock? clock = null)
        {
            _o = options ?? throw new ArgumentNullException(nameof(options));
            if (string.IsNullOrEmpty(_o.DataDir)) throw new ArgumentException("DataDir");
            _gate = gate ?? new SemaphoreGate();
            _clock = clock ?? SystemClock.Instance;
        }

        public string SuggestedFileName(DateTimeOffset now) => $"HelmetDesk-backup-{now:yyyy-MM-dd-HHmm}.zip";

        // ======================= export =======================

        public Task<BackupManifest> ExportAsync(string zipPath, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) =>
            Task.Run(() => Export(zipPath, progress, ct), ct);

        private BackupManifest Export(string zipPath, IProgress<OperationProgress>? progress, CancellationToken ct)
        {
            progress?.Report(new(5, T("Lendo os dados…")));
            // a consistent snapshot: copy the files into memory while the app is not writing them
            var files = new List<(string rel, byte[] bytes)>();
            var folderFiles = new List<(string rel, string full, long size)>();
            using (_gate.Enter())
            {
                foreach (var rel in _o.DataFiles)
                {
                    var full = Path.Combine(_o.DataDir, rel);
                    if (File.Exists(full)) files.Add((Normalize(rel), File.ReadAllBytes(full)));
                }
                // folder files (attachments) are never rewritten in place, only added/removed: listing them is enough
                foreach (var folder in _o.DataFolders)
                {
                    var root = Path.Combine(_o.DataDir, folder);
                    if (!Directory.Exists(root)) continue;
                    foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                        folderFiles.Add((Normalize(Path.GetRelativePath(_o.DataDir, f)), f, new FileInfo(f).Length));
                }
            }
            if (files.Count == 0) throw new BackupException(BackupError.Io, T("Não há dados para exportar."));
            ct.ThrowIfCancellationRequested();

            var manifest = new BackupManifest
            {
                App = BackupManifest.AppName,
                AppVersion = _o.AppVersion,
                SchemaVersion = _o.CurrentSchema,
                ExportedAtUtc = _clock.Now.UtcDateTime,
                Files = files.Select(f => new BackupFileEntry { Path = f.rel, Size = f.bytes.LongLength, Sha256 = Hash(f.bytes) }).ToList()
            };
            long folderTotal = Math.Max(1, folderFiles.Sum(f => f.size)), folderDone = 0;

            progress?.Report(new(40, T("Compactando…")));
            // null only for a drive root ("C:\"), which is not a file name (the save dialog never returns one)
            var dir = Path.GetDirectoryName(Path.GetFullPath(zipPath)) ?? throw new BackupException(BackupError.UnsafePath, TF("Caminho recusado: {0}", zipPath));
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
                    foreach (var (rel, full, size) in folderFiles)
                    {
                        ct.ThrowIfCancellationRequested();
                        progress?.Report(new(40 + (int)(55 * folderDone / folderTotal), T("Compactando anexos…")));
                        FileStream src;
                        try { src = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete); }
                        catch (FileNotFoundException) { continue; } // removed meanwhile
                        catch (DirectoryNotFoundException) { continue; }
                        using (src)
                        {
                            var e = zip.CreateEntry(rel, IsCompressed(rel) ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                            using var s = e.Open();
                            var (hash, length) = CopyHashing(src, s, ct);
                            manifest.Files.Add(new BackupFileEntry { Path = rel, Size = length, Sha256 = hash });
                        }
                        folderDone += size;
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
            progress?.Report(new(100, T("Backup exportado.")));
            return manifest;
        }

        // ======================= validation =======================

        public Task<BackupManifest> InspectAsync(string zipPath, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) =>
            Task.Run(() => Validate(zipPath, progress, ct), ct);

        private BackupManifest Validate(string zipPath, IProgress<OperationProgress>? progress, CancellationToken ct)
        {
            progress?.Report(new(5, T("Verificando o backup…")));
            ZipArchive zip;
            try { zip = ZipFile.OpenRead(zipPath); }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                throw new BackupException(BackupError.Corrupt, T("O arquivo não é um .zip válido ou está corrompido."), ex);
            }
            using (zip)
            {
                // ---- size and paths, before reading anything ----
                IReadOnlyCollection<ZipArchiveEntry> entries;
                try { entries = zip.Entries; }
                catch (InvalidDataException ex) { throw new BackupException(BackupError.Corrupt, T("O arquivo .zip está corrompido."), ex); }
                int dataCount = 0, folderCount = 0;
                long total = 0;
                foreach (var e in entries)
                {
                    CheckSafePath(e.FullName);
                    if (InDataFolder(e.FullName))
                    {
                        if (++folderCount > _o.MaxFolderEntries)
                            throw new BackupException(BackupError.TooLarge, T("O backup tem arquivos demais para ser um backup do Helmet Desk."));
                        if (e.Length > 1024 * 1024 && e.Length > e.CompressedLength * _o.MaxCompressionRatio)
                            throw new BackupException(BackupError.TooLarge, TF("Um arquivo do backup está compactado de forma suspeita e foi recusado por segurança: {0}", e.FullName));
                        continue;
                    }
                    if (++dataCount > _o.MaxEntries)
                        throw new BackupException(BackupError.TooLarge, T("O backup tem arquivos demais para ser um backup do Helmet Desk."));
                    total += e.Length;
                    if (total > _o.MaxUncompressedBytes)
                        throw new BackupException(BackupError.TooLarge, TF("O backup é grande demais (limite de {0} MB descompactado).", _o.MaxUncompressedBytes / (1024 * 1024)));
                }

                // ---- manifest ----
                var me = zip.GetEntry(BackupManifest.FileName)
                    ?? throw new BackupException(BackupError.NotHelmetDesk, T("Este .zip não é um backup do Helmet Desk (falta o manifest.json)."));
                BackupManifest? manifest;
                try
                {
                    using var s = me.Open();
                    manifest = JsonSerializer.Deserialize<BackupManifest>(ReadLimited(s, 1024 * 1024));
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or BackupException)
                {
                    throw new BackupException(BackupError.Corrupt, T("O manifest.json do backup está ilegível."), ex);
                }
                if (manifest == null || manifest.App != BackupManifest.AppName)
                    throw new BackupException(BackupError.NotHelmetDesk, T("Este .zip não é um backup do Helmet Desk."));
                if (manifest.SchemaVersion > _o.CurrentSchema)
                    throw new BackupException(BackupError.NewerSchema,
                        TF("Este backup foi criado por uma versão mais nova do Helmet Desk ({0}). Atualize o Helmet Desk para importá-lo.", manifest.AppVersion));
                // "files": [null] deserializes to a null entry (the JSON comes from the zip, not from this app)
                if (manifest.SchemaVersion < 1 || manifest.Files == null || manifest.Files.Count == 0 || manifest.Files.Any(f => f is null))
                    throw new BackupException(BackupError.Corrupt, T("O manifest.json do backup está incompleto."));

                // ---- every listed file is there, nothing else is, and the hashes match ----
                var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in manifest.Files)
                {
                    CheckSafePath(f.Path);
                    if (!listed.Add(Normalize(f.Path)))
                        throw new BackupException(BackupError.Corrupt, T("O manifest.json lista o mesmo arquivo duas vezes."));
                }
                foreach (var e in entries)
                {
                    if (e.FullName.EndsWith("/") && e.Length == 0) continue; // folder entry
                    if (e.FullName == BackupManifest.FileName) continue;
                    if (!listed.Contains(Normalize(e.FullName)))
                        throw new BackupException(BackupError.HashMismatch, TF("O backup tem um arquivo que não está no manifesto: {0}", e.FullName));
                }
                int i = 0;
                foreach (var f in manifest.Files)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new(10 + 60 * i++ / manifest.Files.Count, T("Conferindo a integridade…")));
                    var e = zip.GetEntry(Normalize(PathOf(f)))
                        ?? throw new BackupException(BackupError.HashMismatch, TF("Falta no backup o arquivo {0}.", f.Path));
                    string h;
                    try { using var s = e.Open(); h = Hash(s, f.Size); }
                    catch (InvalidDataException ex) { throw new BackupException(BackupError.Corrupt, TF("O arquivo {0} dentro do backup está corrompido.", f.Path), ex); }
                    if (!string.Equals(h, f.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new BackupException(BackupError.HashMismatch, TF("O arquivo {0} não confere com o manifesto (SHA-256 diferente). O backup pode ter sido alterado ou danificado.", f.Path));
                }
                progress?.Report(new(70, T("Backup verificado.")));
                return manifest;
            }
        }

        /// <summary>
        /// A manifest entry's path. <see cref="Validate"/> already refused a null/empty one (<see cref="CheckSafePath"/>),
        /// so after it this never throws; it only states that to the compiler without a "!".
        /// </summary>
        private static string PathOf(BackupFileEntry f) =>
            f.Path ?? throw new BackupException(BackupError.UnsafePath, T("O backup tem um nome de arquivo inválido."));

        /// <summary>Zip-slip guard: relative, no "..", and resolving inside a destination folder.</summary>
        internal static void CheckSafePath([NotNull] string? name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Contains('\0'))
                throw new BackupException(BackupError.UnsafePath, T("O backup tem um nome de arquivo inválido."));
            if (Path.IsPathRooted(name) || name.StartsWith("/") || name.StartsWith("\\") || name.Contains(':'))
                throw new BackupException(BackupError.UnsafePath, TF("O backup tem um caminho absoluto, recusado por segurança: {0}", name));
            var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "hd-zip-check")) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(root, name));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new BackupException(BackupError.UnsafePath, TF("O backup tenta gravar fora da pasta de dados, recusado por segurança: {0}", name));
        }

        // ======================= import =======================

        public Task<string?> ImportAsync(string zipPath, IProgress<OperationProgress>? progress = null, CancellationToken ct = default) =>
            Task.Run(() => Import(zipPath, progress, ct), ct);

        private string? Import(string zipPath, IProgress<OperationProgress>? progress, CancellationToken ct)
        {
            var manifest = Validate(zipPath, progress, ct);   // nothing has been touched yet

            var dataDir = Path.GetFullPath(_o.DataDir).TrimEnd(Path.DirectorySeparatorChar);
            var tmp = dataDir + ".import-tmp";
            var old = dataDir + ".old";

            // the backup is extracted next to the current data before the swap: there must be room for it
            long need = manifest.Files.Sum(f => f.Size) + 64L * 1024 * 1024;
            long free = FreeSpace(dataDir);
            if (free >= 0 && free < need)
                throw new BackupException(BackupError.TooLarge,
                    TF("Espaço em disco insuficiente para importar este backup: são necessários {0} livres e há {1}.", Gb(need), Gb(free)));

            // 1. safety copy of what is there now
            progress?.Report(new(72, T("Guardando uma cópia dos dados atuais…")));
            string? safety = null;
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
                progress?.Report(new(80, T("Extraindo…")));
                if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                Directory.CreateDirectory(tmp);
                var tmpRoot = tmp + Path.DirectorySeparatorChar;
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    foreach (var f in manifest.Files)
                    {
                        var target = Path.GetFullPath(Path.Combine(tmp, Normalize(PathOf(f))));
                        if (!target.StartsWith(tmpRoot, StringComparison.OrdinalIgnoreCase))
                            throw new BackupException(BackupError.UnsafePath, TF("Caminho recusado: {0}", f.Path));
                        // target is inside tmp (checked above), so it always has a parent folder
                        Directory.CreateDirectory(Path.GetDirectoryName(target) ?? tmp);
                        // Validate found it, but the .zip is opened again here: it may have been replaced in between
                        var entry = zip.GetEntry(Normalize(PathOf(f)))
                            ?? throw new BackupException(BackupError.HashMismatch, TF("Falta no backup o arquivo {0}.", f.Path));
                        using (var src = entry.Open())
                        using (var dst = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                            CopyLimited(src, dst, f.Size);
                        // validate what actually landed on disk
                        using var check = File.OpenRead(target);
                        if (!string.Equals(Hash(check, f.Size), f.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new BackupException(BackupError.HashMismatch, TF("O arquivo {0} mudou ao ser extraído.", f.Path));
                    }
                }
                _o.FaultInjector?.Invoke(ImportStage.Extracted);

                // 3. older schema → current
                if (manifest.SchemaVersion < _o.CurrentSchema) _o.Migrate?.Invoke(tmp, manifest.SchemaVersion);
                _o.FaultInjector?.Invoke(ImportStage.Migrated);

                // 4. keep everything in the data folder that is not user data (old backups, the startup .bak, …)
                if (Directory.Exists(dataDir))
                {
                    // data files and data folders come from the backup only; everything else in the folder is kept
                    var dataNames = new HashSet<string>(manifest.Files.Select(f => Normalize(PathOf(f)).Split('/')[0]).Concat(_o.DataFiles.Select(Normalize))
                        .Concat(_o.DataFolders.Select(Normalize)), StringComparer.OrdinalIgnoreCase);
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
                progress?.Report(new(92, T("Substituindo os dados…")));
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
                        TF("A importação falhou e não foi possível restaurar a pasta automaticamente. Seus dados originais estão em \"{0}\" e a cópia de segurança em \"{1}\".", old, safety), new AggregateException(ex, rex));
                }
                finally { TryDeleteDir(tmp); }
                if (ex is BackupException) throw;
                throw new BackupException(BackupError.Io, T("A importação falhou e os dados atuais foram mantidos: ") + ex.Message, ex);
            }

            TryDeleteDir(old); // only after success
            progress?.Report(new(100, T("Backup importado.")));
            return safety;
        }

        private void PruneSafetyBackups()
        {
            try
            {
                var all = new DirectoryInfo(_o.SafetyBackupDir).GetFiles("pre-import-*.zip")
                    .OrderByDescending(f => f.LastWriteTimeUtc).ThenByDescending(f => f.Name).ToList();
                long sum = 0;
                for (int i = 0; i < all.Count; i++)
                {
                    sum += all[i].Length;
                    // the newest always stays; older ones go past the count or the total size (attachments make them big)
                    if (i > 0 && (i >= _o.KeepSafetyBackups || sum > _o.MaxSafetyBytes)) all[i].Delete();
                }
            }
            catch { /* best effort */ }
        }

        // ======================= helpers =======================

        private static string Normalize(string rel) => rel.Replace('\\', '/').TrimStart('/');

        private bool InDataFolder(string rel)
        {
            var n = Normalize(rel);
            return _o.DataFolders.Any(f => n.StartsWith(Normalize(f).TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
        }

        private static readonly HashSet<string> CompressedExt = new(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".gif", ".webp", ".mp4", ".mkv", ".mov", ".avi", ".webm", ".m4v", ".mp3", ".m4a", ".aac", ".ogg", ".opus",
            ".flac", ".zip", ".7z", ".rar", ".gz", ".pdf", ".docx", ".xlsx", ".pptx", ".odt", ".heic", ".avif",
        };

        /// <summary>Already-compressed formats are stored as they are: deflating them again only costs time.</summary>
        private static bool IsCompressed(string rel) => CompressedExt.Contains(Path.GetExtension(rel));

        private static (string hash, long length) CopyHashing(Stream src, Stream dst, CancellationToken ct)
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[1 << 17];
            long total = 0;
            int n;
            while ((n = src.Read(buf, 0, buf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                sha.AppendData(buf, 0, n);
                dst.Write(buf, 0, n);
                total += n;
            }
            return (Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant(), total);
        }

        private static long FreeSpace(string dir)
        {
            // GetPathRoot is null only for a null path; new DriveInfo(null) used to throw and land in the catch below
            try { return Path.GetPathRoot(Path.GetFullPath(dir)) is { } root ? new DriveInfo(root).AvailableFreeSpace : -1; }
            catch { return -1; } // unknown (network path…): don't block
        }

        private static string Gb(long bytes) => bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB" : $"{bytes / (double)(1 << 20):0} MB";

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
                if (total > declared) throw new BackupException(BackupError.HashMismatch, T("Um arquivo do backup é maior do que o manifesto diz."));
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
                if (total > max) throw new BackupException(BackupError.HashMismatch, T("Um arquivo do backup é maior do que o manifesto diz."));
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
