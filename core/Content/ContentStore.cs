using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HelmetDesk.Core.Content
{
    /// <summary>
    /// Reading and writing conteudo.json (painel experimental "Conteúdo"), with no WPF. The file is separate from data.json,
    /// so this panel never touches the app's other data. Written like data.json: temporary file forced to disk, then swapped.
    /// </summary>
    public static class ContentStore
    {
        /// <summary>Format version written as "schemaVersion".</summary>
        public const int CurrentSchema = 1;

        public static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        /// <summary>
        /// Reads <paramref name="path"/>. First use (no file): three sample channels. An unreadable file is kept aside as
        /// conteudo.corrompido.&lt;time&gt;.json and the panel starts empty (without samples), so nothing is lost.
        /// </summary>
        public static ContentData Load(string path, DateTime now, Action<string>? warn = null)
        {
            if (!File.Exists(path))
            {
                var fresh = new ContentData();
                Seed(fresh, now);
                return fresh;
            }
            try
            {
                var data = JsonSerializer.Deserialize<ContentData>(File.ReadAllText(path), JsonOptions)
                           ?? throw new InvalidDataException("conteudo.json vazio.");
                if (data.SchemaVersion > CurrentSchema)
                    warn?.Invoke($"conteudo.json usa o esquema {data.SchemaVersion}, mais novo que o deste app ({CurrentSchema})");
                Normalize(data);
                return data;
            }
            catch (Exception ex)
            {
                var copy = Path.Combine(Path.GetDirectoryName(path) ?? ".", $"conteudo.corrompido.{now:yyyyMMddHHmmss}.json");
                try { File.Copy(path, copy, true); } catch { copy = "(cópia não criada)"; }
                warn?.Invoke($"Não foi possível ler conteudo.json ({ex.Message}); cópia guardada em {copy}");
                return new ContentData();
            }
        }

        /// <summary>The sample channels of the first use; editable and deletable like any other.</summary>
        public static void Seed(ContentData data, DateTime now)
        {
            if (data.Channels.Count > 0) return;
            (string name, string color)[] samples = { ("X", "#78B7FF"), ("YouTube", "#FF6F91"), ("IG/TikTok", "#C8A8F5") };
            for (int i = 0; i < samples.Length; i++)
                data.Channels.Add(new Channel { Name = samples[i].name, ColorHex = samples[i].color, SortOrder = i, CreatedAt = now });
        }

        /// <summary>Null lists, null entries, null texts and unknown stages are made safe to show.</summary>
        internal static void Normalize(ContentData data)
        {
            data.Channels = (data.Channels ?? new()).Where(c => c != null).ToList();
            data.Items = (data.Items ?? new()).Where(i => i != null).ToList();
            foreach (var c in data.Channels)
            {
                c.Name ??= "";
                if (string.IsNullOrWhiteSpace(c.ColorHex)) c.ColorHex = "#8950D9";
                if (c.Followers < 0) c.Followers = 0;
            }
            foreach (var i in data.Items)
            {
                i.Title ??= "";
                i.Notes ??= "";
                if (!Enum.IsDefined(i.Stage)) i.Stage = Stage.Ideia;
            }
        }

        public static string Serialize(ContentData data) => JsonSerializer.Serialize(data, JsonOptions);

        /// <summary>Writes through a temporary file forced to disk, then replaces the old file.</summary>
        public static void Save(string path, ContentData data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            var tmp = path + ".tmp";
            var bytes = new UTF8Encoding(false).GetBytes(Serialize(data));
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, true);
        }
    }
}
