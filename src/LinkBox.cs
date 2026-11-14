using System;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace HelmetDesk
{
    /// <summary>
    /// Plain-text editor where web addresses become chips (<see cref="LinkChip"/>). The bound <see cref="Text"/> stays
    /// plain text with the full addresses, so data.json, search, widgets and backups are unchanged. Addresses turn into
    /// chips when pasted, when a space/Enter follows one that was typed, and when the text is loaded. Formatting
    /// shortcuts are disabled: it is a text box, not a word processor. Copy/cut give back the addresses as text.
    /// </summary>
    public sealed class LinkBox : RichTextBox
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(LinkBox),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (o, e) => ((LinkBox)o).OnTextSet((string)e.NewValue)));

        public static readonly DependencyProperty IsEmptyProperty = DependencyProperty.Register(
            nameof(IsEmpty), typeof(bool), typeof(LinkBox), new PropertyMetadata(true));

        public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
        public bool IsEmpty { get => (bool)GetValue(IsEmptyProperty); private set => SetValue(IsEmptyProperty, value); }

        private string _last = "";
        private bool _busy;
        private bool _convertPending;

        public LinkBox()
        {
            AcceptsReturn = true;
            AcceptsTab = true;
            IsDocumentEnabled = true; // chips inside the text take clicks
            SpellCheck.IsEnabled = false;
            Background = Brushes.Transparent;
            BorderThickness = new Thickness(0);
            Padding = new Thickness(0, 2, 0, 2);
            SetResourceReference(ForegroundProperty, "Text");
            SetResourceReference(CaretBrushProperty, "Text");
            SetResourceReference(SelectionBrushProperty, "Accent");
            FocusVisualStyle = null;

            var doc = new FlowDocument { PagePadding = new Thickness(0) };
            doc.SetBinding(FlowDocument.FontFamilyProperty, new Binding(nameof(FontFamily)) { Source = this });
            doc.SetBinding(FlowDocument.FontSizeProperty, new Binding(nameof(FontSize)) { Source = this });
            doc.SetBinding(FlowDocument.ForegroundProperty, new Binding(nameof(Foreground)) { Source = this });
            Document = doc;
            Rebuild("");

            TextChanged += (s, e) => OnEdited();
            DataObject.AddPastingHandler(this, OnPasting);
            // copy/cut give the addresses back as plain text (the chips are not text)
            CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (s, e) => { CopySelection(); e.Handled = true; }, (s, e) => { e.CanExecute = !Selection.IsEmpty; e.Handled = true; }));
            CommandBindings.Add(new CommandBinding(ApplicationCommands.Cut, (s, e) => { if (CopySelection()) Selection.Text = ""; e.Handled = true; }, (s, e) => { e.CanExecute = !Selection.IsEmpty && !IsReadOnly; e.Handled = true; }));
            foreach (var cmd in new RoutedUICommand[]
                     {
                         EditingCommands.ToggleBold, EditingCommands.ToggleItalic, EditingCommands.ToggleUnderline,
                         EditingCommands.IncreaseFontSize, EditingCommands.DecreaseFontSize, EditingCommands.ToggleBullets,
                         EditingCommands.ToggleNumbering, EditingCommands.ToggleSubscript, EditingCommands.ToggleSuperscript,
                         EditingCommands.AlignCenter, EditingCommands.AlignJustify, EditingCommands.AlignRight, EditingCommands.IncreaseIndentation,
                         EditingCommands.DecreaseIndentation,
                     })
                CommandBindings.Add(new CommandBinding(cmd, (s, e) => e.Handled = true, (s, e) => { e.CanExecute = false; e.Handled = true; }));
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Space || e.Key == Key.Enter || e.Key == Key.Tab) _convertPending = true;
            base.OnPreviewKeyDown(e);
        }

        // ================= text ⇄ document =================

        private void OnTextSet(string text)
        {
            text ??= "";
            if (_busy || text == _last) return;
            Rebuild(text);
        }

        private void Rebuild(string text)
        {
            _busy = true;
            try
            {
                _last = text;
                Document.Blocks.Clear();
                foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
                {
                    var p = new Paragraph { Margin = new Thickness(0) };
                    foreach (var (piece, isUrl) in LinkText.Split(line))
                        p.Inlines.Add(isUrl ? Chip(piece) : new Run(piece));
                    Document.Blocks.Add(p);
                }
                IsEmpty = text.Length == 0;
                CaretPosition = Document.ContentEnd;
            }
            finally { _busy = false; }
        }

        private InlineUIContainer Chip(string url) =>
            new(LinkChip.Create(url, FontSize)) { BaselineAlignment = BaselineAlignment.Center };

        private void OnEdited()
        {
            if (_busy) return;
            if (_convertPending)
            {
                _convertPending = false;
                ConvertUrls();
            }
            var text = Serialize(Document.ContentStart, Document.ContentEnd);
            IsEmpty = text.Length == 0;
            if (text == _last) return;
            _last = text;
            _busy = true;
            try { SetCurrentValue(TextProperty, text); }
            finally { _busy = false; }
        }

        /// <summary>Turns every address still written as text into a chip, keeping the caret where it was.</summary>
        internal void ConvertUrls()
        {
            _busy = true;
            BeginChange();
            try
            {
                var caret = CaretPosition.GetPositionAtOffset(0, LogicalDirection.Forward);
                for (int guard = 0; guard < 200; guard++)
                {
                    var hit = FindUrlRun();
                    if (hit == null) break;
                    var (run, index, url) = hit.Value;
                    var start = run.ContentStart.GetPositionAtOffset(index);
                    var range = new TextRange(start, start.GetPositionAtOffset(url.Length));
                    range.Text = "";
                    new InlineUIContainer(LinkChip.Create(url, FontSize), range.Start) { BaselineAlignment = BaselineAlignment.Center };
                }
                if (caret != null) CaretPosition = caret;
            }
            finally
            {
                EndChange();
                _busy = false;
            }
        }

        private (Run run, int index, string url)? FindUrlRun()
        {
            foreach (var p in Document.Blocks.OfType<Paragraph>())
                foreach (var run in Runs(p.Inlines))
                {
                    int pos = 0;
                    foreach (var (piece, isUrl) in LinkText.Split(run.Text))
                    {
                        if (isUrl) return (run, pos, piece);
                        pos += piece.Length;
                    }
                }
            return null;
        }

        private static System.Collections.Generic.IEnumerable<Run> Runs(InlineCollection inlines)
        {
            foreach (var i in inlines.ToList())
            {
                if (i is Run r) yield return r;
                else if (i is Span s) foreach (var x in Runs(s.Inlines)) yield return x;
            }
        }

        /// <summary>Plain text between two positions: chips become their address, paragraphs and line breaks "\n".</summary>
        internal static string Serialize(TextPointer from, TextPointer to)
        {
            var sb = new StringBuilder();
            var p = from;
            while (p != null && p.CompareTo(to) < 0)
            {
                switch (p.GetPointerContext(LogicalDirection.Forward))
                {
                    case TextPointerContext.Text:
                        var t = p.GetTextInRun(LogicalDirection.Forward);
                        var room = p.GetOffsetToPosition(to);
                        sb.Append(room < t.Length ? t.Substring(0, room) : t);
                        p = p.GetPositionAtOffset(Math.Min(t.Length, room));
                        continue;
                    case TextPointerContext.EmbeddedElement:
                        if (p.GetAdjacentElement(LogicalDirection.Forward) is Border { Tag: string url }) sb.Append(url);
                        break;
                    case TextPointerContext.ElementStart:
                        var el = p.GetAdjacentElement(LogicalDirection.Forward);
                        if (el is Paragraph { PreviousBlock: not null }) sb.Append('\n'); // a new line (the first has nothing before it)
                        else if (el is LineBreak) sb.Append('\n');
                        else if (el is InlineUIContainer { Child: Border { Tag: string u } })
                        {
                            sb.Append(u);
                            p = ((InlineUIContainer)el).ElementEnd;
                            continue;
                        }
                        break;
                }
                p = p.GetNextContextPosition(LogicalDirection.Forward);
            }
            return sb.ToString();
        }

        // ================= clipboard =================

        private void OnPasting(object sender, DataObjectPastingEventArgs e)
        {
            // plain text only (no fonts/colors from web pages); addresses become chips right after
            var text = e.DataObject.GetData(DataFormats.UnicodeText) as string ?? e.DataObject.GetData(DataFormats.Text) as string;
            if (text == null) { e.CancelCommand(); return; }
            var d = new DataObject();
            d.SetData(DataFormats.UnicodeText, text.Replace("\r\n", "\n"));
            e.DataObject = d;
            _convertPending = true;
        }

        private bool CopySelection()
        {
            if (Selection.IsEmpty) return false;
            try { Clipboard.SetText(Serialize(Selection.Start, Selection.End).Replace("\n", Environment.NewLine)); return true; }
            catch { return false; } // clipboard busy
        }
    }
}
