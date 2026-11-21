using System;
using System.Collections.Generic;

namespace HelmetDesk.Core.Content
{
    /// <summary>Production stages, in order. Saved by name in conteudo.json.</summary>
    public enum Stage
    {
        Ideia,
        /// <summary>Shown as "Gravando/escrevendo".</summary>
        Gravando,
        Editando,
        Programado,
        Publicado
    }

    /// <summary>A social channel (X, YouTube…). Followers are typed by hand.</summary>
    public sealed class Channel
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "";
        public string ColorHex { get; set; } = "#8950D9";
        public int Followers { get; set; }
        public int SortOrder { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>One piece of content on its way from idea to published. Points at its channel by Id, never by name.</summary>
    public sealed class ContentItem
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Title { get; set; } = "";
        public Guid ChannelId { get; set; }
        public Stage Stage { get; set; } = Stage.Ideia;
        public DateTime? PublishDate { get; set; }
        public string Notes { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>Everything in conteudo.json.</summary>
    public sealed class ContentData
    {
        public int SchemaVersion { get; set; } = ContentStore.CurrentSchema;
        public List<Channel> Channels { get; set; } = new();
        public List<ContentItem> Items { get; set; } = new();
    }
}
