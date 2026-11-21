using System;
using System.Globalization;
using System.Linq;
using HelmetDesk.Core.Finance;

namespace HelmetDesk.Core.Content
{
    /// <summary>
    /// Gerenciar canais: create, rename, color, followers, reorder and delete. A channel that still has content can't be
    /// deleted: the user is told why instead. Content points at channels by Id, so a rename shows on every card at once.
    /// </summary>
    public sealed class ChannelsViewModel : ObservableObject
    {
        private readonly ContentPanelViewModel _panel;

        public ChannelsViewModel(ContentPanelViewModel panel) => _panel = panel;

        private ContentData Data => _panel.Data;
        private IContentPanelHost Host => _panel.Host;

        // ================= the channel form =================

        /// <summary>The channel being edited; null for a new one (when <see cref="FormOpen"/>).</summary>
        public Channel? Editing { get; private set; }
        public bool FormOpen { get; private set; }
        public bool IsNew => FormOpen && Editing == null;

        public string Name { get; set; } = "";
        public string ColorHex { get; set; } = ContentPanelViewModel.Colors[0];
        /// <summary>As typed ("1.234", "1,234" and "1234" all mean 1234).</summary>
        public string FollowersText { get; set; } = "";

        private string? _error;
        public string? Error { get => _error; private set => Set(ref _error, value); }

        public void New()
        {
            Editing = null;
            FormOpen = true;
            Name = "";
            // first color not used yet, so a new channel stands out
            ColorHex = ContentPanelViewModel.Colors.FirstOrDefault(c => !Data.Channels.Any(ch => string.Equals(ch.ColorHex, c, StringComparison.OrdinalIgnoreCase)))
                       ?? ContentPanelViewModel.Colors[0];
            FollowersText = "";
            Error = null;
        }

        public void Edit(Channel c)
        {
            Editing = c;
            FormOpen = true;
            Name = c.Name;
            ColorHex = c.ColorHex;
            FollowersText = c.Followers.ToString(CultureInfo.InvariantCulture);
            Error = null;
        }

        /// <summary>Back to the list of channels without saving.</summary>
        public void Close()
        {
            Editing = null;
            FormOpen = false;
            Error = null;
        }

        /// <summary>"1.234" / "1,234" / " 1234 " → 1234. Digits only once the separators are gone; never negative.</summary>
        public static bool TryParseFollowers(string? text, out int followers)
        {
            var digits = (text ?? "").Trim().Replace(".", "").Replace(",", "").Replace(" ", "").Replace(" ", "");
            if (digits.Length == 0) { followers = 0; return true; }
            return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out followers);
        }

        public bool Save()
        {
            if (!FormOpen) return false;
            var name = (Name ?? "").Trim();
            if (name.Length == 0) { Error = T("Dê um nome ao canal."); return false; }
            if (Data.Channels.Any(c => c != Editing && string.Equals(c.Name.Trim(), name, StringComparison.CurrentCultureIgnoreCase)))
            { Error = T("Já existe um canal com esse nome."); return false; }
            if (!TryParseFollowers(FollowersText, out var followers)) { Error = T("Seguidores: use só números."); return false; }

            var c = Editing;
            if (c == null)
            {
                c = new Channel { CreatedAt = _panel.Now, SortOrder = Data.Channels.Count == 0 ? 0 : Data.Channels.Max(x => x.SortOrder) + 1 };
                Data.Channels.Add(c);
            }
            c.Name = name;
            c.ColorHex = string.IsNullOrWhiteSpace(ColorHex) ? ContentPanelViewModel.Colors[0] : ColorHex;
            c.Followers = followers;
            Close();
            Host.Commit();
            return true;
        }

        // ================= the list =================

        public int ContentCount(Guid channelId) => Data.Items.Count(i => i.ChannelId == channelId);

        public bool CanDelete(Channel c) => ContentCount(c.Id) == 0;

        /// <summary>Moves the channel one place up (-1) or down (+1). False at the ends.</summary>
        public bool Move(Channel c, int delta)
        {
            var list = _panel.Channels.ToList();
            int i = list.IndexOf(c), j = i + delta;
            if (i < 0 || j < 0 || j >= list.Count) return false;
            (list[i], list[j]) = (list[j], list[i]);
            Renumber(list);
            Host.Commit();
            return true;
        }

        private static void Renumber(System.Collections.Generic.IList<Channel> ordered)
        {
            for (int k = 0; k < ordered.Count; k++) ordered[k].SortOrder = k;
        }

        /// <summary>
        /// Excluir canal. With content still on it: blocked, and the user is told why (nothing changes).
        /// Otherwise asks first. Returns false when blocked.
        /// </summary>
        public bool RequestDelete(Channel c)
        {
            int n = ContentCount(c.Id);
            if (n > 0)
            {
                Host.Alert(T("Este canal ainda tem conteúdos"),
                    TF("\"{0}\" tem {1} conteúdo(s). Mova esses conteúdos para outro canal ou exclua-os antes de excluir o canal.", c.Name, n));
                return false;
            }
            Host.Confirm(T("Excluir canal?"), TF("O canal \"{0}\" será excluído.", c.Name), T("Excluir"), true, () =>
            {
                Data.Channels.Remove(c);
                Renumber(_panel.Channels.ToList());
                if (_panel.ChannelFilter == c.Id) _panel.SelectChannel(null);
                if (Editing == c) Close();
                Host.Commit();
            });
            return true;
        }
    }
}
