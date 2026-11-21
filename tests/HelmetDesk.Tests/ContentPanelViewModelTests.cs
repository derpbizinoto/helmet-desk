using System;
using System.Linq;
using HelmetDesk.Core.Content;
using Xunit;

namespace HelmetDesk.Tests
{
    public class ContentPanelViewModelTests
    {
        private static readonly DateTime Now = new(2026, 11, 21, 10, 0, 0);

        private static (FakeContentHost host, ContentPanelViewModel vm, Channel x, Channel yt, Channel ig) Setup()
        {
            var host = new FakeContentHost();
            ContentStore.Seed(host.Data, Now);
            var vm = new ContentPanelViewModel(() => host.Data, host, () => Now);
            var ch = vm.Channels;
            return (host, vm, ch[0], ch[1], ch[2]);
        }

        private static ContentItem Add(FakeContentHost host, Channel c, Stage s, string title = "t", DateTime? date = null)
        {
            var i = new ContentItem { Title = title, ChannelId = c.Id, Stage = s, PublishDate = date, CreatedAt = Now, UpdatedAt = Now };
            host.Data.Items.Add(i);
            return i;
        }

        [Fact]
        public void Seed_creates_the_three_sample_channels_in_order()
        {
            var (_, vm, x, yt, ig) = Setup();
            Assert.Equal(new[] { "X", "YouTube", "IG/TikTok" }, vm.Channels.Select(c => c.Name));
            Assert.Equal(new[] { 0, 1, 2 }, new[] { x.SortOrder, yt.SortOrder, ig.SortOrder });
        }

        [Fact]
        public void Stage_counts_follow_the_channel_filter_only()
        {
            var (host, vm, x, yt, _) = Setup();
            Add(host, x, Stage.Ideia); Add(host, x, Stage.Ideia); Add(host, x, Stage.Editando);
            Add(host, yt, Stage.Ideia); Add(host, yt, Stage.Publicado);

            Assert.Equal(3, vm.CountInStage(Stage.Ideia));
            Assert.Equal(1, vm.CountInStage(Stage.Publicado));

            vm.SelectChannel(x.Id);
            vm.ToggleStage(Stage.Editando); // the stage filter doesn't change the spheres
            Assert.Equal(2, vm.CountInStage(Stage.Ideia));
            Assert.Equal(1, vm.CountInStage(Stage.Editando));
            Assert.Equal(0, vm.CountInStage(Stage.Publicado));
        }

        [Fact]
        public void In_production_counts_everything_but_published()
        {
            var (host, vm, x, yt, ig) = Setup();
            Add(host, x, Stage.Ideia); Add(host, x, Stage.Programado); Add(host, x, Stage.Publicado);
            Add(host, yt, Stage.Gravando);
            Assert.Equal(2, vm.InProduction(x.Id));
            Assert.Equal(1, vm.InProduction(yt.Id));
            Assert.Equal(0, vm.InProduction(ig.Id));
            Assert.Equal(3, vm.InProduction(null));
        }

        [Fact]
        public void Visible_applies_both_filters_and_toggling_a_sphere_removes_it()
        {
            var (host, vm, x, yt, _) = Setup();
            var a = Add(host, x, Stage.Ideia);
            var b = Add(host, x, Stage.Editando);
            var c = Add(host, yt, Stage.Editando);

            Assert.Equal(3, vm.Visible.Count);
            vm.ToggleStage(Stage.Editando);
            Assert.Equal(new[] { b, c }.ToHashSet(), vm.Visible.ToHashSet());
            vm.SelectChannel(x.Id);
            Assert.Equal(new[] { b }, vm.Visible);
            vm.ToggleStage(Stage.Editando);
            Assert.Null(vm.StageFilter);
            Assert.Equal(new[] { a, b }.ToHashSet(), vm.Visible.ToHashSet());
            vm.ToggleStage(Stage.Ideia);
            vm.ClearStage();
            Assert.Null(vm.StageFilter);
            vm.SelectChannel(null);
            Assert.Equal(3, vm.Visible.Count);
        }

        [Fact]
        public void Selecting_a_missing_channel_means_all()
        {
            var (_, vm, _, _, _) = Setup();
            vm.SelectChannel(Guid.NewGuid());
            Assert.Null(vm.ChannelFilter);
        }

        [Fact]
        public void Visible_is_ordered_by_stage_then_date_with_no_date_last()
        {
            var (host, vm, x, _, _) = Setup();
            var late = Add(host, x, Stage.Programado, date: new DateTime(2026, 12, 5));
            var none = Add(host, x, Stage.Programado);
            var soon = Add(host, x, Stage.Programado, date: new DateTime(2026, 11, 25));
            var idea = Add(host, x, Stage.Ideia);
            Assert.Equal(new[] { idea, soon, late, none }, vm.Visible);
        }

        [Fact]
        public void Summary_names_the_filters()
        {
            var (host, vm, x, _, _) = Setup();
            Add(host, x, Stage.Editando); Add(host, x, Stage.Ideia);
            Assert.Equal("2 conteúdos · todos os canais · todas as etapas", vm.Summary);
            vm.SelectChannel(x.Id);
            vm.ToggleStage(Stage.Editando);
            Assert.Equal("1 conteúdo · X · Editando", vm.Summary);
        }

        [Fact]
        public void Advance_and_back_walk_the_stages_and_stop_at_the_ends()
        {
            var (host, vm, x, _, _) = Setup();
            var i = Add(host, x, Stage.Ideia);
            Assert.False(vm.Back(i));
            Assert.Equal(0, host.Commits);
            for (int k = 0; k < 4; k++) Assert.True(vm.Advance(i));
            Assert.Equal(Stage.Publicado, i.Stage);
            Assert.False(vm.Advance(i));
            Assert.Equal(4, host.Commits);
            Assert.True(vm.Back(i));
            Assert.Equal(Stage.Programado, i.Stage);
        }

        [Fact]
        public void Stage_texts()
        {
            Assert.Equal("Editando · etapa 3 de 5", ContentPanelViewModel.ProgressText(Stage.Editando));
            Assert.Equal("Ir para Gravando/escrevendo", ContentPanelViewModel.AdvanceText(Stage.Ideia));
            Assert.Equal("", ContentPanelViewModel.AdvanceText(Stage.Publicado));
            Assert.Equal(5, ContentPanelViewModel.StageCount);
        }

        [Fact]
        public void New_for_a_channel_starts_in_idea_moves_the_filter_and_opens_the_form()
        {
            var (host, vm, x, yt, _) = Setup();
            vm.SelectChannel(x.Id);
            vm.ToggleStage(Stage.Publicado);
            var item = vm.NewFor(yt.Id);
            Assert.NotNull(item);
            Assert.Equal(Stage.Ideia, item.Stage);
            Assert.Equal(yt.Id, item.ChannelId);
            Assert.Equal(yt.Id, vm.ChannelFilter);
            Assert.Null(vm.StageFilter); // Publicado would hide the new card
            Assert.Same(item, vm.Form.Editing);
            Assert.True(vm.Form.IsNew);
            Assert.Contains(item, vm.Visible);
        }

        [Fact]
        public void An_untitled_new_item_is_dropped_when_the_form_closes()
        {
            var (host, vm, x, _, _) = Setup();
            var item = vm.NewFor(x.Id);
            vm.FormsClosed();
            Assert.DoesNotContain(item, host.Data.Items);

            var kept = vm.NewFor(x.Id);
            vm.Form.Title = "Vídeo novo";
            Assert.True(vm.Form.Save());
            vm.FormsClosed();
            Assert.Contains(kept, host.Data.Items);
            Assert.Equal("Vídeo novo", kept.Title);
        }

        [Fact]
        public void Clicking_new_twice_leaves_only_one_untitled_item()
        {
            var (host, vm, x, yt, _) = Setup();
            vm.NewFor(x.Id);
            vm.NewFor(yt.Id);
            Assert.Single(host.Data.Items);
        }

        [Fact]
        public void Form_save_validates_and_writes_every_field()
        {
            var (host, vm, x, yt, _) = Setup();
            var i = Add(host, x, Stage.Ideia, "antigo");
            vm.Form.Open(i);
            vm.Form.Title = "  ";
            Assert.False(vm.Form.Save());
            Assert.NotNull(vm.Form.Error);
            vm.Form.Title = "Novo título";
            vm.Form.ChannelId = Guid.NewGuid();
            Assert.False(vm.Form.Save());

            vm.Form.ChannelId = yt.Id;
            vm.Form.Stage = Stage.Programado;
            vm.Form.PublishDate = new DateTime(2026, 12, 1, 15, 30, 0);
            vm.Form.Notes = "roteiro curto";
            Assert.True(vm.Form.Save());
            Assert.Equal(("Novo título", yt.Id, Stage.Programado, new DateTime(2026, 12, 1), "roteiro curto"),
                (i.Title, i.ChannelId, i.Stage, i.PublishDate.Value, i.Notes));
            Assert.Equal(1, host.Closes);
        }

        [Fact]
        public void Deleting_an_item_asks_first()
        {
            var (host, vm, x, _, _) = Setup();
            var i = Add(host, x, Stage.Ideia);
            vm.Form.Open(i);
            host.Answer = false;
            vm.Form.RequestDelete();
            Assert.Contains(i, host.Data.Items);
            host.Answer = true;
            vm.Form.RequestDelete();
            Assert.DoesNotContain(i, host.Data.Items);
            Assert.Null(vm.Form.Editing);
        }
    }
}
