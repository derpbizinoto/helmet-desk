using System;

namespace HelmetDesk.Core.Finance
{
    /// <summary>
    /// What the Finanças view models need from the screen that shows them (FinanceView): saving, the side form,
    /// the confirmation dialog and the undo bar. Kept small so the view models can be tested without WPF.
    /// </summary>
    public interface IFinanceHost
    {
        /// <summary>Marks the data for saving, tells the window and redraws the panel.</summary>
        void Commit();

        /// <summary>Marks the data for saving and tells the window, without redrawing (recurring bills were generated).</summary>
        void Changed();

        /// <summary>Puts a whole copy of the finance data back in place (undo).</summary>
        void ReplaceData(FinanceData data);

        /// <summary>Closes the side form.</summary>
        void ClosePanel();

        /// <summary>
        /// Asks for confirmation. <paramref name="onConfirm"/> runs only when confirmed, with the state of the check box
        /// (false when <paramref name="checkText"/> is null and there is none).
        /// </summary>
        void Confirm(string title, string text, string confirm, bool danger, string? checkText, Action<bool> onConfirm);

        /// <summary>Shows the bar at the bottom; <paramref name="action"/> is a link such as "Desfazer".</summary>
        void Snack(string text, string? action, Action? onAction);
    }
}
