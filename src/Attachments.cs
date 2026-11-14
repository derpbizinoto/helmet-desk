using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace HelmetDesk
{
    /// <summary>
    /// Files of note attachments: data folder/attachments/&lt;item id&gt;/. Copies in (no size limit), opens with the
    /// default app, and cleans up files that no note references any more (at startup, so "Desfazer" still works).
    /// </summary>
    public static class Attachments
    {
        public static string Root => Path.Combine(Store.Dir, "attachments");

        public static string FullPath(Attachment a) => a.File == null ? null : Path.Combine(Store.Dir, a.File);

        private static string ItemDir(Item it) => Path.Combine(Root, it.Id);

        private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico", ".jfif" };
        private static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".m4v", ".flv" };
        private static readonly HashSet<string> AudioExt = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".ogg", ".flac", ".m4a", ".aac", ".opus", ".wma" };
        private static readonly HashSet<string> DocExt = new(StringComparer.OrdinalIgnoreCase) { ".pdf", ".doc", ".docx", ".txt", ".md", ".rtf", ".odt", ".xls", ".xlsx", ".csv", ".ppt", ".pptx" };

        public static bool IsImage(string path) => ImageExt.Contains(Path.GetExtension(path ?? ""));

        /// <summary>Icon resource for a file attachment.</summary>
        public static string IconKey(Attachment a)
        {
            if (a.Kind == AttachmentKind.Link) return "I.Globe";
            if (a.Kind == AttachmentKind.Image) return "I.Image";
            var ext = Path.GetExtension(a.Name ?? "");
            return VideoExt.Contains(ext) ? "I.Video" : AudioExt.Contains(ext) ? "I.Music" : DocExt.Contains(ext) ? "I.FileText" : "I.File";
        }

        public static string FormatSize(long bytes) =>
            bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.#} GB".Replace('.', ',')
            : bytes >= 1 << 20 ? $"{bytes / (double)(1 << 20):0.#} MB".Replace('.', ',')
            : bytes >= 1 << 10 ? $"{bytes / 1024.0:0} KB"
            : $"{bytes} B";

        private static string UniqueName(string dir, string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            if (name.Length > 120) name = Path.GetFileNameWithoutExtension(name).Substring(0, 100) + Path.GetExtension(name);
            var path = Path.Combine(dir, name);
            for (int i = 2; File.Exists(path); i++)
                path = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
            return path;
        }

        private static string Rel(string full) => Path.GetRelativePath(Store.Dir, full);

        /// <summary>Copies files in (in the background: videos can be big). Folders are skipped.</summary>
        public static async Task<int> AddFilesAsync(Item it, IEnumerable<string> paths)
        {
            int added = 0;
            var dir = ItemDir(it);
            foreach (var src in paths.Where(File.Exists))
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    var dest = UniqueName(dir, Path.GetFileName(src));
                    await Task.Run(() => File.Copy(src, dest));
                    it.Attachments.Add(new Attachment
                    {
                        Kind = IsImage(src) ? AttachmentKind.Image : AttachmentKind.File,
                        Name = Path.GetFileName(src), File = Rel(dest), Size = new FileInfo(dest).Length,
                    });
                    added++;
                }
                catch (Exception ex) { Log.Warn("anexo: falha ao copiar " + Path.GetExtension(src), ex); }
            }
            return added;
        }

        /// <summary>Saves a pasted picture (screenshot, image copied from the browser) as PNG.</summary>
        public static Attachment AddImage(Item it, BitmapSource img)
        {
            var dir = ItemDir(it);
            Directory.CreateDirectory(dir);
            var dest = UniqueName(dir, $"Imagem {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png");
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(img));
            using (var fs = File.Create(dest)) enc.Save(fs);
            var a = new Attachment { Kind = AttachmentKind.Image, Name = Path.GetFileName(dest), File = Rel(dest), Size = new FileInfo(dest).Length };
            it.Attachments.Add(a);
            return a;
        }

        public static Attachment AddLink(Item it, string url)
        {
            url = LinkText.Normalize(url);
            var a = new Attachment { Kind = AttachmentKind.Link, Name = url, Url = url };
            it.Attachments.Add(a);
            LinkPreview.Get(url); // start fetching title/icon
            return a;
        }

        /// <summary>Copies a note's attachments to another note (Duplicar).</summary>
        public static void CopyAll(Item from, Item to)
        {
            foreach (var a in from.Attachments)
            {
                var copy = new Attachment { Kind = a.Kind, Name = a.Name, Url = a.Url, Size = a.Size };
                var src = FullPath(a);
                if (src != null)
                {
                    if (!File.Exists(src)) continue;
                    try
                    {
                        Directory.CreateDirectory(ItemDir(to));
                        var dest = UniqueName(ItemDir(to), Path.GetFileName(src));
                        File.Copy(src, dest);
                        copy.File = Rel(dest);
                    }
                    catch (Exception ex) { Log.Warn("anexo: falha ao duplicar", ex); continue; }
                }
                to.Attachments.Add(copy);
            }
        }

        public static void Open(Attachment a)
        {
            if (a.Kind == AttachmentKind.Link) { LinkPreview.Open(a.Url); return; }
            var p = FullPath(a);
            if (p == null || !File.Exists(p)) { MessageBox.Show("O arquivo deste anexo não foi encontrado.", "Helmet Desk"); return; }
            try { Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Warn("anexo: não abriu", ex); MessageBox.Show("Nenhum aplicativo abriu este arquivo.", "Helmet Desk"); }
        }

        public static void Reveal(Attachment a)
        {
            var p = FullPath(a);
            if (p == null || !File.Exists(p)) return;
            try { Process.Start("explorer.exe", $"/select,\"{p}\""); } catch (Exception ex) { Log.Warn("anexo: explorer", ex); }
        }

        /// <summary>
        /// Startup cleanup: deletes files no attachment points to (removed attachments, notes deleted for good).
        /// Only runs after data.json loaded fine, so a damaged file never takes the media with it.
        /// </summary>
        public static int Purge(IEnumerable<Item> items, DateTime olderThan)
        {
            int removed = 0;
            try
            {
                if (!Directory.Exists(Root)) return 0;
                var used = new HashSet<string>(items.SelectMany(i => i.Attachments).Where(a => a.File != null)
                    .Select(a => Path.GetFullPath(FullPath(a))), StringComparer.OrdinalIgnoreCase);
                foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                {
                    if (used.Contains(Path.GetFullPath(f)) || File.GetLastWriteTime(f) >= olderThan) continue;
                    try { File.Delete(f); removed++; } catch { }
                }
                foreach (var d in Directory.EnumerateDirectories(Root))
                    try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
            }
            catch (Exception ex) { Log.Warn("anexo: limpeza falhou", ex); }
            if (removed > 0) Log.Info($"anexos: {removed} arquivo(s) sem uso removido(s)");
            return removed;
        }
    }
}
