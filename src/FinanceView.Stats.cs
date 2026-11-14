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
    /// month-by-month bars. All numbers come from <see cref="FinanceService"/>; this file only draws them.
    /// </summary>
    public partial class FinanceView
    {
        private int _period = 1;                 // months; 0 = year to date
        private TxKind _pieKind = TxKind.Expense;
        private List<CategoryTotal> _pieData = new();
        private int _pieSel = -1;
        private bool _statsBusy;

        /// <summary>First month and length of the selected period (it always ends at the selected month).</summary>
        private (DateTime from, int months) StatsPeriod() =>
            _period == 0 ? (new DateTime(_month.Year, 1, 1), _month.Month) : (_month.AddMonths(1 - _period), _period);

        private (DateTime from, int months) PreviousPeriod()
        {
            var (from, months) = StatsPeriod();
            return _period == 0 ? (from.AddYears(-1), months) : (from.AddMonths(-months), months);
        }

        private static string Mon(DateTime m, bool year = false) =>
            Dates.Cap(m.ToString(year ? "MMM/yy" : "MMM", Dates.Pt).Replace(".", ""));

        private void Period_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized || StatsTab == null) return;
            _period = int.Parse((string)((FrameworkElement)sender).Tag);
            if (_tab == "stats" && !_statsBusy) BuildStats();
        }

        private void PieKind_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsInitialized || StatsTab == null) return;
            _pieKind = sender == PieIncome ? TxKind.Income : TxKind.Expense;
            if (_tab == "stats" && !_statsBusy) BuildStats();
        }

        /// <summary>For the self-test and snapshots: 1, 3, 6, 12 or 0 (year).</summary>
        internal void SetPeriod(int months)
        {
            _statsBusy = true;
            (months switch { 3 => Period3, 6 => Period6, 12 => Period12, 0 => PeriodYear, _ => Period1 }).IsChecked = true;
            _statsBusy = false;
            _period = months;
            if (_tab == "stats") BuildStats();
        }

        internal void SetPieKind(TxKind kind)
        {
            _statsBusy = true;
            (kind == TxKind.Income ? PieIncome : PieExpense).IsChecked = true;
            _statsBusy = false;
            _pieKind = kind;
            if (_tab == "stats") BuildStats();
        }

        internal void SelectPieSlice(int index)
        {
            Pie.Selected = index;
            OnPieSelected(index);
        }

        internal int PieSliceCount => _pieData.Count;

        // ================= build =================

        private void BuildStats()
        {
            var (from, months) = StatsPeriod();
            var prev = PreviousPeriod();
            var now = FinanceService.Totals(D, from, months);
            var before = FinanceService.Totals(D, prev.from, prev.months);
            var last = from.AddMonths(months - 1);

            string vs = _period == 1 ? "vs " + Dates.MonthTitle(prev.from).ToLower(Dates.Pt)
                      : _period == 0 ? "vs mesmo período de " + prev.from.Year
                      : "vs " + months + " meses anteriores";
            StatsRange.Text = months == 1 ? Dates.MonthTitle(from)
                : (from.Year == last.Year ? $"{Mon(from)} – {Mon(last)} de {last.Year}" : $"{Mon(from, true)} – {Mon(last, true)}")
                  + $" · {months} meses";

            StIncome.Text = Money.Format(now.Income);
            Delta(StIncomeDelta, before.Income, now.Income, vs, upIsGood: true);
            StExpense.Text = Money.Format(now.Expense);
            Delta(StExpenseDelta, before.Expense, now.Expense, vs, upIsGood: false);
            StNet.Text = Money.Format(now.Net);
            StNet.Foreground = now.Net < 0 ? Ui.Res("Danger") : Ui.Res("Text");
            Delta(StNetDelta, before.Net, now.Net, vs, upIsGood: true);

            StRate.Text = now.SavingsRate is double r ? r.ToString("P0", Dates.Pt) : "—";
            StRate.Foreground = now.SavingsRate < 0 ? Ui.Res("Danger") : Ui.Res("Text");
            if (now.SavingsRate is double rn && before.SavingsRate is double rb)
            {
                double pp = Math.Round((rn - rb) * 100);
                StRateDelta.Text = pp == 0 ? "= " + vs : $"{(pp > 0 ? "▲" : "▼")} {Math.Abs(pp):0} p.p. {vs}";
                StRateDelta.Foreground = pp == 0 ? Ui.Res("Text2") : pp > 0 ? Ui.Res("Success") : Ui.Res("Danger");
            }
            else
            {
                StRateDelta.Text = now.SavingsRate == null ? "Sem ganhos no período" : "Sem base para comparar";
                StRateDelta.Foreground = Ui.Res("Text2");
            }

            BuildPie(from, months);
            BuildBars();
        }

        private static void Delta(TextBlock tb, decimal before, decimal now, string vs, bool upIsGood)
        {
            var ch = FinanceService.Change(before, now);
            if (ch == null)
            {
                tb.Text = now == 0 ? "Nada no período" : "Sem dados para comparar";
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
            tb.Text = $"{(c > 0 ? "▲" : "▼")} {Math.Abs(c).ToString("P0", Dates.Pt)} {vs}";
            tb.Foreground = (c > 0) == upIsGood ? Ui.Res("Success") : Ui.Res("Danger");
        }

        // ================= ring chart =================

        private void BuildPie(DateTime from, int months)
        {
            bool income = _pieKind == TxKind.Income;
            PieTitle.Text = income ? "Ganhos por categoria" : "Gastos por categoria";
            _pieData = FinanceService.ByCategory(D, _pieKind, from, months);
            _pieSel = -1;
            decimal total = _pieData.Sum(c => c.Total);

            Pie.SliceClicked -= OnPieSelected;
            Pie.SliceClicked += OnPieSelected;
            Pie.SetSlices(_pieData.Select(c =>
            {
                var cat = CategoryOf(c.CategoryId);
                double pct = total > 0 ? (double)(c.Total / total) : 0;
                return new DonutChart.Slice((double)c.Total, Palette.BrushFrom(cat?.Color ?? "#9A93A8"),
                    $"{cat?.Name ?? "Sem categoria"}: {Money.Format(c.Total)} ({pct.ToString("P0", Dates.Pt)})");
            }), animate: true);

            PieLegend.Children.Clear();
            if (_pieData.Count == 0)
                PieLegend.Children.Add(new TextBlock
                {
                    Text = income ? "Nenhum ganho neste período." : "Nenhum gasto neste período.",
                    Style = Ui.St("MutedItalic"), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap,
                });
            for (int i = 0; i < _pieData.Count; i++)
                PieLegend.Children.Add(LegendRow(i, total));

            UpdatePieCenter();
            BuildTopList();
        }

        private Border LegendRow(int index, decimal total)
        {
            var ct = _pieData[index];
            var cat = CategoryOf(ct.CategoryId);
            double pct = total > 0 ? (double)(ct.Total / total) : 0;
            var row = new DockPanel();
            var value = new TextBlock { Text = Money.Format(ct.Total), FontSize = 12.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            DockPanel.SetDock(value, Dock.Right);
            row.Children.Add(value);
            var p = new TextBlock { Text = pct.ToString("P0", Dates.Pt), Style = Ui.St("Muted"), Width = 38, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(p, Dock.Right);
            row.Children.Add(p);
            row.Children.Add(new Border
            {
                Width = 10, Height = 10, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 8, 0),
                Background = Palette.BrushFrom(cat?.Color ?? "#9A93A8"), VerticalAlignment = VerticalAlignment.Center,
            });
            row.Children.Add(new TextBlock { Text = cat?.Name ?? "Sem categoria", FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });

            var b = new Border { Child = row, Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent, Cursor = Cursors.Hand, Tag = index };
            b.MouseEnter += (s, e) => { if (_pieSel != index) b.Background = Ui.Res("AccentSoft"); };
            b.MouseLeave += (s, e) => { if (_pieSel != index) b.Background = Brushes.Transparent; };
            b.MouseLeftButtonUp += (s, e) =>
            {
                int sel = _pieSel == index ? -1 : index;
                Pie.Selected = sel;
                OnPieSelected(sel);
            };
            return b;
        }

        private void OnPieSelected(int index)
        {
            _pieSel = index;
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
            decimal total = _pieData.Sum(c => c.Total);
            if (_pieSel >= 0 && _pieSel < _pieData.Count)
            {
                var ct = _pieData[_pieSel];
                PieCenterLabel.Text = CategoryOf(ct.CategoryId)?.Name ?? "Sem categoria";
                PieCenterValue.Text = Money.Format(ct.Total);
                PieCenterPct.Text = total > 0 ? ((double)(ct.Total / total)).ToString("P0", Dates.Pt) + " do total" : "";
            }
            else
            {
                PieCenterLabel.Text = "Total";
                PieCenterValue.Text = Money.Format(total);
                PieCenterPct.Text = _pieData.Count switch { 0 => "", 1 => "1 categoria", var n => n + " categorias" };
            }
        }

        // ================= largest transactions =================

        private void BuildTopList()
        {
            var (from, months) = StatsPeriod();
            bool income = _pieKind == TxKind.Income;
            bool byCat = _pieSel >= 0 && _pieSel < _pieData.Count;
            string catId = byCat ? _pieData[_pieSel].CategoryId : null;
            var list = FinanceService.Largest(D, _pieKind, from, months, 6, byCat, catId);

            TopTitle.Text = income ? "Maiores ganhos" : "Maiores gastos";
            TopMeta.Text = byCat ? CategoryOf(catId)?.Name ?? "Sem categoria" : "Todas as categorias";
            TopList.Children.Clear();
            if (list.Count == 0)
            {
                TopList.Children.Add(new TextBlock { Text = "Nada neste período.", Style = Ui.St("MutedItalic"), Margin = new Thickness(0, 4, 0, 0) });
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
            string meta = t.Date.ToString("dd/MM/yyyy", Dates.Pt);
            if (showCategory) meta += " · " + (cat?.Name ?? "Sem categoria");
            if (!t.Paid) meta += " · pendente";
            text.Children.Add(new TextBlock { Text = meta, Style = Ui.St("Muted"), FontSize = 11, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(text);

            var b = new Border { Child = row, Padding = new Thickness(6, 6, 6, 6), Margin = new Thickness(-6, 0, -6, 2), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = "Abrir lançamento" };
            b.MouseEnter += (s, e) => b.Background = Ui.Res("AccentSoft");
            b.MouseLeave += (s, e) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonUp += (s, e) => OpenTxForm(t);
            return b;
        }

        // ================= month bars =================

        private void BuildBars()
        {
            var (from, months) = StatsPeriod();
            int n = Math.Max(months, 6); // short periods still get some history to compare with
            var start = _month.AddMonths(1 - n);
            bool years = start.Year != _month.Year;
            BarsMeta.Text = n == months ? "Ganhos, gastos e saldo de cada mês do período" : $"Ganhos, gastos e saldo · últimos {n} meses";
            Bars.SetData(FinanceService.Monthly(D, start, n).Select(m => new MonthBarChart.Month(
                Mon(m.Month, years), m.Income, m.Expense,
                $"{Dates.MonthTitle(m.Month)}\nGanhos: {Money.Format(m.Income)}\nGastos: {Money.Format(m.Expense)}\nSaldo: {Money.Format(m.Income - m.Expense)}")), animate: true);
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
