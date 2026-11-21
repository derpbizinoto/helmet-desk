using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using HelmetDesk.Core.Content;

namespace HelmetDesk
{
    /// <summary>
    /// Painel experimental "Conteúdo" (esteira de produção): "Novo:" buttons, channel cards, the five stage spheres,
    /// the summary line and the content cards, plus a side panel for editing a content item and managing channels.
    /// The state and rules live in <see cref="ContentPanelViewModel"/> (core); this class draws them and answers
    /// <see cref="IContentPanelHost"/>. Data is read from conteudo.json the first time the panel opens (never while the panel is turned off in Configurações)
    /// and written on every change.
    /// </summary>
    public partial class ContentView : UserControl, IContentPanelHost
    {
        private ContentData _data;
        /// <summary>Internal for the self-test.</summary>
        internal readonly ContentPanelViewModel Vm;
        private bool _panelWanted;

        /// <summary>One color per stage (Ideia → Publicado), used by the spheres and the progress bars.</summary>
        private static readonly string[] StageColors = { "#F2C36B", "#FF8FB1", "#C8A8F5", "#78B7FF", "#64D6A2" };

        public ContentView()
        {
            Vm = new ContentPanelViewModel(() => _data, this);
            InitializeComponent();
            Belt.SizeChanged += (s, e) => PlaceRail();
        }

        private MainWindow Host => Window.GetWindow(this) as MainWindow;

        // ================= public API (MainWindow) =================

        /// <summary>The panel was opened: loads conteudo.json the first time, then redraws.</summary>
        public void Activate()
        {
            if (_data == null)
            {
                _data = ContentStore.Load(AppPaths.ContentFile, DateTime.Now, m => Log.Warn(m));
                Log.Info($"Conteúdo (experimental): {_data.Channels.Count} canal(is), {_data.Items.Count} conteúdo(s)");
            }
            FillStageCombo();
            Refresh();
        }

        /// <summary>Forgets the data in memory: the next <see cref="Activate"/> reads conteudo.json again (self-test cleanup).</summary>
        internal void Unload()
        {
            ClosePanel();
            _data = null;
        }

        /// <summary>A form is open (false as soon as it starts closing).</summary>
        public bool PanelOpen => _panelWanted && Panel.Visibility == Visibility.Visible;

        public void ClosePanel()
        {
            if (!PanelOpen) return;
            _panelWanted = false;
            Motion.Exit(Panel, dx: Motion.ShiftPanel, done: () => { if (!_panelWanted) Panel.Visibility = Visibility.Collapsed; });
            Vm.FormsClosed();
        }

        public void Refresh()
        {
            if (_data == null) return;
            BuildNewButtons();
            BuildChannelCards();
            BuildSpheres();
            AllStagesBtn.Visibility = Vm.StageFilter == null ? Visibility.Hidden : Visibility.Visible;
            SummaryText.Text = Vm.Summary;
            BuildCards();
            if (ChannelsList.Visibility == Visibility.Visible && PanelOpen) BuildChannelRows();
        }

        // ================= IContentPanelHost =================

        void IContentPanelHost.Commit()
        {
            // a backup is being imported (or was, until the restart): nothing is written over the restored files
            if (Store.Frozen) { Refresh(); return; }
            try
            {
                bool saved;
                using (Store.Gate.Enter()) saved = ContentStore.Save(AppPaths.ContentFile, _data); // same gate as data.json: a backup never reads a half-written state
                if (!saved)
                    Log.Warn("conteudo.json é de uma versão mais nova do app: alterações não foram gravadas para não perder dados");
            }
            catch (Exception ex) { Log.Warn("Falha ao salvar conteudo.json (nova tentativa na próxima alteração)", ex); }
            Refresh();
        }

        void IContentPanelHost.ClosePanel() => ClosePanel();

        void IContentPanelHost.Confirm(string title, string text, string confirm, bool danger, Action onConfirm) =>
            Host?.OpenDialog(title, text, confirm, T("Cancelar"), danger ? MainWindow.DialogKind.Danger : MainWindow.DialogKind.Info, false,
                ok => { if (ok) onConfirm(); });

        void IContentPanelHost.Alert(string title, string text) =>
            Host?.OpenDialog(title, text, T("Entendi"), null, MainWindow.DialogKind.Info, false, null);

        // ================= layout =================

        /// <summary>Cards are at least ~280 px wide: as many columns as fit.</summary>
        private void Scroll_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double w = Scroll.ActualWidth - 56 + 12;
            CardGrid.Columns = Math.Max(1, (int)(w / 292));
        }

        /// <summary>The rail runs from the center of the first sphere to the center of the last.</summary>
        private void PlaceRail()
        {
            double inset = Belt.ActualWidth / (ContentPanelViewModel.StageCount * 2);
            Rail.Margin = new Thickness(inset, 30, inset, 0);
        }

        // ================= header =================

        private void BuildNewButtons()
        {
            // keep the "Novo:" label (first child)
            while (NewButtons.Children.Count > 1) NewButtons.Children.RemoveAt(1);
            foreach (var c in Vm.Channels)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                sp.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = Palette.BrushFrom(c.ColorHex), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(new TextBlock { Text = c.Name });
                var b = new Button
                {
                    Style = Ui.St("SecondaryButton"), Content = sp, Margin = new Thickness(0, 0, 8, 8), ToolTip = TF("Nova ideia em {0}", c.Name)
                };
                AutomationProperties.SetName(b, TF("Nova ideia em {0}", c.Name));
                var id = c.Id;
                b.Click += (s, e) => NewFor(id);
                NewButtons.Children.Add(b);
            }
            if (Vm.Channels.Count == 0)
                NewButtons.Children.Add(new TextBlock { Text = T("crie um canal em “Gerenciar canais”"), Style = Ui.St("MutedItalic"), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) });
        }

        private void NewFor(Guid channelId)
        {
            var item = Vm.NewFor(channelId);
            if (item != null) OpenItemForm(item);
        }

        // ================= channel cards =================

        private void BuildChannelCards()
        {
            ChannelCards.Children.Clear();
            ChannelCards.Children.Add(ChannelCard(null, T("Todos os canais"), null, Vm.Data.Channels.Sum(c => (long)c.Followers), Vm.InProduction(null)));
            foreach (var c in Vm.Channels) ChannelCards.Children.Add(ChannelCard(c.Id, c.Name, c.ColorHex, c.Followers, Vm.InProduction(c.Id)));
        }

        /// <summary>A channel card: a button (click, Tab + Enter/Space) that selects the channel filter.</summary>
        private Button ChannelCard(Guid? id, string name, string color, long followers, int inProduction)
        {
            bool on = Vm.ChannelFilter == id;
            var head = new StackPanel { Orientation = Orientation.Horizontal };
            if (color != null) head.Children.Add(new Ellipse { Width = 10, Height = 10, Fill = Palette.BrushFrom(color), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            head.Children.Add(new TextBlock { Text = name, FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            var body = new StackPanel();
            body.Children.Add(head);
            body.Children.Add(new TextBlock { Text = TN(followers, "{0:N0} seguidor", "{0:N0} seguidores"), Style = Ui.St("Muted"), Margin = new Thickness(0, 6, 0, 0) });
            body.Children.Add(new TextBlock { Text = TF("{0} em produção", inProduction), Style = Ui.St("Muted"), Margin = new Thickness(0, 2, 0, 0) });
            var card = new Border
            {
                Style = Ui.St("Card"), Width = 190, Child = body,
                Padding = on ? new Thickness(14, 12, 14, 12) : new Thickness(15, 13, 15, 13)
            };
            if (on) { card.BorderBrush = Ui.Res("Primary"); card.BorderThickness = new Thickness(2); }
            var b = new Button
            {
                Template = BareTemplate, Content = card, Background = Brushes.Transparent, Cursor = Cursors.Hand,
                Margin = new Thickness(6, 0, 6, 12), HorizontalContentAlignment = HorizontalAlignment.Left
            };
            AutomationProperties.SetName(b, name);
            b.Click += (s, e) => { Vm.SelectChannel(id); Refresh(); };
            return b;
        }

        // ================= the esteira =================

        private void BuildSpheres()
        {
            Spheres.Children.Clear();
            Spheres.Columns = ContentPanelViewModel.StageCount;
            foreach (var s in ContentPanelViewModel.Stages)
                Spheres.Children.Add(Sphere(s, Vm.CountInStage(s), Vm.StageFilter == s, Vm.StageFilter != null && Vm.StageFilter != s));
        }

        private static Color ColorOf(Stage s) => Palette.BrushFrom(StageColors[(int)s]).Color;

        private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
            (byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

        /// <summary>A glossy sphere (Frutiger Aero): radial gradient, a white shine on top, the count in the middle.</summary>
        private UIElement Sphere(Stage s, int count, bool selected, bool dimmed)
        {
            var c = ColorOf(s);
            var fill = new RadialGradientBrush
            {
                GradientOrigin = new Point(0.35, 0.28), Center = new Point(0.42, 0.38), RadiusX = 0.7, RadiusY = 0.7,
                GradientStops =
                {
                    new GradientStop(Mix(c, Colors.White, 0.7), 0),
                    new GradientStop(c, 0.55),
                    new GradientStop(Mix(c, Colors.Black, 0.45), 1)
                }
            };
            var ball = new Grid { Width = 68, Height = 68, HorizontalAlignment = HorizontalAlignment.Center };
            ball.Children.Add(new Ellipse
            {
                Fill = fill,
                Stroke = selected ? Ui.Res("Primary") : new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                StrokeThickness = selected ? 3 : 1
            });
            // shine: a soft white lens over the top half
            ball.Children.Add(new Ellipse
            {
                Width = 44, Height = 24, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0), IsHitTestVisible = false,
                Fill = new LinearGradientBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 90)
            });
            ball.Children.Add(new TextBlock
            {
                Text = count.ToString(L.Culture), FontSize = 20, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(0x24, 0x12, 0x40)),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 0, 0)
            });

            var col = new StackPanel { Opacity = dimmed ? 0.55 : 1 };
            col.Children.Add(ball);
            col.Children.Add(new TextBlock
            {
                Text = ContentPanelViewModel.StageLabel(s), FontSize = 12.5, Margin = new Thickness(4, 8, 4, 0), TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
                FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal, Foreground = selected ? Ui.Res("Text") : Ui.Res("Text2")
            });

            var b = new Button
            {
                Template = BareTemplate, Content = col, Cursor = Cursors.Hand, Background = Brushes.Transparent,
                ToolTip = selected ? T("Clique de novo para ver todas as etapas") : TF("Mostrar só {0}", ContentPanelViewModel.StageLabel(s)),
                RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(selected ? 1.06 : 1, selected ? 1.06 : 1)
            };
            AutomationProperties.SetName(b, ContentPanelViewModel.StageLabel(s) + ": " + count);
            b.Click += (o, e) => { Vm.ToggleStage(s); Refresh(); };
            return b;
        }

        private static ControlTemplate _bare;
        /// <summary>A button that draws only its content (a sphere or a channel card), keeping click and keyboard focus.</summary>
        private static ControlTemplate BareTemplate
        {
            get
            {
                if (_bare != null) return _bare;
                var t = new ControlTemplate(typeof(Button));
                var root = new FrameworkElementFactory(typeof(Border));
                root.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
                root.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
                t.VisualTree = root;
                return _bare = t;
            }
        }

        private void AllStages_Click(object sender, RoutedEventArgs e) { Vm.ClearStage(); Refresh(); }

        // ================= content cards =================

        private void BuildCards()
        {
            CardGrid.Children.Clear();
            var items = Vm.Visible;
            foreach (var i in items) CardGrid.Children.Add(Card(i));
            bool empty = items.Count == 0;
            EmptyBox.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            CardGrid.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            EmptyText.Text = Vm.Data.Items.Count == 0
                ? T("Nenhum conteúdo ainda. Use os botões “Novo:” acima para anotar a primeira ideia.")
                : T("Nenhum conteúdo com esses filtros. Escolha outro canal ou etapa, ou use os botões “Novo:”.");
        }

        private Border Card(ContentItem i)
        {
            var ch = Vm.ChannelOf(i.ChannelId);
            var root = new StackPanel();

            // line 1: channel · date
            var top = new DockPanel { LastChildFill = true };
            var date = new TextBlock
            {
                Text = i.PublishDate is DateTime d ? d.ToString(T("dd/MM/yyyy"), Dates.Culture) : T("Sem data"),
                Style = Ui.St("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0)
            };
            DockPanel.SetDock(date, Dock.Right);
            top.Children.Add(date);
            var chan = new StackPanel { Orientation = Orientation.Horizontal };
            chan.Children.Add(new Ellipse { Width = 9, Height = 9, Fill = Palette.BrushFrom(ch?.ColorHex ?? "#9A93A8"), Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
            chan.Children.Add(new TextBlock { Text = ch?.Name ?? T("Canal removido"), Style = Ui.St("Muted"), TextTrimming = TextTrimming.CharacterEllipsis });
            top.Children.Add(chan);
            root.Children.Add(top);

            // title
            bool untitled = string.IsNullOrWhiteSpace(i.Title);
            root.Children.Add(new TextBlock
            {
                Text = untitled ? T("Sem título") : i.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 10), MaxHeight = 44, TextTrimming = TextTrimming.CharacterEllipsis,
                FontStyle = untitled ? FontStyles.Italic : FontStyles.Normal, Foreground = untitled ? Ui.Res("Text2") : Ui.Res("Text")
            });

            // progress: 5 segments filled up to the current stage
            var bar = new UniformGrid { Rows = 1, Columns = ContentPanelViewModel.StageCount };
            foreach (var s in ContentPanelViewModel.Stages)
                bar.Children.Add(new Border
                {
                    Height = 6, CornerRadius = new CornerRadius(3), Margin = new Thickness(0, 0, s == Stage.Publicado ? 0 : 4, 0),
                    Background = s <= i.Stage ? new SolidColorBrush(ColorOf(s)) : Ui.Res("Line")
                });
            root.Children.Add(bar);
            root.Children.Add(new TextBlock { Text = ContentPanelViewModel.ProgressText(i.Stage), Style = Ui.St("Muted"), Margin = new Thickness(0, 6, 0, 12) });

            // actions
            var actions = new DockPanel { LastChildFill = false };
            var edit = new Button
            {
                Style = Ui.St("IconButton"), Padding = new Thickness(6), ToolTip = T("Editar"),
                Content = new Icon { Data = Ui.Geo("I.Edit"), Width = 14, Height = 14 }
            };
            edit.Click += (s, e) => OpenItemForm(i);
            DockPanel.SetDock(edit, Dock.Right);
            actions.Children.Add(edit);
            if (ContentPanelViewModel.NextStage(i.Stage) != null)
            {
                var next = new Button { Style = Ui.St("PrimaryButton"), Content = ContentPanelViewModel.AdvanceText(i.Stage), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 0) };
                next.Click += (s, e) => { if (Vm.Advance(i)) SyncOpenStage(i); };
                DockPanel.SetDock(next, Dock.Right);
                actions.Children.Add(next);
            }
            if (ContentPanelViewModel.PrevStage(i.Stage) != null)
            {
                var back = new Button { Style = Ui.St("SecondaryButton"), Content = T("Voltar"), Padding = new Thickness(10, 5, 10, 5) };
                back.Click += (s, e) => { if (Vm.Back(i)) SyncOpenStage(i); };
                DockPanel.SetDock(back, Dock.Left);
                actions.Children.Add(back);
            }
            root.Children.Add(actions);

            var card = new Border { Style = Ui.St("Card"), Margin = new Thickness(6, 0, 6, 12), Child = root };
            card.MouseLeftButtonDown += (s, e) => { if (e.ClickCount == 2) { OpenItemForm(i); e.Handled = true; } };
            return card;
        }

        /// <summary>The card's stage moved while its form is open: the form shows the new stage, so Salvar doesn't undo it.</summary>
        private void SyncOpenStage(ContentItem i)
        {
            if (!PanelOpen || Vm.Form.Editing != i) return;
            Vm.Form.Stage = i.Stage;
            ItemStage.SelectedItem = ItemStage.Items.Cast<ComboBoxItem>().FirstOrDefault(x => (Stage)x.Tag == i.Stage);
        }

        // ================= side panel =================

        private void ShowPanel(FrameworkElement form)
        {
            foreach (var f in new FrameworkElement[] { ItemForm, ChannelsList, ChannelForm }) f.Visibility = f == form ? Visibility.Visible : Visibility.Collapsed;
            bool was = PanelOpen;
            _panelWanted = true;
            Panel.Visibility = Visibility.Visible;
            if (!was) Motion.Enter(Panel, dx: Motion.ShiftPanel);
        }

        private void ClosePanel_Click(object sender, RoutedEventArgs e) => ClosePanel();

        private void FillStageCombo()
        {
            if (ItemStage.Items.Count > 0) return;
            foreach (var s in ContentPanelViewModel.Stages) ItemStage.Items.Add(new ComboBoxItem { Content = ContentPanelViewModel.StageLabel(s), Tag = s });
        }

        internal void OpenItemForm(ContentItem item)
        {
            if (Vm.Form.Editing != item)
            {
                Vm.Form.Close(); // another untitled "Novo" is dropped
                Vm.ChannelsVm.Close();
                Vm.Form.Open(item);
            }
            var f = Vm.Form;
            ItemFormTitle.Text = f.IsNew ? T("Novo conteúdo") : T("Editar conteúdo");
            ItemTitle.Text = f.Title;
            ItemChannel.Items.Clear();
            foreach (var c in Vm.Channels) ItemChannel.Items.Add(new ComboBoxItem { Content = c.Name, Tag = c.Id });
            ItemChannel.SelectedItem = ItemChannel.Items.Cast<ComboBoxItem>().FirstOrDefault(x => (Guid)x.Tag == f.ChannelId);
            ItemStage.SelectedItem = ItemStage.Items.Cast<ComboBoxItem>().FirstOrDefault(x => (Stage)x.Tag == f.Stage);
            ItemDate.SelectedDate = f.PublishDate;
            ItemNotes.Text = f.Notes;
            ItemError.Visibility = Visibility.Collapsed;
            ItemDeleteBtn.Visibility = f.IsNew ? Visibility.Collapsed : Visibility.Visible;
            ShowPanel(ItemForm);
            Dispatcher.BeginInvoke(new Action(() => { ItemTitle.Focus(); ItemTitle.SelectAll(); }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void ItemForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { ItemSave_Click(null, null); e.Handled = true; }
        }

        internal void ItemSave_Click(object sender, RoutedEventArgs e)
        {
            var f = Vm.Form;
            f.Title = ItemTitle.Text;
            f.ChannelId = (ItemChannel.SelectedItem as ComboBoxItem)?.Tag is Guid g ? g : Guid.Empty;
            f.Stage = (ItemStage.SelectedItem as ComboBoxItem)?.Tag is Stage s ? s : Stage.Ideia;
            f.PublishDate = ItemDate.SelectedDate;
            f.Notes = ItemNotes.Text;
            if (f.Save()) return;
            ItemError.Text = f.Error;
            ItemError.Visibility = Visibility.Visible;
            ItemTitle.Focus();
        }

        internal void ItemDelete_Click(object sender, RoutedEventArgs e) => Vm.Form.RequestDelete();

        // ----- Gerenciar canais -----

        private void ManageChannels_Click(object sender, RoutedEventArgs e) => OpenChannelsList();

        private void OpenChannelsList()
        {
            Vm.Form.Close(); // an untitled "Novo" is dropped
            Vm.ChannelsVm.Close();
            BuildChannelRows();
            ShowPanel(ChannelsList);
        }

        private void BuildChannelRows()
        {
            ChannelRows.Children.Clear();
            var list = Vm.Channels;
            ChannelsEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            for (int k = 0; k < list.Count; k++)
            {
                var c = list[k];
                var grid = new DockPanel();
                var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                buttons.Children.Add(SmallButton("I.ArrowUp", T("Subir"), k > 0, () => Vm.ChannelsVm.Move(c, -1)));
                buttons.Children.Add(SmallButton("I.ArrowDown", T("Descer"), k < list.Count - 1, () => Vm.ChannelsVm.Move(c, +1)));
                buttons.Children.Add(SmallButton("I.Edit", T("Editar"), true, () => OpenChannelForm(c)));
                buttons.Children.Add(SmallButton("I.Trash", Vm.ChannelsVm.CanDelete(c) ? T("Excluir") : T("Tem conteúdos: não pode ser excluído"), true,
                    () => Vm.ChannelsVm.RequestDelete(c), danger: true));
                DockPanel.SetDock(buttons, Dock.Right);
                grid.Children.Add(buttons);

                var dot = new Ellipse { Width = 12, Height = 12, Fill = Palette.BrushFrom(c.ColorHex), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(dot, Dock.Left);
                grid.Children.Add(dot);
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = c.Name, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis });
                text.Children.Add(new TextBlock
                {
                    Text = TN(c.Followers, "{0:N0} seguidor", "{0:N0} seguidores") + " · " + TN(Vm.ChannelsVm.ContentCount(c.Id), "{0} conteúdo", "{0} conteúdos"),
                    Style = Ui.St("Muted"), Margin = new Thickness(0, 2, 0, 0)
                });
                grid.Children.Add(text);

                ChannelRows.Children.Add(new Border
                {
                    Background = Ui.Res("RowFill"), BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(12, 8, 6, 8), Margin = new Thickness(0, 0, 0, 6), Child = grid
                });
            }
        }

        private static Button SmallButton(string geo, string tip, bool enabled, Action click, bool danger = false)
        {
            var b = new Button
            {
                Style = Ui.St(danger ? "DangerGhost" : "IconButton"), Padding = new Thickness(6), ToolTip = tip, IsEnabled = enabled,
                Content = new Icon { Data = Ui.Geo(geo), Width = 14, Height = 14 }, Opacity = enabled ? 1 : 0.35
            };
            ToolTipService.SetShowOnDisabled(b, true);
            b.Click += (s, e) => click();
            return b;
        }

        private void NewChannel_Click(object sender, RoutedEventArgs e) => OpenChannelForm(null);

        private void OpenChannelForm(Channel c)
        {
            var f = Vm.ChannelsVm;
            if (c == null) f.New(); else f.Edit(c);
            ChannelFormTitle.Text = c == null ? T("Novo canal") : T("Editar canal");
            ChannelName.Text = f.Name;
            ChannelFollowers.Text = f.FollowersText;
            ChannelError.Visibility = Visibility.Collapsed;
            BuildColorSwatches();
            ShowPanel(ChannelForm);
            Dispatcher.BeginInvoke(new Action(() => { ChannelName.Focus(); ChannelName.SelectAll(); }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void BuildColorSwatches()
        {
            ChannelColors.Children.Clear();
            foreach (var hex in ContentPanelViewModel.Colors)
            {
                bool on = string.Equals(hex, Vm.ChannelsVm.ColorHex, StringComparison.OrdinalIgnoreCase);
                var b = new Button
                {
                    Width = 26, Height = 26, Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand, ToolTip = hex,
                    Template = SwatchTemplate, Background = Palette.BrushFrom(hex), BorderBrush = on ? Ui.Res("Text") : Brushes.Transparent
                };
                b.Click += (s, e) => { Vm.ChannelsVm.ColorHex = hex; BuildColorSwatches(); };
                ChannelColors.Children.Add(b);
            }
        }

        private static ControlTemplate _swatch;
        private static ControlTemplate SwatchTemplate
        {
            get
            {
                if (_swatch != null) return _swatch;
                var t = new ControlTemplate(typeof(Button));
                var outer = new FrameworkElementFactory(typeof(Border));
                outer.SetValue(Border.CornerRadiusProperty, new CornerRadius(13));
                outer.SetValue(Border.BorderThicknessProperty, new Thickness(2));
                outer.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
                outer.SetValue(Border.PaddingProperty, new Thickness(2));
                var inner = new FrameworkElementFactory(typeof(Border));
                inner.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
                inner.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
                outer.AppendChild(inner);
                t.VisualTree = outer;
                return _swatch = t;
            }
        }

        private void ChannelForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { ChannelSave_Click(null, null); e.Handled = true; }
        }

        private void ChannelSave_Click(object sender, RoutedEventArgs e)
        {
            var f = Vm.ChannelsVm;
            f.Name = ChannelName.Text;
            f.FollowersText = ChannelFollowers.Text;
            if (f.Save()) { OpenChannelsList(); return; }
            ChannelError.Text = f.Error;
            ChannelError.Visibility = Visibility.Visible;
        }

        private void ChannelBack_Click(object sender, RoutedEventArgs e) => OpenChannelsList();
    }
}
