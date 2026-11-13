using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
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
    /// expenses-by-category chart. Code-behind like the rest of the app; the money rules are in <see cref="FinanceService"/>.
    /// </summary>
    public partial class FinanceView : UserControl
    {
        private static FinanceData D => Store.Data.Finance;

        private DateTime _month = FinanceService.MonthStart(DateTime.Today);
        private string _tab = "tx";
        private bool _filling;

        // transaction form
        private FinanceTransaction _editing;   // null = new
        private bool _paidTouched;
        private bool _descBusy;

        // other forms
        private Account _editingAcc;
        private string _accColor;
        private Account _checking;
        private Category _editingCat;
        private string _catColor, _catIcon;

        /// <summary>Raised after any change, so the window can update the sidebar counter and the Hoje card.</summary>
        public event Action Changed;

        private static readonly string[] Colors =
            { "#8950D9", "#C8A8F5", "#78B7FF", "#5CC8C8", "#64D6A2", "#F2C36B", "#F2A65A", "#FF8FB1", "#FF6F91", "#9A93A8" };
        private static readonly string[] CategoryIcons =
            { "food", "home", "car", "repeat", "fun", "health", "cart", "book", "gift", "salary", "laptop", "coin", "dots" };

        public FinanceView()
        {
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
            GenerateMonth();
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
            _editing = null; _editingAcc = null; _checking = null; _editingCat = null;
        }

        private bool _panelWanted;

        public void Refresh()
        {
            ThisMonthBtn.Visibility = _month == FinanceService.MonthStart(DateTime.Today) ? Visibility.Collapsed : Visibility.Visible;
            MonthTitle.Text = Dates.MonthTitle(_month);
            var s = FinanceService.Summary(D, _month);
            SumIncome.Text = Money.Format(s.Income);
            SumExpense.Text = Money.Format(s.Expense);
            SumBalance.Text = Money.Format(s.CurrentBalance);
            SumBalance.Foreground = s.CurrentBalance < 0 ? Ui.Res("Danger") : Ui.Res("Text");
            SumProjected.Text = Money.Format(s.ProjectedBalance);
            SumProjected.Foreground = s.ProjectedBalance < 0 ? Ui.Res("Danger") : Ui.Res("Text");
            SumProjectedLabel.Text = "Previsto em " + FinanceService.MonthEnd(_month).ToString("dd/MM", Dates.Pt);

            TxTab.Visibility = _tab == "tx" ? Visibility.Visible : Visibility.Collapsed;
            AccTab.Visibility = _tab == "acc" ? Visibility.Visible : Visibility.Collapsed;
            RecTab.Visibility = _tab == "rec" ? Visibility.Visible : Visibility.Collapsed;
            CatTab.Visibility = _tab == "cat" ? Visibility.Visible : Visibility.Collapsed;
            switch (_tab)
            {
                case "tx": FillFilters(); BuildTransactions(); BuildChart(); break;
                case "acc": BuildAccounts(); break;
                case "rec": BuildRecurrences(); break;
                case "cat": BuildCategories(); break;
            }
        }

        /// <summary>Saves, tells the window and redraws.</summary>
        private void Commit()
        {
            Store.MarkDirty();
            Changed?.Invoke();
            Refresh();
        }

        private void GenerateMonth()
        {
            if (FinanceService.GenerateRecurring(D, _month).Count > 0) { Store.MarkDirty(); Changed?.Invoke(); }
        }

        // ================= layout =================

        /// <summary>Narrow (form open or small window): 2×2 summary, tabs on their own line, chart under the list.</summary>
        private void Scroll_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double w = Scroll.ActualWidth - 56;
            bool narrow = w < 860;
            SumGrid.Columns = w < 640 ? 2 : 4;
            DockPanel.SetDock(TabsBox, w < 720 ? Dock.Bottom : Dock.Right);
            TabsBox.Margin = w < 720 ? new Thickness(0, 12, 0, 0) : new Thickness(0);
            Grid.SetColumnSpan(TxCol, narrow ? 3 : 1);
            Grid.SetRow(ChartCard, narrow ? 1 : 0);
            Grid.SetColumn(ChartCard, narrow ? 0 : 2);
            Grid.SetColumnSpan(ChartCard, narrow ? 3 : 1);
            ChartCard.Margin = narrow ? new Thickness(0, 16, 0, 0) : new Thickness(0);
        }

        // ================= month / tabs =================

        private void SetMonth(DateTime m)
        {
            _month = FinanceService.MonthStart(m);
            GenerateMonth();
            Refresh();
        }

        internal void PrevMonth_Click(object sender, RoutedEventArgs e) => SetMonth(_month.AddMonths(-1));
        internal void NextMonth_Click(object sender, RoutedEventArgs e) => SetMonth(_month.AddMonths(1));
        internal void ThisMonth_Click(object sender, RoutedEventArgs e) => SetMonth(DateTime.Today);

        private void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized) return;
            _tab = (string)((FrameworkElement)sender).Tag;
            if (TxTab == null) return;
            Refresh();
        }

        /// <summary>For the self-test and snapshots.</summary>
        internal void ShowTab(string tab)
        {
            (tab switch { "acc" => TabAcc, "rec" => TabRec, "cat" => TabCat, _ => TabTx }).IsChecked = true;
            _tab = tab;
            Refresh();
        }

        internal DateTime Month => _month;

        // ================= helpers =================

        private static Category CategoryOf(string id) => id == null ? null : D.Categories.FirstOrDefault(c => c.Id == id);
        private static Account AccountOf(string id) => id == null ? null : D.Accounts.FirstOrDefault(a => a.Id == id);

        private static Geometry CatGeo(string icon) =>
            Application.Current.TryFindResource("F." + (string.IsNullOrEmpty(icon) ? "dots" : icon)) as Geometry ?? Ui.Geo("F.dots");

        private static Geometry AccGeo(AccountType t) => Ui.Geo("A." + t);

        public static string TypeName(AccountType t) => t switch
        {
            AccountType.Checking => "Conta corrente",
            AccountType.Wallet => "Carteira",
            AccountType.CreditCard => "Cartão de crédito",
            AccountType.Savings => "Poupança",
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
                Background = Ui.Res("Surface"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 9, 8, 9), Margin = new Thickness(0, 0, 0, 6), Child = content
            };
            var normal = border ?? Ui.Res("Line");
            Motion.SetBorderBrush(row, normal);
            if (actions != null) actions.Opacity = 0;
            void Update()
            {
                bool on = row.IsMouseOver || row.IsKeyboardFocusWithin;
                if (actions != null) actions.Opacity = on ? 1 : 0;
                Motion.SetBorderBrush(row, row.IsMouseOver ? Palette.BrushFrom("#4C3270") : normal);
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

        private static string Snapshot() => JsonSerializer.Serialize(D);
        private static void Restore(string json) => Store.Data.Finance = JsonSerializer.Deserialize<FinanceData>(json);

        // ================= transactions list =================

        private void FillStaticCombos()
        {
            _filling = true;
            Fill(FilterKind, new[] { ("", "Todos os tipos"), ("0", "Despesas"), ("1", "Receitas"), ("2", "Transferências") }, "");
            Fill(FilterStatus, new[] { ("", "Todos"), ("paid", "Pagos"), ("pending", "Pendentes"), ("overdue", "Vencidos") }, "");
            Fill(AccType, Enum.GetValues<AccountType>().Select(t => (((int)t).ToString(), TypeName(t))), "0");
            _filling = false;
        }

        private void FillFilters()
        {
            _filling = true;
            var acc = Sel(FilterAccount) ?? "";
            var cat = Sel(FilterCategory) ?? "";
            Fill(FilterAccount, new[] { ("", "Todas as contas") }.Concat(D.Accounts.Where(a => !a.Archived || a.Id == acc).Select(a => (a.Id, a.Name))), acc);
            Fill(FilterCategory, new[] { ("", "Todas as categorias") }
                .Concat(D.Categories.OrderBy(c => c.Kind).ThenBy(c => c.Name).Select(c => (c.Id, c.Name + (c.Kind == CategoryKind.Income ? " (receita)" : "")))), cat);
            _filling = false;
            ClearFiltersBtn.Visibility = (Sel(FilterAccount) + Sel(FilterCategory) + Sel(FilterKind) + Sel(FilterStatus)).Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Filter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_filling || !IsInitialized || TxList == null) return;
            ClearFiltersBtn.Visibility = (Sel(FilterAccount) + Sel(FilterCategory) + Sel(FilterKind) + Sel(FilterStatus)).Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            BuildTransactions();
        }

        private void ClearFilters_Click(object sender, RoutedEventArgs e)
        {
            _filling = true;
            foreach (var cb in new[] { FilterAccount, FilterCategory, FilterKind, FilterStatus }) cb.SelectedIndex = 0;
            _filling = false;
            ClearFiltersBtn.Visibility = Visibility.Collapsed;
            BuildTransactions();
        }

        internal List<FinanceTransaction> VisibleTransactions()
        {
            var today = DateTime.Today;
            string acc = Sel(FilterAccount), cat = Sel(FilterCategory), kind = Sel(FilterKind), status = Sel(FilterStatus);
            return FinanceService.InMonth(D, _month)
                .Where(t => string.IsNullOrEmpty(acc) || t.AccountId == acc || t.ToAccountId == acc)
                .Where(t => string.IsNullOrEmpty(cat) || t.CategoryId == cat)
                .Where(t => string.IsNullOrEmpty(kind) || ((int)t.Kind).ToString() == kind)
                .Where(t => status switch
                {
                    "paid" => t.Paid,
                    "pending" => !t.Paid,
                    "overdue" => FinanceService.IsOverdue(t, today),
                    _ => true
                })
                .OrderByDescending(t => t.Date).ThenByDescending(t => t.CreatedAt).ToList();
        }

        private void BuildTransactions()
        {
            TxList.Children.Clear();
            var list = VisibleTransactions();
            foreach (var day in list.GroupBy(t => t.Date.Date))
            {
                var net = day.Where(t => !t.IsAdjustment).Sum(t => t.Kind == TxKind.Income ? t.Amount : t.Kind == TxKind.Expense ? -t.Amount : 0);
                var head = new DockPanel { Margin = new Thickness(0, TxList.Children.Count == 0 ? 0 : 12, 0, 8) };
                var total = new TextBlock { Text = net == 0 ? "" : Money.Signed(net), Style = Ui.St("Muted"), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(total, Dock.Right);
                head.Children.Add(total);
                head.Children.Add(new TextBlock { Text = DayTitle(day.Key), FontSize = 14, FontWeight = FontWeights.SemiBold });
                TxList.Children.Add(head);
                foreach (var t in day) TxList.Children.Add(TxRow(t));
            }
            bool filtered = ClearFiltersBtn.Visibility == Visibility.Visible;
            TxEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            TxEmptyText.Text = filtered ? "Nenhum lançamento com esses filtros." : "Nenhum lançamento neste mês.\nUse “Novo lançamento” ou Ctrl+N.";
        }

        private static string DayTitle(DateTime d)
        {
            var today = DateTime.Today;
            var s = Dates.Cap(d.ToString("ddd, d 'de' MMMM", Dates.Pt).Replace(".", ""));
            if (d == today) return "Hoje · " + s;
            if (d == today.AddDays(-1)) return "Ontem · " + s;
            if (d == today.AddDays(1)) return "Amanhã · " + s;
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
                ToolTip = t.Paid ? "Pago · clique para marcar como pendente" : overdue ? "Vencido · clique para marcar como pago" : "Pendente · clique para marcar como pago",
                Content = new Icon
                {
                    Data = Ui.Geo(t.Paid ? "I.Check2" : "I.Clock"), Width = 18, Height = 18,
                    Foreground = t.Paid ? Ui.Res("Success") : overdue ? Ui.Res("Danger") : Ui.Res("Warning")
                }
            };
            AutomationPropertiesName(status, t.Paid ? "Marcar como pendente" : "Marcar como pago");
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
            var title = new TextBlock { Text = string.IsNullOrWhiteSpace(t.Description) ? "(sem descrição)" : t.Description, FontSize = 14, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis };
            if (!t.Paid) title.Foreground = Ui.Res("Text2");
            main.Children.Add(title);
            var pills = new WrapPanel();
            if (t.Kind == TxKind.Transfer)
                pills.Children.Add(Pill($"{AccountOf(t.AccountId)?.Name ?? "?"} → {AccountOf(t.ToAccountId)?.Name ?? "?"}", icon: Ui.Geo("I.Swap")));
            else
            {
                if (t.IsAdjustment) pills.Children.Add(Pill("Ajuste", icon: Ui.Geo("I.Scale")));
                else pills.Children.Add(Pill(cat?.Name ?? "Sem categoria", dot: cat?.Color ?? "#9A93A8"));
                pills.Children.Add(Pill(AccountOf(t.AccountId)?.Name ?? "Sem conta", icon: AccGeo(AccountOf(t.AccountId)?.Type ?? AccountType.Wallet)));
            }
            if (t.IsInstallment) pills.Children.Add(Pill($"Parcela {t.InstallmentNumber}/{t.InstallmentCount}", icon: Ui.Geo("I.List")));
            if (t.IsRecurring) pills.Children.Add(Pill("Mensal", icon: Ui.Geo("I.Repeat")));
            if (overdue) pills.Children.Add(Pill("Vencido", Ui.Res("Danger"), Ui.Geo("I.Clock")));
            else if (!t.Paid) pills.Children.Add(Pill("Pendente", Ui.Res("Warning")));
            if (!string.IsNullOrWhiteSpace(t.Note)) pills.Children.Add(Pill("Obs.", icon: Ui.Geo("I.Note")));
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
            actions.Children.Add(IconBtn("I.Copy", "Duplicar", (s, e) => DuplicateTx(t)));
            actions.Children.Add(IconBtn("I.Trash", "Excluir", (s, e) => RequestDeleteTx(t), danger: true));
            actions.Children.Add(IconBtn("I.Edit", "Editar", (s, e) => OpenTxForm(t)));
            Grid.SetColumn(actions, 4);
            grid.Children.Add(actions);

            var row = Row(grid, actions, overdue ? Palette.BrushFrom("#5A2A3E") : null);
            if (!t.Paid) row.Background = Ui.Res("Bg");
            row.Cursor = Cursors.Hand;
            row.Tag = t;
            row.MouseLeftButtonUp += (s, e) => { if (!e.Handled) OpenTxForm(t); };
            return row;
        }

        private static void AutomationPropertiesName(DependencyObject o, string name) =>
            System.Windows.Automation.AutomationProperties.SetName(o, name);

        internal void TogglePaid(FinanceTransaction t)
        {
            t.Paid = !t.Paid;
            t.UpdatedAt = DateTime.Now;
            Commit();
        }

        // ================= chart =================

        private void BuildChart()
        {
            Chart.Children.Clear();
            var list = FinanceService.ExpensesByCategory(D, _month);
            var total = list.Sum(c => c.Total);
            ChartMeta.Text = total > 0 ? $"Total {Money.Format(total)} · pagos e pendentes" : "Nenhum gasto neste mês.";
            foreach (var ct in list)
            {
                var cat = CategoryOf(ct.CategoryId);
                var color = cat?.Color ?? "#9A93A8";
                double pct = total > 0 ? (double)(ct.Total / total) : 0;
                var item = new StackPanel { Margin = new Thickness(0, 0, 0, 12), ToolTip = $"{cat?.Name ?? "Sem categoria"}: {Money.Format(ct.Total)} ({pct:P0})" };
                var head = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };
                var value = new TextBlock { Text = Money.Format(ct.Total), FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(value, Dock.Right);
                head.Children.Add(value);
                var name = new StackPanel { Orientation = Orientation.Horizontal };
                name.Children.Add(new Icon { Data = CatGeo(cat?.Icon), Foreground = Palette.BrushFrom(color), Width = 14, Height = 14, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
                name.Children.Add(new TextBlock { Text = cat?.Name ?? "Sem categoria", FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                name.Children.Add(new TextBlock { Text = $"  {pct:P0}", Style = Ui.St("Muted"), VerticalAlignment = VerticalAlignment.Center });
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
            _editing = t;
            var src = t ?? prefill;
            _paidTouched = t != null || prefill != null;
            TxFormTitle.Text = t == null ? "Novo lançamento" : "Editar lançamento";
            TxEditActions.Visibility = t == null ? Visibility.Collapsed : Visibility.Visible;
            TxError.Visibility = Visibility.Collapsed;

            var kind = src?.Kind ?? TxKind.Expense;
            (kind switch { TxKind.Income => KindIncome, TxKind.Transfer => KindTransfer, _ => KindExpense }).IsChecked = true;
            _descBusy = true;
            TxAmount.Text = src == null ? "" : Money.Plain(src.Amount);
            TxDesc.Text = src?.Description ?? "";
            _descBusy = false;
            FillCategoryCombo(kind, src?.CategoryId);
            var accounts = D.Accounts.Where(a => !a.Archived || a.Id == src?.AccountId || a.Id == src?.ToAccountId).Select(a => (a.Id, a.Name)).ToList();
            Fill(TxAccount, accounts, src?.AccountId ?? D.LastAccountId);
            Fill(TxToAccount, accounts, src?.ToAccountId ?? accounts.FirstOrDefault(a => a.Id != Sel(TxAccount)).Id);
            TxDate.SelectedDate = t?.Date ?? DateTime.Today;
            TxPaid.IsChecked = src?.Paid ?? true;
            TxNote.Text = src?.Note ?? "";
            TxInstallments.Text = "";
            TxRepeat.IsChecked = false;
            InstallmentField.Visibility = t == null ? Visibility.Visible : Visibility.Collapsed;
            RepeatField.Visibility = t == null ? Visibility.Visible : Visibility.Collapsed;
            UpdateKindFields();

            TxSeriesInfo.Visibility = Visibility.Collapsed;
            if (t != null && t.IsInstallment)
            {
                TxSeriesInfo.Text = $"Parcela {t.InstallmentNumber} de {t.InstallmentCount}. Ao salvar, você pode aplicar a mudança às parcelas seguintes.";
                TxSeriesInfo.Visibility = Visibility.Visible;
            }
            else if (t != null && t.IsRecurring)
            {
                var r = D.Recurrences.FirstOrDefault(x => x.Id == t.RecurrenceId);
                TxSeriesInfo.Text = r == null ? "Gerado por uma recorrência que não existe mais." : $"Recorrente: todo dia {r.DayOfMonth}. Ao salvar, você pode aplicar a mudança aos meses seguintes.";
                TxSeriesInfo.Visibility = Visibility.Visible;
            }
            bool showMore = t != null && (!t.Paid || !string.IsNullOrWhiteSpace(t.Note));
            SetMore(showMore);

            ShowPanel(TxForm);
            Dispatcher.BeginInvoke(new Action(() => { TxAmount.Focus(); TxAmount.SelectAll(); }), DispatcherPriority.Input);
        }

        private void FillCategoryCombo(TxKind kind, string select)
        {
            var ck = kind == TxKind.Income ? CategoryKind.Income : CategoryKind.Expense;
            var cats = D.Categories.Where(c => c.Kind == ck).Select(c => (c.Id, c.Name)).ToList();
            var keep = select ?? Sel(TxCategory);
            Fill(TxCategory, new[] { ("", "Sem categoria") }.Concat(cats), cats.Any(c => c.Id == keep) ? keep : cats.FirstOrDefault().Id ?? "");
        }

        private TxKind FormKind => KindIncome.IsChecked == true ? TxKind.Income : KindTransfer.IsChecked == true ? TxKind.Transfer : TxKind.Expense;

        private void Kind_Checked(object sender, RoutedEventArgs e)
        {
            if (TxCategory == null) return;
            FillCategoryCombo(FormKind, null);
            UpdateKindFields();
        }

        private void UpdateKindFields()
        {
            bool transfer = FormKind == TxKind.Transfer;
            CatField.Visibility = transfer ? Visibility.Collapsed : Visibility.Visible;
            ToField.Visibility = transfer ? Visibility.Visible : Visibility.Collapsed;
            AccountLabel.Text = transfer ? "Da conta" : "Conta";
            if (_editing == null) InstallmentField.Visibility = transfer ? Visibility.Collapsed : Visibility.Visible;
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
            // new transaction: dated in the future → pending, today/past → paid (until the user flips the switch)
            if (_editing == null && !_paidTouched && TxDate.SelectedDate is DateTime d) TxPaid.IsChecked = d.Date <= DateTime.Today;
        }

        private void TxPaid_Click(object sender, RoutedEventArgs e) => _paidTouched = true;

        private void TxRepeat_Click(object sender, RoutedEventArgs e)
        {
            if (TxRepeat.IsChecked == true) TxInstallments.Text = "";
        }

        private void TxAmount_Changed(object sender, TextChangedEventArgs e) { TxError.Visibility = Visibility.Collapsed; UpdateInstallmentHint(); }
        private void TxInstallments_Changed(object sender, TextChangedEventArgs e)
        {
            if (int.TryParse(TxInstallments.Text, out var n) && n > 1) TxRepeat.IsChecked = false;
            UpdateInstallmentHint();
        }

        private void UpdateInstallmentHint()
        {
            if (InstallmentHint == null) return;
            if (int.TryParse(TxInstallments.Text, out var n) && n > 1 && n <= 360 && Money.TryParse(TxAmount.Text, out var v) && v > 0)
            {
                var parts = FinanceService.SplitInstallments(v, n);
                InstallmentHint.Text = parts[0] == parts[^1] ? $"{n}× de {Money.Format(parts[0])}" : $"{n - 1}× de {Money.Format(parts[0])} + 1× de {Money.Format(parts[^1])}";
            }
            else InstallmentHint.Text = "O valor acima é o total da compra.";
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
            if (_descBusy || _editing != null) return;
            var list = FinanceService.Descriptions(D, TxDesc.Text)
                .Where(s => !string.Equals(s, TxDesc.Text.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToList();
            SuggestList.Children.Clear();
            foreach (var s in list)
            {
                var sug = FinanceService.Suggest(D, s);
                var content = new DockPanel();
                var v = new TextBlock { Text = sug.HasValue ? Money.Format(sug.Value.Amount) : "", Style = Ui.St("Muted"), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
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
            if (_editing == null && !SuggestPopup.IsKeyboardFocusWithin && !(e.NewFocus is DependencyObject n && IsInSuggest(n))) ApplySuggestion();
        }

        private bool IsInSuggest(DependencyObject o)
        {
            while (o != null) { if (o == SuggestList) return true; o = VisualTreeHelper.GetParent(o) ?? LogicalTreeHelper.GetParent(o); }
            return false;
        }

        /// <summary>Same description as an earlier transaction: fills value (if empty), type, category and account from the latest one.</summary>
        internal void ApplySuggestion()
        {
            if (_editing != null || !(FinanceService.Suggest(D, TxDesc.Text) is Suggestion s)) return;
            if (!Money.TryParse(TxAmount.Text, out var typed) || typed == 0) TxAmount.Text = Money.Plain(s.Amount);
            (s.Kind switch { TxKind.Income => KindIncome, TxKind.Transfer => KindTransfer, _ => KindExpense }).IsChecked = true;
            FillCategoryCombo(s.Kind, s.CategoryId ?? "");
            if (s.AccountId != null && D.Accounts.Any(a => a.Id == s.AccountId && !a.Archived))
                TxAccount.SelectedItem = TxAccount.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.AccountId);
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
            if (!Money.TryParse(TxAmount.Text, out var amount) || amount <= 0) { ShowTxError("Informe um valor maior que zero (ex.: 1.234,56).", TxAmount); return false; }
            var kind = FormKind;
            string acc = Sel(TxAccount), to = kind == TxKind.Transfer ? Sel(TxToAccount) : null;
            if (string.IsNullOrEmpty(acc)) { ShowTxError("Escolha uma conta (crie uma na aba Contas).", TxAccount); return false; }
            if (kind == TxKind.Transfer && (string.IsNullOrEmpty(to) || to == acc)) { ShowTxError("Escolha uma conta de destino diferente da de origem.", TxToAccount); return false; }
            int installments = 1;
            if (_editing == null && kind != TxKind.Transfer && TxInstallments.Text.Trim().Length > 0
                && (!int.TryParse(TxInstallments.Text.Trim(), out installments) || installments < 1 || installments > 360))
            { ShowTxError("Parcelas: um número de 1 a 360.", TxInstallments); SetMore(true); return false; }
            bool repeat = _editing == null && TxRepeat.IsChecked == true;
            if (repeat && installments > 1) { ShowTxError("Escolha parcelas ou repetição, não os dois.", TxInstallments); return false; }

            string cat = kind == TxKind.Transfer ? null : (Sel(TxCategory) is { Length: > 0 } c ? c : null);
            string desc = TxDesc.Text.Trim();
            if (desc.Length == 0) desc = kind == TxKind.Transfer ? "Transferência" : CategoryOf(cat)?.Name ?? (kind == TxKind.Income ? "Receita" : "Despesa");
            var date = (TxDate.SelectedDate ?? DateTime.Today).Date;
            bool paid = TxPaid.IsChecked == true;
            string note = TxNote.Text.Trim();

            if (_editing == null)
            {
                var t = new FinanceTransaction
                {
                    Kind = kind, Amount = amount, Date = date, Description = desc, CategoryId = cat, AccountId = acc,
                    ToAccountId = to, Paid = paid, Note = note,
                };
                D.LastAccountId = acc;
                if (installments > 1) FinanceService.AddInstallments(D, t, installments);
                else
                {
                    D.Transactions.Add(t);
                    if (repeat) FinanceService.StartRecurrence(D, t);
                }
                _month = FinanceService.MonthStart(date); // show the month the new transaction landed in
                GenerateMonth();
                ClosePanel();
                Commit();
                return true;
            }

            var e0 = _editing;
            bool seriesFields = e0.Amount != amount || e0.Kind != kind || e0.CategoryId != cat || e0.AccountId != acc
                                || e0.ToAccountId != to || e0.Description != desc || (e0.Note ?? "") != note;
            int following = FinanceService.Series(D, e0, true).Count - 1;
            void Apply(bool toFollowing)
            {
                e0.Amount = amount; e0.Kind = kind; e0.CategoryId = cat; e0.AccountId = acc; e0.ToAccountId = to;
                e0.Description = desc; e0.Date = date; e0.Paid = paid; e0.Note = note; e0.UpdatedAt = DateTime.Now;
                if (toFollowing) FinanceService.ApplyToFollowing(D, e0);
                ClosePanel();
                Commit();
            }
            if (seriesFields && (e0.IsRecurring || (e0.IsInstallment && following > 0)))
            {
                string check = e0.IsInstallment
                    ? $"Aplicar também às {following} parcela{(following == 1 ? "" : "s")} seguinte{(following == 1 ? "" : "s")}"
                    : "Aplicar também aos meses seguintes (atualiza a recorrência)";
                Host?.OpenDialog("Salvar alteração?", $"“{e0.Description}” faz parte de {(e0.IsInstallment ? "um parcelamento" : "uma recorrência")}.",
                    "Salvar", "Cancelar", MainWindow.DialogKind.Info, false, ok => { if (ok) Apply(Host.DialogChecked); }, check);
                return true;
            }
            Apply(false);
            return true;
        }

        // ----- duplicate / delete -----

        private void DuplicateTx(FinanceTransaction t)
        {
            var copy = FinanceService.Duplicate(D, t);
            D.Transactions.Remove(copy); // only a prefill: nothing is saved until "Salvar"
            copy.Date = DateTime.Today;
            copy.Paid = true;
            OpenTxForm(null, copy);
            TxDate.SelectedDate = DateTime.Today;
        }

        private void TxDuplicateForm_Click(object sender, RoutedEventArgs e) { if (_editing != null) DuplicateTx(_editing); }
        private void TxDeleteForm_Click(object sender, RoutedEventArgs e) { if (_editing != null) RequestDeleteTx(_editing); }

        internal void RequestDeleteTx(FinanceTransaction t)
        {
            int following = FinanceService.Series(D, t, true).Count - 1;
            string check = t.IsInstallment && following > 0
                ? $"Excluir também as {following} parcela{(following == 1 ? "" : "s")} seguinte{(following == 1 ? "" : "s")}"
                : t.IsRecurring ? "Excluir também os meses seguintes e encerrar a recorrência" : null;
            Host?.OpenDialog("Excluir lançamento?",
                $"“{t.Description}” ({Money.Format(t.Amount)}) será excluído. Logo depois, você ainda pode desfazer pelo aviso no rodapé.",
                "Excluir", "Cancelar", MainWindow.DialogKind.Danger, false, ok =>
                {
                    if (!ok) return;
                    DeleteTx(t, Host.DialogChecked);
                }, check);
        }

        internal void DeleteTx(FinanceTransaction t, bool following)
        {
            var before = Snapshot();
            int n = FinanceService.Delete(D, t, following);
            if (_editing == t) ClosePanel();
            Commit();
            Host?.ShowSnack(n == 1 ? "Lançamento excluído" : $"{n} lançamentos excluídos", "Desfazer", () => { Restore(before); Commit(); });
        }

        // ================= accounts =================

        private void ShowArchived_Changed(object sender, RoutedEventArgs e) { if (AccList != null) BuildAccounts(); }

        private void BuildAccounts()
        {
            AccList.Children.Clear();
            var end = FinanceService.MonthEnd(_month);
            foreach (var a in D.Accounts.Where(a => ShowArchived.IsChecked == true || !a.Archived).OrderBy(a => a.Archived).ThenBy(a => a.CreatedAt))
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
                money.Children.Add(new TextBlock { Text = $"Previsto em {end:dd/MM}: {Money.Format(proj)}", Style = Ui.St("MutedItalic"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 0, 0) });
                Grid.SetColumn(money, 2);
                grid.Children.Add(money);

                var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                actions.Children.Add(IconBtn("I.Scale", "Conferir saldo", (s, e) => OpenCheckForm(a)));
                actions.Children.Add(IconBtn("I.Archive", a.Archived ? "Desarquivar" : "Arquivar", (s, e) => ToggleArchive(a)));
                actions.Children.Add(IconBtn("I.Edit", "Editar", (s, e) => OpenAccForm(a)));
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
                AccList.Children.Add(new TextBlock { Text = "Nenhuma conta. Crie uma em “Nova conta”.", Style = Ui.St("MutedItalic"), FontSize = 13, Margin = new Thickness(0, 12, 0, 0) });
        }

        private void ToggleArchive(Account a)
        {
            a.Archived = !a.Archived;
            if (a.Archived && D.LastAccountId == a.Id) D.LastAccountId = D.Accounts.FirstOrDefault(x => !x.Archived)?.Id;
            Commit();
            Host?.ShowSnack(a.Archived ? $"“{a.Name}” arquivada" : $"“{a.Name}” desarquivada", a.Archived ? "Desfazer" : null,
                a.Archived ? () => { a.Archived = false; Commit(); } : null);
        }

        private void NewAccount_Click(object sender, RoutedEventArgs e) => OpenAccForm(null);

        internal void OpenAccForm(Account a)
        {
            _editingAcc = a;
            AccFormTitle.Text = a == null ? "Nova conta" : "Editar conta";
            AccName.Text = a?.Name ?? "";
            Fill(AccType, Enum.GetValues<AccountType>().Select(t => (((int)t).ToString(), TypeName(t))), ((int)(a?.Type ?? AccountType.Checking)).ToString());
            AccInitial.Text = a == null ? "" : Money.Plain(a.InitialBalance);
            AccArchived.IsChecked = a?.Archived ?? false;
            AccArchiveRow.Visibility = a == null ? Visibility.Collapsed : Visibility.Visible;
            AccDeleteBtn.Visibility = a != null && !FinanceService.HasTransactions(D, a) && D.Recurrences.All(r => r.AccountId != a.Id && r.ToAccountId != a.Id)
                ? Visibility.Visible : Visibility.Collapsed;
            AccError.Visibility = Visibility.Collapsed;
            _accColor = a?.Color ?? Colors[D.Accounts.Count % Colors.Length];
            BuildSwatches(AccColors, () => _accColor, c => _accColor = c);
            ShowPanel(AccForm);
            Dispatcher.BeginInvoke(new Action(() => AccName.Focus()), DispatcherPriority.Input);
        }

        private void AccForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { AccSave_Click(null, null); e.Handled = true; }
        }

        private void AccSave_Click(object sender, RoutedEventArgs e)
        {
            var name = AccName.Text.Trim();
            if (name.Length == 0) { AccError.Text = "Dê um nome à conta."; AccError.Visibility = Visibility.Visible; AccName.Focus(); return; }
            decimal initial = 0;
            if (AccInitial.Text.Trim().Length > 0 && !Money.TryParse(AccInitial.Text, out initial))
            { AccError.Text = "Saldo inicial inválido (ex.: 1.234,56 ou -200)."; AccError.Visibility = Visibility.Visible; AccInitial.Focus(); return; }
            var a = _editingAcc ?? new Account();
            a.Name = name;
            a.Type = (AccountType)int.Parse(Sel(AccType) ?? "0");
            a.InitialBalance = initial;
            a.Color = _accColor;
            a.Icon = a.Type.ToString();
            if (_editingAcc == null) { D.Accounts.Add(a); D.LastAccountId ??= a.Id; }
            else a.Archived = AccArchived.IsChecked == true;
            ClosePanel();
            Commit();
        }

        private void AccDelete_Click(object sender, RoutedEventArgs e)
        {
            var a = _editingAcc;
            if (a == null) return;
            Host?.OpenDialog("Excluir conta?", $"“{a.Name}” não tem lançamentos e será excluída.", "Excluir", "Cancelar", MainWindow.DialogKind.Danger, false, ok =>
            {
                if (!ok) return;
                D.Accounts.Remove(a);
                if (D.LastAccountId == a.Id) D.LastAccountId = D.Accounts.FirstOrDefault(x => !x.Archived)?.Id;
                ClosePanel();
                Commit();
            });
        }

        // ----- check balance -----

        internal void OpenCheckForm(Account a)
        {
            _checking = a;
            CheckAccount.Text = a.Name;
            CheckCurrent.Text = "Saldo no Helmet Desk: " + Money.Format(FinanceService.Balance(D, a));
            CheckReal.Text = "";
            CheckReal_Changed(null, null);
            ShowPanel(CheckForm);
            Dispatcher.BeginInvoke(new Action(() => CheckReal.Focus()), DispatcherPriority.Input);
        }

        private void CheckReal_Changed(object sender, TextChangedEventArgs e)
        {
            if (_checking == null || CheckDiff == null) return;
            if (!Money.TryParse(CheckReal.Text, out var real)) { CheckDiff.Text = "Digite o saldo que aparece no banco ou na carteira."; CheckSaveBtn.IsEnabled = false; return; }
            var diff = real - FinanceService.Balance(D, _checking);
            CheckSaveBtn.IsEnabled = diff != 0;
            CheckDiff.Text = diff == 0 ? "O saldo já confere. Nada a ajustar."
                : $"Será criado um ajuste de {Money.Signed(diff)} com a data de hoje. Ele muda o saldo, mas não entra em Entradas/Saídas nem no gráfico.";
        }

        private void CheckForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { CheckSave_Click(null, null); e.Handled = true; }
        }

        internal void CheckSave_Click(object sender, RoutedEventArgs e)
        {
            if (_checking == null || !Money.TryParse(CheckReal.Text, out var real)) return;
            var t = FinanceService.Adjust(D, _checking, real, DateTime.Today);
            ClosePanel();
            if (t != null) { Commit(); Host?.ShowSnack("Ajuste criado: " + Money.Signed(t.Kind == TxKind.Income ? t.Amount : -t.Amount), null, null); }
        }

        // ================= recurrences =================

        private void BuildRecurrences()
        {
            RecList.Children.Clear();
            var thisMonth = FinanceService.MonthStart(DateTime.Today);
            bool Ended(Recurrence r) => r.EndMonth.HasValue && FinanceService.MonthStart(r.EndMonth.Value) < thisMonth;
            foreach (var r in D.Recurrences.OrderBy(Ended).ThenBy(r => r.Paused).ThenBy(r => r.DayOfMonth))
            {
                var cat = CategoryOf(r.CategoryId);
                bool ended = Ended(r);
                var grid = new Grid();
                foreach (var w in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
                var badge = IconBadge(Ui.Geo("I.Repeat"), r.Kind == TxKind.Transfer ? "#78B7FF" : cat?.Color ?? "#9A93A8");
                badge.Margin = new Thickness(0, 0, 12, 0);
                grid.Children.Add(badge);

                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                info.Children.Add(new TextBlock { Text = r.Description, FontSize = 14, FontWeight = FontWeights.Medium, TextTrimming = TextTrimming.CharacterEllipsis });
                var pills = new WrapPanel();
                pills.Children.Add(Pill($"Todo dia {r.DayOfMonth}", icon: Ui.Geo("I.Calendar")));
                if (r.Kind != TxKind.Transfer) pills.Children.Add(Pill(cat?.Name ?? "Sem categoria", dot: cat?.Color ?? "#9A93A8"));
                pills.Children.Add(Pill(AccountOf(r.AccountId)?.Name ?? "Sem conta"));
                if (ended) pills.Children.Add(Pill("Encerrada em " + Dates.Cap(r.EndMonth.Value.ToString("MMM/yyyy", Dates.Pt).Replace(".", ""))));
                else if (r.Paused) pills.Children.Add(Pill("Pausada", Ui.Res("Warning"), Ui.Geo("I.Pause")));
                else
                {
                    var next = FinanceService.OccurrenceDate(r, DateTime.Today);
                    if (next < DateTime.Today) next = FinanceService.OccurrenceDate(r, DateTime.Today.AddMonths(1));
                    pills.Children.Add(Pill("Próximo: " + next.ToString("dd/MM"), Ui.Res("Success")));
                    if (r.EndMonth.HasValue) pills.Children.Add(Pill("até " + r.EndMonth.Value.ToString("MM/yyyy")));
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

                StackPanel actions = null;
                if (!ended)
                {
                    actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                    actions.Children.Add(IconBtn(r.Paused ? "I.Play" : "I.Pause", r.Paused ? "Retomar" : "Pausar", (s, e) => { r.Paused = !r.Paused; if (!r.Paused) GenerateMonth(); Commit(); }));
                    actions.Children.Add(IconBtn("I.Stop", "Encerrar", (s, e) => RequestEndRecurrence(r), danger: true));
                    Grid.SetColumn(actions, 3);
                    grid.Children.Add(actions);
                }
                var row = Row(grid, actions);
                row.Padding = new Thickness(12, 10, 8, 10);
                if (ended || r.Paused) row.Opacity = 0.65;
                RecList.Children.Add(row);
            }
            RecEmpty.Visibility = D.Recurrences.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RequestEndRecurrence(Recurrence r)
        {
            var month = DateTime.Today;
            Host?.OpenDialog("Encerrar recorrência?",
                $"“{r.Description}” não vai gerar lançamentos depois de {Dates.MonthTitle(month).ToLower(Dates.Pt)}. Pendentes de meses seguintes que já existirem são removidos; os já lançados ficam.",
                "Encerrar", "Cancelar", MainWindow.DialogKind.Danger, false, ok =>
                {
                    if (!ok) return;
                    var before = Snapshot();
                    FinanceService.EndRecurrence(D, r, month);
                    Commit();
                    Host?.ShowSnack("Recorrência encerrada", "Desfazer", () => { Restore(before); Commit(); });
                });
        }

        // ================= categories =================

        private void BuildCategories()
        {
            CatExpenseList.Children.Clear();
            CatIncomeList.Children.Clear();
            foreach (var c in D.Categories.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
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
                info.Children.Add(new TextBlock { Text = used == 0 ? "Sem lançamentos" : used == 1 ? "1 lançamento" : $"{used} lançamentos", Style = Ui.St("Muted"), Margin = new Thickness(0, 1, 0, 0) });
                Grid.SetColumn(info, 1);
                grid.Children.Add(info);
                var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                actions.Children.Add(IconBtn("I.Edit", "Editar", (s, e) => OpenCatForm(c)));
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
            _editingCat = c;
            CatFormTitle.Text = c == null ? "Nova categoria" : "Editar categoria";
            CatName.Text = c?.Name ?? "";
            (c?.Kind == CategoryKind.Income ? CatKindIncome : CatKindExpense).IsChecked = true;
            CatError.Visibility = Visibility.Collapsed;
            _catColor = c?.Color ?? Colors[D.Categories.Count % Colors.Length];
            _catIcon = string.IsNullOrEmpty(c?.Icon) ? "dots" : c.Icon;
            BuildSwatches(CatColors, () => _catColor, v => { _catColor = v; BuildIconChoices(); });
            BuildIconChoices();

            CatDeleteBox.Visibility = c == null ? Visibility.Collapsed : Visibility.Visible;
            if (c != null)
            {
                int used = FinanceService.UsageCount(D, c);
                CatUsage.Text = used == 0 ? "Esta categoria não está em uso." : $"Em uso por {used} lançamento{(used == 1 ? "" : "s")} ou recorrência{(used == 1 ? "" : "s")}.";
                CatMoveField.Visibility = used > 0 ? Visibility.Visible : Visibility.Collapsed;
                var others = D.Categories.Where(x => x != c).OrderBy(x => x.Kind != c.Kind).ThenBy(x => x.Name)
                    .Select(x => (x.Id, x.Name + (x.Kind != c.Kind ? (x.Kind == CategoryKind.Income ? " (receita)" : " (despesa)") : ""))).ToList();
                Fill(CatMoveTo, others, others.FirstOrDefault(o => o.Item2 == "Outros").Id ?? others.FirstOrDefault().Id);
            }
            ShowPanel(CatForm);
            Dispatcher.BeginInvoke(new Action(() => CatName.Focus()), DispatcherPriority.Input);
        }

        private void BuildIconChoices()
        {
            CatIcons.Children.Clear();
            foreach (var key in CategoryIcons)
            {
                bool on = key == _catIcon;
                var b = new Button
                {
                    Style = Ui.St("IconButton"), Padding = new Thickness(7), Margin = new Thickness(0, 0, 6, 6), ToolTip = key,
                    Background = on ? Tint(_catColor, 0x40) : Brushes.Transparent,
                    BorderBrush = on ? Palette.BrushFrom(_catColor) : Brushes.Transparent,
                    Content = new Icon { Data = CatGeo(key), Width = 16, Height = 16, Foreground = on ? Palette.BrushFrom(_catColor) : Ui.Res("Text2") }
                };
                b.Click += (s, e) => { _catIcon = key; BuildIconChoices(); };
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
            var name = CatName.Text.Trim();
            if (name.Length == 0) { CatError.Text = "Dê um nome à categoria."; CatError.Visibility = Visibility.Visible; CatName.Focus(); return; }
            var kind = CatKindIncome.IsChecked == true ? CategoryKind.Income : CategoryKind.Expense;
            if (D.Categories.Any(x => x != _editingCat && x.Kind == kind && string.Equals(x.Name, name, StringComparison.CurrentCultureIgnoreCase)))
            { CatError.Text = "Já existe uma categoria com esse nome."; CatError.Visibility = Visibility.Visible; CatName.Focus(); return; }
            var c = _editingCat ?? new Category();
            c.Name = name; c.Kind = kind; c.Color = _catColor; c.Icon = _catIcon;
            if (_editingCat == null) D.Categories.Add(c);
            ClosePanel();
            Commit();
        }

        internal void CatDelete_Click(object sender, RoutedEventArgs e)
        {
            var c = _editingCat;
            if (c == null) return;
            int used = FinanceService.UsageCount(D, c);
            var moveTo = used > 0 ? CategoryOf(Sel(CatMoveTo)) : null;
            if (used > 0 && moveTo == null) return;
            Host?.OpenDialog("Excluir categoria?",
                used > 0 ? $"“{c.Name}” será excluída e {used} lançamento{(used == 1 ? "" : "s")} passa{(used == 1 ? "" : "m")} para “{moveTo.Name}”." : $"“{c.Name}” será excluída.",
                "Excluir", "Cancelar", MainWindow.DialogKind.Danger, false, ok =>
                {
                    if (!ok) return;
                    var before = Snapshot();
                    FinanceService.DeleteCategory(D, c, moveTo);
                    ClosePanel();
                    Commit();
                    Host?.ShowSnack("Categoria excluída", "Desfazer", () => { Restore(before); Commit(); });
                });
        }
    }
}
