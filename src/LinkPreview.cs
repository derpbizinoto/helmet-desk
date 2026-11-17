using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HelmetDesk
{
    /// <summary>
    /// Title and icon of web pages pasted into notes. Fetched once (page HTML up to 1 MB, icon up to 512 KB) and kept in
    /// the data folder (links/), so it works offline afterwards. Only the pasted address is requested: nothing about
    /// the user is sent. Until the page answers (or when it can't), the chip shows the site's domain and a globe.
    /// </summary>
    public static class LinkPreview
    {
        public sealed class Entry
        {
            public string Title { get; set; }
            public string Site { get; set; }
            /// <summary>File name of the 32 px PNG icon inside links/ (per host), or null.</summary>
            public string Icon { get; set; }
            public DateTime FetchedAt { get; set; }
            public bool Failed { get; set; }
        }

        static LinkPreview() { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } // windows-1252 pages

        /// <summary>Off in the self-test and snapshots (no network).</summary>
        public static bool Enabled { get; set; } = true;

        /// <summary>Raised on the UI thread when a page's title/icon arrives.</summary>
        public static event Action<string> Updated;

        private static string Dir => Path.Combine(Store.Dir, "links");
        private static string IndexPath => Path.Combine(Dir, "index.json");
        private static Dictionary<string, Entry> _index;
        private static readonly Dictionary<string, Task> _running = new();
        private static readonly Dictionary<string, ImageSource> _icons = new();
        private static readonly object _gate = new();

        private static readonly Lazy<HttpClient> Http = new(() =>
        {
            var h = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = true, MaxAutomaticRedirections = 6 };
            var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(10) };
            // a plain browser identity: some sites refuse unknown clients. No personal data goes in here.
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
            c.DefaultRequestHeaders.AcceptLanguage.ParseAdd("pt-BR,pt;q=0.9,en;q=0.7");
            return c;
        });

        private static Dictionary<string, Entry> Index
        {
            get
            {
                if (_index != null) return _index;
                try { _index = File.Exists(IndexPath) ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(IndexPath)) : null; }
                catch (Exception ex) { Log.Warn("links: índice ilegível, recriando", ex); }
                return _index ??= new Dictionary<string, Entry>();
            }
        }

        /// <summary>The on-disk cache folder (titles + favicons); Settings → "Limpar cache" empties it.</summary>
        public static string CacheDir => Dir;

        /// <summary>Forgets every cached title/icon in memory (call before deleting <see cref="CacheDir"/>); they are fetched again when shown.</summary>
        public static void ForgetAll()
        {
            lock (_gate) { _index = new Dictionary<string, Entry>(); _icons.Clear(); }
        }

        private static void SaveIndex()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string json;
                lock (_gate) json = JsonSerializer.Serialize(Index);
                File.WriteAllText(IndexPath + ".tmp", json);
                File.Move(IndexPath + ".tmp", IndexPath, true);
            }
            catch (Exception ex) { Log.Warn("links: falha ao salvar o índice", ex); }
        }

        /// <summary>Cached info (null = not fetched yet). Starts the fetch in the background when needed.</summary>
        public static Entry Get(string url)
        {
            url = LinkText.Normalize(url);
            Entry e;
            lock (_gate) Index.TryGetValue(url, out e);
            bool stale = e == null || (e.Failed && (DateTime.Now - e.FetchedAt).TotalHours > 6);
            if (stale && Enabled) Fetch(url);
            return e;
        }

        /// <summary>Test/snapshot hook: put a known title in the cache (nothing is downloaded).</summary>
        public static void Seed(string url, string title, string site = null)
        {
            lock (_gate) Index[LinkText.Normalize(url)] = new Entry { Title = title, Site = site, FetchedAt = DateTime.Now };
        }

        public static string Host(string url)
        {
            try
            {
                var h = new Uri(LinkText.Normalize(url)).Host;
                return h.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? h.Substring(4) : h;
            }
            catch { return url; }
        }

        /// <summary>Short name for a chip: the first part of the page title ("Escitalopram – Wikipédia" → "Escitalopram"),
        /// else the site name, else the domain.</summary>
        public static string Label(string url)
        {
            var e = Get(url);
            var t = Clean(e?.Title);
            if (!string.IsNullOrEmpty(t))
            {
                var part = Regex.Split(t, @"\s+[-|–—·:•]\s+|\s+::\s+").Select(x => x.Trim()).FirstOrDefault(x => x.Length >= 3) ?? t;
                return Cut(part, 40);
            }
            if (!string.IsNullOrWhiteSpace(e?.Site)) return Cut(Clean(e.Site), 40);
            return Cut(Host(url), 40);
        }

        public static string FullTitle(string url) => Clean(Get(url)?.Title);

        private static string Cut(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1).TrimEnd() + "…";
        private static string Clean(string s) => string.IsNullOrWhiteSpace(s) ? null : Regex.Replace(WebUtility.HtmlDecode(s), @"\s+", " ").Trim();

        /// <summary>The site's icon, or null (show a globe).</summary>
        public static ImageSource Icon(string url)
        {
            var e = Get(url);
            if (e?.Icon == null) return null;
            lock (_gate)
            {
                if (_icons.TryGetValue(e.Icon, out var img)) return img;
                try
                {
                    var path = Path.Combine(Dir, e.Icon);
                    if (!File.Exists(path)) return null;
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.UriSource = new Uri(path);
                    bi.EndInit();
                    bi.Freeze();
                    _icons[e.Icon] = bi;
                    return bi;
                }
                catch { return null; }
            }
        }

        public static void Open(string url)
        {
            url = LinkText.Normalize(url);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https")) return;
            try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Warn("links: não abriu " + u.Host, ex); }
        }

        // ================= download =================

        private static void Fetch(string url)
        {
            lock (_gate)
            {
                if (_running.ContainsKey(url)) return;
                _running[url] = Task.Run(() => FetchAsync(url));
            }
        }

        private static async Task FetchAsync(string url)
        {
            var entry = new Entry { FetchedAt = DateTime.Now };
            try
            {
                var uri = new Uri(url);
                using var resp = await Http.Value.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
                var final = resp.RequestMessage?.RequestUri ?? uri;
                var type = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (resp.IsSuccessStatusCode && type.Contains("html"))
                {
                    var bytes = await ReadUpTo(resp, 1 << 20);
                    var html = Decode(bytes, resp.Content.Headers.ContentType?.CharSet);
                    entry.Title = Meta(html, "og:title") ?? TitleTag(html) ?? Meta(html, "twitter:title");
                    entry.Site = Meta(html, "og:site_name");
                    entry.Icon = await FetchIcon(final, IconCandidates(html, final));
                }
                else
                {
                    entry.Icon = await FetchIcon(final, new List<Uri>());
                    if (!resp.IsSuccessStatusCode) entry.Failed = true;
                }
            }
            catch (Exception ex)
            {
                entry.Failed = true;
                Log.Info("links: sem prévia para " + Host(url) + " (" + ex.GetType().Name + ")");
            }
            lock (_gate) { Index[url] = entry; _running.Remove(url); }
            SaveIndex();
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => Updated?.Invoke(url)));
        }

        private static async Task<byte[]> ReadUpTo(HttpResponseMessage resp, int max)
        {
            using var s = await resp.Content.ReadAsStreamAsync();
            var ms = new MemoryStream();
            var buf = new byte[16384];
            int n;
            while (ms.Length < max && (n = await s.ReadAsync(buf, 0, (int)Math.Min(buf.Length, max - ms.Length))) > 0) ms.Write(buf, 0, n);
            return ms.ToArray();
        }

        private static string Decode(byte[] bytes, string charset)
        {
            Encoding enc = null;
            try { if (!string.IsNullOrEmpty(charset)) enc = Encoding.GetEncoding(charset.Trim('"')); } catch { }
            if (enc == null)
            {
                var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
                var m = Regex.Match(head, @"<meta[^>]+charset\s*=\s*[""']?([\w-]+)", RegexOptions.IgnoreCase);
                try { if (m.Success) enc = Encoding.GetEncoding(m.Groups[1].Value); } catch { }
            }
            return (enc ?? Encoding.UTF8).GetString(bytes);
        }

        private static IEnumerable<Dictionary<string, string>> Tags(string html, string name)
        {
            foreach (Match m in Regex.Matches(html, "<" + name + @"\b([^>]*)>", RegexOptions.IgnoreCase))
            {
                var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match a in Regex.Matches(m.Groups[1].Value, @"([\w:-]+)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>]+))"))
                    attrs[a.Groups[1].Value] = a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Value;
                yield return attrs;
            }
        }

        private static string Meta(string html, string key) =>
            Tags(html, "meta").Where(a => (a.TryGetValue("property", out var p) && p.Equals(key, StringComparison.OrdinalIgnoreCase))
                                       || (a.TryGetValue("name", out var n) && n.Equals(key, StringComparison.OrdinalIgnoreCase)))
                .Select(a => a.TryGetValue("content", out var c) ? c : null).FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));

        private static string TitleTag(string html)
        {
            var m = Regex.Match(html, @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            return m.Success && !string.IsNullOrWhiteSpace(m.Groups[1].Value) ? m.Groups[1].Value : null;
        }

        private static List<Uri> IconCandidates(string html, Uri page)
        {
            var list = new List<(Uri u, int score)>();
            foreach (var a in Tags(html, "link"))
            {
                if (!a.TryGetValue("rel", out var rel) || !rel.ToLowerInvariant().Contains("icon")) continue;
                if (!a.TryGetValue("href", out var href) || string.IsNullOrWhiteSpace(href)) continue;
                if (!Uri.TryCreate(page, WebUtility.HtmlDecode(href.Trim()), out var u)) continue;
                if (u.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || (a.TryGetValue("type", out var t) && t.Contains("svg"))) continue;
                int size = 16;
                if (a.TryGetValue("sizes", out var sz)) { var m = Regex.Match(sz, @"(\d+)x"); if (m.Success) size = int.Parse(m.Groups[1].Value); }
                if (rel.Contains("apple-touch")) size = Math.Max(size, 120);
                // ~32-64 px is ideal for a 16 px chip; huge touch icons are fine but slower
                list.Add((u, size >= 32 && size <= 96 ? 0 : size > 96 ? 1 : 2));
            }
            return list.OrderBy(x => x.score).Select(x => x.u).ToList();
        }

        private static async Task<string> FetchIcon(Uri page, List<Uri> candidates)
        {
            string file = Regex.Replace(page.Host.ToLowerInvariant(), @"[^a-z0-9.-]", "_") + ".png";
            var path = Path.Combine(Dir, file);
            if (File.Exists(path)) return file;
            candidates.Add(new Uri(page, "/favicon.ico"));
            foreach (var u in candidates.Take(4))
            {
                try
                {
                    using var resp = await Http.Value.GetAsync(u, HttpCompletionOption.ResponseHeadersRead);
                    if (!resp.IsSuccessStatusCode) continue;
                    var bytes = await ReadUpTo(resp, 512 * 1024);
                    var png = ToPng(bytes);
                    if (png == null) continue;
                    Directory.CreateDirectory(Dir);
                    File.WriteAllBytes(path, png);
                    return file;
                }
                catch { }
            }
            return null;
        }

        /// <summary>Any format WPF reads (ico, png, jpg, gif, bmp) → 32 px PNG. Null when it isn't an image.</summary>
        private static byte[] ToPng(byte[] bytes)
        {
            try
            {
                var dec = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = dec.Frames.OrderBy(f => Math.Abs(f.PixelWidth - 32) + (f.PixelWidth < 32 ? 64 : 0)).First();
                BitmapSource src = frame;
                if (frame.PixelWidth != 32)
                    src = new TransformedBitmap(frame, new ScaleTransform(32.0 / frame.PixelWidth, 32.0 / frame.PixelHeight));
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(src));
                using var ms = new MemoryStream();
                enc.Save(ms);
                return ms.ToArray();
            }
            catch { return null; }
        }
    }

    /// <summary>Finds web addresses in plain text and draws them as chips.</summary>
    public static class LinkText
    {
        private static readonly Regex Url = new(@"(?<![\w@/])(?:https?://|www\.)[^\s<>""'`]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string Normalize(string url)
        {
            url = (url ?? "").Trim();
            return url.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + url : url;
        }

        /// <summary>True when the whole text is one address (what a "copy link" puts on the clipboard).</summary>
        public static bool IsSingleUrl(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0 || text.Any(char.IsWhiteSpace)) return false;
            var m = Url.Match(text);
            return m.Success && m.Index == 0 && Trim(m.Value).Length == text.Length
                && Uri.TryCreate(Normalize(text), UriKind.Absolute, out var u) && u.Host.Contains('.');
        }

        /// <summary>Strips punctuation that ends a sentence rather than the address ("veja https://x.com/a." → no dot).</summary>
        private static string Trim(string url)
        {
            while (url.Length > 0)
            {
                char c = url[^1];
                if (".,;:!?'\"]}>".IndexOf(c) >= 0) { url = url[..^1]; continue; }
                if (c == ')' && url.Count(ch => ch == '(') < url.Count(ch => ch == ')')) { url = url[..^1]; continue; }
                break;
            }
            return url;
        }

        /// <summary>Text split into plain pieces and addresses, in order.</summary>
        public static List<(string text, bool url)> Split(string text)
        {
            var list = new List<(string, bool)>();
            if (string.IsNullOrEmpty(text)) return list;
            int pos = 0;
            foreach (Match m in Url.Matches(text))
            {
                var u = Trim(m.Value);
                if (u.Length < 8 || !Uri.TryCreate(Normalize(u), UriKind.Absolute, out var parsed) || !parsed.Host.Contains('.')) continue;
                if (m.Index > pos) list.Add((text.Substring(pos, m.Index - pos), false));
                list.Add((u, true));
                pos = m.Index + u.Length;
            }
            if (pos < text.Length) list.Add((text.Substring(pos), false));
            return list;
        }

        public static bool HasUrl(string text) => Split(text).Any(p => p.url);

        /// <summary>Plain text with each address replaced by its short name (card previews).</summary>
        public static string Friendly(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf("http", StringComparison.OrdinalIgnoreCase) < 0 && text.IndexOf("www.", StringComparison.OrdinalIgnoreCase) < 0) return text;
            return string.Concat(Split(text).Select(p => p.url ? "🔗 " + LinkPreview.Label(p.text) : p.text));
        }

        // ---------- read-only text with chips ----------

        public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
            "Source", typeof(string), typeof(LinkText), new PropertyMetadata(null, (o, e) => { if (o is TextBlock tb) Fill(tb, (string)e.NewValue); }));
        public static string GetSource(DependencyObject o) => (string)o.GetValue(SourceProperty);
        public static void SetSource(DependencyObject o, string v) => o.SetValue(SourceProperty, v);

        public static void Fill(TextBlock tb, string text)
        {
            tb.Inlines.Clear();
            foreach (var (piece, isUrl) in Split(text ?? ""))
            {
                if (isUrl) tb.Inlines.Add(new InlineUIContainer(LinkChip.Create(piece, tb.FontSize)) { BaselineAlignment = BaselineAlignment.Center });
                else tb.Inlines.Add(new Run(piece));
            }
        }
    }

    /// <summary>The pill that stands in for an address: site icon + short page name. Click opens the page.</summary>
    public static class LinkChip
    {
        public static Border Create(string url, double fontSize = 14)
        {
            url = url.Trim();
            var icon = new ContentControl { Width = 15, Height = 15, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center, Focusable = false, IsTabStop = false };
            var label = new TextBlock { FontSize = Math.Max(11, fontSize - 1), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 280 };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Text");
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(icon);
            row.Children.Add(label);
            var chip = new Border
            {
                Child = row, Tag = url, CornerRadius = new CornerRadius(6), Padding = new Thickness(5, 1, 7, 1), Margin = new Thickness(1, 1, 1, 1),
                BorderThickness = new Thickness(1), Cursor = Cursors.Hand,
            };
            chip.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
            chip.SetResourceReference(Border.BorderBrushProperty, "Line");
            ToolTipService.SetInitialShowDelay(chip, 250);

            void Update()
            {
                var img = LinkPreview.Icon(url);
                if (img != null) icon.Content = new Image { Source = img, Width = 15, Height = 15, Stretch = Stretch.Uniform };
                else
                {
                    var g = new Icon { Data = Ui.Geo("I.Globe"), Width = 14, Height = 14, Thickness = 1.8 };
                    g.SetResourceReference(Icon.ForegroundProperty, "Accent");
                    icon.Content = g;
                }
                label.Text = LinkPreview.Label(url);
                var title = LinkPreview.FullTitle(url);
                chip.ToolTip = (title != null ? title + "\n" : "") + url + "\nClique para abrir no navegador";
            }
            Update();

            void OnUpdated(string u) { if (u == LinkText.Normalize(url)) Update(); }
            chip.Loaded += (s, e) => { LinkPreview.Updated -= OnUpdated; LinkPreview.Updated += OnUpdated; Update(); };
            chip.Unloaded += (s, e) => LinkPreview.Updated -= OnUpdated;
            LinkPreview.Updated += OnUpdated;

            chip.MouseEnter += (s, e) => chip.SetResourceReference(Border.BorderBrushProperty, "Accent");
            chip.MouseLeave += (s, e) => chip.SetResourceReference(Border.BorderBrushProperty, "Line");
            chip.PreviewMouseLeftButtonDown += (s, e) => e.Handled = true; // keep the caret where it is inside the editor
            chip.PreviewMouseLeftButtonUp += (s, e) => { e.Handled = true; LinkPreview.Open(url); };
            return chip;
        }
    }
}
