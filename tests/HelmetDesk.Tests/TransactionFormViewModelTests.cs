using System;
using System.Linq;
using HelmetDesk.Core.Finance;
using Xunit;

namespace HelmetDesk.Tests
{
    public class TransactionFormViewModelTests
    {
        private static readonly DateTime Today = new(2026, 3, 15);

        private static (FakeFinanceHost host, FinancePanelViewModel panel, TransactionFormViewModel f, Account a, Account b, Category food, Category salary) Setup()
        {
            var host = new FakeFinanceHost();
            FinanceService.Seed(host.Data);
            var a = host.Data.Accounts[0];
            var b = new Account { Name = "Banco", Type = AccountType.Checking };
            host.Data.Accounts.Add(b);
            var panel = new FinancePanelViewModel(() => host.Data, host, () => Today);
            return (host, panel, panel.Transaction, a, b,
                host.Data.Categories.First(c => c.Name == "Alimentação"), host.Data.Categories.First(c => c.Name == "Salário"));
        }

        [Fact]
        public void New_form_starts_with_defaults()
        {
            var (host, _, f, a, b, food, _) = Setup();
            host.Data.LastAccountId = b.Id;
            f.Open(null);
            Assert.True(f.IsNew);
            Assert.Equal("Novo lançamento", f.Title);
            Assert.Equal(TxKind.Expense, f.Kind);
            Assert.Equal("", f.AmountText);
            Assert.Equal(food.Id, f.CategoryId);                 // first expense category
            Assert.Equal("", f.CategoryOptions[0].Id);           // "Sem categoria" first
            Assert.Equal(b.Id, f.AccountId);                     // the last account used
            Assert.Equal(a.Id, f.ToAccountId);                   // another one
            Assert.Equal(Today, f.Date);
            Assert.True(f.Paid);
            Assert.False(f.PaidTouched);
            Assert.True(f.ShowInstallments);
            Assert.True(f.ShowRepeat);
            Assert.Null(f.SeriesInfo);
            Assert.False(f.StartWithMore);
            Assert.Equal("O valor acima é o total da compra.", f.InstallmentHint);
        }

        [Fact]
        public void A_new_form_keeps_the_category_chosen_last_time_when_it_fits()
        {
            var (_, _, f, _, _, _, _) = Setup();
            f.Open(null);
            var home = f.CategoryOptions.First(o => o.Name == "Moradia").Id;
            f.CategoryId = home;
            f.Open(null);
            Assert.Equal(home, f.CategoryId);
            f.ChangeKind(TxKind.Income);          // not an income category: first income one
            Assert.Equal("Salário", f.CategoryOptions.First(o => o.Id == f.CategoryId).Name);
        }

        [Fact]
        public void Editing_fills_the_values_and_hides_installments_and_repeat()
        {
            var (host, _, f, a, b, food, _) = Setup();
            b.Archived = true;
            var t = new FinanceTransaction { Kind = TxKind.Expense, Amount = 1234.5m, Date = new DateTime(2026, 3, 2), Description = "Mercado", CategoryId = food.Id, AccountId = b.Id, Paid = false, Note = "obs" };
            host.Data.Transactions.Add(t);
            f.Open(t);
            Assert.False(f.IsNew);
            Assert.Equal("Editar lançamento", f.Title);
            Assert.Equal("1.234,50", f.AmountText);
            Assert.Equal(b.Id, f.AccountId);                     // archived, but it is this transaction's account
            Assert.Contains(f.AccountOptions, o => o.Id == b.Id);
            Assert.Equal(new DateTime(2026, 3, 2), f.Date);
            Assert.False(f.Paid);
            Assert.True(f.PaidTouched);
            Assert.False(f.ShowInstallments);
            Assert.False(f.ShowRepeat);
            Assert.True(f.StartWithMore);
        }

        [Fact]
        public void Series_info_for_installments_and_recurrences()
        {
            var (host, _, f, a, _, food, _) = Setup();
            var first = new FinanceTransaction { Kind = TxKind.Expense, Amount = 90m, Date = Today, AccountId = a.Id, CategoryId = food.Id, Description = "TV", Paid = true };
            FinanceService.AddInstallments(host.Data, first, 3);
            f.Open(host.Data.Transactions[1]);
            Assert.Equal("Parcela 2 de 3. Ao salvar, você pode aplicar a mudança às parcelas seguintes.", f.SeriesInfo);

            var orphan = new FinanceTransaction { RecurrenceId = "gone", RecurrenceMonth = "2026-03", AccountId = a.Id, Paid = true };
            f.Open(orphan);
            Assert.Equal("Gerado por uma recorrência que não existe mais.", f.SeriesInfo);
        }

        [Fact]
        public void Date_decides_paid_until_the_user_flips_it()
        {
            var (_, _, f, _, _, _, _) = Setup();
            f.Open(null);
            f.ChangeDate(Today.AddDays(3));
            Assert.False(f.Paid);
            f.ChangeDate(Today);
            Assert.True(f.Paid);
            f.ChangePaid(false);
            f.ChangeDate(Today.AddDays(-1));
            Assert.False(f.Paid);
        }

        [Fact]
        public void Installments_and_repeat_exclude_each_other()
        {
            var (_, _, f, _, _, _, _) = Setup();
            f.Open(null);
            f.ChangeRepeat(true);
            f.ChangeInstallments("3");
            Assert.False(f.Repeat);
            f.ChangeRepeat(true);
            Assert.Equal("", f.InstallmentsText);
            f.ChangeInstallments("1");
            Assert.True(f.Repeat);
        }

        [Fact]
        public void Installment_hint_shows_the_split()
        {
            var (_, _, f, _, _, _, _) = Setup();
            f.Open(null);
            f.AmountText = "100,00";
            f.ChangeInstallments("3");
            Assert.Equal("2× de R$ 33,33 + 1× de R$ 33,34", f.InstallmentHint);
            f.ChangeInstallments("4");
            Assert.Equal("4× de R$ 25,00", f.InstallmentHint);
            f.ChangeInstallments("361");
            Assert.Equal("O valor acima é o total da compra.", f.InstallmentHint);
        }

        [Fact]
        public void Validation_messages_point_at_the_field()
        {
            var (host, _, f, a, b, _, _) = Setup();
            f.Open(null);
            Assert.False(f.Save());
            Assert.Equal("Informe um valor maior que zero (ex.: 1.234,56).", f.Error);
            Assert.Equal(TxField.Amount, f.ErrorField);

            f.AmountText = "10";
            Assert.Null(f.Error);                                // typing clears it
            f.ChangeKind(TxKind.Transfer);
            f.ToAccountId = f.AccountId;
            Assert.False(f.Save());
            Assert.Equal(TxField.ToAccount, f.ErrorField);

            f.ChangeKind(TxKind.Expense);
            f.ChangeInstallments("abc");
            Assert.False(f.Save());
            Assert.Equal("Parcelas: um número de 1 a 360.", f.Error);
            Assert.True(f.ErrorOpensMore);

            f.ChangeInstallments("2");
            f.Repeat = true;
            Assert.False(f.Save());
            Assert.Equal("Escolha parcelas ou repetição, não os dois.", f.Error);
            Assert.False(f.ErrorOpensMore);

            f.AccountId = null;
            Assert.False(f.Save());
            Assert.Equal(TxField.Account, f.ErrorField);
            Assert.Empty(host.Data.Transactions);
            Assert.Equal(0, host.Commits);
        }

        [Fact]
        public void Saving_a_new_one_shows_its_month_and_remembers_the_account()
        {
            var (host, panel, f, a, b, food, _) = Setup();
            f.Open(null);
            f.AmountText = "45,90";
            f.AccountId = b.Id;
            f.ChangeDate(new DateTime(2026, 5, 2));
            Assert.True(f.Save());
            var t = Assert.Single(host.Data.Transactions);
            Assert.Equal(45.90m, t.Amount);
            Assert.Equal("Alimentação", t.Description);          // empty description: the category's name
            Assert.Equal(food.Id, t.CategoryId);
            Assert.False(t.Paid);                                // future date
            Assert.Equal(b.Id, host.Data.LastAccountId);
            Assert.Equal(new DateTime(2026, 5, 1), panel.Month);
            Assert.Equal(new[] { "close", "commit" }, host.Calls);
        }

        [Fact]
        public void Saving_with_installments_or_repeat()
        {
            var (host, _, f, _, _, _, _) = Setup();
            f.Open(null);
            f.AmountText = "300";
            f.Description = " Geladeira ";
            f.ChangeInstallments("3");
            Assert.True(f.Save());
            Assert.Equal(3, host.Data.Transactions.Count);
            Assert.All(host.Data.Transactions, t => Assert.Equal(100m, t.Amount));

            f.Open(null);
            f.AmountText = "50";
            f.Description = "Academia";
            f.ChangeRepeat(true);
            Assert.True(f.Save());
            Assert.Single(host.Data.Recurrences);
        }

        [Fact]
        public void Transfer_without_description_and_without_category()
        {
            var (host, _, f, a, b, _, _) = Setup();
            f.Open(null);
            f.ChangeKind(TxKind.Transfer);
            f.AmountText = "200";
            f.AccountId = a.Id;
            f.ToAccountId = b.Id;
            Assert.True(f.Save());
            var t = Assert.Single(host.Data.Transactions);
            Assert.Equal("Transferência", t.Description);
            Assert.Null(t.CategoryId);
            Assert.Equal(b.Id, t.ToAccountId);
        }

        [Fact]
        public void Editing_part_of_a_series_asks_whether_to_change_the_following_ones()
        {
            var (host, _, f, a, _, food, _) = Setup();
            var first = new FinanceTransaction { Kind = TxKind.Expense, Amount = 300m, Date = Today, AccountId = a.Id, CategoryId = food.Id, Description = "TV", Paid = true };
            FinanceService.AddInstallments(host.Data, first, 3);
            var t = host.Data.Transactions[0];

            f.Open(t);
            f.AmountText = "120";
            host.Answer = true;
            Assert.True(f.Save());
            Assert.Equal("Aplicar também às 2 parcelas seguintes", host.LastDialog.Value.check);
            Assert.False(host.LastDialog.Value.danger);
            Assert.All(host.Data.Transactions, x => Assert.Equal(120m, x.Amount));

            f.Open(t);
            f.AmountText = "130";
            host.Answer = null;                                  // cancelled: nothing changes
            Assert.True(f.Save());
            Assert.Equal(120m, t.Amount);

            f.Open(t);
            f.ChangeDate(Today.AddDays(1));                      // only the date: no question
            host.Calls.Clear();
            Assert.True(f.Save());
            Assert.Equal(new[] { "close", "commit" }, host.Calls);
        }

        [Fact]
        public void Suggestion_fills_from_the_latest_transaction_with_that_description()
        {
            var (host, _, f, a, b, _, salary) = Setup();
            host.Data.Transactions.Add(new FinanceTransaction { Kind = TxKind.Income, Amount = 5000m, Date = new DateTime(2026, 2, 5), Description = "Salário empresa", CategoryId = salary.Id, AccountId = b.Id, Paid = true });
            f.Open(null);
            f.Description = "sal";
            Assert.Equal(new[] { ("Salário empresa", (decimal?)5000m) }, f.Suggestions());
            Assert.False(f.ApplySuggestion());                   // not a full description

            f.Description = "Salário empresa";
            Assert.Empty(f.Suggestions());                       // the same text is not suggested
            Assert.True(f.ApplySuggestion());
            Assert.Equal(TxKind.Income, f.Kind);
            Assert.Equal("5.000,00", f.AmountText);
            Assert.Equal(salary.Id, f.CategoryId);
            Assert.Equal(b.Id, f.AccountId);

            f.AmountText = "4.000,00";                           // a typed amount stays
            Assert.True(f.ApplySuggestion());
            Assert.Equal("4.000,00", f.AmountText);
        }

        [Fact]
        public void Duplicate_prefills_a_new_one_dated_today_without_saving()
        {
            var (host, _, f, a, _, food, _) = Setup();
            var t = new FinanceTransaction { Kind = TxKind.Expense, Amount = 80m, Date = new DateTime(2026, 1, 3), Description = "Pizza", CategoryId = food.Id, AccountId = a.Id, Paid = false };
            host.Data.Transactions.Add(t);
            f.Duplicate(t);
            Assert.Single(host.Data.Transactions);
            Assert.True(f.IsNew);
            Assert.Equal("Pizza", f.Description);
            Assert.Equal("80,00", f.AmountText);
            Assert.Equal(Today, f.Date);
            Assert.True(f.Paid);
            Assert.True(f.PaidTouched);
        }

        [Fact]
        public void Deleting_the_transaction_being_edited_closes_the_form()
        {
            var (host, panel, f, a, _, food, _) = Setup();
            var t = new FinanceTransaction { Kind = TxKind.Expense, Amount = 80m, Date = Today, Description = "Pizza", CategoryId = food.Id, AccountId = a.Id, Paid = true };
            host.Data.Transactions.Add(t);
            f.Open(t);
            panel.DeleteTx(t, false);
            Assert.Equal(1, host.Closes);
            panel.FormsClosed();
            Assert.Null(f.Editing);
        }
    }
}
