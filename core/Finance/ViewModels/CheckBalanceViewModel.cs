namespace HelmetDesk.Core.Finance
{
    /// <summary>
    /// "Conferir saldo": the user types the balance the bank shows; the difference becomes an adjustment dated today
    /// (it changes the balance but stays out of income/expense and the chart).
    /// </summary>
    public sealed class CheckBalanceViewModel : ObservableObject
    {
        private readonly FinancePanelViewModel _panel;

        public CheckBalanceViewModel(FinancePanelViewModel panel) => _panel = panel;

        private FinanceData D => _panel.Data;

        /// <summary>The account being checked, or null when the form is closed.</summary>
        public Account? Account { get; private set; }

        private string _realText = "";
        /// <summary>The real balance as typed.</summary>
        public string RealText
        {
            get => _realText;
            set { if (Set(ref _realText, value ?? "")) { Raise(nameof(DiffText)); Raise(nameof(CanSave)); } }
        }

        public void Open(Account a)
        {
            Account = a;
            Raise(nameof(Account)); Raise(nameof(CurrentText));
            RealText = "";
            Raise(nameof(DiffText)); Raise(nameof(CanSave));
        }

        public void Close() => Account = null;

        public string CurrentText => Account == null ? "" : T("Saldo no Helmet Desk: ") + Money.Format(FinanceService.Balance(D, Account));

        /// <summary>The adjustment that "Salvar" would create, or why there is none.</summary>
        public string DiffText
        {
            get
            {
                if (Account == null) return "";
                if (!Money.TryParse(RealText, out var real)) return T("Digite o saldo que aparece no banco ou na carteira.");
                var diff = real - FinanceService.Balance(D, Account);
                return diff == 0 ? T("O saldo já confere. Nada a ajustar.")
                    : TF("Será criado um ajuste de {0} com a data de hoje. Ele muda o saldo, mas não entra em Entradas/Saídas nem no gráfico.", Money.Signed(diff));
            }
        }

        /// <summary>There is a valid balance that differs from the app's.</summary>
        public bool CanSave =>
            Account != null && Money.TryParse(RealText, out var real) && real - FinanceService.Balance(D, Account) != 0;

        public void Save()
        {
            if (Account == null || !Money.TryParse(RealText, out var real)) return;
            var t = FinanceService.Adjust(D, Account, real, _panel.Today);
            _panel.Host.ClosePanel();
            if (t != null)
            {
                _panel.Host.Commit();
                _panel.Host.Snack(T("Ajuste criado: ") + Money.Signed(t.Kind == TxKind.Income ? t.Amount : -t.Amount), null, null);
            }
        }
    }
}
