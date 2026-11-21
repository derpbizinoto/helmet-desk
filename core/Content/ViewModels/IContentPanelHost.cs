using System;

namespace HelmetDesk.Core.Content
{
    /// <summary>
    /// What the Conteúdo view models need from the screen that shows them (ContentView): saving, the side panel and
    /// dialogs. Kept small so the view models can be tested without WPF (same idea as IFinanceHost).
    /// </summary>
    public interface IContentPanelHost
    {
        /// <summary>Saves conteudo.json and redraws the panel.</summary>
        void Commit();

        /// <summary>Closes the side panel.</summary>
        void ClosePanel();

        /// <summary>Asks for confirmation; <paramref name="onConfirm"/> runs only when confirmed.</summary>
        void Confirm(string title, string text, string confirm, bool danger, Action onConfirm);

        /// <summary>Shows a message with a single OK button (e.g. why a channel can't be deleted).</summary>
        void Alert(string title, string text);
    }
}
