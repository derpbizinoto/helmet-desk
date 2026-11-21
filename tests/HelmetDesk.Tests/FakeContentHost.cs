using System;
using System.Collections.Generic;
using HelmetDesk.Core.Content;

namespace HelmetDesk.Tests
{
    /// <summary>Records what the Conteúdo view models ask of the screen; confirms dialogs as told.</summary>
    internal sealed class FakeContentHost : IContentPanelHost
    {
        public ContentData Data = new();
        public int Commits, Closes;
        public readonly List<string> Calls = new();

        /// <summary>False: the dialog is cancelled.</summary>
        public bool Answer = true;
        public (string title, string text)? LastAlert;
        public string LastConfirmTitle;

        public void Commit() { Commits++; Calls.Add("commit"); }
        public void ClosePanel() { Closes++; Calls.Add("close"); }

        public void Confirm(string title, string text, string confirm, bool danger, Action onConfirm)
        {
            LastConfirmTitle = title;
            Calls.Add("confirm");
            if (Answer) onConfirm();
        }

        public void Alert(string title, string text)
        {
            LastAlert = (title, text);
            Calls.Add("alert");
        }
    }
}
