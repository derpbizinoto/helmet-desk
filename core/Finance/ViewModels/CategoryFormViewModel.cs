using System;
using System.Collections.Generic;
using System.Linq;

namespace HelmetDesk.Core.Finance
{
    /// <summary>
    /// The new / edit category form: name, kind, color and icon; deleting moves what uses the category to another one
    /// (by default "Outros" of the same kind).
    /// </summary>
    public sealed class CategoryFormViewModel : ObservableObject
    {
        private readonly FinancePanelViewModel _panel;

        public CategoryFormViewModel(FinancePanelViewModel panel) => _panel = panel;

        private FinanceData D => _panel.Data;
        private IFinanceHost Host => _panel.Host;

        /// <summary>The category being edited, or null for a new one.</summary>
        public Category? Editing { get; private set; }
        public bool IsNew => Editing == null;
        public string Title => IsNew ? T("Nova categoria") : T("Editar categoria");

        private string _name = "";
        public string Name { get => _name; set => Set(ref _name, value ?? ""); }

        private CategoryKind _kind;
        public CategoryKind Kind { get => _kind; set => Set(ref _kind, value); }

        private string _color = "";
        public string Color { get => _color; set => Set(ref _color, value ?? ""); }

        private string _icon = "dots";
        public string Icon { get => _icon; set => Set(ref _icon, value ?? "dots"); }

        private string? _error;
        public string? Error { get => _error; private set => Set(ref _error, value); }

        /// <summary>Transactions and recurrences that use the edited category.</summary>
        public int UsageCount { get; private set; }
        public string UsageText => UsageCount == 0 ? T("Esta categoria não está em uso.")
            : TN(UsageCount, "Em uso por {0} lançamento ou recorrência.", "Em uso por {0} lançamentos ou recorrências.");
        /// <summary>"Mover para" is shown only when something uses the category.</summary>
        public bool ShowMove => UsageCount > 0;

        /// <summary>Where its transactions go on delete: the other categories, same kind first, the other kind marked.</summary>
        public IReadOnlyList<(string Id, string Name)> MoveOptions { get; private set; } = new List<(string, string)>();

        private string? _moveToId;
        public string? MoveToId { get => _moveToId; set => Set(ref _moveToId, value); }

        public void Open(Category? c)
        {
            Editing = c;
            Name = c?.Name ?? "";
            Kind = c?.Kind == CategoryKind.Income ? CategoryKind.Income : CategoryKind.Expense;
            Error = null;
            Color = c?.Color ?? FinancePanelViewModel.Colors[D.Categories.Count % FinancePanelViewModel.Colors.Count];
            Icon = c == null || string.IsNullOrEmpty(c.Icon) ? "dots" : c.Icon;
            UsageCount = c == null ? 0 : FinanceService.UsageCount(D, c);
            if (c != null)
            {
                var others = D.Categories.Where(x => x != c).OrderBy(x => x.Kind != c.Kind).ThenBy(x => x.Name)
                    .Select(x => (x.Id, x.Name + (x.Kind != c.Kind ? (x.Kind == CategoryKind.Income ? T(" (receita)") : T(" (despesa)")) : ""))).ToList();
                MoveOptions = others;
                var preferred = others.FirstOrDefault(o => o.Item2 == T("Outros")).Id ?? others.FirstOrDefault().Id;
                MoveToId = others.Any(o => o.Id == preferred) ? preferred : others.Count > 0 ? others[0].Id : null;
            }
            Raise(nameof(Editing)); Raise(nameof(IsNew)); Raise(nameof(Title));
            Raise(nameof(UsageCount)); Raise(nameof(UsageText)); Raise(nameof(ShowMove)); Raise(nameof(MoveOptions));
        }

        public void Close() => Editing = null;

        private bool Fail(string message)
        {
            Error = null;
            Error = message;
            return false;
        }

        /// <summary>Validates and saves. Returns false when something is wrong (<see cref="Error"/> says what).</summary>
        public bool Save()
        {
            var name = Name.Trim();
            if (name.Length == 0) return Fail(T("Dê um nome à categoria."));
            var kind = Kind;
            var d = D;
            if (d.Categories.Any(x => x != Editing && x.Kind == kind && string.Equals(x.Name, name, StringComparison.CurrentCultureIgnoreCase)))
                return Fail(T("Já existe uma categoria com esse nome."));
            var c = Editing ?? new Category();
            c.Name = name; c.Kind = kind; c.Color = Color; c.Icon = Icon;
            if (Editing == null) d.Categories.Add(c);
            Host.ClosePanel();
            Host.Commit();
            return true;
        }

        /// <summary>Asks, then deletes the category and moves what used it to <see cref="MoveToId"/> (with undo).</summary>
        public void RequestDelete()
        {
            var c = Editing;
            if (c == null) return;
            int used = FinanceService.UsageCount(D, c);
            var moveTo = used > 0 ? _panel.CategoryOf(MoveToId) : null;
            if (used > 0 && moveTo == null) return;
            var text = moveTo != null
                ? TF(used == 1 ? "“{0}” será excluída e {1} lançamento passa para “{2}”." : "“{0}” será excluída e {1} lançamentos passam para “{2}”.", c.Name, used, moveTo.Name)
                : TF("“{0}” será excluída.", c.Name);
            Host.Confirm(T("Excluir categoria?"), text, T("Excluir"), true, null, _ =>
            {
                var before = _panel.Snapshot();
                FinanceService.DeleteCategory(D, c, moveTo);
                Host.ClosePanel();
                Host.Commit();
                Host.Snack(T("Categoria excluída"), T("Desfazer"), () => _panel.Undo(before));
            });
        }
    }
}
