using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HelmetDesk.Core
{
    /// <summary>What <see cref="DataStore.Read"/> found in data.json.</summary>
    public sealed class DataFileContent
    {
        /// <summary>Schema written in the file ("Version"); 1 when the file has none.</summary>
        public int Schema { get; init; }
        /// <summary>The file after <see cref="DataStore.Migrate"/> and <see cref="DataStore.FillMissing"/>, ready to deserialize.</summary>
        public string Json { get; init; }
    }

    /// <summary>
    /// Reading, migrating and writing data.json, with no WPF. The app (src/Store.cs) deserializes <see cref="DataFileContent.Json"/>
    /// into its model, shows messages and schedules saves; everything that touches the file is here, where the tests reach it.
    /// </summary>
    public static class DataStore
    {
        public const string FileName = "data.json";

        /// <summary>Data format version written in data.json ("Version"). Bump it and add a step to <see cref="Migrate"/> when the format changes.</summary>
        /// 2 = Finanças added ("Finance" section). Bumped so an older app refuses a newer backup instead of dropping Finanças on save.
        public const int CurrentSchema = 2;

        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        public static string FilePath(string dir) => Path.Combine(dir, FileName);

        /// <summary>Brings a data.json in <paramref name="dir"/> from <paramref name="fromSchema"/> up to <see cref="CurrentSchema"/>.</summary>
        public static void Migrate(string dir, int fromSchema)
        {
            // 1 → 2: the "Finance" section is new and optional; a file without it loads with empty Finanças
            // (seeded in Store.Load), so nothing in data.json has to be rewritten. Future steps go here, e.g.:
            // if (fromSchema < 3) { …rewrite data.json… }
        }

        /// <summary>
        /// Reads data.json in <paramref name="dir"/>: detects the schema, migrates an older file, fills missing sections.
        /// Null when there is no file (first run). Throws when the file can't be read or isn't a JSON object;
        /// the file itself is never changed here (see <see cref="KeepCorruptCopy"/>).
        /// </summary>
        public static DataFileContent Read(string dir, Action<string> info = null, Action<string> warn = null)
        {
            var path = FilePath(dir);
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            int schema = ReadSchema(text);
            if (schema < CurrentSchema)
            {
                info?.Invoke($"Migrando dados do esquema {schema} para {CurrentSchema}");
                Migrate(dir, schema);
                text = File.ReadAllText(path);
            }
            else if (schema > CurrentSchema)
                warn?.Invoke($"data.json usa o esquema {schema}, mais novo que o deste app ({CurrentSchema})");
            return new DataFileContent { Schema = schema, Json = FillMissing(text) };
        }

        /// <summary>The "Version" of a data.json text; 1 when absent. Throws when the text isn't a JSON object.</summary>
        public static int ReadSchema(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("Version", out var v) && v.TryGetInt32(out var n) ? n : 1;
        }

        // ---------- missing / null sections ----------

        /// <summary>Sections that get a default when missing or written as null: the same values the model's constructors use.</summary>
        private static readonly Dictionary<string, string> Defaults = new()
        {
            ["Items"] = "[]",
            ["Labels"] = "[]",
            ["Settings"] = "{}",
            ["Settings.Main"] = "{}",
            ["Settings.TasksWidgetBounds"] = """{"W":300,"H":380}""",
            ["Settings.CollapsedGroups"] = "[]",
            ["Finance"] = "{}",
            ["Finance.Accounts"] = "[]",
            ["Finance.Categories"] = "[]",
            ["Finance.Transactions"] = "[]",
            ["Finance.Recurrences"] = "[]",
        };

        /// <summary>Lists whose null entries are dropped.</summary>
        private static readonly HashSet<string> DropNullEntries = new() { "Items", "Labels" };

        /// <summary>Objects whose members are looked at (the root and the sections above); anything else is copied as is.</summary>
        private static readonly HashSet<string> Containers = new() { "", "Settings", "Finance" };

        /// <summary>
        /// A data.json with a section written as null (or a null entry in a list) still loads: the gap gets an empty
        /// default instead of failing the whole load, which would start the app with no data. Values are copied untouched.
        /// Throws when the text isn't a JSON object.
        /// </summary>
        public static string FillMissing(string json)
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("data.json não contém um objeto JSON.");
            var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
                CopyObject(w, doc.RootElement, "");
            return Encoding.UTF8.GetString(buffer.ToArray());
        }

        private static void CopyObject(Utf8JsonWriter w, JsonElement obj, string path)
        {
            w.WriteStartObject();
            var seen = new HashSet<string>();
            foreach (var p in obj.EnumerateObject())
            {
                var child = path.Length == 0 ? p.Name : path + "." + p.Name;
                seen.Add(child);
                w.WritePropertyName(p.Name);
                if (p.Value.ValueKind == JsonValueKind.Null && Defaults.ContainsKey(child)) WriteDefault(w, child);
                else if (p.Value.ValueKind == JsonValueKind.Object && Containers.Contains(child)) CopyObject(w, p.Value, child);
                else if (p.Value.ValueKind == JsonValueKind.Array && DropNullEntries.Contains(child)) CopyArrayWithoutNulls(w, p.Value);
                else p.Value.WriteTo(w);
            }
            // sections this object should have but the file doesn't: written out so the result is complete
            foreach (var key in Defaults.Keys)
            {
                int dot = key.LastIndexOf('.');
                var parent = dot < 0 ? "" : key.Substring(0, dot);
                if (parent != path || seen.Contains(key)) continue;
                w.WritePropertyName(key.Substring(dot + 1));
                WriteDefault(w, key);
            }
            w.WriteEndObject();
        }

        /// <summary>The default of <paramref name="key"/>; a section with sections of its own (Settings, Finance) gets theirs too.</summary>
        private static void WriteDefault(Utf8JsonWriter w, string key)
        {
            if (Containers.Contains(key))
            {
                using var empty = JsonDocument.Parse(Defaults[key]);
                CopyObject(w, empty.RootElement, key);
            }
            else w.WriteRawValue(Defaults[key]);
        }

        private static void CopyArrayWithoutNulls(Utf8JsonWriter w, JsonElement arr)
        {
            w.WriteStartArray();
            foreach (var x in arr.EnumerateArray())
                if (x.ValueKind != JsonValueKind.Null) x.WriteTo(w);
            w.WriteEndArray();
        }

        // ---------- writing ----------

        /// <summary>
        /// Writes data.json through a temporary file that is forced to disk (Flush(true)) before it replaces data.json,
        /// so neither a failed write nor a power cut right after it leaves data.json half-written or empty.
        /// </summary>
        public static void Write(string dir, string json)
        {
            Directory.CreateDirectory(dir);
            var path = FilePath(dir);
            var tmp = path + ".tmp";
            var bytes = new UTF8Encoding(false).GetBytes(json); // same bytes File.WriteAllText wrote (UTF-8, no BOM)
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, true);
        }

        // ---------- startup copies ----------

        /// <summary>Copy of data.json as it was at the last good start (kept from 1.0; overwritten on every start).</summary>
        public const string LastStartBackupName = "data.bak.json";

        /// <summary>How many dated copies (data.bak.&lt;yyyyMMdd-HHmm&gt;.json) are kept.</summary>
        public const int KeepDatedBackups = 5;

        private const string DatedPrefix = "data.bak.", DatedFormat = "yyyyMMdd-HHmm", DatedSuffix = ".json";

        /// <summary>
        /// After a good load: refreshes data.bak.json and makes a dated copy (data.bak.20261119-0105.json) at most once a day,
        /// and only when data.json changed since the newest dated copy. Only the newest <paramref name="keep"/> dated copies stay,
        /// so a mistake (something deleted, then the app reopened) can still be undone from an earlier day.
        /// Returns the dated copy made, or null. Errors are ignored: a missing copy never stops the app.
        /// </summary>
        public static string BackupOnStartup(string dir, DateTime now, int keep = KeepDatedBackups)
        {
            var source = FilePath(dir);
            if (!File.Exists(source)) return null;
            try { File.Copy(source, Path.Combine(dir, LastStartBackupName), true); } catch { }

            string made = null;
            try
            {
                var dated = DatedBackups(dir);
                var newest = dated.Count > 0 ? dated[^1] : default;
                bool today = dated.Exists(b => b.When.Date == now.Date);
                if (!today && (newest.Path == null || !SameContent(source, newest.Path)))
                {
                    var copy = Path.Combine(dir, DatedPrefix + now.ToString(DatedFormat, CultureInfo.InvariantCulture) + DatedSuffix);
                    File.Copy(source, copy, true);
                    made = copy;
                    dated.Add((made, now));
                }
                // oldest first: everything but the newest `keep` goes
                foreach (var old in dated.OrderBy(b => b.When).SkipLast(keep))
                    try { File.Delete(old.Path); } catch { }
            }
            catch { }
            return made;
        }

        /// <summary>The dated copies in <paramref name="dir"/>, oldest first. data.bak.json and other names are left out.</summary>
        public static List<(string Path, DateTime When)> DatedBackups(string dir)
        {
            var list = new List<(string, DateTime)>();
            foreach (var f in Directory.EnumerateFiles(dir, DatedPrefix + "*" + DatedSuffix))
            {
                var name = Path.GetFileName(f);
                if (name.Length <= DatedPrefix.Length + DatedSuffix.Length) continue; // data.bak.json itself
                var stamp = name.Substring(DatedPrefix.Length, name.Length - DatedPrefix.Length - DatedSuffix.Length);
                if (DateTime.TryParseExact(stamp, DatedFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when))
                    list.Add((f, when));
            }
            return list.OrderBy(b => b.Item2).ToList();
        }

        private static bool SameContent(string a, string b)
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            return fa.Length == fb.Length && File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b));
        }

        /// <summary>
        /// Keeps an unreadable data.json aside as data.corrompido.&lt;time&gt;.json before the app starts over with empty data.
        /// data.json itself is not touched. Returns the copy's path, or null when it couldn't be made.
        /// </summary>
        public static string KeepCorruptCopy(string dir, DateTime now)
        {
            var copy = Path.Combine(dir, $"data.corrompido.{now:yyyyMMddHHmmss}.json");
            try { File.Copy(FilePath(dir), copy, true); return copy; } catch { return null; }
        }
    }
}
