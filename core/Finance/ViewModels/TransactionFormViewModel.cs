using System;
using System.Collections.Generic;
using System.Linq;

namespace HelmetDesk.Core.Finance
{
    /// <summary>Field of the transaction form that an error message refers to (the view focuses it).</summary>
    public enum TxField { None, Amount, Account, ToAccount, Installments }

    /// <summary>
    /// The new / edit transaction form: its values, the choices of the combos, the autocomplete by description,
    /// validation and saving (with the "apply to the following ones" question for installments and recurrences).
    /// Combo values are ids; "" in <see cref="CategoryId"/> means "Sem categoria".
    /// </summary>
    public sealed class TransactionFormViewModel : ObservableObject
    {
        private readonly FinancePanelViewModel _panel;

        public TransactionFormViewModel(FinancePanelViewModel panel) => _panel = panel;

        private FinanceData D => _panel.Data;
        private IFinanceHost Host => _panel.Host;

        /// <summary>The transaction being edited, or null for a new one.</summary>
        public FinanceTransaction? Editing { get; private set; }
        public bool IsNew => Editing == null;
        public string Title => IsNew ? T("Novo lançamento") : T("Editar lançamento");

        /// <summary>The user chose paid/pending (or the form was prefilled): the date no longer decides it.</summary>
        public bool PaidTouched { get; set; }

        private TxKind _kind;
        public TxKind Kind
        {
            get => _kind;
            set { if (Set(ref _kind, value)) { Raise(nameof(IsTransfer)); Raise(nameof(AccountLabel)); Raise(nameof(ShowInstallments)); } }
        }
        public bool IsTransfer => Kind == TxKind.Transfer;
        public string AccountLabel => IsTransfer ? T("Da conta") : T("Conta");
        public bool ShowInstallments => IsNew && !IsTransfer;
        public bool ShowRepeat => IsNew;

        private string _amountText = "";
        /// <summary>The amount as typed. Changing it clears the error message.</summary>
        public string AmountText
        {
            get => _amountText;
            set { if (Set(ref _amountText, value ?? "")) { Error = null; Raise(nameof(InstallmentHint)); } }
        }

        private string _description = "";
        public string Description { get => _description; set => Set(ref _description, value ?? ""); }

        private string? _categoryId;
        public string? CategoryId { get => _categoryId; set => Set(ref _categoryId, value); }

        private string? _accountId, _toAccountId;
        public string? AccountId { get => _accountId; set => Set(ref _accountId, value); }
        public string? ToAccountId { get => _toAccountId; set => Set(ref _toAccountId, value); }

        private DateTime? _date;
        public DateTime? Date { get => _date; set => Set(ref _date, value); }

        private bool _paid;
        public bool Paid { get => _paid; set => Set(ref _paid, value); }

        private string _note = "";
        public string Note { get => _note; set => Set(ref _note, value ?? ""); }

        private string _installmentsText = "";
        public string InstallmentsText
        {
            get => _installmentsText;
            set { if (Set(ref _installmentsText, value ?? "")) Raise(nameof(InstallmentHint)); }
        }

        private bool _repeat;
        public bool Repeat { get => _repeat; set => Set(ref _repeat, value); }

        private string? _error;
        /// <summary>Validation message, or null.</summary>
        public string? Error { get => _error; private set => Set(ref _error, value); }
        public TxField ErrorField { get; private set; }
        /// <summary>The error is about a field inside "Mais opções", which must be opened.</summary>
        public bool ErrorOpensMore { get; private set; }

        public IReadOnlyList<(string Id, string Name)> CategoryOptions { get; private set; } = new List<(string, string)>();
        public IReadOnlyList<(string Id, string Name)> AccountOptions { get; private set; } = new List<(string, string)>();

        /// <summary>Line about the installment / recurrence the edited transaction belongs to, or null.</summary>
        public string? SeriesInfo { get; private set; }
        /// <summary>"Mais opções" starts open (editing a pending transaction or one with a note).</summary>
        public bool StartWithMore { get; private set; }

        // ================= open =================

        /// <summary>Fills the form for <paramref name="t"/> (edit) or a new transaction (null); <paramref name="prefill"/> gives a new one's values (Duplicar).</summary>
        public void Open(FinanceTransaction? t, FinanceTransaction? prefill = null)
        {
            Editing = t;
            var src = t ?? prefill;
            PaidTouched = t != null || prefill != null;
            Kind = src?.Kind ?? TxKind.Expense;
            AmountText = src == null ? "" : Money.Plain(src.Amount);
            Description = src?.Description ?? "";
            PickCategory(Kind, src?.CategoryId);
            AccountOptions = D.Accounts.Where(a => !a.Archived || a.Id == src?.AccountId || a.Id == src?.ToAccountId).Select(a => (a.Id, a.Name)).ToList();
            AccountId = Pick(AccountOptions, src?.AccountId ?? D.LastAccountId);
            ToAccountId = Pick(AccountOptions, src?.ToAccountId ?? AccountOptions.FirstOrDefault(a => a.Id != AccountId).Id);
            Date = t?.Date ?? _panel.Today;
            Paid = src?.Paid ?? true;
            Note = src?.Note ?? "";
            InstallmentsText = "";
            Repeat = false;
            Error = null;

            SeriesInfo = null;
            if (t != null && t.IsInstallment)
                SeriesInfo = TF("Parcela {0} de {1}. Ao salvar, você pode aplicar a mudança às parcelas seguintes.", t.InstallmentNumber, t.InstallmentCount);
            else if (t != null && t.IsRecurring)
            {
                var r = D.Recurrences.FirstOrDefault(x => x.Id == t.RecurrenceId);
                SeriesInfo = r == null ? T("Gerado por uma recorrência que não existe mais.") : TF("Recorrente: todo dia {0}. Ao salvar, você pode aplicar a mudança aos meses seguintes.", r.DayOfMonth);
            }
            StartWithMore = t != null && (!t.Paid || !string.IsNullOrWhiteSpace(t.Note));
            Raise(nameof(Editing)); Raise(nameof(IsNew)); Raise(nameof(Title)); Raise(nameof(ShowInstallments)); Raise(nameof(ShowRepeat));
        }

        /// <summary>"Duplicar": a new transaction with <paramref name="t"/>'s values, dated today and paid. Nothing is saved until "Salvar".</summary>
        public void Duplicate(FinanceTransaction t)
        {
            var copy = FinanceService.Duplicate(D, t);
            D.Transactions.Remove(copy);
            copy.Date = _panel.Today;
            copy.Paid = true;
            Open(null, copy);
        }

        /// <summary>The form was closed.</summary>
        public void Close() => Editing = null;

        /// <summary>The combo's choice: <paramref name="id"/> when it is there, else the first one (null when empty).</summary>
        private static string? Pick(IReadOnlyList<(string Id, string Name)> options, string? id) =>
            options.Any(o => o.Id == id) ? id : options.Count > 0 ? options[0].Id : null;

        /// <summary>Categories of the kind, keeping <paramref name="select"/> (or the current one) when it fits, else the first.</summary>
        private void PickCategory(TxKind kind, string? select)
        {
            var ck = kind == TxKind.Income ? CategoryKind.Income : CategoryKind.Expense;
            var cats = D.Categories.Where(c => c.Kind == ck).Select(c => (c.Id, c.Name)).ToList();
            var keep = select ?? CategoryId;
            CategoryOptions = new[] { ("", T("Sem categoria")) }.Concat(cats).ToList();
            CategoryId = cats.Any(c => c.Id == keep) ? keep : cats.FirstOrDefault().Id ?? "";
            Raise(nameof(CategoryOptions));
        }

        // ================= user changes =================

        /// <summary>Despesa / Receita / Transferência: the category list follows the kind.</summary>
        public void ChangeKind(TxKind kind)
        {
            Kind = kind;
            PickCategory(kind, null);
        }

        /// <summary>New transaction: dated in the future → pending, today/past → paid (until the user flips the switch).</summary>
        public void ChangeDate(DateTime? date)
        {
            Date = date;
            if (Editing == null && !PaidTouched && date is DateTime d) Paid = d.Date <= _panel.Today;
        }

        public void ChangePaid(bool paid)
        {
            Paid = paid;
            PaidTouched = true;
        }

        /// <summary>Repeating monthly and installments don't go together: ticking one clears the other.</summary>
        public void ChangeRepeat(bool repeat)
        {
            Repeat = repeat;
            if (repeat) InstallmentsText = "";
        }

        public void ChangeInstallments(string text)
        {
            InstallmentsText = text;
            if (int.TryParse(InstallmentsText, out var n) && n > 1) Repeat = false;
        }

        /// <summary>"3× de R$ 33,33" under the installments box, or the reminder that the amount is the total.</summary>
        public string InstallmentHint
        {
            get
            {
                if (int.TryParse(InstallmentsText, out var n) && n > 1 && n <= 360 && Money.TryParse(AmountText, out var v) && v > 0)
                {
                    var parts = FinanceService.SplitInstallments(v, n);
                    return parts[0] == parts[^1] ? TF("{0}× de {1}", n, Money.Format(parts[0]))
                        : TF("{0}× de {1}", n - 1, Money.Format(parts[0])) + " + " + TF("{0}× de {1}", 1, Money.Format(parts[^1]));
                }
                return T("O valor acima é o total da compra.");
            }
        }

        // ================= autocomplete =================

        /// <summary>Earlier descriptions starting like the typed one (not the same text), with their latest amount.</summary>
        public List<(string Description, decimal? Amount)> Suggestions() =>
            FinanceService.Descriptions(D, Description)
                .Where(s => !string.Equals(s, Description.Trim(), StringComparison.CurrentCultureIgnoreCase))
                .Select(s => (s, FinanceService.Suggest(D, s)?.Amount))
                .ToList();

        /// <summary>
        /// Same description as an earlier transaction: fills the amount (if empty), kind, category and account from the
        /// latest one. New transactions only. Returns whether anything was applied.
        /// </summary>
        public bool ApplySuggestion()
        {
            if (Editing != null || !(FinanceService.Suggest(D, Description) is Suggestion s)) return false;
            if (!Money.TryParse(AmountText, out var typed) || typed == 0) AmountText = Money.Plain(s.Amount);
            Kind = s.Kind;
            PickCategory(s.Kind, s.CategoryId ?? "");
            if (s.AccountId != null && D.Accounts.Any(a => a.Id == s.AccountId && !a.Archived))
                AccountId = AccountOptions.Any(o => o.Id == s.AccountId) ? s.AccountId : null;
            return true;
        }

        // ================= save =================

        private bool Fail(string message, TxField field, bool openMore = false)
        {
            ErrorField = field;
            ErrorOpensMore = openMore;
            Error = null; // raise even when the same message shows again
            Error = message;
            return false;
        }

        /// <summary>
        /// Validates and saves. Returns false when something is missing (<see cref="Error"/> says what). Editing part of a
        /// series asks first whether to apply the change to the following ones; that answer comes later through the host.
        /// </summary>
        public bool Save()
        {
            if (!Money.TryParse(AmountText, out var amount) || amount <= 0) return Fail(T("Informe um valor maior que zero (ex.: 1.234,56)."), TxField.Amount);
            var kind = Kind;
            string? acc = AccountId, to = kind == TxKind.Transfer ? ToAccountId : null;
            if (string.IsNullOrEmpty(acc)) return Fail(T("Escolha uma conta (crie uma na aba Contas)."), TxField.Account);
            if (kind == TxKind.Transfer && (string.IsNullOrEmpty(to) || to == acc)) return Fail(T("Escolha uma conta de destino diferente da de origem."), TxField.ToAccount);
            int installments = 1;
            if (Editing == null && kind != TxKind.Transfer && InstallmentsText.Trim().Length > 0
                && (!int.TryParse(InstallmentsText.Trim(), out installments) || installments < 1 || installments > 360))
                return Fail(T("Parcelas: um número de 1 a 360."), TxField.Installments, openMore: true);
            bool repeat = Editing == null && Repeat;
            if (repeat && installments > 1) return Fail(T("Escolha parcelas ou repetição, não os dois."), TxField.Installments);

            string? cat = kind == TxKind.Transfer ? null : (CategoryId is { Length: > 0 } c ? c : null);
            string desc = Description.Trim();
            if (desc.Length == 0) desc = kind == TxKind.Transfer ? T("Transferência") : _panel.CategoryOf(cat)?.Name ?? (kind == TxKind.Income ? T("Receita") : T("Despesa"));
            var date = (Date ?? _panel.Today).Date;
            bool paid = Paid;
            string note = Note.Trim();
            var d = D;

            if (Editing == null)
            {
                var t = new FinanceTransaction
                {
                    Kind = kind, Amount = amount, Date = date, Description = desc, CategoryId = cat, AccountId = acc,
                    ToAccountId = to, Paid = paid, Note = note,
                };
                d.LastAccountId = acc;
                if (installments > 1) FinanceService.AddInstallments(d, t, installments);
                else
                {
                    d.Transactions.Add(t);
                    if (repeat) FinanceService.StartRecurrence(d, t);
                }
                _panel.SetMonth(date); // show the month the new transaction landed in
                Host.ClosePanel();
                Host.Commit();
                return true;
            }

            var e0 = Editing;
            bool seriesFields = e0.Amount != amount || e0.Kind != kind || e0.CategoryId != cat || e0.AccountId != acc
                                || e0.ToAccountId != to || e0.Description != desc || (e0.Note ?? "") != note;
            int following = _panel.FollowingCount(e0);
            void Apply(bool toFollowing)
            {
                e0.Amount = amount; e0.Kind = kind; e0.CategoryId = cat; e0.AccountId = acc; e0.ToAccountId = to;
                e0.Description = desc; e0.Date = date; e0.Paid = paid; e0.Note = note; e0.UpdatedAt = DateTime.Now;
                if (toFollowing) FinanceService.ApplyToFollowing(d, e0);
                Host.ClosePanel();
                Host.Commit();
            }
            if (seriesFields && (e0.IsRecurring || (e0.IsInstallment && following > 0)))
            {
                string check = e0.IsInstallment
                    ? TN(following, "Aplicar também à parcela seguinte", "Aplicar também às {0} parcelas seguintes")
                    : T("Aplicar também aos meses seguintes (atualiza a recorrência)");
                Host.Confirm(T("Salvar alteração?"), TF(e0.IsInstallment ? "“{0}” faz parte de um parcelamento." : "“{0}” faz parte de uma recorrência.", e0.Description),
                    T("Salvar"), false, check, Apply);
                return true;
            }
            Apply(false);
            return true;
        }
    }
}
