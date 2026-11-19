using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HelmetDesk.Core.Finance;

namespace HelmetDesk
{
    /// <summary>
    /// "Estatísticas" tab: summary of a period ending at the selected month (with the change against the previous
    /// period), a ring chart per category (expenses or income, click a slice for its largest transactions) and the
    /// month-by-month bars. The period, the selection and the numbers come from <see cref="FinancePanelViewModel"/> and
    /// <see cref="FinanceService"/>; this file only draws them.
    /// </summary>
    public partial class FinanceView
    {
        private bool _statsBusy;

        private static string Mon(DateTime m, bool year = false) =>
            Dates.Cap(m.ToString(year ? "MMM/yy" : "MMM", Dates.Culture).Replace(".", ""));

        private void Period_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized || StatsTab == null) return;
            Vm.Period = int.Parse((string)((FrameworkElement)sender).Tag);
            if (Vm.Tab == "stats" && !_statsBusy) BuildStats();
        }

        private void PieKind_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized || StatsTab == null) return;
            Vm.PieKind = sender == PieIncome ? TxKind.Income : TxKind.Expense;
            if (Vm.Tab == "stats" && !_statsBusy) BuildStats();
        }

        /// <summary>For the self-test and snapshots: 1, 3, 6, 12 or 0 (year).</summary>
        internal void SetPeriod(int months)
        {
            _statsBusy = true;
            (months switch { 3 => Period3, 6 => Period6, 12 => Period12, 0 => PeriodYear, _ => Period1 }).IsChecked = true;
            _statsBusy = false;
            Vm.Period = months;
            if (Vm.Tab == "stats") BuildStats();
        }

        internal void SetPieKind(TxKind kind)
        {
            _statsBusy = true;
            (kind == TxKind.Income ? PieIncome : PieExpense).IsChecked = true;
            _statsBusy = false;
            Vm.PieKind = kind;
            if (Vm.Tab == "stats") BuildStats();
        }

        internal void SelectPieSlice(int index)
        {
            Pie.Selected = index;
            OnPieSelected(index);
        }

        internal int PieSliceCount => Vm.PieData.Count;

        // ================= build =================

        private void BuildStats()
        {
            var (from, months) = Vm.StatsPeriod();
            var prev = Vm.PreviousPeriod();
            var now = FinanceService.Totals(D, from, months);
            var before = FinanceService.Totals(D, prev.from, prev.months);
            var last = from.AddMonths(months - 1);

            string vs = Vm.Period == 1 ? TF("vs {0}", Dates.MonthTitle(prev.from).ToLower(Dates.Culture))
                      : Vm.Period == 0 ? TF("vs mesmo período de {0}", prev.from.Year)
                      : TF("vs {0} meses anteriores", months);
            StatsRange.Text = months == 1 ? Dates.MonthTitle(from)
                : (from.Year == last.Year ? TF("{0} – {1} de {2}", Mon(from), Mon(last), last.Year) : $"{Mon(from, true)} – {Mon(last, true)}")
                  + " · " + TF("{0} meses", months);

            StIncome.Text = Money.Format(now.Income);
            Delta(StIncomeDelta, before.Income, now.Income, vs, upIsGood: true);
            StExpense.Text = Money.Format(now.Expense);
            Delta(StExpenseDelta, before.Expense, now.Expense, vs, upIsGood: false);
            StNet.Text = Money.Format(now.Net);
            StNet.Foreground = now.Net < 0 ? Ui.Res("Danger") : Ui.Res("Text");
            Delta(StNetDelta, before.Net, now.Net, vs, upIsGood: true);

            StRate.Text = now.SavingsRate is double r ? r.ToString("P0", Dates.Culture) : "—";
            StRate.Foreground = now.SavingsRate < 0 ? Ui.Res("Danger") : Ui.Res("Text");
            if (now.SavingsRate is double rn && before.SavingsRate is double rb)
            {
                double pp = Math.Round((rn - rb) * 100);
                StRateDelta.Text = pp == 0 ? "= " + vs : $"{(pp > 0 ? "▲" : "▼")} {Math.Abs(pp):0} {T("p.p.")} {vs}";
                StRateDelta.Foreground = pp == 0 ? Ui.Res("Text2") : pp > 0 ? Ui.Res("Success") : Ui.Res("Danger");
            }
            else
            {
                StRateDelta.Text = now.SavingsRate == null ? T("Sem ganhos no período") : T("Sem base para comparar");
                StRateDelta.Foreground = Ui.Res("Text2");
            }

            BuildPie();
            BuildBars();
        }

        private static void Delta(TextBlock tb, decimal before, decimal now, string vs, bool upIsGood)
        {
            var ch = FinanceService.Change(before, now);
            if (ch == null)
            {
                tb.Text = now == 0 ? T("Nada no período") : T("Sem dados para comparar");
                tb.Foreground = Ui.Res("Text2");
                return;
            }
            double c = ch.Value;
            if (Math.Abs(c) < 0.005)
            {
                tb.Text = "= " + vs;
                tb.Foreground = Ui.Res("Text2");
                return;
            }
            tb.Text = $"{(c > 0 ? "▲" : "▼")} {Math.Abs(c).ToString("P0", Dates.Culture)} {vs}";
            tb.Foreground = (c > 0) == upIsGood ? Ui.Res("Success") : Ui.Res("Danger");
        }

        // ================= ring chart =================

        private void BuildPie()
        {
            bool income = Vm.PieKind == TxKind.Income;
            PieTitle.Text = income ? T("Ganhos por categoria") : T("Gastos por categoria");
            Vm.LoadPie();
            var data = Vm.PieData;
            decimal total = data.Sum(c => c.Total);

            Pie.SliceClicked -= OnPieSelected;
            Pie.SliceClicked += OnPieSelected;
            Pie.SetSlices(data.Select(c =>
            {
                var cat = CategoryOf(c.CategoryId);
                double pct = total > 0 ? (double)(c.Total / total) : 0;
                return new DonutChart.Slice((double)c.Total, Palette.BrushFrom(cat?.Color ?? "#9A93A8"),
                    $"{cat?.Name ?? T("Sem categoria")}: {Money.Format(c.Total)} ({pct.ToString("P0", Dates.Culture)})");
            }), animate: true);

            PieLegend.Children.Clear();
            if (data.Count == 0)
                PieLegend.Children.Add(new TextBlock
                {
                    Text = income ? T("Nenhum ganho neste período.") : T("Nenhum gasto neste período."),
                    Style = Ui.St("MutedItalic"), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap,
                });
            for (int i = 0; i < data.Count; i++)
                PieLegend.Children.Add(LegendRow(i, total));

            UpdatePieCenter();
            BuildTopList();
        }

        private Border LegendRow(int index, decimal total)
        {
            var ct = Vm.PieData[index];
            var cat = CategoryOf(ct.CategoryId);
            double pct = total > 0 ? (double)(ct.Total / total) : 0;
            var row = new DockPanel();
            var value = new TextBlock { Text = Money.Format(ct.Total), FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(value, Dock.Right);
            row.Children.Add(value);
            var p = new TextBlock { Text = pct.ToString("P0", Dates.Culture), Style = Ui.St("Muted"), Width = 38, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(p, Dock.Right);
            row.Children.Add(p);
            row.Children.Add(new Border
            {
                Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 8, 0),
                Background = Palette.BrushFrom(cat?.Color ?? "#9A93A8"), VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock { Text = cat?.Name ?? T("Sem categoria"), FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });

            var b = new Border { Child = row, Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = Cursors.Hand, Tag = index };
            b.MouseEnter += (s, e) => { if (Vm.PieSelected != index) b.Background = Ui.Res("AccentSoft"); };
            b.MouseLeave += (s, e) => { if (Vm.PieSelected != index) b.Background = Brushes.Transparent; };
            b.MouseLeftButtonUp += (s, e) =>
            {
                int sel = Vm.PieSelected == index ? -1 : index;
                Pie.Selected = sel;
                OnPieSelected(sel);
            };
            return b;
        }

        private void OnPieSelected(int index)
        {
            Vm.PieSelected = index;
            foreach (Border b in PieLegend.Children.OfType<Border>())
            {
                bool on = (int)b.Tag == index;
                b.Background = on ? Ui.Res("AccentSoft") : Brushes.Transparent;
                b.BorderBrush = on ? Ui.Res("Accent") : null;
                b.BorderThickness = new Thickness(on ? 1 : 0);
                b.Padding = on ? new Thickness(7, 5, 7, 5) : new Thickness(8, 6, 8, 6);
            }
            UpdatePieCenter();
            BuildTopList();
        }

        private void UpdatePieCenter()
        {
            var data = Vm.PieData;
            decimal total = data.Sum(c => c.Total);
            if (Vm.HasPieSelection)
            {
                var ct = data[Vm.PieSelected];
                PieCenterLabel.Text = CategoryOf(ct.CategoryId)?.Name ?? T("Sem categoria");
                PieCenterValue.Text = Money.Format(ct.Total);
                PieCenterPct.Text = total > 0 ? ((double)(ct.Total / total)).ToString("P0", Dates.Culture) + T(" do total") : "";
            }
            else
            {
                PieCenterLabel.Text = T("Total");
                PieCenterValue.Text = Money.Format(total);
                PieCenterPct.Text = data.Count switch { 0 => "", var n => TN(n, "{0} categoria", "{0} categorias") };
            }
        }

        // ================= largest transactions =================

        private void BuildTopList()
        {
            bool income = Vm.PieKind == TxKind.Income;
            bool byCat = Vm.HasPieSelection;
            string catId = Vm.SelectedPieCategoryId;
            var list = Vm.TopTransactions();

            TopTitle.Text = income ? T("Maiores ganhos") : T("Maiores gastos");
            TopMeta.Text = byCat ? CategoryOf(catId)?.Name ?? T("Sem categoria") : T("Todas as categorias");
            TopList.Children.Clear();
            if (list.Count == 0)
            {
                TopList.Children.Add(new TextBlock { Text = T("Nada neste período."), Style = Ui.St("MutedItalic"), Margin = new Thickness(0, 4, 0, 0) });
                return;
            }
            foreach (var t in list) TopList.Children.Add(TopRow(t, showCategory: !byCat));
        }

        private Border TopRow(FinanceTransaction t, bool showCategory)
        {
            var cat = CategoryOf(t.CategoryId);
            var row = new DockPanel();
            var value = new TextBlock
            {
                Text = (t.Kind == TxKind.Income ? "+" : "-") + Money.Format(t.Amount), FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = t.Kind == TxKind.Income ? Ui.Res("Success") : Ui.Res("Text"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
            };
            DockPanel.SetDock(value, Dock.Right);
            row.Children.Add(value);
            var badge = new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 10, 0),
                Background = Palette.BrushFrom(cat?.Color ?? "#9A93A8"), VerticalAlignment = VerticalAlignment.Center,
                Child = new Icon { Data = CatGeo(cat?.Icon), Foreground = Ui.Res("OnAccent"), Width = 14, Height = 14 },
            };
            row.Children.Add(badge);
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = t.Description, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis });
            string meta = t.Date.ToString(T("dd/MM/yyyy"), Dates.Culture);
            if (showCategory) meta += " · " + (cat?.Name ?? T("Sem categoria"));
            if (!t.Paid) meta += " · " + T("pendente");
            text.Children.Add(new TextBlock { Text = meta, Style = Ui.St("Muted"), FontSize = 11, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(text);

            var b = new Border { Child = row, Padding = new Thickness(6, 6, 6, 6), Margin = new Thickness(-6, 0, -6, 2), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = T("Abrir lançamento") };
            b.MouseEnter += (s, e) => b.Background = Ui.Res("AccentSoft");
            b.MouseLeave += (s, e) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonUp += (s, e) => OpenTxForm(t);
            return b;
        }

        // ================= month bars =================

        private void BuildBars()
        {
            var months = Vm.StatsPeriod().months;
            var (start, n) = Vm.BarsRange(); // short periods still get some history to compare with
            bool years = start.Year != Vm.Month.Year;
            BarsMeta.Text = n == months ? T("Ganhos, gastos e saldo de cada mês do período") : TF("Ganhos, gastos e saldo · últimos {0} meses", n);
            Bars.SetData(FinanceService.Monthly(D, start, n).Select(m => new MonthBarChart.Month(
                Mon(m.Month, years), m.Income, m.Expense,
                Dates.MonthTitle(m.Month) + "\n" + TF("Ganhos: {0}", Money.Format(m.Income)) + "\n" + TF("Gastos: {0}", Money.Format(m.Expense)) + "\n" + TF("Saldo: {0}", Money.Format(m.Income - m.Expense)))), animate: true);
        }

        // ================= layout =================

        /// <summary>Narrow: 2×2 summary, largest-transactions card under the ring chart.</summary>
        private void StatsLayout(double w)
        {
            if (StatsSumGrid == null) return;
            bool narrow = w < 900;
            StatsSumGrid.Columns = w < 640 ? 2 : 4;
            Grid.SetColumnSpan(DonutCard, narrow ? 3 : 1);
            Grid.SetRow(TopCard, narrow ? 1 : 0);
            Grid.SetColumn(TopCard, narrow ? 0 : 2);
            Grid.SetColumnSpan(TopCard, narrow ? 3 : 1);
            TopCard.Margin = narrow ? new Thickness(0, 16, 0, 0) : new Thickness(0);
        }
    }
}
