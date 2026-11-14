using System;
using System.Collections.Generic;
using System.Linq;

namespace HelmetDesk.Core.Finance
{
    public readonly record struct MonthSummary(decimal Income, decimal Expense, decimal CurrentBalance, decimal ProjectedBalance);
    public readonly record struct CategoryTotal(string CategoryId, decimal Total);
    public readonly record struct MonthTotals(DateTime Month, decimal Income, decimal Expense);
    public readonly record struct PeriodTotals(decimal Income, decimal Expense)
    {
        public decimal Net => Income - Expense;
        /// <summary>Share of income left after expenses (null when there was no income).</summary>
        public double? SavingsRate => Income > 0 ? (double)((Income - Expense) / Income) : null;
    }
    public readonly record struct Suggestion(decimal Amount, TxKind Kind, string CategoryId, string AccountId);

    /// <summary>
    /// All the money rules of the Finanças panel, with no UI: balances, month summary, installments,
    /// recurrences, balance adjustment, autocomplete and series edits. The screen only calls these and shows the result.
    /// </summary>
    public static class FinanceService
    {
        public const string AdjustmentDescription = "Ajuste de saldo";

        // ================= first run =================

        /// <summary>Creates the default categories and a "Carteira" account once (also for users updating from a version without Finanças).</summary>
        public static void Seed(FinanceData d)
        {
            if (d.Seeded) return;
            if (d.Categories.Count == 0)
            {
                (string name, string color, string icon)[] expense =
                {
                    ("Alimentação", "#F2A65A", "food"), ("Moradia", "#78B7FF", "home"), ("Transporte", "#5CC8C8", "car"),
                    ("Assinaturas", "#B48CF2", "repeat"), ("Lazer", "#FF8FB1", "fun"), ("Saúde", "#64D6A2", "health"),
                    ("Outros", "#9A93A8", "dots"),
                };
                (string name, string color, string icon)[] income =
                {
                    ("Salário", "#64D6A2", "salary"), ("Freelance", "#8BD3FF", "laptop"), ("Outros", "#9A93A8", "dots"),
                };
                foreach (var (n, c, i) in expense) d.Categories.Add(new Category { Name = n, Kind = CategoryKind.Expense, Color = c, Icon = i });
                foreach (var (n, c, i) in income) d.Categories.Add(new Category { Name = n, Kind = CategoryKind.Income, Color = c, Icon = i });
            }
            if (d.Accounts.Count == 0)
                d.Accounts.Add(new Account { Name = "Carteira", Type = AccountType.Wallet, Color = "#8950D9" });
            d.LastAccountId ??= d.Accounts[0].Id;
            d.Seeded = true;
        }

        // ================= balances =================

        /// <summary>How a transaction changes one account (+ in, − out; 0 if it doesn't touch it).</summary>
        public static decimal EffectOn(FinanceTransaction t, string accountId) => t.Kind switch
        {
            TxKind.Income => t.AccountId == accountId ? t.Amount : 0,
            TxKind.Expense => t.AccountId == accountId ? -t.Amount : 0,
            TxKind.Transfer => (t.AccountId == accountId ? -t.Amount : 0) + (t.ToAccountId == accountId ? t.Amount : 0),
            _ => 0
        };

        /// <summary>Current balance: initial balance + PAID transactions (any date).</summary>
        public static decimal Balance(FinanceData d, Account a) =>
            a.InitialBalance + d.Transactions.Where(t => t.Paid).Sum(t => EffectOn(t, a.Id));

        /// <summary>Projected balance on <paramref name="until"/>: current balance + pending transactions dated up to that day (overdue ones included).</summary>
        public static decimal ProjectedBalance(FinanceData d, Account a, DateTime until) =>
            Balance(d, a) + d.Transactions.Where(t => !t.Paid && t.Date.Date <= until.Date).Sum(t => EffectOn(t, a.Id));

        /// <summary>Accounts counted in totals: archived ones are left out.</summary>
        public static IEnumerable<Account> ActiveAccounts(FinanceData d) => d.Accounts.Where(a => !a.Archived);

        public static decimal TotalBalance(FinanceData d) => ActiveAccounts(d).Sum(a => Balance(d, a));
        public static decimal TotalProjected(FinanceData d, DateTime until) => ActiveAccounts(d).Sum(a => ProjectedBalance(d, a, until));

        // ================= month =================

        public static DateTime MonthStart(DateTime day) => new(day.Year, day.Month, 1);
        public static DateTime MonthEnd(DateTime day) => MonthStart(day).AddMonths(1).AddDays(-1);
        public static string MonthKey(DateTime day) => day.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

        public static IEnumerable<FinanceTransaction> InMonth(FinanceData d, DateTime month)
        {
            var a = MonthStart(month); var b = a.AddMonths(1);
            return d.Transactions.Where(t => t.Date >= a && t.Date < b);
        }

        /// <summary>
        /// Income/expense of the month: paid AND pending, without transfers and balance adjustments.
        /// Current balance = all active accounts now; projected = at the end of that month counting pending ones.
        /// </summary>
        public static MonthSummary Summary(FinanceData d, DateTime month)
        {
            var list = InMonth(d, month).Where(t => !t.IsAdjustment).ToList();
            return new MonthSummary(
                list.Where(t => t.Kind == TxKind.Income).Sum(t => t.Amount),
                list.Where(t => t.Kind == TxKind.Expense).Sum(t => t.Amount),
                TotalBalance(d),
                TotalProjected(d, MonthEnd(month)));
        }

        /// <summary>Expenses of the month per category (largest first). Adjustments are left out; no category = null id.</summary>
        public static List<CategoryTotal> ExpensesByCategory(FinanceData d, DateTime month) =>
            InMonth(d, month).Where(t => t.Kind == TxKind.Expense && !t.IsAdjustment)
                .GroupBy(t => t.CategoryId)
                .Select(g => new CategoryTotal(g.Key, g.Sum(t => t.Amount)))
                .OrderByDescending(c => c.Total).ToList();

        // ================= statistics =================

        /// <summary>
        /// Income/expense transactions (paid and pending, no transfers, no balance adjustments) of the
        /// <paramref name="months"/> months starting at <paramref name="from"/>.
        /// </summary>
        public static IEnumerable<FinanceTransaction> InPeriod(FinanceData d, DateTime from, int months)
        {
            var a = MonthStart(from); var b = a.AddMonths(Math.Max(1, months));
            return d.Transactions.Where(t => t.Date >= a && t.Date < b && !t.IsAdjustment && t.Kind != TxKind.Transfer);
        }

        public static PeriodTotals Totals(FinanceData d, DateTime from, int months)
        {
            decimal inc = 0, exp = 0;
            foreach (var t in InPeriod(d, from, months))
                if (t.Kind == TxKind.Income) inc += t.Amount; else exp += t.Amount;
            return new PeriodTotals(inc, exp);
        }

        /// <summary>Totals per category of one kind in the period, largest first. No category = null id.</summary>
        public static List<CategoryTotal> ByCategory(FinanceData d, TxKind kind, DateTime from, int months) =>
            InPeriod(d, from, months).Where(t => t.Kind == kind)
                .GroupBy(t => t.CategoryId)
                .Select(g => new CategoryTotal(g.Key, g.Sum(t => t.Amount)))
                .OrderByDescending(c => c.Total).ToList();

        /// <summary>One entry per month of the period (months without transactions included, as zero).</summary>
        public static List<MonthTotals> Monthly(FinanceData d, DateTime from, int months)
        {
            var start = MonthStart(from);
            var list = new List<MonthTotals>();
            for (int i = 0; i < Math.Max(1, months); i++)
            {
                var m = start.AddMonths(i);
                var t = Totals(d, m, 1);
                list.Add(new MonthTotals(m, t.Income, t.Expense));
            }
            return list;
        }

        /// <summary>Largest transactions of one kind in the period; with <paramref name="byCategory"/>, only that category (null id = no category).</summary>
        public static List<FinanceTransaction> Largest(FinanceData d, TxKind kind, DateTime from, int months, int max = 6, bool byCategory = false, string categoryId = null) =>
            InPeriod(d, from, months).Where(t => t.Kind == kind && (!byCategory || t.CategoryId == categoryId))
                .OrderByDescending(t => t.Amount).ThenByDescending(t => t.Date).Take(max).ToList();

        /// <summary>Relative change from <paramref name="before"/> to <paramref name="now"/> (null when there is nothing to compare).</summary>
        public static double? Change(decimal before, decimal now) =>
            before == 0 ? null : (double)((now - before) / Math.Abs(before));

        /// <summary>Pending transactions overdue or due today (sidebar counter).</summary>
        public static IEnumerable<FinanceTransaction> Due(FinanceData d, DateTime today) =>
            d.Transactions.Where(t => !t.Paid && t.Date.Date <= today.Date);

        public static bool IsOverdue(FinanceTransaction t, DateTime today) => !t.Paid && t.Date.Date < today.Date;

        // ================= installments =================

        /// <summary>
        /// Splits <paramref name="total"/> in <paramref name="count"/> parts rounded down to the cent; the last part takes the
        /// leftover cents, so the parts always add up to the total. (100 / 3 = 33,33 + 33,33 + 33,34.)
        /// </summary>
        public static decimal[] SplitInstallments(decimal total, int count)
        {
            if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
            var part = Math.Floor(total * 100m / count) / 100m;
            var parts = Enumerable.Repeat(part, count).ToArray();
            parts[count - 1] = total - part * (count - 1);
            return parts;
        }

        /// <summary>
        /// Creates the N monthly installments of a purchase from <paramref name="first"/> (its Amount is the TOTAL,
        /// its Date the first installment's). "Nome (3/10)" descriptions; only the first keeps its paid status.
        /// </summary>
        public static List<FinanceTransaction> AddInstallments(FinanceData d, FinanceTransaction first, int count)
        {
            if (count < 2) { d.Transactions.Add(first); return new() { first }; }
            var parts = SplitInstallments(first.Amount, count);
            var group = NewId.Make();
            var name = BaseDescription(first.Description);
            var made = new List<FinanceTransaction>();
            for (int i = 0; i < count; i++)
            {
                var t = Clone(first);
                t.Amount = parts[i];
                t.Date = first.Date.AddMonths(i);
                t.Description = $"{name} ({i + 1}/{count})";
                t.InstallmentGroupId = group;
                t.InstallmentNumber = i + 1;
                t.InstallmentCount = count;
                t.Paid = i == 0 && first.Paid;
                made.Add(t);
            }
            d.Transactions.AddRange(made);
            return made;
        }

        /// <summary>"Celular (3/10)" → "Celular".</summary>
        public static string BaseDescription(string description)
        {
            var s = (description ?? "").Trim();
            var m = System.Text.RegularExpressions.Regex.Match(s, @"^(.*?)\s*\(\d+/\d+\)$");
            return m.Success ? m.Groups[1].Value : s;
        }

        // ================= recurrences =================

        public static DateTime OccurrenceDate(Recurrence r, DateTime month)
        {
            var m = MonthStart(month);
            int day = Math.Min(Math.Max(r.DayOfMonth, 1), DateTime.DaysInMonth(m.Year, m.Month));
            return new DateTime(m.Year, m.Month, day);
        }

        public static bool IsActiveIn(Recurrence r, DateTime month)
        {
            var m = MonthStart(month);
            return !r.Paused && m >= MonthStart(r.StartMonth) && (r.EndMonth == null || m <= MonthStart(r.EndMonth.Value))
                   && !r.SkippedMonths.Contains(MonthKey(m));
        }

        /// <summary>
        /// Creates the pending transactions of <paramref name="month"/> for every active recurrence that doesn't have one yet
        /// (matched by recurrence id + month, so a moved date or edited value never duplicates). Returns what was created.
        /// </summary>
        public static List<FinanceTransaction> GenerateRecurring(FinanceData d, DateTime month)
        {
            var key = MonthKey(month);
            var made = new List<FinanceTransaction>();
            foreach (var r in d.Recurrences)
            {
                if (!IsActiveIn(r, month)) continue;
                if (d.Transactions.Any(t => t.RecurrenceId == r.Id && t.RecurrenceMonth == key)) continue;
                var t = new FinanceTransaction
                {
                    Kind = r.Kind, Amount = r.Amount, Date = OccurrenceDate(r, month), Description = r.Description,
                    CategoryId = r.CategoryId, AccountId = r.AccountId, ToAccountId = r.ToAccountId,
                    Paid = false, RecurrenceId = r.Id, RecurrenceMonth = key,
                };
                d.Transactions.Add(t);
                made.Add(t);
            }
            return made;
        }

        /// <summary>
        /// Generates every month from the oldest recurrence up to the month of <paramref name="today"/>, so months the app
        /// was not opened in still get their pending bills (used at startup). Never generates future months.
        /// </summary>
        public static List<FinanceTransaction> GenerateUpTo(FinanceData d, DateTime today)
        {
            var made = new List<FinanceTransaction>();
            if (d.Recurrences.Count == 0) return made;
            var last = MonthStart(today);
            for (var m = d.Recurrences.Min(r => MonthStart(r.StartMonth)); m <= last; m = m.AddMonths(1))
                made.AddRange(GenerateRecurring(d, m));
            return made;
        }

        /// <summary>Creates a recurrence from a transaction the user is saving, and links that transaction to it as its first month.</summary>
        public static Recurrence StartRecurrence(FinanceData d, FinanceTransaction t)
        {
            var r = new Recurrence
            {
                Kind = t.Kind, Description = t.Description, Amount = t.Amount, CategoryId = t.CategoryId,
                AccountId = t.AccountId, ToAccountId = t.ToAccountId, DayOfMonth = t.Date.Day, StartMonth = MonthStart(t.Date),
            };
            d.Recurrences.Add(r);
            t.RecurrenceId = r.Id;
            t.RecurrenceMonth = MonthKey(t.Date);
            return r;
        }

        /// <summary>
        /// "Encerrar": no months after <paramref name="lastMonth"/>; pending ones already generated after it are removed
        /// (paid ones stay). Ending before the first month leaves nothing to keep: if no transaction of it is left either,
        /// the recurrence itself is removed.
        /// </summary>
        public static void EndRecurrence(FinanceData d, Recurrence r, DateTime lastMonth)
        {
            r.EndMonth = MonthStart(lastMonth);
            var cut = MonthKey(lastMonth);
            d.Transactions.RemoveAll(t => t.RecurrenceId == r.Id && !t.Paid && string.CompareOrdinal(t.RecurrenceMonth, cut) > 0);
            if (MonthStart(lastMonth) < MonthStart(r.StartMonth) && !d.Transactions.Any(t => t.RecurrenceId == r.Id))
                d.Recurrences.Remove(r);
        }

        /// <summary>
        /// "Excluir recorrência": removes the model and its pending transactions. Paid ones stay as ordinary transactions
        /// (unlinked from the series) unless <paramref name="includePaid"/>, which removes them too and so changes balances.
        /// Returns how many transactions were removed.
        /// </summary>
        public static int DeleteRecurrence(FinanceData d, Recurrence r, bool includePaid)
        {
            int removed = d.Transactions.RemoveAll(t => t.RecurrenceId == r.Id && (includePaid || !t.Paid));
            foreach (var t in d.Transactions.Where(t => t.RecurrenceId == r.Id))
            {
                t.RecurrenceId = null;
                t.RecurrenceMonth = null;
            }
            d.Recurrences.Remove(r);
            return removed;
        }

        /// <summary>
        /// Next date this recurrence produces, from <paramref name="today"/> on: skips paused state, skipped months and
        /// months after its end. Null when nothing is left.
        /// </summary>
        public static DateTime? NextOccurrence(Recurrence r, DateTime today)
        {
            if (r.Paused) return null;
            var m = MonthStart(today);
            if (m < MonthStart(r.StartMonth)) m = MonthStart(r.StartMonth);
            for (int i = 0; i < 240; i++, m = m.AddMonths(1))
            {
                if (r.EndMonth.HasValue && m > MonthStart(r.EndMonth.Value)) return null;
                if (!IsActiveIn(r, m)) continue;
                var date = OccurrenceDate(r, m);
                if (date >= today.Date) return date;
            }
            return null;
        }

        // ================= adjustment =================

        /// <summary>"Conferir saldo": creates a paid adjustment with the difference to <paramref name="realBalance"/>; null if it already matches.</summary>
        public static FinanceTransaction Adjust(FinanceData d, Account a, decimal realBalance, DateTime date)
        {
            var diff = realBalance - Balance(d, a);
            if (diff == 0) return null;
            var t = new FinanceTransaction
            {
                Kind = diff > 0 ? TxKind.Income : TxKind.Expense, Amount = Math.Abs(diff), Date = date.Date,
                Description = AdjustmentDescription, AccountId = a.Id, Paid = true, IsAdjustment = true,
            };
            d.Transactions.Add(t);
            return t;
        }

        // ================= autocomplete =================

        /// <summary>Earlier descriptions starting with <paramref name="prefix"/> (installment suffix removed), most recent first.</summary>
        public static List<string> Descriptions(FinanceData d, string prefix, int max = 6)
        {
            prefix = (prefix ?? "").Trim();
            if (prefix.Length == 0) return new();
            return d.Transactions.Where(t => !t.IsAdjustment)
                .OrderByDescending(t => t.Date).ThenByDescending(t => t.CreatedAt)
                .Select(t => BaseDescription(t.Description))
                .Where(s => s.Length > 0 && s.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))
                .Distinct(StringComparer.CurrentCultureIgnoreCase).Take(max).ToList();
        }

        /// <summary>Value, type, category and account of the latest transaction with this exact description (case-insensitive), if any.</summary>
        public static Suggestion? Suggest(FinanceData d, string description)
        {
            var key = BaseDescription(description);
            if (key.Length == 0) return null;
            var last = d.Transactions.Where(t => !t.IsAdjustment && string.Equals(BaseDescription(t.Description), key, StringComparison.CurrentCultureIgnoreCase))
                .OrderByDescending(t => t.Date).ThenByDescending(t => t.CreatedAt).FirstOrDefault();
            if (last == null) return null;
            // for an installment this is the value of one part
            return new Suggestion(last.Amount, last.Kind, last.CategoryId, last.AccountId);
        }

        // ================= series (installments / recurrences) =================

        /// <summary>The transaction and, if <paramref name="following"/>, the later ones of its installment group or recurrence.</summary>
        public static List<FinanceTransaction> Series(FinanceData d, FinanceTransaction t, bool following)
        {
            if (!following) return new() { t };
            if (t.IsInstallment)
                return d.Transactions.Where(x => x.InstallmentGroupId == t.InstallmentGroupId && x.InstallmentNumber >= t.InstallmentNumber)
                    .OrderBy(x => x.InstallmentNumber).ToList();
            if (t.IsRecurring)
                return d.Transactions.Where(x => x.RecurrenceId == t.RecurrenceId && string.CompareOrdinal(x.RecurrenceMonth, t.RecurrenceMonth) >= 0)
                    .OrderBy(x => x.RecurrenceMonth).ToList();
            return new() { t };
        }

        /// <summary>
        /// Deletes the transaction (and the following ones of its series). A deleted recurring month is remembered so it isn't
        /// generated again; "this and following" ends the recurrence at the previous month.
        /// </summary>
        public static int Delete(FinanceData d, FinanceTransaction t, bool following)
        {
            var list = Series(d, t, following);
            if (t.IsRecurring && d.Recurrences.FirstOrDefault(r => r.Id == t.RecurrenceId) is { } rec)
            {
                if (following)
                {
                    var month = DateTime.ParseExact(t.RecurrenceMonth, "yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
                    if (MonthStart(month) <= MonthStart(rec.StartMonth)) d.Recurrences.Remove(rec);
                    else rec.EndMonth = month.AddMonths(-1);
                }
                else if (!rec.SkippedMonths.Contains(t.RecurrenceMonth)) rec.SkippedMonths.Add(t.RecurrenceMonth);
            }
            foreach (var x in list) d.Transactions.Remove(x);
            return list.Count;
        }

        /// <summary>
        /// Copies the edited fields of <paramref name="edited"/> to the following transactions of its series (value, type,
        /// category, accounts, note and the description without the "(3/10)" part). For a recurrence the model is updated
        /// too, so future months come out the same.
        /// </summary>
        public static void ApplyToFollowing(FinanceData d, FinanceTransaction edited)
        {
            var name = BaseDescription(edited.Description);
            foreach (var x in Series(d, edited, true))
            {
                if (x == edited) continue;
                x.Amount = edited.Amount; x.Kind = edited.Kind; x.CategoryId = edited.CategoryId;
                x.AccountId = edited.AccountId; x.ToAccountId = edited.ToAccountId; x.Note = edited.Note;
                x.Description = x.IsInstallment ? $"{name} ({x.InstallmentNumber}/{x.InstallmentCount})" : edited.Description;
                x.UpdatedAt = DateTime.Now;
            }
            if (edited.IsRecurring && d.Recurrences.FirstOrDefault(r => r.Id == edited.RecurrenceId) is { } rec)
            {
                rec.Amount = edited.Amount; rec.Kind = edited.Kind; rec.CategoryId = edited.CategoryId;
                rec.AccountId = edited.AccountId; rec.ToAccountId = edited.ToAccountId; rec.Description = edited.Description;
            }
        }

        /// <summary>Copy with a new id and no installment/recurrence link (the "Duplicar" action).</summary>
        public static FinanceTransaction Duplicate(FinanceData d, FinanceTransaction t)
        {
            var c = Clone(t);
            c.Description = t.IsInstallment ? BaseDescription(t.Description) : t.Description;
            c.RecurrenceId = null; c.RecurrenceMonth = null;
            c.InstallmentGroupId = null; c.InstallmentNumber = 0; c.InstallmentCount = 0;
            c.IsAdjustment = false;
            d.Transactions.Add(c);
            return c;
        }

        // ================= categories / accounts =================

        public static int UsageCount(FinanceData d, Category c) =>
            d.Transactions.Count(t => t.CategoryId == c.Id) + d.Recurrences.Count(r => r.CategoryId == c.Id);

        /// <summary>Deletes a category, moving its transactions and recurrences to <paramref name="moveTo"/> (may be null when unused).</summary>
        public static void DeleteCategory(FinanceData d, Category c, Category moveTo)
        {
            if (moveTo == c) throw new ArgumentException("A categoria de destino é a mesma que será excluída.");
            if (moveTo == null && UsageCount(d, c) > 0) throw new InvalidOperationException("Escolha para qual categoria mover os lançamentos.");
            foreach (var t in d.Transactions.Where(t => t.CategoryId == c.Id)) t.CategoryId = moveTo.Id;
            foreach (var r in d.Recurrences.Where(r => r.CategoryId == c.Id)) r.CategoryId = moveTo.Id;
            d.Categories.Remove(c);
        }

        public static bool HasTransactions(FinanceData d, Account a) =>
            d.Transactions.Any(t => t.AccountId == a.Id || t.ToAccountId == a.Id);

        private static FinanceTransaction Clone(FinanceTransaction t) => new()
        {
            Kind = t.Kind, Amount = t.Amount, Date = t.Date, Description = t.Description, CategoryId = t.CategoryId,
            AccountId = t.AccountId, ToAccountId = t.ToAccountId, Paid = t.Paid, Note = t.Note,
            RecurrenceId = t.RecurrenceId, RecurrenceMonth = t.RecurrenceMonth,
            InstallmentGroupId = t.InstallmentGroupId, InstallmentNumber = t.InstallmentNumber, InstallmentCount = t.InstallmentCount,
            IsAdjustment = t.IsAdjustment,
        };
    }
}
