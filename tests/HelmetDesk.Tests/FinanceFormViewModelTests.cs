using System;
using System.Linq;
using HelmetDesk.Core.Finance;
using Xunit;

namespace HelmetDesk.Tests
{
    /// <summary>Account, "Conferir saldo" and category forms.</summary>
    public class FinanceFormViewModelTests
    {
        private static readonly DateTime Today = new(2026, 3, 15);

        private static (FakeFinanceHost host, FinancePanelViewModel panel, Account a) Setup()
        {
            var host = new FakeFinanceHost();
            FinanceService.Seed(host.Data);
            var panel = new FinancePanelViewModel(() => host.Data, host, () => Today);
            return (host, panel, host.Data.Accounts[0]);
        }

        private static Category Cat(FinanceData d, string name, CategoryKind kind = CategoryKind.Expense) =>
            d.Categories.First(c => c.Name == name && c.Kind == kind);

        // ================= account =================

        [Fact]
        public void New_account_defaults_and_save()
        {
            var (host, panel, _) = Setup();
            var f = panel.Account;
            f.Open(null);
            Assert.Equal("Nova conta", f.Title);
            Assert.Equal(AccountType.Checking, f.Type);
            Assert.Equal(FinancePanelViewModel.Colors[1], f.Color);   // one account already: second color
            Assert.False(f.ShowArchive);
            Assert.False(f.CanDelete);

            Assert.False(f.Save());
            Assert.Equal("Dê um nome à conta.", f.Error);
            Assert.Equal(AccountField.Name, f.ErrorField);

            f.Name = "  Nubank ";
            f.InitialText = "abc";
            Assert.False(f.Save());
            Assert.Equal(AccountField.Initial, f.ErrorField);

            f.InitialText = "-200";
            f.Type = AccountType.CreditCard;
            Assert.True(f.Save());
            var a = host.Data.Accounts[^1];
            Assert.Equal("Nubank", a.Name);
            Assert.Equal(-200m, a.InitialBalance);
            Assert.Equal("CreditCard", a.Icon);
            Assert.Equal(new[] { "close", "commit" }, host.Calls);
        }

        [Fact]
        public void Edit_account_can_archive_and_delete_only_when_unused()
        {
            var (host, panel, a) = Setup();
            var f = panel.Account;
            f.Open(a);
            Assert.Equal("Editar conta", f.Title);
            Assert.True(f.ShowArchive);
            Assert.True(f.CanDelete);
            f.Archived = true;
            f.InitialText = "";
            Assert.True(f.Save());
            Assert.True(a.Archived);
            Assert.Equal(0m, a.InitialBalance);

            host.Data.Recurrences.Add(new Recurrence { ToAccountId = a.Id });
            f.Open(a);
            Assert.False(f.CanDelete);
        }

        [Fact]
        public void Deleting_the_default_account_picks_another_one()
        {
            var (host, panel, a) = Setup();
            var b = new Account { Name = "Banco" };
            host.Data.Accounts.Add(b);
            host.Data.LastAccountId = a.Id;
            panel.Account.Open(a);
            host.Answer = null;
            panel.Account.RequestDelete();
            Assert.Equal(2, host.Data.Accounts.Count);
            host.Answer = false;
            panel.Account.RequestDelete();
            Assert.Equal(new[] { b }, host.Data.Accounts);
            Assert.Equal(b.Id, host.Data.LastAccountId);
            Assert.Equal("“Carteira” não tem lançamentos e será excluída.", host.LastDialog.Value.text);
        }

        // ================= check balance =================

        [Fact]
        public void Check_balance_explains_the_adjustment_and_creates_it()
        {
            var (host, panel, a) = Setup();
            a.InitialBalance = 100m;
            var f = panel.Check;
            f.Open(a);
            Assert.Equal("Saldo no Helmet Desk: R$ 100,00", f.CurrentText.Replace(' ', ' '));
            Assert.Equal("Digite o saldo que aparece no banco ou na carteira.", f.DiffText);
            Assert.False(f.CanSave);
            f.RealText = "100";
            Assert.Equal("O saldo já confere. Nada a ajustar.", f.DiffText);
            Assert.False(f.CanSave);
            f.RealText = "75,50";
            Assert.True(f.CanSave);
            Assert.StartsWith("Será criado um ajuste de", f.DiffText);

            f.Save();
            var t = Assert.Single(host.Data.Transactions);
            Assert.True(t.IsAdjustment);
            Assert.Equal(TxKind.Expense, t.Kind);
            Assert.Equal(24.50m, t.Amount);
            Assert.Equal(Today, t.Date);
            Assert.Equal(75.50m, FinanceService.Balance(host.Data, a));
            Assert.Equal(new[] { "close", "commit", "snack" }, host.Calls);
            Assert.Null(host.LastSnack.Value.action);
        }

        [Fact]
        public void Check_balance_without_difference_only_closes()
        {
            var (host, panel, a) = Setup();
            panel.Check.Open(a);
            panel.Check.RealText = "0";
            panel.Check.Save();
            Assert.Empty(host.Data.Transactions);
            Assert.Equal(new[] { "close" }, host.Calls);
            panel.FormsClosed();
            Assert.Null(panel.Check.Account);
            panel.Check.Save();                                  // closed: nothing happens
            Assert.Single(host.Calls);
        }

        // ================= category =================

        [Fact]
        public void New_category_validates_the_name_per_kind()
        {
            var (host, panel, _) = Setup();
            var f = panel.Category;
            f.Open(null);
            Assert.Equal("Nova categoria", f.Title);
            Assert.Equal("dots", f.Icon);
            Assert.Equal(CategoryKind.Expense, f.Kind);
            Assert.False(f.ShowMove);
            Assert.False(f.Save());
            Assert.Equal("Dê um nome à categoria.", f.Error);

            f.Name = "lazer";                                    // "Lazer" already exists as an expense
            Assert.False(f.Save());
            Assert.Equal("Já existe uma categoria com esse nome.", f.Error);

            f.Kind = CategoryKind.Income;                        // as income it is fine
            f.Icon = "fun";
            f.Color = "#FF6F91";
            Assert.True(f.Save());
            var c = host.Data.Categories[^1];
            Assert.Equal(("lazer", CategoryKind.Income, "fun", "#FF6F91"), (c.Name, c.Kind, c.Icon, c.Color));
        }

        [Fact]
        public void Editing_a_category_in_use_offers_others_with_Outros_first()
        {
            var (host, panel, a) = Setup();
            var food = Cat(host.Data, "Alimentação");
            host.Data.Transactions.Add(new FinanceTransaction { Kind = TxKind.Expense, Amount = 1m, CategoryId = food.Id, AccountId = a.Id });
            var f = panel.Category;
            f.Open(food);
            Assert.Equal("Editar categoria", f.Title);
            Assert.Equal(1, f.UsageCount);
            Assert.Equal("Em uso por 1 lançamento ou recorrência.", f.UsageText);
            Assert.True(f.ShowMove);
            Assert.DoesNotContain(f.MoveOptions, o => o.Id == food.Id);
            Assert.Equal(Cat(host.Data, "Outros").Id, f.MoveToId);
            Assert.Contains(f.MoveOptions, o => o.Name == "Salário (receita)");
            Assert.Equal(f.Name, food.Name);
            Assert.Equal(food.Icon, f.Icon);

            f.Name = "Alimentação ";                             // its own name is not a duplicate
            Assert.True(f.Save());
        }

        [Fact]
        public void Deleting_a_category_moves_its_transactions_and_can_be_undone()
        {
            var (host, panel, a) = Setup();
            var food = Cat(host.Data, "Alimentação");
            var home = Cat(host.Data, "Moradia");
            host.Data.Transactions.Add(new FinanceTransaction { Kind = TxKind.Expense, Amount = 1m, CategoryId = food.Id, AccountId = a.Id });
            host.Data.Transactions.Add(new FinanceTransaction { Kind = TxKind.Expense, Amount = 2m, CategoryId = food.Id, AccountId = a.Id });
            var f = panel.Category;
            f.Open(food);
            f.MoveToId = home.Id;
            host.Answer = false;
            f.RequestDelete();
            Assert.Equal("“Alimentação” será excluída e 2 lançamentos passam para “Moradia”.", host.LastDialog.Value.text);
            Assert.DoesNotContain(food, host.Data.Categories);
            Assert.All(host.Data.Transactions, t => Assert.Equal(home.Id, t.CategoryId));
            Assert.Equal("Categoria excluída", host.LastSnack.Value.text);

            host.LastSnack.Value.onAction();
            Assert.Contains(host.Data.Categories, c => c.Id == food.Id);
            Assert.All(host.Data.Transactions, t => Assert.Equal(food.Id, t.CategoryId));
        }

        [Fact]
        public void Deleting_a_category_in_use_needs_a_destination()
        {
            var (host, panel, a) = Setup();
            var food = Cat(host.Data, "Alimentação");
            host.Data.Transactions.Add(new FinanceTransaction { Kind = TxKind.Expense, Amount = 1m, CategoryId = food.Id, AccountId = a.Id });
            panel.Category.Open(food);
            panel.Category.MoveToId = "gone";
            panel.Category.RequestDelete();
            Assert.Null(host.LastDialog);
            Assert.Contains(food, host.Data.Categories);

            var unused = Cat(host.Data, "Freelance", CategoryKind.Income);
            panel.Category.Open(unused);
            panel.Category.RequestDelete();
            Assert.Equal("“Freelance” será excluída.", host.LastDialog.Value.text);
        }
    }
}
