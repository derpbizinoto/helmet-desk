using System;
using HelmetDesk.Core.Finance;

namespace HelmetDesk.Core.Content
{
    /// <summary>
    /// The edit form of a content item (side panel): título, canal, etapa, data and notas, plus delete with confirmation.
    /// The view copies the controls into these properties and calls <see cref="Save"/>.
    /// A new item ("Novo:") that is closed while its title is still empty is dropped, so a stray click leaves nothing behind.
    /// </summary>
    public sealed class ContentItemFormViewModel : ObservableObject
    {
        private readonly ContentPanelViewModel _panel;

        public ContentItemFormViewModel(ContentPanelViewModel panel) => _panel = panel;

        /// <summary>The item being edited; null when the form is closed.</summary>
        public ContentItem? Editing { get; private set; }

        /// <summary>Created by "Novo:" and not saved yet.</summary>
        public bool IsNew { get; private set; }

        public string Title { get; set; } = "";
        public Guid ChannelId { get; set; }
        public Stage Stage { get; set; }
        public DateTime? PublishDate { get; set; }
        public string Notes { get; set; } = "";

        private string? _error;
        public string? Error { get => _error; private set => Set(ref _error, value); }

        public void Open(ContentItem item, bool isNew = false)
        {
            Editing = item;
            IsNew = isNew;
            Title = item.Title;
            ChannelId = item.ChannelId;
            Stage = item.Stage;
            PublishDate = item.PublishDate;
            Notes = item.Notes;
            Error = null;
        }

        /// <summary>Writes the fields into the item. False (with <see cref="Error"/>) when something is missing.</summary>
        public bool Save()
        {
            if (Editing == null) return false;
            var title = (Title ?? "").Trim();
            if (title.Length == 0) { Error = T("Dê um título ao conteúdo."); return false; }
            if (_panel.ChannelOf(ChannelId) == null) { Error = T("Escolha um canal."); return false; }
            Editing.Title = title;
            Editing.ChannelId = ChannelId;
            Editing.Stage = Stage;
            Editing.PublishDate = PublishDate?.Date;
            Editing.Notes = Notes ?? "";
            Editing.UpdatedAt = _panel.Now;
            IsNew = false;
            Error = null;
            _panel.Host.Commit();
            _panel.Host.ClosePanel();
            return true;
        }

        /// <summary>Excluir: asks first; the item goes only when confirmed.</summary>
        public void RequestDelete()
        {
            var item = Editing;
            if (item == null) return;
            var name = string.IsNullOrWhiteSpace(item.Title) ? T("Sem título") : item.Title;
            _panel.Host.Confirm(T("Excluir conteúdo?"), TF("\"{0}\" será excluído. Isso não pode ser desfeito.", name), T("Excluir"), true, () =>
            {
                _panel.Data.Items.Remove(item);
                Editing = null;
                IsNew = false;
                _panel.Host.Commit();
                _panel.Host.ClosePanel();
            });
        }

        /// <summary>The panel closed (Cancelar, Esc, other form): a new item with no title is dropped.</summary>
        public void Close()
        {
            var item = Editing;
            Editing = null;
            if (item != null && IsNew && string.IsNullOrWhiteSpace(item.Title))
            {
                IsNew = false;
                _panel.Data.Items.Remove(item);
                _panel.Host.Commit();
            }
            IsNew = false;
        }
    }
}
