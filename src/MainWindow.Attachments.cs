using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace HelmetDesk
{
    /// <summary>
    /// "Anexos" of a note, under the checklist (edit and view modes). Ctrl+V anywhere in the open note adds what is on
    /// the clipboard (picture, files, a link); inside the description a link stays in the text as a chip instead.
    /// Dropping files/links on the panel also attaches them.
    /// </summary>
    public partial class MainWindow
    {
        private Item _attachItem;
        private static readonly Dictionary<string, ImageSource> ThumbCache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Called when the side panel shows another item (or none).</summary>
        private void BindAttachments(Item it)
        {
            if (_attachItem != null) _attachItem.Attachments.CollectionChanged -= OnAttachmentsChanged;
            _attachItem = it;
            if (it != null) it.Attachments.CollectionChanged += OnAttachmentsChanged;
            LinkPreview.Updated -= OnLinkPreviewUpdated;
            if (it != null) LinkPreview.Updated += OnLinkPreviewUpdated;
            BuildAttachments();
        }

        private void OnAttachmentsChanged(object s, NotifyCollectionChangedEventArgs e) => BuildAttachments();

        private void OnLinkPreviewUpdated(string url)
        {
            if (_attachItem?.Attachments.Any(a => a.Kind == AttachmentKind.Link && a.Url == url) == true) BuildAttachments();
        }

        private void BuildAttachments()
        {
            var it = _attachItem;
            Fill(EdAttach, it, editable: true);
            Fill(ViewAttach, it, editable: false);
            if (it != null) ViewEmpty.Visibility = !it.HasPreview && !it.HasChecklist && !it.HasAttachments ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Fill(StackPanel host, Item it, bool editable)
        {
            host.Children.Clear();
            bool show = it != null && (it.HasAttachments || (editable && it.IsNote));
            host.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show) return;

            // header
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
            if (editable)
            {
                var add = new Button { Style = (Style)FindResource("GhostButton"), Padding = new Thickness(8, 4, 8, 4), ToolTip = T("Escolher arquivos do computador") };
                var addRow = new StackPanel { Orientation = Orientation.Horizontal };
                addRow.Children.Add(new Icon { Data = Ui.Geo("I.Plus"), Width = 14, Height = 14, Margin = new Thickness(0, 0, 6, 0) });
                addRow.Children.Add(new TextBlock { Text = T("Adicionar") });
                add.Content = addRow;
                add.Click += (s, e) => PickFiles(it);
                DockPanel.SetDock(add, Dock.Right);
                head.Children.Add(add);
            }
            var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var clip = new Icon { Data = Ui.Geo("I.Paperclip"), Width = 15, Height = 15, Margin = new Thickness(0, 0, 8, 0) };
            clip.SetResourceReference(HelmetDesk.Icon.ForegroundProperty, "Text2");
            title.Children.Add(clip);
            title.Children.Add(new TextBlock { Text = T("Anexos"), FontSize = 14, FontWeight = FontWeights.SemiBold });
            if (it.HasAttachments) title.Children.Add(new TextBlock { Text = "  " + it.Attachments.Count, Style = Ui.St("Muted"), VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(title);
            host.Children.Add(head);

            // pictures as thumbnails, everything else as rows
            var images = it.Attachments.Where(a => a.Kind == AttachmentKind.Image).ToList();
            if (images.Count > 0)
            {
                var wrap = new WrapPanel { Margin = new Thickness(0, 0, -8, 6) };
                foreach (var a in images) wrap.Children.Add(Thumb(it, a, editable));
                host.Children.Add(wrap);
            }
            foreach (var a in it.Attachments.Where(a => a.Kind != AttachmentKind.Image))
                host.Children.Add(AttachRow(it, a, editable));

            if (editable) host.Children.Add(DropZone());
        }

        // ---------- pieces ----------

        private Border DropZone()
        {
            var text = new TextBlock
            {
                Text = T("Cole aqui com Ctrl+V ou arraste imagens, links e arquivos"), TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center, FontSize = 12.5, Margin = new Thickness(12, 14, 12, 14),
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Text2");
            var dash = new Rectangle { RadiusX = 10, RadiusY = 10, StrokeThickness = 1.2, StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false };
            dash.SetResourceReference(Shape.StrokeProperty, "Line");
            var grid = new Grid();
            grid.Children.Add(dash);
            grid.Children.Add(text);
            var zone = new Border
            {
                Child = grid, Margin = new Thickness(0, 6, 0, 0), Focusable = true, FocusVisualStyle = null, Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(10), Cursor = Cursors.Hand, Name = "AttachZone",
                ToolTip = T("Clique para escolher arquivos no computador"),
            };
            zone.GotKeyboardFocus += (s, e) => { dash.SetResourceReference(Shape.StrokeProperty, "Accent"); zone.SetResourceReference(Border.BackgroundProperty, "AccentSoft"); };
            zone.LostKeyboardFocus += (s, e) => { dash.SetResourceReference(Shape.StrokeProperty, "Line"); zone.Background = Brushes.Transparent; };
            // a click opens the file picker; the zone keeps the keyboard, so Ctrl+V right after also lands here
            zone.MouseLeftButtonDown += (s, e) => { zone.Focus(); e.Handled = true; };
            zone.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (_attachItem != null && zone.IsMouseOver) PickFiles(_attachItem);
            };
            zone.KeyDown += (s, e) =>
            {
                if ((e.Key == Key.Enter || e.Key == Key.Space) && _attachItem != null) { PickFiles(_attachItem); e.Handled = true; }
            };
            return zone;
        }

        private FrameworkElement Thumb(Item it, Attachment a, bool editable)
        {
            var path = Attachments.FullPath(a);
            var img = new Image { Stretch = Stretch.UniformToFill, Width = 112, Height = 84 };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            LoadThumb(img, path);
            var frame = new Border
            {
                Width = 112, Height = 84, CornerRadius = new CornerRadius(8), ClipToBounds = true, BorderThickness = new Thickness(1),
                Child = img, Cursor = Cursors.Hand,
            };
            frame.SetResourceReference(Border.BorderBrushProperty, "Line");
            frame.SetResourceReference(Border.BackgroundProperty, "Bg");
            // rounded clip for the picture itself
            frame.SizeChanged += (s, e) => img.Clip = new RectangleGeometry(new Rect(0, 0, frame.ActualWidth - 2, frame.ActualHeight - 2), 7, 7);

            var grid = new Grid { Margin = new Thickness(0, 0, 8, 8), ToolTip = $"{a.Name}\n{Attachments.FormatSize(a.Size)} · {T("clique para abrir")}" };
            grid.Children.Add(frame);
            if (editable)
            {
                var rm = RemoveButton(it, a);
                rm.HorizontalAlignment = HorizontalAlignment.Right; rm.VerticalAlignment = VerticalAlignment.Top; rm.Margin = new Thickness(0, 4, 4, 0);
                rm.Visibility = Visibility.Hidden;
                grid.Children.Add(rm);
                grid.MouseEnter += (s, e) => rm.Visibility = Visibility.Visible;
                grid.MouseLeave += (s, e) => rm.Visibility = Visibility.Hidden;
            }
            grid.MouseEnter += (s, e) => frame.SetResourceReference(Border.BorderBrushProperty, "Accent");
            grid.MouseLeave += (s, e) => frame.SetResourceReference(Border.BorderBrushProperty, "Line");
            frame.MouseLeftButtonUp += (s, e) => Attachments.Open(a);
            grid.ContextMenu = AttachMenu(it, a, editable);
            return grid;
        }

        private static void LoadThumb(Image img, string path)
        {
            if (path == null) return;
            lock (ThumbCache) if (ThumbCache.TryGetValue(path, out var cached)) { img.Source = cached; return; }
            Task.Run(() =>
            {
                try
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad; // file is not kept open
                    bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                    bi.DecodePixelWidth = 240;
                    bi.UriSource = new Uri(path);
                    bi.EndInit();
                    bi.Freeze();
                    return (ImageSource)bi;
                }
                catch { return null; }
            }).ContinueWith(t =>
            {
                if (t.Result == null) return;
                lock (ThumbCache) ThumbCache[path] = t.Result;
                img.Source = t.Result;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private Border AttachRow(Item it, Attachment a, bool editable)
        {
            bool link = a.Kind == AttachmentKind.Link;
            var row = new DockPanel();
            if (editable)
            {
                var rm = RemoveButton(it, a);
                rm.Visibility = Visibility.Hidden;
                DockPanel.SetDock(rm, Dock.Right);
                row.Children.Add(rm);
            }

            FrameworkElement glyph;
            var siteIcon = link ? LinkPreview.Icon(a.Url) : null;
            if (siteIcon != null) glyph = new Image { Source = siteIcon, Width = 18, Height = 18 };
            else
            {
                var ic = new Icon { Data = Ui.Geo(Attachments.IconKey(a)), Width = 17, Height = 17 };
                ic.SetResourceReference(HelmetDesk.Icon.ForegroundProperty, "Accent");
                glyph = ic;
            }
            var badge = new Border { Width = 34, Height = 34, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 10, 0), Child = glyph, VerticalAlignment = VerticalAlignment.Center };
            badge.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
            row.Children.Add(badge);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            string name = link ? LinkPreview.FullTitle(a.Url) ?? LinkPreview.Label(a.Url) : a.Name;
            text.Children.Add(new TextBlock { Text = name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
            string meta = link ? LinkPreview.Host(a.Url)
                : $"{Attachments.FormatSize(a.Size)} · {(System.IO.Path.GetExtension(a.Name) is { Length: > 1 } ext ? ext.Substring(1).ToUpperInvariant() : T("arquivo"))}";
            text.Children.Add(new TextBlock { Text = meta, Style = Ui.St("Muted"), FontSize = 11.5, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(text);

            var b = new Border
            {
                Child = row, Padding = new Thickness(6), Margin = new Thickness(-6, 0, -6, 4), CornerRadius = new CornerRadius(8),
                Background = Brushes.Transparent, Cursor = Cursors.Hand,
                ToolTip = (link ? a.Url : a.Name) + (link ? T("\nClique para abrir no navegador") : T("\nClique para abrir")),
            };
            b.MouseEnter += (s, e) => { b.SetResourceReference(Border.BackgroundProperty, "AccentSoft"); if (editable) row.Children[0].Visibility = Visibility.Visible; };
            b.MouseLeave += (s, e) => { b.Background = Brushes.Transparent; if (editable) row.Children[0].Visibility = Visibility.Hidden; };
            b.MouseLeftButtonUp += (s, e) => Attachments.Open(a);
            b.ContextMenu = AttachMenu(it, a, editable);
            return b;
        }

        private Button RemoveButton(Item it, Attachment a)
        {
            var rm = new Button
            {
                Style = (Style)FindResource("IconButton"), Padding = new Thickness(4), ToolTip = T("Remover anexo"), VerticalAlignment = VerticalAlignment.Center,
                Content = new Icon { Data = Ui.Geo("I.X"), Width = 13, Height = 13 },
            };
            rm.SetResourceReference(Control.BackgroundProperty, "Elevated");
            rm.Click += (s, e) => { RemoveAttachment(it, a); e.Handled = true; };
            return rm;
        }

        private ContextMenu AttachMenu(Item it, Attachment a, bool editable)
        {
            var menu = new ContextMenu();
            void Add(string header, Action act)
            {
                var mi = new MenuItem { Header = header };
                mi.Click += (s, e) => act();
                menu.Items.Add(mi);
            }
            Add(a.Kind == AttachmentKind.Link ? T("Abrir no navegador") : T("Abrir"), () => Attachments.Open(a));
            if (a.Kind == AttachmentKind.Link) Add(T("Copiar link"), () => { try { Clipboard.SetText(a.Url); } catch { } });
            else Add(T("Mostrar na pasta"), () => Attachments.Reveal(a));
            if (editable) { menu.Items.Add(new Separator()); Add(T("Remover"), () => RemoveAttachment(it, a)); }
            return menu;
        }

        private void RemoveAttachment(Item it, Attachment a)
        {
            int index = it.Attachments.IndexOf(a);
            if (index < 0) return;
            it.Attachments.Remove(a);
            // the file stays on disk until the next start, so "Desfazer" brings it back intact
            ShowSnack(T("Anexo removido"), T("Desfazer"), () => it.Attachments.Insert(Math.Min(index, it.Attachments.Count), a));
        }

        private async void PickFiles(Item it)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = T("Anexar arquivos") };
            if (dlg.ShowDialog(this) != true) return;
            await AddFiles(it, dlg.FileNames);
        }

        private async Task AddFiles(Item it, IEnumerable<string> files)
        {
            var list = files.ToList();
            if (list.Any(Directory.Exists)) ShowSnack(T("Pastas não podem ser anexadas, só arquivos"), null, null);
            long big = list.Where(File.Exists).Sum(f => new FileInfo(f).Length);
            if (big > 50L << 20) ShowSnack(T("Copiando arquivos para a nota…"), null, null);
            int n = await Attachments.AddFilesAsync(it, list);
            if (big > 50L << 20 && n > 0) ShowSnack(n == 1 ? T("Arquivo anexado") : TF("{0} arquivos anexados", n), null, null);
            RevealAttachments();
        }

        private void RevealAttachments()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var host = _editMode ? EdAttach : ViewAttach;
                if (host.IsVisible && host.Children.Count > 1) ((FrameworkElement)host.Children[^1]).BringIntoView();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // ---------- clipboard / drop ----------

        private void SidePanel_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool paste = (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control) || (e.Key == Key.Insert && Keyboard.Modifiers == ModifierKeys.Shift);
            if (!paste || _editing == null) return;
            IDataObject data;
            try { data = Clipboard.GetDataObject(); }
            catch { return; } // clipboard busy: let the normal paste try
            var focus = Keyboard.FocusedElement as DependencyObject;
            bool inDescription = focus != null && (focus == EdBody || EdBody.IsAncestorOf(focus));
            bool inText = focus is TextBoxBase;
            if (PasteInto(_editing, data, inDescription, inText)) e.Handled = true;
        }

        /// <summary>
        /// Decides what a paste does inside an open note. Returns true when it became an attachment.
        /// Files and pictures → attachments (text wins when there is also text and the caret is in a text field, e.g.
        /// cells copied from Excel carry a picture too). A lone link → attachment, except in the description where it
        /// stays in the text as a chip. Anything else → the normal paste.
        /// </summary>
        internal bool PasteInto(Item it, IDataObject data, bool inDescription, bool inText)
        {
            if (it == null || data == null || !it.IsNote) return false;
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            {
                _ = AddFiles(it, files);
                return true;
            }
            var text = data.GetDataPresent(DataFormats.UnicodeText) ? data.GetData(DataFormats.UnicodeText) as string : null;
            bool hasText = !string.IsNullOrWhiteSpace(text);
            if (!(hasText && inText) && ClipboardImage(data) is BitmapSource img)
            {
                try
                {
                    Attachments.AddImage(it, img);
                    RevealAttachments();
                }
                catch (Exception ex)
                {
                    // disk full, no permission…: say so instead of the generic error dialog
                    Log.Warn("anexo: falha ao salvar a imagem colada", ex);
                    ShowSnack(T("Não foi possível salvar a imagem"), null, null);
                }
                return true;
            }
            if (hasText && !inDescription && LinkText.IsSingleUrl(text))
            {
                Attachments.AddLink(it, text.Trim());
                RevealAttachments();
                return true;
            }
            return false;
        }

        /// <summary>Picture on the clipboard: the "PNG" format keeps transparency (browsers, Snipping Tool), else the bitmap.</summary>
        private static BitmapSource ClipboardImage(IDataObject data)
        {
            try
            {
                if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream png)
                {
                    var dec = BitmapDecoder.Create(png, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    return dec.Frames[0];
                }
                if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bmp) return bmp;
            }
            catch (Exception ex) { Log.Warn("anexo: imagem da área de transferência ilegível", ex); }
            return null;
        }

        private void SidePanel_DragOver(object sender, DragEventArgs e)
        {
            bool ok = _editing?.IsNote == true && (e.Data.GetDataPresent(DataFormats.FileDrop)
                || (e.Data.GetData(DataFormats.UnicodeText) is string t && LinkText.IsSingleUrl(t)));
            // text dragged inside the description keeps its normal behavior
            if (e.OriginalSource is DependencyObject d && (d == EdBody || EdBody.IsAncestorOf(d)) && !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void SidePanel_Drop(object sender, DragEventArgs e)
        {
            if (_editing?.IsNote != true) return;
            if (e.OriginalSource is DependencyObject d && (d == EdBody || EdBody.IsAncestorOf(d)) && !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) { _ = AddFiles(_editing, files); e.Handled = true; }
            else if (e.Data.GetData(DataFormats.UnicodeText) is string t && LinkText.IsSingleUrl(t)) { Attachments.AddLink(_editing, t.Trim()); RevealAttachments(); e.Handled = true; }
        }

        /// <summary>A click on an empty part of the note puts the keyboard on the drop zone, so Ctrl+V attaches there.</summary>
        private void HookEmptyClicks()
        {
            void Hook(ScrollViewer sv, Func<StackPanel> host)
            {
                sv.AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler((s, e) =>
                {
                    if (e.OriginalSource is DependencyObject d && (FindParent<TextBoxBase>(d) != null || FindParent<ButtonBase>(d) != null
                        || FindParent<ListBoxItem>(d) != null || FindParent<ScrollBar>(d) != null || FindParent<LinkBox>(d) != null)) return;
                    var zone = host().Children.OfType<Border>().LastOrDefault(b => b.Name == "AttachZone");
                    if (zone != null) zone.Focus();
                    else if (_editMode == false) ViewScroll.Focus();
                }), true);
            }
            Hook(EdScroll, () => EdAttach);
            Hook(ViewScroll, () => ViewAttach);
            ViewScroll.Focusable = true;
            ViewScroll.FocusVisualStyle = null;
        }
    }
}
