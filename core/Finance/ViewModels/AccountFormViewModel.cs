using System.Linq;

namespace HelmetDesk.Core.Finance
{
    /// <summary>Field of the account form that an error message refers to.</summary>
    public enum AccountField { None, Name, Initial }

    /// <summary>The new / edit account form: values, validation, saving and deleting (only accounts nothing refers to).</summary>
    public sealed class AccountFormViewModel : ObservableObject
    {
        private readonly FinancePanelViewModel _panel;

        public AccountFormViewModel(FinancePanelViewModel panel) => _panel = panel;

        private FinanceData D => _panel.Data;
        private IFinanceHost Host => _panel.Host;

        /// <summary>The account being edited, or null for a new one.</summary>
        public Account? Editing { get; private set; }
        public bool IsNew => Editing == null;
        public string Title => IsNew ? T("Nova conta") : T("Editar conta");

        private string _name = "";
        public string Name { get => _name; set => Set(ref _name, value ?? ""); }

        private AccountType _type;
        public AccountType Type { get => _type; set => Set(ref _type, value); }

        private string _initialText = "";
        public string InitialText { get => _initialText; set => Set(ref _initialText, value ?? ""); }

        private bool _archived;
        public bool Archived { get => _archived; set => Set(ref _archived, value); }

        private string _color = "";
        public string Color { get => _color; set => Set(ref _color, value ?? ""); }

        /// <summary>"Arquivada" is offered only when editing.</summary>
        public bool ShowArchive => !IsNew;
        /// <summary>Only an account without transactions or recurrences can be deleted.</summary>
        public bool CanDelete { get; private set; }

        private string? _error;
        public string? Error { get => _error; private set => Set(ref _error, value); }
        public AccountField ErrorField { get; private set; }

        public void Open(Account? a)
        {
            Editing = a;
            Name = a?.Name ?? "";
            Type = a?.Type ?? AccountType.Checking;
            InitialText = a == null ? "" : Money.Plain(a.InitialBalance);
            Archived = a?.Archived ?? false;
            CanDelete = a != null && !FinanceService.HasTransactions(D, a) && D.Recurrences.All(r => r.AccountId != a.Id && r.ToAccountId != a.Id);
            Error = null;
            Color = a?.Color ?? FinancePanelViewModel.Colors[D.Accounts.Count % FinancePanelViewModel.Colors.Count];
            Raise(nameof(Editing)); Raise(nameof(IsNew)); Raise(nameof(Title)); Raise(nameof(ShowArchive)); Raise(nameof(CanDelete));
        }

        public void Close() => Editing = null;

        private bool Fail(string message, AccountField field)
        {
            ErrorField = field;
            Error = null;
            Error = message;
            return false;
        }

        /// <summary>Validates and saves. Returns false when something is wrong (<see cref="Error"/> says what).</summary>
        public bool Save()
        {
            var name = Name.Trim();
            if (name.Length == 0) return Fail(T("Dê um nome à conta."), AccountField.Name);
            decimal initial = 0;
            if (InitialText.Trim().Length > 0 && !Money.TryParse(InitialText, out initial))
                return Fail(T("Saldo inicial inválido (ex.: 1.234,56 ou -200)."), AccountField.Initial);
            var d = D;
            var a = Editing ?? new Account();
            a.Name = name;
            a.Type = Type;
            a.InitialBalance = initial;
            a.Color = Color;
            a.Icon = a.Type.ToString();
            if (Editing == null) { d.Accounts.Add(a); d.LastAccountId ??= a.Id; }
            else a.Archived = Archived;
            Host.ClosePanel();
            Host.Commit();
            return true;
        }

        public void RequestDelete()
        {
            var a = Editing;
            if (a == null) return;
            Host.Confirm(T("Excluir conta?"), TF("“{0}” não tem lançamentos e será excluída.", a.Name), T("Excluir"), true, null, _ =>
            {
                var d = D;
                d.Accounts.Remove(a);
                if (d.LastAccountId == a.Id) d.LastAccountId = d.Accounts.FirstOrDefault(x => !x.Archived)?.Id;
                Host.ClosePanel();
                Host.Commit();
            });
        }
    }
}
