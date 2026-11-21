using System;
using System.IO;
using HelmetDesk.Core;
using HelmetDesk.Core.Content;
using Xunit;

namespace HelmetDesk.Tests
{
    public class ContentStoreTests : IDisposable
    {
        private static readonly DateTime Now = new(2026, 11, 21, 10, 0, 0);
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "hd-content-" + Guid.NewGuid().ToString("N"));
        private string FilePath => Path.Combine(_dir, "conteudo.json");

        public ContentStoreTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        [Fact]
        public void First_use_seeds_channels_and_writes_nothing()
        {
            var d = ContentStore.Load(FilePath, Now);
            Assert.Equal(3, d.Channels.Count);
            Assert.Equal(1, d.SchemaVersion);
            Assert.False(File.Exists(FilePath));
        }

        [Fact]
        public void Round_trips_with_schema_version_and_stage_names()
        {
            var d = ContentStore.Load(FilePath, Now);
            d.Items.Add(new ContentItem { Title = "Vídeo", ChannelId = d.Channels[1].Id, Stage = Stage.Programado, PublishDate = new DateTime(2026, 12, 1), Notes = "n" });
            ContentStore.Save(FilePath, d);

            var text = File.ReadAllText(FilePath);
            Assert.Contains("\"schemaVersion\": 1", text);
            Assert.Contains("\"Programado\"", text);
            Assert.False(File.Exists(FilePath + ".tmp"));

            var back = ContentStore.Load(FilePath, Now);
            Assert.Equal(3, back.Channels.Count);
            var i = Assert.Single(back.Items);
            Assert.Equal((d.Items[0].Id, "Vídeo", d.Channels[1].Id, Stage.Programado), (i.Id, i.Title, i.ChannelId, i.Stage));
        }

        [Fact]
        public void Channels_deleted_by_the_user_are_not_seeded_again()
        {
            var d = ContentStore.Load(FilePath, Now);
            d.Channels.Clear();
            ContentStore.Save(FilePath, d);
            Assert.Empty(ContentStore.Load(FilePath, Now).Channels);
        }

        [Fact]
        public void A_broken_file_is_kept_aside_and_the_panel_starts_empty()
        {
            File.WriteAllText(FilePath, "{ isto não é json");
            string warned = null;
            var d = ContentStore.Load(FilePath, Now, m => warned = m);
            Assert.Empty(d.Channels);
            Assert.NotNull(warned);
            Assert.True(File.Exists(Path.Combine(_dir, "conteudo.corrompido.20261121100000.json")));
            Assert.Equal("{ isto não é json", File.ReadAllText(FilePath));
        }

        [Fact]
        public void Null_sections_load_safely()
        {
            File.WriteAllText(FilePath, """{"schemaVersion":1,"channels":null,"items":[null,{"title":null,"stage":"Editando"}]}""");
            var d = ContentStore.Load(FilePath, Now);
            Assert.Empty(d.Channels);
            var i = Assert.Single(d.Items);
            Assert.Equal(("", Stage.Editando), (i.Title, i.Stage));
        }

        [Theory]
        [InlineData(null, false)]
        [InlineData("{\"conteudo\": true}", true)]
        [InlineData("{\"conteudo\": false}", false)]
        [InlineData("{\"conteudo\": \"true\"}", false)]
        [InlineData("[true]", false)]
        [InlineData("não é json", false)]
        public void Experimental_flag_is_off_unless_explicitly_true(string content, bool expected)
        {
            var path = Path.Combine(_dir, "experimental.json");
            if (content != null) File.WriteAllText(path, content);
            Assert.Equal(expected, Experimental.ReadFlag(path, "conteudo"));
        }
    }
}
