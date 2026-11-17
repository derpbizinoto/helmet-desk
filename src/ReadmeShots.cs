using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using HelmetDesk.Core.Finance;

namespace HelmetDesk
{
    public partial class MainWindow
    {
        /// <summary>
        /// Developer aid: the README screenshots (run with --readme-shots &lt;folder&gt; on an empty HELMETDESK_DATA folder,
        /// with HELMETDESK_LANG and HELMETDESK_THEME set). Fills tidy sample data in the current language and saves
        /// hoje/calendario/financas/estatisticas.png (plus two Settings captures for checking). Never run it on real data: the sample data is saved.
        /// </summary>
        public async Task ReadmeShots(string dir)
        {
            string S(string pt, string en) => L.IsEnglish ? en : pt;
            async Task Shot(string name)
            {
                await Snapshots.Idle(Dispatcher);
                await Task.Delay(250);
                await Snapshots.Idle(Dispatcher);
                Snapshots.Save(this, Path.Combine(dir, name + ".png"));
            }
            Motion.Off = true;
            SoundService.Muted = true;
            Directory.CreateDirectory(dir);

            // ---------- notes and tasks ----------
            var today = DateTime.Today;
            var labels = Store.Data.Labels;
            string Lb(int i) => labels[Math.Min(i, labels.Count - 1)].Id;
            Item AddTask(string pt, string en, int day, string time, int est, int prio, int label, bool done = false)
            {
                var it = Store.NewItem(ItemKind.Task, today.AddDays(day));
                it.Title = S(pt, en); it.Time = time; it.EstimateMinutes = est; it.Priority = prio; it.LabelId = Lb(label);
                if (done) { it.Done = true; it.DoneAt = DateTime.Now; }
                return it;
            }
            foreach (var old in Store.Data.Items.Where(i => i.IsTask).ToList()) Store.Data.Items.Remove(old);
            AddTask("Planejar a semana", "Plan the week", 0, "09:00", 30, 2, 2, done: true);
            AddTask("Responder e-mails dos clientes", "Reply to client e-mails", 0, "10:00", 45, 2, 2);
            AddTask("Revisar roteiro do vídeo", "Review the video script", 0, "14:00", 90, 3, 4);
            AddTask("Academia", "Gym", 0, "18:30", 60, 1, 1);
            AddTask("Pagar conta de luz", "Pay the electricity bill", -1, "", 10, 3, 6);
            AddTask("Gravar vídeo novo", "Record the new video", 1, "10:00", 120, 2, 4);
            AddTask("Reunião de pauta", "Planning meeting", 1, "15:00", 60, 2, 2);
            AddTask("Estudar inglês", "Study Spanish", 2, "20:00", 45, 1, 3);
            AddTask("Editar vídeo", "Edit the video", 3, "09:30", 180, 2, 4);
            AddTask("Comprar presente de aniversário", "Buy a birthday present", 4, "", 30, 1, 1);
            AddTask("Faxina", "House cleaning", 5, "", 120, 1, 6);
            AddTask("Enviar orçamento", "Send the quote", 8, "11:00", 30, 3, 2);
            AddTask("Consulta no dentista", "Dentist appointment", 10, "16:00", 60, 2, 5);
            AddTask("Backup do computador", "Back up the computer", 13, "", 20, 1, 7);
            var note = Store.NewItem(ItemKind.Note, today);
            note.Title = S("Ideias de vídeo", "Video ideas"); note.LabelId = Lb(4);
            note.Body = S("Bastidores da edição\nComo eu organizo a semana", "Behind the edit\nHow I plan my week");

            // ---------- finances ----------
            var D = Store.Data.Finance;
            var m = new DateTime(today.Year, today.Month, 1);
            var wallet = D.Accounts[0];
            wallet.InitialBalance = 180m;
            var bank = new Account { Name = S("Banco", "Bank"), Type = AccountType.Checking, InitialBalance = 2350m, Color = "#8950D9" };
            var card = new Account { Name = S("Cartão de crédito", "Credit card"), Type = AccountType.CreditCard, Color = "#F2A65A" };
            var save = new Account { Name = S("Reserva", "Savings"), Type = AccountType.Savings, InitialBalance = 5000m, Color = "#64D6A2" };
            D.Accounts.AddRange(new[] { bank, card, save });
            D.LastAccountId = bank.Id;
            string C(string pt) => D.Categories.First(c => c.Name == T(pt)).Id;
            void Tx(TxKind k, decimal v, int day, string pt, string en, string cat, Account a, bool paid) =>
                D.Transactions.Add(new FinanceTransaction
                {
                    Kind = k, Amount = v, Date = m.AddDays(Math.Min(day, DateTime.DaysInMonth(m.Year, m.Month)) - 1),
                    Description = S(pt, en), CategoryId = cat, AccountId = a.Id, Paid = paid
                });
            var E = TxKind.Expense; var I = TxKind.Income;
            Tx(I, 6500m, 5, "Salário", "Salary", C("Salário"), bank, true);
            Tx(I, 1200m, 12, "Freela edição de vídeo", "Video editing gig", C("Freelance"), bank, today.Day >= 12);
            Tx(E, 312.47m, 3, "Mercado", "Groceries", C("Alimentação"), bank, true);
            Tx(E, 89.90m, Math.Max(1, today.Day - 1), "Padaria e feira", "Bakery and market", C("Alimentação"), wallet, true);
            Tx(E, 64.00m, today.Day, "Uber", "Uber", C("Transporte"), card, true);
            Tx(E, 187.35m, Math.Max(1, today.Day - 3), "Conta de luz", "Electricity bill", C("Moradia"), bank, false);
            Tx(E, 120.00m, today.Day + 4, "Academia", "Gym", C("Saúde"), bank, false);
            Tx(E, 230m, 18, "Cinema e jantar", "Movie and dinner", C("Lazer"), card, today.Day >= 18);
            var rent = new FinanceTransaction { Kind = E, Amount = 1800m, Date = m.AddDays(9), Description = S("Aluguel", "Rent"), CategoryId = C("Moradia"), AccountId = bank.Id, Paid = today.Day >= 10 };
            D.Transactions.Add(rent); FinanceService.StartRecurrence(D, rent);
            var stream = new FinanceTransaction { Kind = E, Amount = 55.90m, Date = m.AddDays(14), Description = "Streaming", CategoryId = C("Assinaturas"), AccountId = card.Id, Paid = today.Day >= 15 };
            D.Transactions.Add(stream); FinanceService.StartRecurrence(D, stream);
            FinanceService.AddInstallments(D, new FinanceTransaction { Kind = E, Amount = 2399m, Date = m.AddDays(7), Description = S("Celular", "Phone"), CategoryId = C("Outros"), AccountId = card.Id, Paid = true }, 10);
            D.Transactions.Add(new FinanceTransaction { Kind = TxKind.Transfer, Amount = 500m, Date = m.AddDays(5), Description = S("Guardar na reserva", "Move to savings"), AccountId = bank.Id, ToAccountId = save.Id, Paid = true });
            var mm = m;
            for (int k = 1; k <= 5; k++)
            {
                m = mm.AddMonths(-k);
                Tx(I, 6500m, 5, "Salário", "Salary", C("Salário"), bank, true);
                if (k % 2 == 1) Tx(I, 800m + k * 150m, 18, "Freela", "Freelance gig", C("Freelance"), bank, true);
                Tx(E, 1800m, 10, "Aluguel", "Rent", C("Moradia"), bank, true);
                Tx(E, 900m + k * 85m, 8, "Mercado do mês", "Monthly groceries", C("Alimentação"), card, true);
                Tx(E, 210m + k * 40m, 15, "Combustível", "Gas", C("Transporte"), card, true);
                Tx(E, k == 3 ? 1450m : 160m, 21, k == 3 ? "Show + viagem" : "Cinema", k == 3 ? "Concert + trip" : "Movies", C("Lazer"), card, true);
                Tx(E, 120m, 2, "Academia", "Gym", C("Saúde"), bank, true);
            }
            m = mm;
            Store.MarkDirty();
            Refresh(animate: false);

            NavToday.IsChecked = true; await Shot("hoje");
            NavCalendar.IsChecked = true; await Shot("calendario");
            NavFinance.IsChecked = true;
            FinanceView.ShowTab("tx");
            await Shot("financas");
            FinanceView.ShowTab("stats");
            FinanceView.SetPeriod(6);
            await Shot("estatisticas");
            NavSettings.IsChecked = true; await Shot("configuracoes");
            await Snapshots.Idle(Dispatcher); SettingsView.ScrollToVerticalOffset(SettingsView.ScrollableHeight - 380); await Shot("configuracoes-2");
        }
    }
}
