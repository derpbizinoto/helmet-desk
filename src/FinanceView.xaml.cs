using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HelmetDesk.Core.Finance;

namespace HelmetDesk
{
    /// <summary>
    /// Finanças panel: month summary, transactions (by day, with filters), accounts, recurrences, categories and the
    /// expenses-by-category chart. The state and rules of the panel and its forms are in the view models
    /// (<see cref="FinancePanelViewModel"/> and its forms, in core); the money rules are in <see cref="FinanceService"/>.
    /// This class draws the rows, moves values between the controls and the view models, and answers
    /// <see cref="IFinanceHost"/> (saving, the side form, dialogs, the undo bar).
    /// </summary>
    public partial class FinanceView : UserControl, IFinanceHost
    {
        private static FinanceData D => Store.Data.Finance;

        private readonly FinancePanelViewModel Vm;
        private TransactionFormViewModel TxVm => Vm.Transaction;

        private bool _filling;
        private bool _descBusy;
        /// <summary>Values are being copied from a view model into the controls: their change events are ignored.</summary>
        private bool _sync;

        /// <summary>Raised after any change, so the window can update the sidebar counter and the Hoje card.</summary>
        public event Action Changed;

        private static IReadOnlyList<string> Colors => FinancePanelViewModel.Colors;
        private static IReadOnlyList<string> CategoryIcons => FinancePanelViewModel.CategoryIcons;

        public FinanceView()
        {
            Vm = new FinancePanelViewModel(() => Store.Data.Finance, this);
            InitializeComponent();
            FillStaticCombos();
            DependencyPropertyDescriptor.FromProperty(DateField.SelectedDateProperty, typeof(DateField))
                .AddValueChanged(TxDate, (s, e) => OnTxDateChanged());
        }

        private MainWindow Host => Window.GetWindow(this) as MainWindow;

        // ================= public API (MainWindow) =================

        /// <summary>The panel was opened: generate this month's recurring bills and redraw.</summary>
        public void Activate()
        {
            Vm.GenerateMonth();
            Refresh();
        }

        /// <summary>A form is open (false as soon as it starts closing).</summary>
        public bool PanelOpen => _panelWanted && Panel.Visibility == Visibility.Visible;

        /// <summary>"+" / Ctrl+N: new transaction with the value focused.</summary>
        public void NewTransaction() => OpenTxForm(null);

        public void ClosePanel()
        {
            SuggestPopup.IsOpen = false;
            if (!PanelOpen) return;
            _panelWanted = false; // before Exit: with motion off its callback runs right away
            Motion.Exit(Panel, dx: Motion.ShiftPanel, done: () => { if (!_panelWanted) Panel.Visibility = Visibility.Collapsed; });
            Vm.FormsClosed();
        }

        private bool _panelWanted;

        /// <summary>Configurações › Moeda changed: redraw everything with the new symbol (amounts are not converted).</summary>
        public void CurrencyChanged()
        {
            if (TxAmount.Text.Length > 0 && Money.TryParse(TxAmount.Text, out var typed)) TxAmount.Text = Money.Plain(typed);
            Refresh();
        }

        /// <summary>The symbol drawn inside the amount box; the text starts after it ("US$" is wider than "$").</summary>
        private void UpdateAmountSymbol()
        {
            TxAmountSymbol.Text = Money.Symbol;
            TxAmountSymbol.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var p = TxAmount.Padding;
            TxAmount.Padding = new Thickness(Math.Max(44, 13 + TxAmountSymbol.DesiredSize.Width + 10), p.Top, p.Right, p.Bottom);
            TxAmount.Tag = AccInitial.Tag = CheckReal.Tag = Money.Plain(0); // placeholder "0,00" / "0.00"
        }

        public void Refresh()
        {
            UpdateAmountSymbol();
            ThisMonthBtn.Visibility = Vm.IsThisMonth ? Visibility.Collapsed : Visibility.Visible;
            MonthTitle.Text = Dates.MonthTitle(Vm.Month);
            var s = Vm.Summary;
            SumIncome.Text = Money.Format(s.Income);
            SumExpense.Text = Money.Format(s.Expense);
            SumBalance.Text = Money.Format(s.CurrentBalance);
            SumBalance.Foreground = s.CurrentBalance < 0 ? Ui.Res("Danger") : Ui.Res("Text");
            SumProjected.Text = Money.Format(s.ProjectedBalance);
            SumProjected.Foreground = s.ProjectedBalance < 0 ? Ui.Res("Danger") : Ui.Res("Text");
            SumProjectedLabel.Text = TF("Previsto em {0}", Vm.MonthEnd.ToString(T("dd/MM"), Dates.Culture));

            var tab = Vm.Tab;
            TxTab.Visibility = tab == "tx" ? Visibility.Visible : Visibility.Collapsed;
            AccTab.Visibility = tab == "acc" ? Visibility.Visible : Visibility.Collapsed;
            RecTab.Visibility = tab == "rec" ? Visibility.Visible : Visibility.Collapsed;
            CatTab.Visibility = tab == "cat" ? Visibility.Visible : Visibility.Collapsed;
            StatsTab.Visibility = tab == "stats" ? Visibility.Visible : Visibility.Collapsed;
            SumGrid.Visibility = tab == "stats" ? Visibility.Collapsed : Visibility.Visible; // the tab has its own period summary
            switch (tab)
            {
                case "tx": FillFilters(); BuildTransactions(); BuildChart(); break;
                case "acc": BuildAccounts(); break;
                case "rec": BuildRecurrences(); break;
                case "cat": BuildCategories(); break;
                case "stats": BuildStats(); break;
            }
        }

        /// <summary>Saves, tells the window and redraws.</summary>
        private void Commit()
        {
            Store.MarkDirty();
            Changed?.Invoke();
            Refresh();
        }

        // ================= IFinanceHost (what the view models ask of this screen) =================

        void IFinanceHost.Commit() => Commit();

        void IFinanceHost.Changed()
        {
            Store.MarkDirty();
            Changed?.Invoke();
        }

        void IFinanceHost.ReplaceData(FinanceData data) => Store.Data.Finance = data;

        void IFinanceHost.Confirm(string title, string text, string confirm, bool danger, string checkText, Action<bool> onConfirm) =>
            Host?.OpenDialog(title, text, confirm, T("Cancelar"), danger ? MainWindow.DialogKind.Danger : MainWindow.DialogKind.Info, false,
                ok => { if (ok) onConfirm(Host?.DialogChecked == true); }, checkText);

        void IFinanceHost.Snack(string text, string action, Action onAction) => Host?.ShowSnack(text, action, onAction);

        // ================= layout =================

        /// <summary>Narrow (form open or small window): 2×2 summary, tabs on their own line, chart under the list.</summary>
        private void Scroll_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double w = Scroll.ActualWidth - 56;
            bool narrow = w < 860;
            SumGrid.Columns = w < 640 ? 2 : 4;
            DockPanel.SetDock(TabsBox, w < 820 ? Dock.Bottom : Dock.Right);
            TabsBox.Margin = w < 820 ? new Thickness(0, 12, 0, 0) : new Thickness(0);
            Grid.SetColumnSpan(TxCol, narrow ? 3 : 1);
            Grid.SetRow(ChartCard, narrow ? 1 : 0);
            Grid.SetColumn(ChartCard, narrow ? 0 : 2);
            Grid.SetColumnSpan(ChartCard, narrow ? 3 : 1);
            ChartCard.Margin = narrow ? new Thickness(0, 16, 0, 0) : new Thickness(0);
            StatsLayout(w);
        }

        // ================= month / tabs =================

        internal void PrevMonth_Click(object sender, RoutedEventArgs e) { Vm.PrevMonth(); Refresh(); }
        internal void NextMonth_Click(object sender, RoutedEventArgs e) { Vm.NextMonth(); Refresh(); }
        internal void ThisMonth_Click(object sender, RoutedEventArgs e) { Vm.ThisMonth(); Refresh(); }

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized) return;
            Vm.Tab = (string)((FrameworkElement)sender).Tag;
            if (TxTab == null) return;
            Refresh();
        }

        /// <summary>For the self-test and snapshots.</summary>
        internal void ShowTab(string tab)
        {
            (tab switch { "acc" => TabAcc, "rec" => TabRec, "cat" => TabCat, "stats" => TabStats, _ => TabTx }).IsChecked = true;
            Vm.Tab = tab;
            Refresh();
        }

        internal DateTime Month => Vm.Month;

        // ================= helpers =================

        private Category CategoryOf(string id) => Vm.CategoryOf(id);
        private Account AccountOf(string id) => Vm.AccountOf(id);

        private static Geometry CatGeo(string icon) =>
            Application.Current.TryFindResource("F." + (string.IsNullOrEmpty(icon) ? "dots" : icon)) as Geometry ?? Ui.Geo("F.dots");

        private static Geometry AccGeo(AccountType t) => Ui.Geo("A." + t);

        public static string TypeName(AccountType t) => t switch
        {
            AccountType.Checking => T("Conta corrente"),
            AccountType.Wallet => T("Carteira"),
            AccountType.CreditCard => T("Cartão de crédito"),
            AccountType.Savings => T("Poupança"),
            _ => ""
        };

        private static Brush Tint(string hex, byte alpha = 0x33)
        {
            var c = Palette.BrushFrom(hex).Color;
            var b = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            b.Freeze();
            return b;
        }

        private static Border IconBadge(Geometry g, string hex, double size = 34)
        {
            return new Border
            {
                Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = Tint(hex),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new Icon { Data = g, Foreground = Palette.BrushFrom(hex), Width = size * 0.47, Height = size * 0.47 }
            };
        }

        private static Border Pill(string text, Brush fg = null, Geometry icon = null, string dot = null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            if (dot != null) sp.Children.Add(new System.Windows.Shapes.Ellipse { Width = 7, Height = 7, Fill = Palette.BrushFrom(dot), Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            if (icon != null) sp.Children.Add(new Icon { Data = icon, Width = 12, Height = 12, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(new TextBlock { Text = text });
            var b = new Border { Style = Ui.St("Pill"), Margin = new Thickness(0, 4, 6, 0), Child = sp };
            if (fg != null) TextElement.SetForeground(b, fg);
            return b;
        }

        private static Button IconBtn(string geo, string tip, RoutedEventHandler click, bool danger = false)
        {
            var b = new Button
            {
                Style = Ui.St(danger ? "DangerGhost" : "IconButton"), Padding = new Thickness(6), ToolTip = tip,
                Content = new Icon { Data = Ui.Geo(geo), Width = 14, Height = 14 }, VerticalAlignment = VerticalAlignment.Center
            };
            b.Click += click;
            return b;
        }

        /// <summary>Row container like the task rows: border lights up and the actions appear on hover/focus.</summary>
        private static Border Row(UIElement content, FrameworkElement actions, Brush border = null)
        {
            var row = new Border
            {
                Background = Ui.Res("RowFill"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 9, 8, 9), Margin = new Thickness(0, 0, 0, 6), Child = content
            };
            var normal = border ?? Ui.Res("Line");
            Motion.SetBorderBrush(row, normal);
            if (actions != null) actions.Opacity = 0;
            void Update()
            {
                bool on = row.IsMouseOver || row.IsKeyboardFocusWithin;
                if (actions != null) actions.Opacity = on ? 1 : 0;
                Motion.SetBorderBrush(row, row.IsMouseOver ? Ui.Res("HoverLine") : normal);
            }
            row.MouseEnter += (s, e) => Update();
            row.MouseLeave += (s, e) => Update();
            row.IsKeyboardFocusWithinChanged += (s, e) => Update();
            return row;
        }

        private static void Fill(ComboBox cb, IEnumerable<(string tag, string text)> items, string select)
        {
            cb.Items.Clear();
            foreach (var (tag, text) in items) cb.Items.Add(new ComboBoxItem { Content = text, Tag = tag });
            cb.SelectedItem = cb.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == select) ?? (cb.Items.Count > 0 ? cb.Items[0] : null);
        }

        private static string Sel(ComboBox cb) => (cb.SelectedItem as ComboBoxItem)?.Tag as string;

        /// <summary>Selects the item with this tag (none when it is not there), without refilling the combo.</summary>
        private static void SelectTag(ComboBox cb, string tag) =>
            cb.SelectedItem = cb.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag);

        private static void SetText(TextBox tb, string text)
        {
            if (tb.Text != text) tb.Text = text;
        }

        // ================= transactions list =================

        private void FillStaticCombos()
        {
            _filling = true;
            Fill(FilterKind, FinancePanelViewModel.KindFilterOptions(), "");
            Fill(FilterStatus, FinancePanelViewModel.StatusFilterOptions(), "");
            Fill(AccType, Enum.GetValues<AccountType>().Select(t => (((int)t).ToString(), TypeName(t))), "0");
            _filling = false;
        }

        /// <summary>Copies the four filter combos into the view model (they can also be set from code, as the snapshots do).</summary>
        private void SyncFilters()
        {
            Vm.FilterAccount = Sel(FilterAccount);
            Vm.FilterCategory = Sel(FilterCategory);
            Vm.FilterKind = Sel(FilterKind);
            Vm.FilterStatus = Sel(FilterStatus);
        }

        private void FillFilters()
        {
            _filling = true;
            SyncFilters();
            Fill(FilterAccount, Vm.AccountFilterOptions(), Vm.FilterAccount);
            Fill(FilterCategory, Vm.CategoryFilterOptions(), Vm.FilterCategory);
            _filling = false;
            SyncFilters();
            ClearFiltersBtn.Visibility = Vm.HasFilters ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Filter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_filling || !IsInitialized || TxList == null) return;
            SyncFilters();
            ClearFiltersBtn.Visibility = Vm.HasFilters ? Visibility.Visible : Visibility.Collapsed;
            BuildTransactions();
        }

        private void ClearFilters_Click(object sender, RoutedEventArgs e)
        {
            _filling = true;
            foreach (var cb in new[] { FilterAccount, FilterCategory, FilterKind, FilterStatus }) cb.SelectedIndex = 0;
            _filling = false;
            Vm.ClearFilters();
            ClearFiltersBtn.Visibility = Visibility.Collapsed;
            BuildTransactions();
        }

        internal List<FinanceTransaction> VisibleTransactions()
        {
            SyncFilters();
            return Vm.VisibleTransactions();
        }

        private void BuildTransactions()
        {
            TxList.Children.Clear();
            SyncFilters();
            var days = Vm.DayGroups();
            foreach (var day in days)
            {
                var net = day.Net;
                var head = new DockPanel { Margin = new Thickness(0, TxList.Children.Count == 0 ? 0 : 12, 0, 8) };
                var total = new TextBlock { Text = net == 0 ? "" : Money.Signed(net), Style = Ui.St("Muted"), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(total, Dock.Right);
                head.Children.Add(total);
                head.Children.Add(new TextBlock { Text = DayTitle(day.Day), FontSize = 14, FontWeight = FontWeights.SemiBold });
                TxList.Children.Add(head);
                foreach (var t in day.Items) TxList.Children.Add(TxRow(t));
            }
            bool filtered = ClearFiltersBtn.Visibility == Visibility.Visible;
            TxEmpty.Visibility = days.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            TxEmptyText.Text = filtered ? T("Nenhum lançamento com esses filtros.") : T("Nenhum lançamento neste mês.\nUse “Novo lançamento” ou Ctrl+N.");
        }

        private static string DayTitle(DateTime d)
        {
            var today = DateTime.Today;
            var s = Dates.Cap(d.ToString(T("ddd, d 'de' MMMM"), Dates.Culture).Replace(".", ""));
            if (d == today) return T("Hoje · ") + s;
            if (d == today.AddDays(-1)) return T("Ontem · ") + s;
            if (d == today.AddDays(1)) return T("Amanhã · ") + s;
            return s;
        }

        private Border TxRow(FinanceTransaction t)
        {
            var today = DateTime.Today;
            bool overdue = FinanceService.IsOverdue(t, today);
            var cat = CategoryOf(t.CategoryId);
            var grid = new Grid();
            foreach (var w in new[] { GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });

            // status: click toggles paid / pending
            var status = new Button
            {
                Style = Ui.St("IconButton"), Padding = new Thickness(4), Margin = new Thickness(-4, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
                ToolTip = t.Paid ? T("Pago · clique para marcar como pendente") : overdue ? T("Vencido · clique para marcar como pago") : T("Pendente · clique para marcar como pago"),
                Content = new Icon
                {
                    Data = Ui.Geo(t.Paid ? "I.Check2" : "I.Clock"), Width = 18, Height = 18,
                    Foreground = t.Paid ? Ui.Res("Success") : overdue ? Ui.Res("Danger") : Ui.Res("Warning")
                }
            };
            AutomationPropertiesName(status, t.Paid ? T("Marcar como pendente") : T("Marcar como pago"));
            status.Click += (s, e) => TogglePaid(t);
            grid.Children.Add(status);

            var bar = new Border
            {
                Width = 3, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 1, 12, 1),
                Background = t.Kind == TxKind.Transfer ? Ui.Res("Info") : Palette.BrushFrom(cat?.Color ?? "#9A93A8")
            };
            Grid.SetColumn(bar, 1);
            grid.Children.Add(bar);

            var main = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var title = new TextBlock { Text = string.IsNullOrWhiteSpace(t.Description) ? T("(sem descrição)") : t.Description, FontSize = 14, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis };
            if (!t.Paid) title.Foreground = Ui.Res("Text2");
            main.Children.Add(title);
            var pills = new WrapPanel();
            if (t.Kind == TxKind.Transfer)
                pills.Children.Add(Pill($"{AccountOf(t.AccountId)?.Name ?? "?"} → {AccountOf(t.ToAccountId)?.Name ?? "?"}", icon: Ui.Geo("I.Swap")));
            else
            {
                if (t.IsAdjustment) pills.Children.Add(Pill(T("Ajuste"), icon: Ui.Geo("I.Scale")));
                else pills.Children.Add(Pill(cat?.Name ?? T("Sem categoria"), dot: cat?.Color ?? "#9A93A8"));
                pills.Children.Add(Pill(AccountOf(t.AccountId)?.Name ?? T("Sem conta"), icon: AccGeo(AccountOf(t.AccountId)?.Type ?? AccountType.Wallet)));
            }
            if (t.IsInstallment) pills.Children.Add(Pill(TF("Parcela {0}/{1}", t.InstallmentNumber, t.InstallmentCount), icon: Ui.Geo("I.List")));
            if (t.IsRecurring) pills.Children.Add(Pill(T("Mensal"), icon: Ui.Geo("I.Repeat")));
            if (overdue) pills.Children.Add(Pill(T("Vencido"), Ui.Res("Danger"), Ui.Geo("I.Clock")));
            else if (!t.Paid) pills.Children.Add(Pill(T("Pendente"), Ui.Res("Warning")));
            if (!string.IsNullOrWhiteSpace(t.Note)) pills.Children.Add(Pill(T("Obs."), icon: Ui.Geo("I.Note")));
            main.Children.Add(pills);
            Grid.SetColumn(main, 2);
            grid.Children.Add(main);

            var amount = new TextBlock
            {
                FontSize = 14.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
                Text = t.Kind switch { TxKind.Income => "+" + Money.Format(t.Amount), TxKind.Expense => "-" + Money.Format(t.Amount), _ => Money.Format(t.Amount) },
                Foreground = t.Kind == TxKind.Income ? Ui.Res("Success") : t.Kind == TxKind.Transfer ? Ui.Res("Text2") : Ui.Res("Text"),
            };
            if (!t.Paid) amount.Opacity = 0.7;
            Grid.SetColumn(amount, 3);
            grid.Children.Add(amount);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            actions.Children.Add(IconBtn("I.Copy", T("Duplicar"), (s, e) => DuplicateTx(t)));
            actions.Children.Add(IconBtn("I.Trash", T("Excluir"), (s, e) => RequestDeleteTx(t), danger: true));
            actions.Children.Add(IconBtn("I.Edit", T("Editar"), (s, e) => OpenTxForm(t)));
            Grid.SetColumn(actions, 4);
            grid.Children.Add(actions);

            var row = Row(grid, actions, overdue ? Ui.Res("OverdueLine") : null);
            if (!t.Paid) row.Background = Ui.Res("Bg");
            row.Cursor = Cursors.Hand;
            row.Tag = t;
            row.MouseLeftButtonUp += (s, e) => { if (!e.Handled) ToggleTx(t); };
            return row;
        }

        /// <summary>Row click: opens the transaction's panel, or closes it if that same transaction is already open.</summary>
        private void ToggleTx(FinanceTransaction t)
        {
            if (PanelOpen && TxForm.Visibility == Visibility.Visible && TxVm.Editing == t) ClosePanel();
            else OpenTxForm(t);
        }

        private static void AutomationPropertiesName(DependencyObject o, string name) =>
            System.Windows.Automation.AutomationProperties.SetName(o, name);

        internal void TogglePaid(FinanceTransaction t) => Vm.TogglePaid(t);

        // ================= chart =================

        private void BuildChart()
        {
            Chart.Children.Clear();
            var list = FinanceService.ExpensesByCategory(D, Vm.Month);
            var total = list.Sum(c => c.Total);
            ChartMeta.Text = total > 0 ? TF("Total {0} · pagos e pendentes", Money.Format(total)) : T("Nenhum gasto neste mês.");
            foreach (var ct in list)
            {
                var cat = CategoryOf(ct.CategoryId);
                var color = cat?.Color ?? "#9A93A8";
                double pct = total > 0 ? (double)(ct.Total / total) : 0;
                var item = new StackPanel { Margin = new Thickness(0, 0, 0, 12), ToolTip = $"{cat?.Name ?? T("Sem categoria")}: {Money.Format(ct.Total)} ({pct.ToString("P0", Dates.Culture)})" };
                var head = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
                var value = new TextBlock { Text = Money.Format(ct.Total), FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(value, Dock.Right);
                head.Children.Add(value);
                var name = new StackPanel { Orientation = Orientation.Horizontal };
                name.Children.Add(new Icon { Data = CatGeo(cat?.Icon), Foreground = Palette.BrushFrom(color), Width = 14, Height = 14, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
                name.Children.Add(new TextBlock { Text = cat?.Name ?? T("Sem categoria"), FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                name.Children.Add(new TextBlock { Text = "  " + pct.ToString("P0", Dates.Culture), Style = Ui.St("Muted"), VerticalAlignment = VerticalAlignment.Center });
                head.Children.Add(name);
                item.Children.Add(head);

                // native bar: two star columns (share / rest) inside a rounded track
                var track = new Grid { Height = 8 };
                track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(pct, 0.001), GridUnitType.Star) });
                track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - pct, 0.0001), GridUnitType.Star) });
                var bg = new Border { Background = Ui.Res("Bg"), CornerRadius = new CornerRadius(4) };
                Grid.SetColumnSpan(bg, 2);
                track.Children.Add(bg);
                track.Children.Add(new Border { Background = Palette.BrushFrom(color), CornerRadius = new CornerRadius(4), MinWidth = 4 });
                item.Children.Add(track);
                Chart.Children.Add(item);
            }
        }

        // ================= transaction form =================

        private void ShowPanel(FrameworkElement form)
        {
            SuggestPopup.IsOpen = false;
            foreach (var f in new FrameworkElement[] { TxForm, AccForm, CheckForm, CatForm }) f.Visibility = f == form ? Visibility.Visible : Visibility.Collapsed;
            bool was = PanelOpen;
            _panelWanted = true;
            Panel.Visibility = Visibility.Visible;
            if (!was) Motion.Enter(Panel, dx: Motion.ShiftPanel);
        }

        private void ClosePanel_Click(object sender, RoutedEventArgs e) => ClosePanel();

        /// <summary>Opens the form for <paramref name="t"/> (edit) or a new transaction (null). <paramref name="prefill"/>: values for a new one (Duplicar).</summary>
        internal void OpenTxForm(FinanceTransaction t, FinanceTransaction prefill = null)
        {
            TxVm.CategoryId = Sel(TxCategory); // a new transaction keeps the category chosen last time when it fits
            TxVm.Open(t, prefill);
            ShowTxForm();
        }

        /// <summary>Shows the transaction form with the view model's values.</summary>
        private void ShowTxForm()
        {
            var f = TxVm;
            TxFormTitle.Text = f.Title;
            TxEditActions.Visibility = f.IsNew ? Visibility.Collapsed : Visibility.Visible;
            TxError.Visibility = Visibility.Collapsed;

            _sync = true;
            _descBusy = true;
            KindRadio(f.Kind).IsChecked = true;
            SetText(TxAmount, f.AmountText);
            SetText(TxDesc, f.Description);
            Fill(TxCategory, f.CategoryOptions, f.CategoryId);
            Fill(TxAccount, f.AccountOptions, f.AccountId);
            Fill(TxToAccount, f.AccountOptions, f.ToAccountId);
            TxDate.SelectedDate = f.Date;
            TxPaid.IsChecked = f.Paid;
            SetText(TxNote, f.Note);
            SetText(TxInstallments, f.InstallmentsText);
            TxRepeat.IsChecked = f.Repeat;
            _descBusy = false;
            _sync = false;

            InstallmentField.Visibility = f.ShowInstallments ? Visibility.Visible : Visibility.Collapsed;
            RepeatField.Visibility = f.ShowRepeat ? Visibility.Visible : Visibility.Collapsed;
            UpdateKindFields();
            TxSeriesInfo.Text = f.SeriesInfo ?? "";
            TxSeriesInfo.Visibility = f.SeriesInfo == null ? Visibility.Collapsed : Visibility.Visible;
            SetMore(f.StartWithMore);

            ShowPanel(TxForm);
            Dispatcher.BeginInvoke(new Action(() => { TxAmount.Focus(); TxAmount.SelectAll(); }), DispatcherPriority.Input);
        }

        private RadioButton KindRadio(TxKind kind) => kind switch { TxKind.Income => KindIncome, TxKind.Transfer => KindTransfer, _ => KindExpense };

        private TxKind FormKind => KindIncome.IsChecked == true ? TxKind.Income : KindTransfer.IsChecked == true ? TxKind.Transfer : TxKind.Expense;

        /// <summary>Copies every control of the transaction form into the view model (tests and snapshots also set them from code).</summary>
        private void PullTxForm()
        {
            var f = TxVm;
            f.Kind = FormKind;
            f.AmountText = TxAmount.Text;
            f.Description = TxDesc.Text;
            f.CategoryId = Sel(TxCategory);
            f.AccountId = Sel(TxAccount);
            f.ToAccountId = Sel(TxToAccount);
            f.Date = TxDate.SelectedDate;
            f.Paid = TxPaid.IsChecked == true;
            f.Note = TxNote.Text;
            f.InstallmentsText = TxInstallments.Text;
            f.Repeat = TxRepeat.IsChecked == true;
        }

        private void Kind_Checked(object sender, RoutedEventArgs e)
        {
            if (TxCategory == null || _sync) return;
            TxVm.CategoryId = Sel(TxCategory);
            TxVm.ChangeKind(FormKind);
            Fill(TxCategory, TxVm.CategoryOptions, TxVm.CategoryId);
            UpdateKindFields();
        }

        private void UpdateKindFields()
        {
            bool transfer = FormKind == TxKind.Transfer;
            CatField.Visibility = transfer ? Visibility.Collapsed : Visibility.Visible;
            ToField.Visibility = transfer ? Visibility.Visible : Visibility.Collapsed;
            AccountLabel.Text = transfer ? T("Da conta") : T("Conta");
            if (TxVm.IsNew) InstallmentField.Visibility = transfer ? Visibility.Collapsed : Visibility.Visible;
            UpdateInstallmentHint();
        }

        private void More_Click(object sender, RoutedEventArgs e) => SetMore(MorePanel.Visibility != Visibility.Visible);

        internal void SetMore(bool open)
        {
            MorePanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            MoreChevron.RenderTransform = open ? null : new RotateTransform(-90);
        }

        private void OnTxDateChanged()
        {
            if (_sync) return;
            // new transaction: dated in the future → pending, today/past → paid (until the user flips the switch)
            TxVm.Paid = TxPaid.IsChecked == true;
            TxVm.ChangeDate(TxDate.SelectedDate);
            TxPaid.IsChecked = TxVm.Paid;
        }

        private void TxPaid_Click(object sender, RoutedEventArgs e) => TxVm.ChangePaid(TxPaid.IsChecked == true);

        private void TxRepeat_Click(object sender, RoutedEventArgs e)
        {
            TxVm.InstallmentsText = TxInstallments.Text;
            TxVm.ChangeRepeat(TxRepeat.IsChecked == true);
            SetText(TxInstallments, TxVm.InstallmentsText);
        }

        private void TxAmount_Changed(object sender, TextChangedEventArgs e) { TxError.Visibility = Visibility.Collapsed; UpdateInstallmentHint(); }

        private void TxInstallments_Changed(object sender, TextChangedEventArgs e)
        {
            if (!_sync && TxRepeat != null)
            {
                TxVm.Repeat = TxRepeat.IsChecked == true;
                TxVm.ChangeInstallments(TxInstallments.Text);
                TxRepeat.IsChecked = TxVm.Repeat;
            }
            UpdateInstallmentHint();
        }

        private void UpdateInstallmentHint()
        {
            // while a view model is being copied into the controls they are half updated: the hint is redone right after
            if (_sync || InstallmentHint == null || TxAmount == null || TxInstallments == null) return;
            TxVm.AmountText = TxAmount.Text;
            TxVm.InstallmentsText = TxInstallments.Text;
            InstallmentHint.Text = TxVm.InstallmentHint;
        }

        private void Form_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                SuggestPopup.IsOpen = false;
                SaveTx();
                e.Handled = true;
            }
            else if (e.Key == Key.Down && sender == TxDesc && SuggestPopup.IsOpen && SuggestList.Children.Count > 0)
            {
                ((UIElement)SuggestList.Children[0]).Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && SuggestPopup.IsOpen)
            {
                SuggestPopup.IsOpen = false;
                e.Handled = true;
            }
        }

        // ----- autocomplete by description -----

        private void TxDesc_Changed(object sender, TextChangedEventArgs e)
        {
            if (_descBusy || !TxVm.IsNew) return;
            TxVm.Description = TxDesc.Text;
            var list = TxVm.Suggestions();
            SuggestList.Children.Clear();
            foreach (var (s, amount) in list)
            {
                var content = new DockPanel();
                var v = new TextBlock { Text = amount is decimal m ? Money.Format(m) : "", Style = Ui.St("Muted"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(v, Dock.Right);
                content.Children.Add(v);
                content.Children.Add(new TextBlock { Text = s, VerticalAlignment = VerticalAlignment.Center });
                var b = new Button
                {
                    Style = Ui.St("GhostButton"), Foreground = Ui.Res("Text"), FontWeight = FontWeights.Normal,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch, Content = content
                };
                b.Click += (o, a) => PickSuggestion(s);
                b.PreviewKeyDown += SuggestKey;
                SuggestList.Children.Add(b);
            }
            SuggestPopup.IsOpen = list.Count > 0 && TxDesc.IsKeyboardFocused;
        }

        private void SuggestKey(object sender, KeyEventArgs e)
        {
            int i = SuggestList.Children.IndexOf((UIElement)sender);
            if (e.Key == Key.Down && i < SuggestList.Children.Count - 1) { ((UIElement)SuggestList.Children[i + 1]).Focus(); e.Handled = true; }
            else if (e.Key == Key.Up) { (i > 0 ? (UIElement)SuggestList.Children[i - 1] : TxDesc).Focus(); e.Handled = true; }
            else if (e.Key == Key.Escape) { SuggestPopup.IsOpen = false; TxDesc.Focus(); e.Handled = true; }
        }

        private void PickSuggestion(string description)
        {
            _descBusy = true;
            TxDesc.Text = description;
            TxDesc.CaretIndex = description.Length;
            _descBusy = false;
            SuggestPopup.IsOpen = false;
            ApplySuggestion();
            TxDesc.Focus();
        }

        private void TxDesc_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (TxVm.IsNew && !SuggestPopup.IsKeyboardFocusWithin && !(e.NewFocus is DependencyObject n && IsInSuggest(n))) ApplySuggestion();
        }

        private bool IsInSuggest(DependencyObject o)
        {
            while (o != null) { if (o == SuggestList) return true; o = VisualTreeHelper.GetParent(o) ?? LogicalTreeHelper.GetParent(o); }
            return false;
        }

        /// <summary>Same description as an earlier transaction: fills value (if empty), type, category and account from the latest one.</summary>
        internal void ApplySuggestion()
        {
            PullTxForm();
            if (!TxVm.ApplySuggestion()) return;
            SetText(TxAmount, TxVm.AmountText);
            _sync = true;
            KindRadio(TxVm.Kind).IsChecked = true;
            _sync = false;
            Fill(TxCategory, TxVm.CategoryOptions, TxVm.CategoryId);
            UpdateKindFields();
            if (TxVm.AccountId != Sel(TxAccount)) SelectTag(TxAccount, TxVm.AccountId);
        }

        // ----- save -----

        private void TxSave_Click(object sender, RoutedEventArgs e) => SaveTx();

        private void ShowTxError(string text, Control focus)
        {
            TxError.Text = text;
            TxError.Visibility = Visibility.Visible;
            focus?.Focus();
        }

        /// <summary>Validates and saves the form. Returns false when something is missing (the message is shown in the form).</summary>
        internal bool SaveTx()
        {
            PullTxForm();
            if (TxVm.Save()) return true;
            ShowTxError(TxVm.Error, TxVm.ErrorField switch
            {
                TxField.Amount => TxAmount,
                TxField.Account => TxAccount,
                TxField.ToAccount => TxToAccount,
                TxField.Installments => TxInstallments,
                _ => null
            });
            if (TxVm.ErrorOpensMore) SetMore(true);
            return false;
        }

        // ----- duplicate / delete -----

        private void DuplicateTx(FinanceTransaction t)
        {
            TxVm.CategoryId = Sel(TxCategory);
            TxVm.Duplicate(t);
            ShowTxForm();
        }

        private void TxDuplicateForm_Click(object sender, RoutedEventArgs e) { if (TxVm.Editing != null) DuplicateTx(TxVm.Editing); }
        private void TxDeleteForm_Click(object sender, RoutedEventArgs e) { if (TxVm.Editing != null) RequestDeleteTx(TxVm.Editing); }

        internal void RequestDeleteTx(FinanceTransaction t) => Vm.RequestDeleteTx(t);

        internal void DeleteTx(FinanceTransaction t, bool following) => Vm.DeleteTx(t, following);

        // ================= accounts =================

        private void ShowArchived_Changed(object sender, RoutedEventArgs e) { if (AccList != null) BuildAccounts(); }

        private void BuildAccounts()
        {
            AccList.Children.Clear();
            var end = Vm.MonthEnd;
            Vm.ShowArchived = ShowArchived.IsChecked == true;
            foreach (var a in Vm.AccountsToShow())
            {
                var bal = FinanceService.Balance(D, a);
                var proj = FinanceService.ProjectedBalance(D, a, end);
                var grid = new Grid();
                foreach (var w in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
                var badge = IconBadge(AccGeo(a.Type), a.Color);
                badge.Margin = new Thickness(0, 0, 12, 0);
                grid.Children.Add(badge);

                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                info.Children.Add(new TextBlock { Text = a.Name, FontSize = 14, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis });
                info.Children.Add(new TextBlock { Text = TypeName(a.Type) + (a.Archived ? " · arquivada" : ""), Style = Ui.St("Muted"), Margin = new Thickness(0, 2, 0, 0) });
                Grid.SetColumn(info, 1);
                grid.Children.Add(info);

                var money = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
                money.Children.Add(new TextBlock { Text = Money.Format(bal), FontSize = 16, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right, Foreground = bal < 0 ? Ui.Res("Danger") : Ui.Res("Text") });
                money.Children.Add(new TextBlock { Text = TF("Previsto em {0}: {1}", end.ToString(T("dd/MM"), Dates.Culture), Money.Format(proj)), Style = Ui.St("MutedItalic"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 0, 0) });
                Grid.SetColumn(money, 2);
                grid.Children.Add(money);

                var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                actions.Children.Add(IconBtn("I.Scale", T("Conferir saldo"), (s, e) => OpenCheckForm(a)));
                actions.Children.Add(IconBtn("I.Archive", a.Archived ? T("Desarquivar") : T("Arquivar"), (s, e) => ToggleArchive(a)));
                actions.Children.Add(IconBtn("I.Edit", T("Editar"), (s, e) => OpenAccForm(a)));
                Grid.SetColumn(actions, 3);
                grid.Children.Add(actions);

                var row = Row(grid, actions);
                row.Padding = new Thickness(12, 10, 8, 10);
                if (a.Archived) row.Opacity = 0.6;
                row.Cursor = Cursors.Hand;
                row.MouseLeftButtonUp += (s, e) => { if (!e.Handled) OpenAccForm(a); };
                AccList.Children.Add(row);
            }
            if (AccList.Children.Count == 0)
                AccList.Children.Add(new TextBlock { Text = T("Nenhuma conta. Crie uma em “Nova conta”."), Style = Ui.St("MutedItalic"), FontSize = 13, Margin = new Thickness(0, 12, 0, 0) });
        }

        private void ToggleArchive(Account a) => Vm.ToggleArchive(a);

        private void NewAccount_Click(object sender, RoutedEventArgs e) => OpenAccForm(null);

        internal void OpenAccForm(Account a)
        {
            var f = Vm.Account;
            f.Open(a);
            AccFormTitle.Text = f.Title;
            AccName.Text = f.Name;
            Fill(AccType, Enum.GetValues<AccountType>().Select(t => (((int)t).ToString(), TypeName(t))), ((int)f.Type).ToString());
            AccInitial.Text = f.InitialText;
            AccArchived.IsChecked = f.Archived;
            AccArchiveRow.Visibility = f.ShowArchive ? Visibility.Visible : Visibility.Collapsed;
            AccDeleteBtn.Visibility = f.CanDelete ? Visibility.Visible : Visibility.Collapsed;
            AccError.Visibility = Visibility.Collapsed;
            BuildSwatches(AccColors, () => f.Color, c => f.Color = c);
            ShowPanel(AccForm);
            Dispatcher.BeginInvoke(new Action(() => AccName.Focus()), DispatcherPriority.Input);
        }

        private void AccForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { AccSave_Click(null, null); e.Handled = true; }
        }

        private void AccSave_Click(object sender, RoutedEventArgs e)
        {
            var f = Vm.Account;
            f.Name = AccName.Text;
            f.InitialText = AccInitial.Text;
            f.Type = (AccountType)int.Parse(Sel(AccType) ?? "0");
            f.Archived = AccArchived.IsChecked == true;
            if (f.Save()) return;
            AccError.Text = f.Error;
            AccError.Visibility = Visibility.Visible;
            (f.ErrorField == AccountField.Initial ? AccInitial : AccName).Focus();
        }

        private void AccDelete_Click(object sender, RoutedEventArgs e) => Vm.Account.RequestDelete();

        // ----- check balance -----

        internal void OpenCheckForm(Account a)
        {
            Vm.Check.Open(a);
            CheckAccount.Text = a.Name;
            CheckCurrent.Text = Vm.Check.CurrentText;
            CheckReal.Text = "";
            CheckReal_Changed(null, null);
            ShowPanel(CheckForm);
            Dispatcher.BeginInvoke(new Action(() => CheckReal.Focus()), DispatcherPriority.Input);
        }

        private void CheckReal_Changed(object sender, TextChangedEventArgs e)
        {
            if (Vm.Check.Account == null || CheckDiff == null) return;
            Vm.Check.RealText = CheckReal.Text;
            CheckDiff.Text = Vm.Check.DiffText;
            CheckSaveBtn.IsEnabled = Vm.Check.CanSave;
        }

        private void CheckForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { CheckSave_Click(null, null); e.Handled = true; }
        }

        internal void CheckSave_Click(object sender, RoutedEventArgs e)
        {
            if (Vm.Check.Account != null) Vm.Check.RealText = CheckReal.Text;
            Vm.Check.Save();
        }

        // ================= recurrences =================

        private void BuildRecurrences()
        {
            RecList.Children.Clear();
            foreach (var r in Vm.RecurrencesInOrder())
            {
                var cat = CategoryOf(r.CategoryId);
                bool ended = Vm.IsEnded(r);
                var grid = new Grid();
                foreach (var w in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
                var badge = IconBadge(Ui.Geo("I.Repeat"), r.Kind == TxKind.Transfer ? "#78B7FF" : cat?.Color ?? "#9A93A8");
                badge.Margin = new Thickness(0, 0, 12, 0);
                grid.Children.Add(badge);

                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                info.Children.Add(new TextBlock { Text = r.Description, FontSize = 14, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis });
                var pills = new WrapPanel();
                pills.Children.Add(Pill(TF("Todo dia {0}", r.DayOfMonth), icon: Ui.Geo("I.Calendar")));
                if (r.Kind != TxKind.Transfer) pills.Children.Add(Pill(cat?.Name ?? T("Sem categoria"), dot: cat?.Color ?? "#9A93A8"));
                pills.Children.Add(Pill(AccountOf(r.AccountId)?.Name ?? T("Sem conta")));
                if (ended) pills.Children.Add(Pill(TF("Encerrada em {0}", Dates.Cap(r.EndMonth.Value.ToString("MMM/yyyy", Dates.Culture).Replace(".", "")))));
                else if (r.Paused) pills.Children.Add(Pill(T("Pausada"), Ui.Res("Warning"), Ui.Geo("I.Pause")));
                else
                {
                    // skips months deleted on purpose and stops at the end month
                    var next = FinanceService.NextOccurrence(r, DateTime.Today);
                    pills.Children.Add(next.HasValue ? Pill(TF("Próximo: {0}", next.Value.ToString(T("dd/MM"), Dates.Culture)), Ui.Res("Success")) : Pill(T("Sem próximos lançamentos")));
                    if (r.EndMonth.HasValue) pills.Children.Add(Pill(TF("até {0}", r.EndMonth.Value.ToString("MM/yyyy", Dates.Culture))));
                }
                info.Children.Add(pills);
                Grid.SetColumn(info, 1);
                grid.Children.Add(info);

                var amount = new TextBlock
                {
                    Text = (r.Kind == TxKind.Income ? "+" : r.Kind == TxKind.Expense ? "-" : "") + Money.Format(r.Amount),
                    FontSize = 14.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
                    Foreground = r.Kind == TxKind.Income ? Ui.Res("Success") : Ui.Res("Text")
                };
                Grid.SetColumn(amount, 2);
                grid.Children.Add(amount);

                var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                if (!ended)
                {
                    actions.Children.Add(IconBtn(r.Paused ? "I.Play" : "I.Pause", r.Paused ? T("Retomar") : T("Pausar"), (s, e) => Vm.TogglePause(r)));
                    actions.Children.Add(IconBtn("I.Stop", T("Encerrar"), (s, e) => RequestEndRecurrence(r), danger: true));
                }
                actions.Children.Add(IconBtn("I.Trash", T("Excluir recorrência"), (s, e) => RequestDeleteRecurrence(r), danger: true));
                Grid.SetColumn(actions, 3);
                grid.Children.Add(actions);
                var row = Row(grid, actions);
                row.Padding = new Thickness(12, 10, 8, 10);
                if (ended || r.Paused) row.Opacity = 0.65;
                RecList.Children.Add(row);
            }
            RecEmpty.Visibility = D.Recurrences.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// "Encerrar": after this month by default; with the box ticked, already this month (its pending entry is removed,
        /// a paid one stays).
        /// </summary>
        internal void RequestEndRecurrence(Recurrence r) => Vm.RequestEndRecurrence(r);

        internal void EndRecurrence(Recurrence r, bool includeThisMonth) => Vm.EndRecurrence(r, includeThisMonth);

        /// <summary>"Excluir recorrência": the model and its pending entries go; paid ones stay unless the box is ticked.</summary>
        internal void RequestDeleteRecurrence(Recurrence r) => Vm.RequestDeleteRecurrence(r);

        internal void DeleteRecurrence(Recurrence r, bool includePaid) => Vm.DeleteRecurrence(r, includePaid);

        // ================= categories =================

        private void BuildCategories()
        {
            CatExpenseList.Children.Clear();
            CatIncomeList.Children.Clear();
            foreach (var c in Vm.CategoriesInOrder())
            {
                var grid = new Grid();
                foreach (var w in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto })
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
                var badge = IconBadge(CatGeo(c.Icon), c.Color, 30);
                badge.Margin = new Thickness(0, 0, 12, 0);
                grid.Children.Add(badge);
                int used = FinanceService.UsageCount(D, c);
                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                info.Children.Add(new TextBlock { Text = c.Name, FontSize = 14, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis });
                info.Children.Add(new TextBlock { Text = used == 0 ? T("Sem lançamentos") : TN(used, "{0} lançamento", "{0} lançamentos"), Style = Ui.St("Muted"), Margin = new Thickness(0, 1, 0, 0) });
                Grid.SetColumn(info, 1);
                grid.Children.Add(info);
                var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                actions.Children.Add(IconBtn("I.Edit", T("Editar"), (s, e) => OpenCatForm(c)));
                Grid.SetColumn(actions, 2);
                grid.Children.Add(actions);
                var row = Row(grid, actions);
                row.Cursor = Cursors.Hand;
                row.MouseLeftButtonUp += (s, e) => { if (!e.Handled) OpenCatForm(c); };
                (c.Kind == CategoryKind.Income ? CatIncomeList : CatExpenseList).Children.Add(row);
            }
        }

        private void NewCategory_Click(object sender, RoutedEventArgs e) => OpenCatForm(null);

        internal void OpenCatForm(Category c)
        {
            var f = Vm.Category;
            f.Open(c);
            CatFormTitle.Text = f.Title;
            CatName.Text = f.Name;
            (f.Kind == CategoryKind.Income ? CatKindIncome : CatKindExpense).IsChecked = true;
            CatError.Visibility = Visibility.Collapsed;
            BuildSwatches(CatColors, () => f.Color, v => { f.Color = v; BuildIconChoices(); });
            BuildIconChoices();

            CatDeleteBox.Visibility = f.IsNew ? Visibility.Collapsed : Visibility.Visible;
            if (!f.IsNew)
            {
                CatUsage.Text = f.UsageText;
                CatMoveField.Visibility = f.ShowMove ? Visibility.Visible : Visibility.Collapsed;
                Fill(CatMoveTo, f.MoveOptions, f.MoveToId);
            }
            ShowPanel(CatForm);
            Dispatcher.BeginInvoke(new Action(() => CatName.Focus()), DispatcherPriority.Input);
        }

        private void BuildIconChoices()
        {
            CatIcons.Children.Clear();
            var f = Vm.Category;
            foreach (var key in CategoryIcons)
            {
                bool on = key == f.Icon;
                var b = new Button
                {
                    Style = Ui.St("IconButton"), Padding = new Thickness(7), Margin = new Thickness(0, 0, 6, 6), ToolTip = key,
                    Background = on ? Tint(f.Color, 0x40) : Brushes.Transparent,
                    BorderBrush = on ? Palette.BrushFrom(f.Color) : Brushes.Transparent,
                    Content = new Icon { Data = CatGeo(key), Width = 16, Height = 16, Foreground = on ? Palette.BrushFrom(f.Color) : Ui.Res("Text2") }
                };
                b.Click += (s, e) => { f.Icon = key; BuildIconChoices(); };
                CatIcons.Children.Add(b);
            }
        }

        private static void BuildSwatches(WrapPanel host, Func<string> current, Action<string> pick)
        {
            host.Children.Clear();
            foreach (var hex in Colors)
            {
                bool on = string.Equals(hex, current(), StringComparison.OrdinalIgnoreCase);
                var b = new Button
                {
                    Width = 26, Height = 26, Margin = new Thickness(0, 0, 8, 8), Cursor = Cursors.Hand, ToolTip = hex, Focusable = true,
                    Template = SwatchTemplate, Background = Palette.BrushFrom(hex), BorderBrush = on ? Ui.Res("Text") : Brushes.Transparent
                };
                b.Click += (s, e) => { pick(hex); BuildSwatches(host, current, pick); };
                host.Children.Add(b);
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

        private void CatForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { CatSave_Click(null, null); e.Handled = true; }
        }

        private void CatSave_Click(object sender, RoutedEventArgs e)
        {
            var f = Vm.Category;
            f.Name = CatName.Text;
            f.Kind = CatKindIncome.IsChecked == true ? CategoryKind.Income : CategoryKind.Expense;
            if (f.Save()) return;
            CatError.Text = f.Error;
            CatError.Visibility = Visibility.Visible;
            CatName.Focus();
        }

        internal void CatDelete_Click(object sender, RoutedEventArgs e)
        {
            Vm.Category.MoveToId = Sel(CatMoveTo);
            Vm.Category.RequestDelete();
        }
    }
}
