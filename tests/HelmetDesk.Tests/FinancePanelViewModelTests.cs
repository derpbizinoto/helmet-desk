using System;
using System.Collections.Generic;
using System.Linq;
using HelmetDesk.Core.Finance;
using Xunit;

namespace HelmetDesk.Tests
{
    public class FinancePanelViewModelTests
    {
        private static readonly DateTime Today = new(2026, 3, 15);

        private static (FakeFinanceHost host, FinancePanelViewModel vm, Account a, Account b, Category food, Category salary) Setup()
        {
            var host = new FakeFinanceHost();
            FinanceService.Seed(host.Data);
            var a = host.Data.Accounts[0];
            var b = new Account { Name = "Banco", Type = AccountType.Checking };
            host.Data.Accounts.Add(b);
            var vm = new FinancePanelViewModel(() => host.Data, host, () => Today);
            return (host, vm, a, b, host.Data.Categories.First(c => c.Name == "Alimentação"), host.Data.Categories.First(c => c.Name == "Salário"));
        }

        private static FinanceTransaction Tx(FinanceData d, TxKind kind, decimal amount, DateTime date, bool paid, string account, string cat = null, string to = null)
        {
            var t = new FinanceTransaction { Kind = kind, Amount = amount, Date = date, Paid = paid, AccountId = account, ToAccountId = to, CategoryId = cat, Description = "x" };
            d.Transactions.Add(t);
            return t;
        }

        [Fact]
        public void Starts_on_the_current_month_and_navigates()
        {
            var (_, vm, _, _, _, _) = Setup();
            Assert.Equal(new DateTime(2026, 3, 1), vm.Month);
            Assert.True(vm.IsThisMonth);
            vm.PrevMonth();
            Assert.Equal(new DateTime(2026, 2, 1), vm.Month);
            Assert.False(vm.IsThisMonth);
            vm.NextMonth(); vm.NextMonth();
            Assert.Equal(new DateTime(2026, 4, 1), vm.Month);
            Assert.Equal(new DateTime(2026, 4, 30), vm.MonthEnd);
            vm.ThisMonth();
            Assert.Equal(new DateTime(2026, 3, 1), vm.Month);
        }

        [Fact]
        public void Opening_a_month_generates_its_recurring_bills_and_tells_the_host_only_then()
        {
            var (host, vm, a, _, food, _) = Setup();
            host.Data.Recurrences.Add(new Recurrence { Kind = TxKind.Expense, Amount = 50m, AccountId = a.Id, CategoryId = food.Id, DayOfMonth = 5, StartMonth = new DateTime(2026, 3, 1), Description = "Academia" });
            vm.NextMonth();
            Assert.Equal(1, host.Changes);
            Assert.Single(host.Data.Transactions, t => t.Date == new DateTime(2026, 4, 5));
            vm.PrevMonth(); vm.NextMonth(); // March and April again: March is new, April already exists
            Assert.Equal(2, host.Changes);
            Assert.Equal(2, host.Data.Transactions.Count);
        }

        [Fact]
        public void Filters_by_account_category_kind_and_status_newest_first()
        {
            var (host, vm, a, b, food, salary) = Setup();
            var d = host.Data;
            var t1 = Tx(d, TxKind.Expense, 10m, new DateTime(2026, 3, 2), true, a.Id, food.Id);
            var t2 = Tx(d, TxKind.Income, 3000m, new DateTime(2026, 3, 5), true, b.Id, salary.Id);
            var t3 = Tx(d, TxKind.Expense, 20m, new DateTime(2026, 3, 10), false, a.Id, food.Id);   // overdue
            var t4 = Tx(d, TxKind.Expense, 30m, new DateTime(2026, 3, 20), false, a.Id, food.Id);   // pending, future
            var t5 = Tx(d, TxKind.Transfer, 100m, new DateTime(2026, 3, 8), true, b.Id, to: a.Id);
            Tx(d, TxKind.Expense, 99m, new DateTime(2026, 2, 28), true, a.Id, food.Id);            // other month

            Assert.Equal(new[] { t4, t3, t5, t2, t1 }, vm.VisibleTransactions());
            Assert.False(vm.HasFilters);

            vm.FilterAccount = b.Id;   // the transfer touches both accounts
            Assert.Equal(new[] { t5, t2 }, vm.VisibleTransactions());
            vm.FilterAccount = a.Id;
            Assert.Equal(new[] { t4, t3, t5, t1 }, vm.VisibleTransactions());
            Assert.True(vm.HasFilters);

            vm.FilterAccount = "";
            vm.FilterCategory = food.Id;
            Assert.Equal(new[] { t4, t3, t1 }, vm.VisibleTransactions());
            vm.FilterStatus = "pending";
            Assert.Equal(new[] { t4, t3 }, vm.VisibleTransactions());
            vm.FilterStatus = "overdue";
            Assert.Equal(new[] { t3 }, vm.VisibleTransactions());
            vm.FilterStatus = "paid";
            Assert.Equal(new[] { t1 }, vm.VisibleTransactions());

            vm.ClearFilters();
            vm.FilterKind = "2";
            Assert.Equal(new[] { t5 }, vm.VisibleTransactions());
            vm.FilterKind = null; // a combo with nothing selected counts as "all"
            Assert.Equal("", vm.FilterKind);
            Assert.False(vm.HasFilters);
        }

        [Fact]
        public void Same_day_is_ordered_by_creation_newest_first()
        {
            var (host, vm, a, _, food, _) = Setup();
            var day = new DateTime(2026, 3, 3);
            var older = Tx(host.Data, TxKind.Expense, 1m, day, true, a.Id, food.Id);
            older.CreatedAt = new DateTime(2026, 3, 3, 8, 0, 0);
            var newer = Tx(host.Data, TxKind.Expense, 2m, day, true, a.Id, food.Id);
            newer.CreatedAt = new DateTime(2026, 3, 3, 9, 0, 0);
            Assert.Equal(new[] { newer, older }, vm.VisibleTransactions());
        }

        [Fact]
        public void Day_groups_sum_income_minus_expense_without_transfers_or_adjustments()
        {
            var (host, vm, a, b, food, salary) = Setup();
            var d = host.Data;
            var day = new DateTime(2026, 3, 4);
            Tx(d, TxKind.Income, 100m, day, true, a.Id, salary.Id);
            Tx(d, TxKind.Expense, 30m, day.AddHours(10), false, a.Id, food.Id);
            Tx(d, TxKind.Transfer, 500m, day, true, a.Id, to: b.Id);
            var adj = Tx(d, TxKind.Income, 7m, day, true, a.Id);
            adj.IsAdjustment = true;
            Tx(d, TxKind.Expense, 5m, new DateTime(2026, 3, 1), true, a.Id, food.Id);

            var groups = vm.DayGroups();
            Assert.Equal(new[] { day, new DateTime(2026, 3, 1) }, groups.Select(g => g.Day));
            Assert.Equal(70m, groups[0].Net);
            Assert.Equal(4, groups[0].Items.Count);
            Assert.Equal(-5m, groups[1].Net);
        }

        [Fact]
        public void Filter_options_keep_an_archived_account_only_while_it_is_filtered()
        {
            var (_, vm, _, b, _, _) = Setup();
            b.Archived = true;
            Assert.DoesNotContain(vm.AccountFilterOptions(), o => o.Id == b.Id);
            vm.FilterAccount = b.Id;
            Assert.Contains(vm.AccountFilterOptions(), o => o.Id == b.Id);
            Assert.Equal("", vm.AccountFilterOptions()[0].Id);

            var cats = vm.CategoryFilterOptions();
            Assert.Equal("", cats[0].Id);
            Assert.Equal("Alimentação", cats[1].Name);              // expenses first, by name
            Assert.Equal("Freelance (receita)", cats[8].Name);      // then income, marked
        }

        [Fact]
        public void Lists_of_the_other_tabs_are_ordered()
        {
            var (host, vm, a, b, food, _) = Setup();
            var d = host.Data;
            a.CreatedAt = new DateTime(2026, 1, 1);
            b.CreatedAt = new DateTime(2025, 1, 1);
            var c = new Account { Name = "Velha", Archived = true, CreatedAt = new DateTime(2024, 1, 1) };
            d.Accounts.Add(c);
            Assert.Equal(new[] { b, a }, vm.AccountsToShow());
            vm.ShowArchived = true;
            Assert.Equal(new[] { b, a, c }, vm.AccountsToShow());

            var ended = new Recurrence { DayOfMonth = 1, EndMonth = new DateTime(2026, 2, 1) };
            var paused = new Recurrence { DayOfMonth = 2, Paused = true };
            var late = new Recurrence { DayOfMonth = 20 };
            var early = new Recurrence { DayOfMonth = 3, EndMonth = new DateTime(2026, 3, 1) }; // ends this month: still running
            d.Recurrences.AddRange(new[] { ended, paused, late, early });
            Assert.True(vm.IsEnded(ended));
            Assert.False(vm.IsEnded(early));
            Assert.Equal(new[] { early, late, paused, ended }, vm.RecurrencesInOrder());

            var names = vm.CategoriesInOrder().Select(x => x.Name).ToList();
            Assert.Equal(names.OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase), names);
        }

        [Theory]
        [InlineData(1, "2026-03-01", 1, "2026-02-01")]
        [InlineData(3, "2026-01-01", 3, "2025-10-01")]
        [InlineData(12, "2025-04-01", 12, "2024-04-01")]
        [InlineData(0, "2026-01-01", 3, "2025-01-01")]
        public void Stats_period_ends_at_the_month_and_compares_with_the_one_before(int period, string from, int months, string prevFrom)
        {
            var (_, vm, _, _, _, _) = Setup();
            vm.Period = period;
            Assert.Equal((DateTime.Parse(from), months), vm.StatsPeriod());
            Assert.Equal((DateTime.Parse(prevFrom), months), vm.PreviousPeriod());
        }

        [Fact]
        public void Bars_show_at_least_six_months()
        {
            var (_, vm, _, _, _, _) = Setup();
            Assert.Equal((new DateTime(2025, 10, 1), 6), vm.BarsRange());
            vm.Period = 12;
            Assert.Equal((new DateTime(2025, 4, 1), 12), vm.BarsRange());
        }

        [Fact]
        public void Pie_selection_narrows_the_largest_transactions()
        {
            var (host, vm, a, _, food, _) = Setup();
            var d = host.Data;
            var home = d.Categories.First(c => c.Name == "Moradia");
            Tx(d, TxKind.Expense, 1500m, new DateTime(2026, 3, 5), true, a.Id, home.Id);
            Tx(d, TxKind.Expense, 40m, new DateTime(2026, 3, 6), true, a.Id, food.Id);
            Tx(d, TxKind.Expense, 60m, new DateTime(2026, 3, 7), false, a.Id, food.Id);

            vm.LoadPie();
            Assert.Equal(2, vm.PieData.Count);
            Assert.False(vm.HasPieSelection);
            Assert.Equal(3, vm.TopTransactions().Count);

            vm.PieSelected = vm.PieData.ToList().FindIndex(c => c.CategoryId == food.Id);
            Assert.Equal(food.Id, vm.SelectedPieCategoryId);
            Assert.Equal(new[] { 60m, 40m }, vm.TopTransactions().Select(t => t.Amount));

            vm.PieSelected = 9; // out of range: like no selection
            Assert.False(vm.HasPieSelection);
            vm.LoadPie();
            Assert.Equal(-1, vm.PieSelected);
        }

        [Fact]
        public void Toggle_paid_commits()
        {
            var (host, vm, a, _, food, _) = Setup();
            var t = Tx(host.Data, TxKind.Expense, 10m, Today, false, a.Id, food.Id);
            vm.TogglePaid(t);
            Assert.True(t.Paid);
            Assert.Equal(1, host.Commits);
        }

        [Fact]
        public void Delete_asks_first_and_undo_brings_the_series_back()
        {
            var (host, vm, a, _, food, _) = Setup();
            var first = new FinanceTransaction { Kind = TxKind.Expense, Amount = 300m, Date = new DateTime(2026, 3, 10), AccountId = a.Id, CategoryId = food.Id, Description = "TV" };
            FinanceService.AddInstallments(host.Data, first, 3);
            var t = host.Data.Transactions[0];

            host.Answer = null; // cancelled: nothing happens
            vm.RequestDeleteTx(t);
            Assert.Equal(3, host.Data.Transactions.Count);
            Assert.Equal("Excluir também as 2 parcelas seguintes", host.LastDialog.Value.check);
            Assert.True(host.LastDialog.Value.danger);

            host.Answer = true; // with the following installments
            vm.EditingTransaction = t;
            vm.RequestDeleteTx(t);
            Assert.Empty(host.Data.Transactions);
            Assert.Equal(new[] { "confirm", "confirm", "close", "commit", "snack" }, host.Calls);
            Assert.Equal("3 lançamentos excluídos", host.LastSnack.Value.text);

            host.LastSnack.Value.onAction();
            Assert.Equal(3, host.Data.Transactions.Count);
            Assert.Equal("replace", host.Calls[^2]);
            Assert.Equal("commit", host.Calls[^1]);
        }

        [Fact]
        public void Deleting_a_transaction_the_form_does_not_edit_leaves_the_form_open()
        {
            var (host, vm, a, _, food, _) = Setup();
            var t = Tx(host.Data, TxKind.Expense, 10m, Today, true, a.Id, food.Id);
            vm.EditingTransaction = Tx(host.Data, TxKind.Expense, 11m, Today, true, a.Id, food.Id);
            vm.DeleteTx(t, following: false);
            Assert.Equal(0, host.Closes);
            Assert.Equal("Lançamento excluído", host.LastSnack.Value.text);
        }

        [Fact]
        public void Archiving_the_default_account_moves_the_default_and_can_be_undone()
        {
            var (host, vm, a, b, _, _) = Setup();
            host.Data.LastAccountId = a.Id;
            vm.ToggleArchive(a);
            Assert.True(a.Archived);
            Assert.Equal(b.Id, host.Data.LastAccountId);
            Assert.Equal("Desfazer", host.LastSnack.Value.action);
            host.LastSnack.Value.onAction();
            Assert.False(a.Archived);

            vm.ToggleArchive(b);
            vm.ToggleArchive(b); // unarchiving has no undo
            Assert.Null(host.LastSnack.Value.action);
            Assert.Null(host.LastSnack.Value.onAction);
        }

        [Fact]
        public void Resuming_a_recurrence_generates_the_month_bill()
        {
            var (host, vm, a, _, food, _) = Setup();
            var r = new Recurrence { Kind = TxKind.Expense, Amount = 80m, AccountId = a.Id, CategoryId = food.Id, DayOfMonth = 20, StartMonth = new DateTime(2026, 1, 1), Paused = true, Description = "Luz" };
            host.Data.Recurrences.Add(r);
            vm.TogglePause(r);
            Assert.False(r.Paused);
            Assert.Single(host.Data.Transactions);
            Assert.Equal(new[] { "changed", "commit" }, host.Calls);
            vm.TogglePause(r);
            Assert.True(r.Paused);
        }

        [Fact]
        public void End_recurrence_this_month_only_when_ticked_and_active()
        {
            var (host, vm, a, _, food, _) = Setup();
            var r = new Recurrence { Kind = TxKind.Expense, Amount = 80m, AccountId = a.Id, CategoryId = food.Id, DayOfMonth = 20, StartMonth = new DateTime(2026, 1, 1), Description = "Luz" };
            host.Data.Recurrences.Add(r);
            vm.GenerateMonth();
            host.Answer = true;
            vm.RequestEndRecurrence(r);
            Assert.Equal("Encerrar já em março de 2026 (remove o lançamento pendente deste mês)", host.LastDialog.Value.check);
            Assert.Equal(new DateTime(2026, 2, 1), r.EndMonth);
            Assert.Empty(host.Data.Transactions);
            Assert.Equal("Recorrência encerrada", host.LastSnack.Value.text);

            host.LastSnack.Value.onAction(); // undo
            r = host.Data.Recurrences[0];
            Assert.Null(r.EndMonth);
            host.Answer = false;
            vm.RequestEndRecurrence(r);
            Assert.Equal(new DateTime(2026, 3, 1), host.Data.Recurrences[0].EndMonth);
        }

        [Fact]
        public void Delete_recurrence_explains_paid_and_pending_entries()
        {
            var (host, vm, a, _, food, _) = Setup();
            var r = new Recurrence { Kind = TxKind.Expense, Amount = 80m, AccountId = a.Id, CategoryId = food.Id, DayOfMonth = 10, StartMonth = new DateTime(2026, 1, 1), Description = "Luz" };
            host.Data.Recurrences.Add(r);
            FinanceService.GenerateUpTo(host.Data, Today);
            host.Data.Transactions.Where(t => t.Date.Month < 3).ToList().ForEach(t => t.Paid = true);

            host.Answer = false;
            vm.RequestDeleteRecurrence(r);
            Assert.Equal("“Luz” deixa de existir e não gera mais lançamentos. O lançamento pendente é removido. Os 2 lançamentos já pagos ficam no histórico como lançamento comum.",
                host.LastDialog.Value.text);
            Assert.NotNull(host.LastDialog.Value.check);
            Assert.Empty(host.Data.Recurrences);
            Assert.Equal(2, host.Data.Transactions.Count);
            Assert.Equal("Recorrência excluída", host.LastSnack.Value.text);
        }

        [Fact]
        public void Property_changes_are_announced_once()
        {
            var (_, vm, _, _, _, _) = Setup();
            var seen = new List<string>();
            vm.PropertyChanged += (s, e) => seen.Add(e.PropertyName);
            vm.Tab = "acc";
            vm.Tab = "acc";
            vm.FilterKind = "1";
            vm.PrevMonth();
            Assert.Equal(new[] { "Tab", "FilterKind", "Month" }, seen);
        }
    }
}
