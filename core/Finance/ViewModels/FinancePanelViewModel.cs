using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace HelmetDesk.Core.Finance
{
    /// <summary>One day of the transaction list: the day, its net (adjustments left out) and its transactions, newest first.</summary>
    public sealed record DayGroup(DateTime Day, decimal Net, IReadOnlyList<FinanceTransaction> Items);

    /// <summary>
    /// State and rules of the Finanças panel, without WPF: the month shown, the tab, the filters, the statistics period
    /// and the actions of the lists (mark paid, delete, archive, pause, end). FinanceView draws what this class says and
    /// sends the user's clicks here. The data is read through a function because undo puts a new copy in place.
    /// </summary>
    public sealed class FinancePanelViewModel : ObservableObject
    {
        /// <summary>Colors offered for accounts and categories.</summary>
        public static readonly IReadOnlyList<string> Colors =
            new[] { "#8950D9", "#C8A8F5", "#78B7FF", "#5CC8C8", "#64D6A2", "#F2C36B", "#F2A65A", "#FF8FB1", "#FF6F91", "#9A93A8" };

        /// <summary>Icons offered for categories.</summary>
        public static readonly IReadOnlyList<string> CategoryIcons =
            new[] { "food", "home", "car", "repeat", "fun", "health", "cart", "book", "gift", "salary", "laptop", "coin", "dots" };

        private readonly Func<FinanceData> _data;
        private readonly Func<DateTime> _today;

        public FinancePanelViewModel(Func<FinanceData> data, IFinanceHost host, Func<DateTime>? today = null)
        {
            _data = data;
            Host = host;
            _today = today ?? (() => DateTime.Today);
            _month = FinanceService.MonthStart(Today);
            Transaction = new TransactionFormViewModel(this);
            Account = new AccountFormViewModel(this);
            Check = new CheckBalanceViewModel(this);
            Category = new CategoryFormViewModel(this);
        }

        /// <summary>The forms of the side panel (one shows at a time).</summary>
        public TransactionFormViewModel Transaction { get; }
        public AccountFormViewModel Account { get; }
        public CheckBalanceViewModel Check { get; }
        public CategoryFormViewModel Category { get; }

        /// <summary>The side form was closed: no form is editing anything any more.</summary>
        public void FormsClosed()
        {
            Transaction.Close();
            Account.Close();
            Check.Close();
            Category.Close();
        }

        public FinanceData Data => _data();
        public IFinanceHost Host { get; }
        public DateTime Today => _today().Date;

        public Category? CategoryOf(string? id) => id == null ? null : Data.Categories.FirstOrDefault(c => c.Id == id);
        public Account? AccountOf(string? id) => id == null ? null : Data.Accounts.FirstOrDefault(a => a.Id == id);

        // ================= month and tab =================

        private DateTime _month;
        /// <summary>First day of the month shown.</summary>
        public DateTime Month { get => _month; private set => Set(ref _month, value); }

        public bool IsThisMonth => Month == FinanceService.MonthStart(Today);
        public DateTime MonthEnd => FinanceService.MonthEnd(Month);
        public MonthSummary Summary => FinanceService.Summary(Data, Month);

        /// <summary>Shows the month of <paramref name="day"/> and generates its recurring bills.</summary>
        public void SetMonth(DateTime day)
        {
            Month = FinanceService.MonthStart(day);
            GenerateMonth();
        }

        public void PrevMonth() => SetMonth(Month.AddMonths(-1));
        public void NextMonth() => SetMonth(Month.AddMonths(1));
        public void ThisMonth() => SetMonth(Today);

        /// <summary>Generates the month's recurring bills; tells the host when something was created.</summary>
        public void GenerateMonth()
        {
            if (FinanceService.GenerateRecurring(Data, Month).Count > 0) Host.Changed();
        }

        private string _tab = "tx";
        /// <summary>"tx", "acc", "rec", "cat" or "stats".</summary>
        public string Tab { get => _tab; set => Set(ref _tab, value); }

        // ================= transaction filters =================

        private string _filterAccount = "", _filterCategory = "", _filterKind = "", _filterStatus = "";
        /// <summary>Account id, or "" for all.</summary>
        public string FilterAccount { get => _filterAccount; set => Set(ref _filterAccount, value ?? ""); }
        /// <summary>Category id, or "" for all.</summary>
        public string FilterCategory { get => _filterCategory; set => Set(ref _filterCategory, value ?? ""); }
        /// <summary>"0" expenses, "1" income, "2" transfers, or "" for all.</summary>
        public string FilterKind { get => _filterKind; set => Set(ref _filterKind, value ?? ""); }
        /// <summary>"paid", "pending", "overdue", or "" for all.</summary>
        public string FilterStatus { get => _filterStatus; set => Set(ref _filterStatus, value ?? ""); }

        public bool HasFilters => (FilterAccount + FilterCategory + FilterKind + FilterStatus).Length > 0;

        public void ClearFilters() => FilterAccount = FilterCategory = FilterKind = FilterStatus = "";

        public static IReadOnlyList<(string Id, string Name)> KindFilterOptions() =>
            new[] { ("", T("Todos os tipos")), ("0", T("Despesas")), ("1", T("Receitas")), ("2", T("Transferências")) };

        public static IReadOnlyList<(string Id, string Name)> StatusFilterOptions() =>
            new[] { ("", T("Todos")), ("paid", T("Pagos")), ("pending", T("Pendentes")), ("overdue", T("Vencidos")) };

        /// <summary>"All" plus the active accounts (and the filtered one, even if archived).</summary>
        public List<(string Id, string Name)> AccountFilterOptions() =>
            new[] { ("", T("Todas as contas")) }
                .Concat(Data.Accounts.Where(a => !a.Archived || a.Id == FilterAccount).Select(a => (a.Id, a.Name))).ToList();

        /// <summary>"All" plus every category, expenses first, income ones marked.</summary>
        public List<(string Id, string Name)> CategoryFilterOptions() =>
            new[] { ("", T("Todas as categorias")) }
                .Concat(Data.Categories.OrderBy(c => c.Kind).ThenBy(c => c.Name)
                    .Select(c => (c.Id, c.Name + (c.Kind == CategoryKind.Income ? T(" (receita)") : "")))).ToList();

        /// <summary>The month's transactions that pass the filters, newest first.</summary>
        public List<FinanceTransaction> VisibleTransactions()
        {
            var today = Today;
            string acc = FilterAccount, cat = FilterCategory, kind = FilterKind, status = FilterStatus;
            return FinanceService.InMonth(Data, Month)
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

        /// <summary><see cref="VisibleTransactions"/> grouped by day, with each day's net (transfers and adjustments count as zero).</summary>
        public List<DayGroup> DayGroups() =>
            VisibleTransactions().GroupBy(t => t.Date.Date)
                .Select(day => new DayGroup(day.Key,
                    day.Where(t => !t.IsAdjustment).Sum(t => t.Kind == TxKind.Income ? t.Amount : t.Kind == TxKind.Expense ? -t.Amount : 0),
                    day.ToList()))
                .ToList();

        // ================= accounts, recurrences, categories =================

        private bool _showArchived;
        public bool ShowArchived { get => _showArchived; set => Set(ref _showArchived, value); }

        /// <summary>Accounts tab: active ones first, then by creation; archived ones only with <see cref="ShowArchived"/>.</summary>
        public List<Account> AccountsToShow() =>
            Data.Accounts.Where(a => ShowArchived || !a.Archived).OrderBy(a => a.Archived).ThenBy(a => a.CreatedAt).ToList();

        /// <summary>The recurrence's end month is already behind the current month.</summary>
        public bool IsEnded(Recurrence r) =>
            r.EndMonth.HasValue && FinanceService.MonthStart(r.EndMonth.Value) < FinanceService.MonthStart(Today);

        /// <summary>Recurrences tab: running ones first, then paused, then ended; by day of the month.</summary>
        public List<Recurrence> RecurrencesInOrder() =>
            Data.Recurrences.OrderBy(IsEnded).ThenBy(r => r.Paused).ThenBy(r => r.DayOfMonth).ToList();

        public List<Category> CategoriesInOrder() =>
            Data.Categories.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        // ================= statistics =================

        private int _period = 1;
        /// <summary>Months of the statistics period (1, 3, 6, 12), or 0 for the year to date. It ends at <see cref="Month"/>.</summary>
        public int Period { get => _period; set => Set(ref _period, value); }

        private TxKind _pieKind = TxKind.Expense;
        public TxKind PieKind { get => _pieKind; set => Set(ref _pieKind, value); }

        /// <summary>First month and length of the statistics period.</summary>
        public (DateTime from, int months) StatsPeriod() =>
            Period == 0 ? (new DateTime(Month.Year, 1, 1), Month.Month) : (Month.AddMonths(1 - Period), Period);

        /// <summary>The period compared with: the same length right before (the same months a year earlier for "year").</summary>
        public (DateTime from, int months) PreviousPeriod()
        {
            var (from, months) = StatsPeriod();
            return Period == 0 ? (from.AddYears(-1), months) : (from.AddMonths(-months), months);
        }

        public IReadOnlyList<CategoryTotal> PieData { get; private set; } = new List<CategoryTotal>();

        private int _pieSelected = -1;
        /// <summary>Selected slice of the ring chart, or -1.</summary>
        public int PieSelected { get => _pieSelected; set => Set(ref _pieSelected, value); }

        /// <summary>Recomputes the ring chart for the period and clears the selection.</summary>
        public void LoadPie()
        {
            var (from, months) = StatsPeriod();
            PieData = FinanceService.ByCategory(Data, PieKind, from, months);
            PieSelected = -1;
        }

        public bool HasPieSelection => PieSelected >= 0 && PieSelected < PieData.Count;
        public string? SelectedPieCategoryId => HasPieSelection ? PieData[PieSelected].CategoryId : null;

        /// <summary>The 6 largest transactions of the period, of the selected slice's category when there is one.</summary>
        public List<FinanceTransaction> TopTransactions()
        {
            var (from, months) = StatsPeriod();
            return FinanceService.Largest(Data, PieKind, from, months, 6, HasPieSelection, SelectedPieCategoryId);
        }

        /// <summary>Months of the bar chart: the period, but at least 6 so short periods have something to compare with.</summary>
        public (DateTime start, int months) BarsRange()
        {
            int n = Math.Max(StatsPeriod().months, 6);
            return (Month.AddMonths(1 - n), n);
        }

        // ================= list actions =================

        public void TogglePaid(FinanceTransaction t)
        {
            t.Paid = !t.Paid;
            t.UpdatedAt = DateTime.Now;
            Host.Commit();
        }

        /// <summary>Following installments or months of <paramref name="t"/>'s series.</summary>
        public int FollowingCount(FinanceTransaction t) => FinanceService.Series(Data, t, true).Count - 1;

        public void RequestDeleteTx(FinanceTransaction t)
        {
            int following = FollowingCount(t);
            string? check = t.IsInstallment && following > 0
                ? TN(following, "Excluir também a parcela seguinte", "Excluir também as {0} parcelas seguintes")
                : t.IsRecurring ? T("Excluir também os meses seguintes e encerrar a recorrência") : null;
            Host.Confirm(T("Excluir lançamento?"),
                TF("“{0}” ({1}) será excluído. Logo depois, você ainda pode desfazer pelo aviso no rodapé.", t.Description, Money.Format(t.Amount)),
                T("Excluir"), true, check, ticked => DeleteTx(t, ticked));
        }

        public void DeleteTx(FinanceTransaction t, bool following)
        {
            var before = Snapshot();
            int n = FinanceService.Delete(Data, t, following);
            if (Transaction.Editing == t) Host.ClosePanel();
            Host.Commit();
            Host.Snack(TN(n, "Lançamento excluído", "{0} lançamentos excluídos"), T("Desfazer"), () => Undo(before));
        }

        public void ToggleArchive(Account a)
        {
            var d = Data;
            a.Archived = !a.Archived;
            if (a.Archived && d.LastAccountId == a.Id) d.LastAccountId = d.Accounts.FirstOrDefault(x => !x.Archived)?.Id;
            Host.Commit();
            Host.Snack(TF(a.Archived ? "“{0}” arquivada" : "“{0}” desarquivada", a.Name), a.Archived ? T("Desfazer") : null,
                a.Archived ? () => { a.Archived = false; Host.Commit(); } : null);
        }

        /// <summary>Pausar / Retomar. Resuming generates the month's bill right away.</summary>
        public void TogglePause(Recurrence r)
        {
            r.Paused = !r.Paused;
            if (!r.Paused) GenerateMonth();
            Host.Commit();
        }

        /// <summary>
        /// "Encerrar": after this month by default; with the box ticked, already this month (its pending entry is removed,
        /// a paid one stays).
        /// </summary>
        public void RequestEndRecurrence(Recurrence r)
        {
            var month = Today;
            var monthName = DateText.MonthTitle(month).ToLower(L.Culture);
            bool activeNow = FinanceService.IsActiveIn(r, month);
            Host.Confirm(T("Encerrar recorrência?"),
                TF("“{0}” não vai gerar lançamentos depois de {1}. Pendentes de meses seguintes que já existirem são removidos; os já pagos ficam.", r.Description, monthName),
                T("Encerrar"), true,
                activeNow ? TF("Encerrar já em {0} (remove o lançamento pendente deste mês)", monthName) : null,
                ticked => EndRecurrence(r, activeNow && ticked));
        }

        public void EndRecurrence(Recurrence r, bool includeThisMonth)
        {
            var before = Snapshot();
            var month = Today;
            FinanceService.EndRecurrence(Data, r, includeThisMonth ? month.AddMonths(-1) : month);
            Host.Commit();
            Host.Snack(T("Recorrência encerrada"), T("Desfazer"), () => Undo(before));
        }

        /// <summary>"Excluir recorrência": the model and its pending entries go; paid ones stay unless the box is ticked.</summary>
        public void RequestDeleteRecurrence(Recurrence r)
        {
            var d = Data;
            int paid = d.Transactions.Count(t => t.RecurrenceId == r.Id && t.Paid);
            int pending = d.Transactions.Count(t => t.RecurrenceId == r.Id && !t.Paid);
            var text = TF("“{0}” deixa de existir e não gera mais lançamentos.", r.Description)
                       + (pending > 0 ? " " + TN(pending, "O lançamento pendente é removido.", "Os {0} lançamentos pendentes são removidos.") : "")
                       + (paid > 0 ? " " + TN(paid, "O lançamento já pago fica no histórico como lançamento comum.", "Os {0} lançamentos já pagos ficam no histórico como lançamento comum.") : "");
            Host.Confirm(T("Excluir recorrência?"), text, T("Excluir"), true,
                paid > 0 ? T("Apagar também os já pagos (muda o saldo das contas)") : null,
                ticked => DeleteRecurrence(r, paid > 0 && ticked));
        }

        public void DeleteRecurrence(Recurrence r, bool includePaid)
        {
            var before = Snapshot();
            FinanceService.DeleteRecurrence(Data, r, includePaid);
            Host.Commit();
            Host.Snack(T("Recorrência excluída"), T("Desfazer"), () => Undo(before));
        }

        // ================= undo =================

        /// <summary>A copy of the whole finance data, for undo.</summary>
        public string Snapshot() => JsonSerializer.Serialize(Data);

        /// <summary>Puts a <see cref="Snapshot"/> back and redraws.</summary>
        public void Undo(string snapshot)
        {
            if (JsonSerializer.Deserialize<FinanceData>(snapshot) is { } d) Host.ReplaceData(d);
            Host.Commit();
        }
    }
}
