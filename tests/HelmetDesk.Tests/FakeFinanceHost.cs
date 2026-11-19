using System;
using System.Collections.Generic;
using HelmetDesk.Core.Finance;

namespace HelmetDesk.Tests
{
    /// <summary>Records what the Finanças view models ask of the screen; confirms dialogs as told.</summary>
    internal sealed class FakeFinanceHost : IFinanceHost
    {
        public FinanceData Data = new();
        public int Commits, Changes, Closes;
        public readonly List<string> Calls = new();

        /// <summary>Null: the dialog is cancelled. Otherwise it is confirmed with this check box state.</summary>
        public bool? Answer = false;
        public (string title, string text, string confirm, bool danger, string check)? LastDialog;
        public (string text, string action, Action onAction)? LastSnack;

        public void Commit() { Commits++; Calls.Add("commit"); }
        public void Changed() { Changes++; Calls.Add("changed"); }
        public void ReplaceData(FinanceData data) { Data = data; Calls.Add("replace"); }
        public void ClosePanel() { Closes++; Calls.Add("close"); }

        public void Confirm(string title, string text, string confirm, bool danger, string checkText, Action<bool> onConfirm)
        {
            LastDialog = (title, text, confirm, danger, checkText);
            Calls.Add("confirm");
            if (Answer is bool ticked) onConfirm(ticked);
        }

        public void Snack(string text, string action, Action onAction)
        {
            LastSnack = (text, action, onAction);
            Calls.Add("snack");
        }
    }
}
