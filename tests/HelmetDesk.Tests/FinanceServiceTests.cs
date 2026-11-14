using System;
using System.Linq;
using HelmetDesk.Core.Finance;
using Xunit;

namespace HelmetDesk.Tests
{
    public class FinanceServiceTests
    {
        private static (FinanceData d, Account a, Account b, Category food, Category salary) Setup()
        {
            var d = new FinanceData();
            FinanceService.Seed(d);
            var a = d.Accounts[0];
            a.InitialBalance = 1000m;
            var b = new Account { Name = "Poupança", Type = AccountType.Savings, InitialBalance = 500m };
            d.Accounts.Add(b);
            return (d, a, b, d.Categories.First(c => c.Name == "Alimentação"), d.Categories.First(c => c.Name == "Salário"));
        }

        private static FinanceTransaction Tx(FinanceData d, TxKind kind, decimal amount, DateTime date, bool paid, string account, string to = null, string cat = null)
        {
            var t = new FinanceTransaction { Kind = kind, Amount = amount, Date = date, Paid = paid, AccountId = account, ToAccountId = to, CategoryId = cat, Description = "x" };
            d.Transactions.Add(t);
            return t;
        }

        [Fact]
        public void Seed_creates_default_categories_and_one_account_once()
        {
            var d = new FinanceData();
            FinanceService.Seed(d);
            FinanceService.Seed(d);
            Assert.Equal(7, d.Categories.Count(c => c.Kind == CategoryKind.Expense));
            Assert.Equal(3, d.Categories.Count(c => c.Kind == CategoryKind.Income));
            Assert.Single(d.Accounts);
            Assert.Equal(d.Accounts[0].Id, d.LastAccountId);
        }

        [Fact]
        public void Seed_keeps_categories_the_user_already_has()
        {
            var d = new FinanceData();
            d.Categories.Add(new Category { Name = "Minha" });
            FinanceService.Seed(d);
            Assert.Single(d.Categories);
        }

        [Fact]
        public void Current_balance_counts_only_paid_transactions()
        {
            var (d, a, _, food, salary) = Setup();
            var day = new DateTime(2026, 3, 10);
            Tx(d, TxKind.Income, 3000m, day, paid: true, a.Id, cat: salary.Id);
            Tx(d, TxKind.Expense, 120.50m, day, paid: true, a.Id, cat: food.Id);
            Tx(d, TxKind.Expense, 999.99m, day, paid: false, a.Id, cat: food.Id); // pending: not in the current balance
            Assert.Equal(1000m + 3000m - 120.50m, FinanceService.Balance(d, a));
        }

        [Fact]
        public void Projected_balance_adds_pending_until_the_date_including_overdue()
        {
            var (d, a, _, food, _) = Setup();
            Tx(d, TxKind.Expense, 100m, new DateTime(2026, 2, 20), paid: false, a.Id, cat: food.Id); // overdue
            Tx(d, TxKind.Expense, 50m, new DateTime(2026, 3, 31), paid: false, a.Id, cat: food.Id);  // last day of the month
            Tx(d, TxKind.Expense, 70m, new DateTime(2026, 4, 1), paid: false, a.Id, cat: food.Id);   // next month: out
            Assert.Equal(1000m, FinanceService.Balance(d, a));
            Assert.Equal(850m, FinanceService.ProjectedBalance(d, a, FinanceService.MonthEnd(new DateTime(2026, 3, 5))));
        }

        [Fact]
        public void Transfer_moves_money_between_accounts_and_keeps_the_total()
        {
            var (d, a, b, _, _) = Setup();
            Tx(d, TxKind.Transfer, 200m, new DateTime(2026, 3, 1), paid: true, a.Id, to: b.Id);
            Assert.Equal(800m, FinanceService.Balance(d, a));
            Assert.Equal(700m, FinanceService.Balance(d, b));
            Assert.Equal(1500m, FinanceService.TotalBalance(d));
            var s = FinanceService.Summary(d, new DateTime(2026, 3, 1));
            Assert.Equal(0m, s.Income);
            Assert.Equal(0m, s.Expense);
        }

        [Fact]
        public void Archived_accounts_are_left_out_of_totals()
        {
            var (d, a, b, _, _) = Setup();
            b.Archived = true;
            Assert.Equal(1000m, FinanceService.TotalBalance(d));
        }

        [Fact]
        public void Summary_counts_paid_and_pending_of_the_month_without_adjustments()
        {
            var (d, a, _, food, salary) = Setup();
            var m = new DateTime(2026, 3, 1);
            Tx(d, TxKind.Income, 3000m, m.AddDays(4), true, a.Id, cat: salary.Id);
            Tx(d, TxKind.Expense, 100m, m.AddDays(5), true, a.Id, cat: food.Id);
            Tx(d, TxKind.Expense, 40m, m.AddDays(20), false, a.Id, cat: food.Id);
            Tx(d, TxKind.Expense, 999m, m.AddMonths(1), false, a.Id, cat: food.Id); // April
            FinanceService.Adjust(d, a, 5000m, m.AddDays(6));
            var s = FinanceService.Summary(d, m);
            Assert.Equal(3000m, s.Income);
            Assert.Equal(140m, s.Expense);
            Assert.Equal(5000m, s.CurrentBalance - 500m); // second account has 500
            Assert.Equal(5000m - 40m + 500m, s.ProjectedBalance);
        }

        [Theory]
        [InlineData("100", 3, new[] { "33.33", "33.33", "33.34" })]
        [InlineData("100", 6, new[] { "16.66", "16.66", "16.66", "16.66", "16.66", "16.70" })]
        [InlineData("1000", 10, new[] { "100", "100", "100", "100", "100", "100", "100", "100", "100", "100" })]
        [InlineData("0.10", 3, new[] { "0.03", "0.03", "0.04" })]
        [InlineData("1234.57", 7, new[] { "176.36", "176.36", "176.36", "176.36", "176.36", "176.36", "176.41" })]
        public void Installments_add_up_and_the_last_takes_the_cents(string total, int n, string[] expected)
        {
            var t = decimal.Parse(total, System.Globalization.CultureInfo.InvariantCulture);
            var parts = FinanceService.SplitInstallments(t, n);
            Assert.Equal(expected.Select(e => decimal.Parse(e, System.Globalization.CultureInfo.InvariantCulture)), parts);
            Assert.Equal(t, parts.Sum());
        }

        [Fact]
        public void AddInstallments_creates_linked_monthly_parts()
        {
            var (d, a, _, food, _) = Setup();
            var first = new FinanceTransaction { Kind = TxKind.Expense, Amount = 1000m, Date = new DateTime(2026, 1, 31), Description = "Celular", AccountId = a.Id, CategoryId = food.Id, Paid = true };
            var made = FinanceService.AddInstallments(d, first, 10);
            Assert.Equal(10, made.Count);
            Assert.Equal(1000m, made.Sum(t => t.Amount));
            Assert.Equal("Celular (3/10)", made[2].Description);
            Assert.Equal(new DateTime(2026, 2, 28), made[1].Date); // Jan 31 + 1 month
            Assert.True(made[0].Paid);
            Assert.All(made.Skip(1), t => Assert.False(t.Paid));
            Assert.Single(made.Select(t => t.InstallmentGroupId).Distinct());
        }

        [Fact]
        public void Recurring_generation_never_duplicates()
        {
            var (d, a, _, food, _) = Setup();
            d.Recurrences.Add(new Recurrence { Kind = TxKind.Expense, Description = "Aluguel", Amount = 1500m, AccountId = a.Id, CategoryId = food.Id, DayOfMonth = 5, StartMonth = new DateTime(2026, 1, 1) });
            var m = new DateTime(2026, 3, 1);
            Assert.Single(FinanceService.GenerateRecurring(d, m));
            Assert.Empty(FinanceService.GenerateRecurring(d, m));
            // the user moved it to another day and changed the value: still not generated again
            var t = d.Transactions.Single();
            t.Date = new DateTime(2026, 3, 28); t.Amount = 1600m;
            Assert.Empty(FinanceService.GenerateRecurring(d, m));
            Assert.Single(d.Transactions);
            Assert.False(t.Paid);
        }

        [Fact]
        public void Recurring_day_31_uses_the_last_day_of_short_months()
        {
            var (d, a, _, _, _) = Setup();
            var r = new Recurrence { Kind = TxKind.Expense, Amount = 10m, AccountId = a.Id, DayOfMonth = 31, StartMonth = new DateTime(2026, 1, 1) };
            d.Recurrences.Add(r);
            Assert.Equal(new DateTime(2026, 2, 28), FinanceService.GenerateRecurring(d, new DateTime(2026, 2, 1)).Single().Date);
            Assert.Equal(new DateTime(2028, 2, 29), FinanceService.OccurrenceDate(r, new DateTime(2028, 2, 1)));
        }

        [Fact]
        public void Recurring_respects_start_end_pause_and_deleted_months()
        {
            var (d, a, _, _, _) = Setup();
            var r = new Recurrence { Kind = TxKind.Expense, Amount = 10m, AccountId = a.Id, DayOfMonth = 1, StartMonth = new DateTime(2026, 3, 1), EndMonth = new DateTime(2026, 5, 1) };
            d.Recurrences.Add(r);
            Assert.Empty(FinanceService.GenerateRecurring(d, new DateTime(2026, 2, 1)));
            Assert.Empty(FinanceService.GenerateRecurring(d, new DateTime(2026, 6, 1)));
            var april = FinanceService.GenerateRecurring(d, new DateTime(2026, 4, 1)).Single();
            FinanceService.Delete(d, april, following: false);
            Assert.Empty(FinanceService.GenerateRecurring(d, new DateTime(2026, 4, 1))); // deleted on purpose: stays deleted
            r.Paused = true;
            Assert.Empty(FinanceService.GenerateRecurring(d, new DateTime(2026, 5, 1)));
            r.Paused = false;
            Assert.Single(FinanceService.GenerateRecurring(d, new DateTime(2026, 5, 1)));
        }

        [Fact]
        public void GenerateUpTo_fills_months_the_app_was_closed_but_not_the_future()
        {
            var (d, a, _, _, _) = Setup();
            var r = new Recurrence { Kind = TxKind.Expense, Amount = 10m, AccountId = a.Id, DayOfMonth = 10, StartMonth = new DateTime(2026, 1, 1) };
            d.Recurrences.Add(r);
            var made = FinanceService.GenerateUpTo(d, new DateTime(2026, 4, 2));
            Assert.Equal(new[] { "2026-01", "2026-02", "2026-03", "2026-04" }, made.Select(t => t.RecurrenceMonth));
            Assert.Empty(FinanceService.GenerateUpTo(d, new DateTime(2026, 4, 30)));
        }

        [Fact]
        public void EndRecurrence_including_this_month_removes_its_pending_but_keeps_paid()
        {
            var (d, a, _, _, _) = Setup();
            var r = new Recurrence { Kind = TxKind.Expense, Amount = 7.95m, AccountId = a.Id, DayOfMonth = 28, StartMonth = new DateTime(2026, 9, 1) };
            d.Recurrences.Add(r);
            var sep = FinanceService.GenerateRecurring(d, new DateTime(2026, 9, 1)).Single(); sep.Paid = true;
            var oct = FinanceService.GenerateRecurring(d, new DateTime(2026, 10, 1)).Single(); oct.Paid = true;
            var nov = FinanceService.GenerateRecurring(d, new DateTime(2026, 11, 1)).Single();   // still pending
            // "encerrar já neste mês" in November = last month is October
            FinanceService.EndRecurrence(d, r, new DateTime(2026, 10, 1));
            Assert.DoesNotContain(nov, d.Transactions);
            Assert.Contains(sep, d.Transactions);
            Assert.Contains(oct, d.Transactions);
            Assert.Contains(r, d.Recurrences);
            Assert.Empty(FinanceService.GenerateRecurring(d, new DateTime(2026, 11, 1)));
            Assert.Null(FinanceService.NextOccurrence(r, new DateTime(2026, 11, 13)));
        }

        [Fact]
        public void EndRecurrence_before_its_first_month_removes_it_when_nothing_is_left()
        {
            var (d, a, _, _, _) = Setup();
            var r = new Recurrence { Kind = TxKind.Expense, Amount = 10m, AccountId = a.Id, DayOfMonth = 28, StartMonth = new DateTime(2026, 11, 1) };
            d.Recurrences.Add(r);
            FinanceService.GenerateRecurring(d, new DateTime(2026, 11, 1));
            FinanceService.EndRecurrence(d, r, new DateTime(2026, 10, 1));
            Assert.Empty(d.Transactions);
            Assert.DoesNotContain(r, d.Recurrences);
        }

        [Fact]
        public void DeleteRecurrence_keeps_paid_history_unlinked_unless_asked()
        {
            var (d, a, _, _, _) = Setup();
            var r = new Recurrence { Kind = TxKind.Expense, Amount = 20m, AccountId = a.Id, DayOfMonth = 5, StartMonth = new DateTime(2026, 9, 1) };
            d.Recurrences.Add(r);
            var paid = FinanceService.GenerateRecurring(d, new DateTime(2026, 9, 1)).Single(); paid.Paid = true;
            FinanceService.GenerateRecurring(d, new DateTime(2026, 10, 1));
            Assert.Equal(1, FinanceService.DeleteRecurrence(d, r, includePaid: false));
            Assert.Empty(d.Recurrences);
            Assert.Same(paid, d.Transactions.Single());
            Assert.False(paid.IsRecurring);

            var r2 = new Recurrence { Kind = TxKind.Expense, Amount = 20m, AccountId = a.Id, DayOfMonth = 5, StartMonth = new DateTime(2026, 9, 1) };
            d.Transactions.Clear(); d.Recurrences.Add(r2);
            FinanceService.GenerateRecurring(d, new DateTime(2026, 9, 1)).Single().Paid = true;
            FinanceService.GenerateRecurring(d, new DateTime(2026, 10, 1));
            Assert.Equal(2, FinanceService.DeleteRecurrence(d, r2, includePaid: true));
            Assert.Empty(d.Transactions);
        }

        [Fact]
        public void NextOccurrence_skips_deleted_months_and_stops_at_the_end()
        {
            var r = new Recurrence { DayOfMonth = 28, StartMonth = new DateTime(2026, 1, 1), EndMonth = new DateTime(2026, 12, 1) };
            r.SkippedMonths.Add("2026-11");
            Assert.Equal(new DateTime(2026, 12, 28), FinanceService.NextOccurrence(r, new DateTime(2026, 11, 13)));
            Assert.Equal(new DateTime(2026, 10, 28), FinanceService.NextOccurrence(r, new DateTime(2026, 10, 2)));
            Assert.Null(FinanceService.NextOccurrence(r, new DateTime(2026, 12, 29)));
            r.Paused = true;
            Assert.Null(FinanceService.NextOccurrence(r, new DateTime(2026, 10, 2)));
        }

        [Fact]
        public void StartRecurrence_links_the_first_transaction_so_its_month_is_not_generated_again()
        {
            var (d, a, _, _, _) = Setup();
            var t = new FinanceTransaction { Kind = TxKind.Expense, Amount = 55.9m, Date = new DateTime(2026, 3, 12), Description = "Streaming", AccountId = a.Id, Paid = true };
            d.Transactions.Add(t);
            var r = FinanceService.StartRecurrence(d, t);
            Assert.Equal(12, r.DayOfMonth);
            Assert.Empty(FinanceService.GenerateRecurring(d, new DateTime(2026, 3, 1)));
            var april = FinanceService.GenerateRecurring(d, new DateTime(2026, 4, 1)).Single();
            Assert.Equal(new DateTime(2026, 4, 12), april.Date);
            Assert.False(april.Paid);
        }

        [Fact]
        public void Finance_data_round_trips_through_json_without_losing_cents()
        {
            var (d, a, _, food, _) = Setup();
            FinanceService.AddInstallments(d, new FinanceTransaction { Kind = TxKind.Expense, Amount = 100m, Date = new DateTime(2026, 1, 5), Description = "X", AccountId = a.Id, CategoryId = food.Id }, 3);
            var json = System.Text.Json.JsonSerializer.Serialize(d);
            var back = System.Text.Json.JsonSerializer.Deserialize<FinanceData>(json);
            Assert.Equal(new[] { 33.33m, 33.33m, 33.34m }, back.Transactions.Select(t => t.Amount));
            Assert.Equal(FinanceService.Balance(d, a), FinanceService.Balance(back, back.Accounts[0]));
            // a data.json written before Finanças has no "Finance" section at all
            var old = System.Text.Json.JsonSerializer.Deserialize<FinanceData>("{}");
            Assert.Empty(old.Transactions);
            FinanceService.Seed(old);
            Assert.Single(old.Accounts);
        }

        [Fact]
        public void Deleting_this_and_following_ends_the_recurrence()
        {
            var (d, a, _, _, _) = Setup();
            var r = new Recurrence { Kind = TxKind.Expense, Amount = 10m, AccountId = a.Id, StartMonth = new DateTime(2026, 1, 1) };
            d.Recurrences.Add(r);
            for (int i = 0; i < 4; i++) FinanceService.GenerateRecurring(d, new DateTime(2026, 1 + i, 1));
            var march = d.Transactions.Single(t => t.RecurrenceMonth == "2026-03");
            Assert.Equal(2, FinanceService.Delete(d, march, following: true));
            Assert.Equal(new DateTime(2026, 2, 1), r.EndMonth);
            Assert.Empty(FinanceService.GenerateRecurring(d, new DateTime(2026, 5, 1)));
            Assert.Equal(2, d.Transactions.Count);
        }

        [Fact]
        public void Deleting_installments_one_or_following()
        {
            var (d, a, _, _, _) = Setup();
            var made = FinanceService.AddInstallments(d, new FinanceTransaction { Kind = TxKind.Expense, Amount = 600m, Date = new DateTime(2026, 1, 10), Description = "TV", AccountId = a.Id }, 6);
            FinanceService.Delete(d, made[1], following: false);
            Assert.Equal(5, d.Transactions.Count);
            FinanceService.Delete(d, made[3], following: true);
            Assert.Equal(new[] { 1, 3 }, d.Transactions.Select(t => t.InstallmentNumber).OrderBy(n => n));
        }

        [Fact]
        public void Editing_following_installments_keeps_the_numbering()
        {
            var (d, a, _, food, _) = Setup();
            var made = FinanceService.AddInstallments(d, new FinanceTransaction { Kind = TxKind.Expense, Amount = 300m, Date = new DateTime(2026, 1, 10), Description = "Curso", AccountId = a.Id }, 3);
            made[1].Description = "Curso online (2/3)";
            made[1].CategoryId = food.Id;
            FinanceService.ApplyToFollowing(d, made[1]);
            Assert.Equal("Curso (1/3)", made[0].Description);
            Assert.Equal("Curso online (3/3)", made[2].Description);
            Assert.Equal(food.Id, made[2].CategoryId);
            Assert.Null(made[0].CategoryId);
        }

        [Fact]
        public void Adjustment_creates_the_difference_and_is_left_out_of_the_chart()
        {
            var (d, a, _, _, _) = Setup();
            var t = FinanceService.Adjust(d, a, 950.25m, new DateTime(2026, 3, 3));
            Assert.Equal(TxKind.Expense, t.Kind);
            Assert.Equal(49.75m, t.Amount);
            Assert.True(t.Paid && t.IsAdjustment);
            Assert.Equal(950.25m, FinanceService.Balance(d, a));
            Assert.Null(FinanceService.Adjust(d, a, 950.25m, DateTime.Today));
            Assert.Empty(FinanceService.ExpensesByCategory(d, new DateTime(2026, 3, 1)));
        }

        [Fact]
        public void Expenses_by_category_are_grouped_and_sorted()
        {
            var (d, a, _, food, _) = Setup();
            var home = d.Categories.First(c => c.Name == "Moradia");
            var m = new DateTime(2026, 3, 1);
            Tx(d, TxKind.Expense, 30m, m, true, a.Id, cat: food.Id);
            Tx(d, TxKind.Expense, 20m, m, false, a.Id, cat: food.Id);
            Tx(d, TxKind.Expense, 1500m, m, true, a.Id, cat: home.Id);
            var list = FinanceService.ExpensesByCategory(d, m);
            Assert.Equal(home.Id, list[0].CategoryId);
            Assert.Equal(50m, list[1].Total);
        }

        [Fact]
        public void Due_counts_pending_overdue_and_today_only()
        {
            var (d, a, _, _, _) = Setup();
            var today = new DateTime(2026, 3, 15);
            Tx(d, TxKind.Expense, 1m, today.AddDays(-3), false, a.Id);
            Tx(d, TxKind.Expense, 1m, today, false, a.Id);
            Tx(d, TxKind.Expense, 1m, today.AddDays(1), false, a.Id);
            Tx(d, TxKind.Expense, 1m, today.AddDays(-1), true, a.Id);
            Assert.Equal(2, FinanceService.Due(d, today).Count());
        }

        [Fact]
        public void Suggest_uses_the_latest_transaction_with_the_same_description()
        {
            var (d, a, b, food, _) = Setup();
            var t1 = Tx(d, TxKind.Expense, 30m, new DateTime(2026, 1, 1), true, a.Id, cat: food.Id); t1.Description = "Padaria";
            var t2 = Tx(d, TxKind.Expense, 35m, new DateTime(2026, 2, 1), true, b.Id, cat: food.Id); t2.Description = "padaria";
            var s = FinanceService.Suggest(d, "PADARIA").Value;
            Assert.Equal(35m, s.Amount);
            Assert.Equal(b.Id, s.AccountId);
            Assert.Null(FinanceService.Suggest(d, "Pada"));
            Assert.Equal(new[] { "padaria" }, FinanceService.Descriptions(d, "pa"));
        }

        [Fact]
        public void Deleting_a_used_category_moves_its_transactions()
        {
            var (d, a, _, food, _) = Setup();
            var other = d.Categories.First(c => c.Name == "Outros" && c.Kind == CategoryKind.Expense);
            var t = Tx(d, TxKind.Expense, 5m, DateTime.Today, true, a.Id, cat: food.Id);
            Assert.Throws<InvalidOperationException>(() => FinanceService.DeleteCategory(d, food, null));
            FinanceService.DeleteCategory(d, food, other);
            Assert.Equal(other.Id, t.CategoryId);
            Assert.DoesNotContain(food, d.Categories);
        }

        [Fact]
        public void Duplicate_drops_series_links()
        {
            var (d, a, _, _, _) = Setup();
            var made = FinanceService.AddInstallments(d, new FinanceTransaction { Kind = TxKind.Expense, Amount = 90m, Date = DateTime.Today, Description = "Fone", AccountId = a.Id }, 3);
            var c = FinanceService.Duplicate(d, made[1]);
            Assert.Equal("Fone", c.Description);
            Assert.Null(c.InstallmentGroupId);
            Assert.NotEqual(made[1].Id, c.Id);
        }

        [Theory]
        [InlineData("1234,56", "1234.56")]
        [InlineData("1.234,56", "1234.56")]
        [InlineData("R$ 50", "50")]
        [InlineData("r$1.234", "1234")]
        [InlineData("12.5", "12.5")]
        [InlineData("-150,00", "-150")]
        [InlineData("0,005", "0.01")]
        [InlineData("1.000.000,00", "1000000")]
        public void Money_parses_pt_br(string text, string expected)
        {
            Assert.True(Money.TryParse(text, out var v));
            Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), v);
        }

        [Theory]
        [InlineData("")]
        [InlineData("abc")]
        [InlineData("1,2,3")]
        [InlineData("12a")]
        public void Money_rejects_garbage(string text) => Assert.False(Money.TryParse(text, out _));

        [Fact]
        public void Money_formats_pt_br()
        {
            Assert.Equal("R$ 1.234,56", Money.Format(1234.56m));
            Assert.Equal("-R$ 0,50", Money.Format(-0.5m));
            Assert.Equal("+R$ 10,00", Money.Signed(10m));
        }

        [Fact]
        public void Statistics_cover_the_period_and_skip_transfers_and_adjustments()
        {
            var (d, a, b, food, salary) = Setup();
            var other = d.Categories.First(c => c.Kind == CategoryKind.Expense && c.Id != food.Id);
            var jan = new DateTime(2026, 1, 1);
            Tx(d, TxKind.Income, 3000m, new DateTime(2026, 1, 5), true, a.Id, cat: salary.Id);
            Tx(d, TxKind.Expense, 200m, new DateTime(2026, 1, 10), true, a.Id, cat: food.Id);
            Tx(d, TxKind.Expense, 50m, new DateTime(2026, 1, 20), false, a.Id, cat: food.Id);
            Tx(d, TxKind.Expense, 400m, new DateTime(2026, 3, 2), true, a.Id, cat: other.Id);
            Tx(d, TxKind.Expense, 30m, new DateTime(2026, 3, 3), true, a.Id);
            Tx(d, TxKind.Transfer, 999m, new DateTime(2026, 1, 15), true, a.Id, to: b.Id);
            FinanceService.Adjust(d, a, 10m, new DateTime(2026, 2, 1));
            Tx(d, TxKind.Expense, 77m, new DateTime(2026, 4, 1), true, a.Id, cat: food.Id); // outside

            var t = FinanceService.Totals(d, jan, 3);
            Assert.Equal(3000m, t.Income);
            Assert.Equal(680m, t.Expense);
            Assert.Equal(2320m, t.Net);
            Assert.Equal(2320.0 / 3000.0, t.SavingsRate.Value, 6);
            Assert.Null(FinanceService.Totals(d, new DateTime(2026, 3, 1), 1).SavingsRate);

            var cats = FinanceService.ByCategory(d, TxKind.Expense, jan, 3);
            Assert.Equal(new[] { other.Id, food.Id, null }, cats.Select(c => c.CategoryId).ToArray());
            Assert.Equal(250m, cats[1].Total);

            var months = FinanceService.Monthly(d, jan, 3);
            Assert.Equal(3, months.Count);
            Assert.Equal(new[] { 250m, 0m, 430m }, months.Select(m => m.Expense).ToArray());
            Assert.Equal(new DateTime(2026, 2, 1), months[1].Month);

            var big = FinanceService.Largest(d, TxKind.Expense, jan, 3, byCategory: true, categoryId: food.Id);
            Assert.Equal(new[] { 200m, 50m }, big.Select(x => x.Amount).ToArray());
            Assert.Single(FinanceService.Largest(d, TxKind.Expense, jan, 3, byCategory: true, categoryId: null));
            Assert.Equal(new[] { 400m, 200m }, FinanceService.Largest(d, TxKind.Expense, jan, 3, max: 2).Select(x => x.Amount).ToArray());

            Assert.Equal(0.5, FinanceService.Change(100m, 150m).Value, 6);
            Assert.Null(FinanceService.Change(0m, 150m));
        }
    }
}
