using System;
using System.Collections.Generic;
using System.Linq;
using HelmetDesk.Core.Finance;

namespace HelmetDesk.Core.Content
{
    /// <summary>
    /// State and rules of the Conteúdo panel (esteira de produção), without WPF: the channel and stage filters, the
    /// counts of each sphere and card, the summary line and the actions of the cards (new, advance, go back).
    /// ContentView draws what this class says and sends the user's clicks here.
    /// </summary>
    public sealed class ContentPanelViewModel : ObservableObject
    {
        /// <summary>The stages in order (the spheres of the esteira).</summary>
        public static readonly IReadOnlyList<Stage> Stages = (Stage[])Enum.GetValues(typeof(Stage));

        public static int StageCount => Stages.Count;

        /// <summary>Colors offered for channels.</summary>
        public static readonly IReadOnlyList<string> Colors =
            new[] { "#8950D9", "#C8A8F5", "#78B7FF", "#5CC8C8", "#64D6A2", "#F2C36B", "#F2A65A", "#FF8FB1", "#FF6F91", "#9A93A8" };

        private readonly Func<ContentData> _data;
        private readonly Func<DateTime> _now;

        public ContentPanelViewModel(Func<ContentData> data, IContentHost host, Func<DateTime>? now = null)
        {
            _data = data;
            Host = host;
            _now = now ?? (() => DateTime.Now);
            Form = new ContentItemFormViewModel(this);
            ChannelsVm = new ChannelsViewModel(this);
        }

        public ContentData Data => _data();
        public IContentHost Host { get; }
        public DateTime Now => _now();

        /// <summary>The edit form of a content item.</summary>
        public ContentItemFormViewModel Form { get; }
        /// <summary>Gerenciar canais.</summary>
        public ChannelsViewModel ChannelsVm { get; }

        /// <summary>The side panel was closed: no form is editing anything any more.</summary>
        public void FormsClosed()
        {
            Form.Close();
            ChannelsVm.Close();
        }

        // ================= stages =================

        /// <summary>1-based position of <paramref name="s"/> (Ideia = 1, Publicado = 5).</summary>
        public static int StageNumber(Stage s) => (int)s + 1;

        public static Stage? NextStage(Stage s) => s == Stage.Publicado ? null : s + 1;
        public static Stage? PrevStage(Stage s) => s == Stage.Ideia ? null : s - 1;

        public static string StageLabel(Stage s) => s switch
        {
            Stage.Ideia => T("Ideia"),
            Stage.Gravando => T("Gravando/escrevendo"),
            Stage.Editando => T("Editando"),
            Stage.Programado => T("Programado"),
            _ => T("Publicado")
        };

        /// <summary>"Editando · etapa 3 de 5".</summary>
        public static string ProgressText(Stage s) => TF("{0} · etapa {1} de {2}", StageLabel(s), StageNumber(s), StageCount);

        /// <summary>"Ir para Editando".</summary>
        public static string AdvanceText(Stage s) => NextStage(s) is Stage n ? TF("Ir para {0}", StageLabel(n)) : "";

        // ================= channels =================

        /// <summary>Channels in the user's order.</summary>
        public IReadOnlyList<Channel> Channels => Data.Channels.OrderBy(c => c.SortOrder).ThenBy(c => c.CreatedAt).ToList();

        public Channel? ChannelOf(Guid id) => Data.Channels.FirstOrDefault(c => c.Id == id);

        /// <summary>Content not yet published ("em produção") of one channel, or of all when null.</summary>
        public int InProduction(Guid? channelId) =>
            Data.Items.Count(i => i.Stage != Stage.Publicado && (channelId == null || i.ChannelId == channelId));

        // ================= filters =================

        private Guid? _channelFilter;
        /// <summary>Selected channel card; null = "Todos os canais".</summary>
        public Guid? ChannelFilter { get => _channelFilter; private set => Set(ref _channelFilter, value); }

        private Stage? _stageFilter;
        /// <summary>Selected sphere; null = todas as etapas.</summary>
        public Stage? StageFilter { get => _stageFilter; private set => Set(ref _stageFilter, value); }

        /// <summary>Single selection: a channel, or null for all. An id that no longer exists means all.</summary>
        public void SelectChannel(Guid? id) => ChannelFilter = id is Guid g && ChannelOf(g) != null ? g : null;

        /// <summary>Clicking a sphere filters by it; clicking the same one again removes the filter.</summary>
        public void ToggleStage(Stage s) => StageFilter = StageFilter == s ? null : s;

        public void ClearStage() => StageFilter = null;

        private bool InChannel(ContentItem i) => ChannelFilter == null || i.ChannelId == ChannelFilter;

        /// <summary>Count of one sphere, respecting the channel filter (not the stage filter).</summary>
        public int CountInStage(Stage s) => Data.Items.Count(i => i.Stage == s && InChannel(i));

        /// <summary>The cards shown: both filters applied. Earliest stage first, then by publish date (none last), then newest.</summary>
        public IReadOnlyList<ContentItem> Visible => Data.Items
            .Where(i => InChannel(i) && (StageFilter == null || i.Stage == StageFilter))
            .OrderBy(i => i.Stage)
            .ThenBy(i => i.PublishDate == null)
            .ThenBy(i => i.PublishDate)
            .ThenByDescending(i => i.CreatedAt)
            .ToList();

        /// <summary>"12 conteúdos · todos os canais · todas as etapas".</summary>
        public string Summary
        {
            get
            {
                var n = Visible.Count;
                var channel = ChannelFilter is Guid g && ChannelOf(g) is { } c ? c.Name : T("todos os canais");
                var stage = StageFilter is Stage s ? StageLabel(s) : T("todas as etapas");
                return TN(n, "{0} conteúdo", "{0} conteúdos") + " · " + channel + " · " + stage;
            }
        }

        // ================= card actions =================

        /// <summary>
        /// "Novo: &lt;canal&gt;": a new item in Ideia on that channel, the channel filter moves to it (and a stage filter
        /// that would hide the new card is removed). The caller opens the form on the returned item.
        /// </summary>
        public ContentItem? NewFor(Guid channelId)
        {
            if (ChannelOf(channelId) == null) return null;
            Form.Close(); // another "Novo" still untitled is dropped first
            var now = Now;
            var item = new ContentItem { ChannelId = channelId, Stage = Stage.Ideia, CreatedAt = now, UpdatedAt = now };
            Data.Items.Add(item);
            ChannelFilter = channelId;
            if (StageFilter != null && StageFilter != Stage.Ideia) StageFilter = null;
            Form.Open(item, isNew: true);
            Host.Commit();
            return item;
        }

        /// <summary>Moves the item to the next stage. False when it is already Publicado.</summary>
        public bool Advance(ContentItem item)
        {
            if (NextStage(item.Stage) is not Stage next) return false;
            item.Stage = next;
            item.UpdatedAt = Now;
            Host.Commit();
            return true;
        }

        /// <summary>Moves the item back one stage. False when it is in Ideia.</summary>
        public bool Back(ContentItem item)
        {
            if (PrevStage(item.Stage) is not Stage prev) return false;
            item.Stage = prev;
            item.UpdatedAt = Now;
            Host.Commit();
            return true;
        }
    }
}
