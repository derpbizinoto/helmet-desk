using System;
using System.Collections.Generic;
using System.IO;
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

        /// <summary>Writes data.json through a temporary file, so a failed write never leaves a half-written data.json.</summary>
        public static void Write(string dir, string json)
        {
            Directory.CreateDirectory(dir);
            var path = FilePath(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, true);
        }

        /// <summary>Copies data.json to data.bak.json (after a good load). Errors are ignored.</summary>
        public static void CopyBackup(string dir)
        {
            try { File.Copy(FilePath(dir), Path.Combine(dir, "data.bak.json"), true); } catch { }
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
