using System;
using System.Linq;
using HelmetDesk.Core.Content;
using Xunit;

namespace HelmetDesk.Tests
{
    public class ChannelsViewModelTests
    {
        private static readonly DateTime Now = new(2026, 11, 21, 10, 0, 0);

        private static (FakeContentHost host, ContentPanelViewModel vm, ChannelsViewModel ch) Setup()
        {
            var host = new FakeContentHost();
            ContentStore.Seed(host.Data, Now);
            var vm = new ContentPanelViewModel(() => host.Data, host, () => Now);
            return (host, vm, vm.ChannelsVm);
        }

        [Fact]
        public void Creates_a_channel_at_the_end_with_an_unused_color()
        {
            var (host, vm, ch) = Setup();
            ch.New();
            Assert.DoesNotContain(host.Data.Channels, c => c.ColorHex == ch.ColorHex);
            ch.Name = "LinkedIn";
            ch.FollowersText = "1.234";
            Assert.True(ch.Save());
            var made = vm.Channels.Last();
            Assert.Equal(("LinkedIn", 1234, 3), (made.Name, made.Followers, made.SortOrder));
            Assert.False(ch.FormOpen);
        }

        [Fact]
        public void Name_is_required_and_unique_and_followers_are_numbers()
        {
            var (_, vm, ch) = Setup();
            ch.New();
            Assert.False(ch.Save());
            ch.Name = "youtube";
            Assert.False(ch.Save());
            ch.Name = "Blog";
            ch.FollowersText = "mil";
            Assert.False(ch.Save());
            Assert.NotNull(ch.Error);
            ch.FollowersText = "";
            Assert.True(ch.Save());
            Assert.Equal(0, vm.Channels.Last().Followers);
        }

        [Theory]
        [InlineData("1.234", 1234)] [InlineData("1,234", 1234)] [InlineData(" 98 ", 98)] [InlineData("", 0)]
        public void Followers_parse(string text, int expected)
        {
            Assert.True(ChannelsViewModel.TryParseFollowers(text, out var n));
            Assert.Equal(expected, n);
        }

        [Theory]
        [InlineData("-5")] [InlineData("12k")] [InlineData("99999999999")]
        public void Followers_reject(string text) => Assert.False(ChannelsViewModel.TryParseFollowers(text, out _));

        [Fact]
        public void Renaming_keeps_the_id_so_every_card_follows()
        {
            var (host, vm, ch) = Setup();
            var yt = vm.Channels[1];
            var item = new ContentItem { Title = "a", ChannelId = yt.Id };
            host.Data.Items.Add(item);
            ch.Edit(yt);
            ch.Name = "YouTube Shorts";
            Assert.True(ch.Save());
            Assert.Equal("YouTube Shorts", vm.ChannelOf(item.ChannelId).Name);
            Assert.Equal(3, host.Data.Channels.Count);
        }

        [Fact]
        public void Reorders_and_stops_at_the_ends()
        {
            var (_, vm, ch) = Setup();
            var x = vm.Channels[0];
            Assert.False(ch.Move(x, -1));
            Assert.True(ch.Move(x, +1));
            Assert.Equal(new[] { "YouTube", "X", "IG/TikTok" }, vm.Channels.Select(c => c.Name));
            Assert.True(ch.Move(vm.Channels[1], +1));
            Assert.False(ch.Move(vm.Channels[2], +1));
            Assert.Equal(new[] { "YouTube", "IG/TikTok", "X" }, vm.Channels.Select(c => c.Name));
        }

        [Fact]
        public void A_channel_with_content_cannot_be_deleted()
        {
            var (host, vm, ch) = Setup();
            var x = vm.Channels[0];
            host.Data.Items.Add(new ContentItem { Title = "a", ChannelId = x.Id, Stage = Stage.Publicado });
            Assert.False(ch.CanDelete(x));
            Assert.False(ch.RequestDelete(x));
            Assert.NotNull(host.LastAlert);
            Assert.Contains("X", host.LastAlert.Value.text);
            Assert.Contains(x, host.Data.Channels);
            Assert.DoesNotContain("confirm", host.Calls);
            Assert.Equal(0, host.Commits);
        }

        [Fact]
        public void An_empty_channel_is_deleted_after_confirming_and_the_filter_resets()
        {
            var (host, vm, ch) = Setup();
            var yt = vm.Channels[1];
            vm.SelectChannel(yt.Id);
            host.Answer = false;
            Assert.True(ch.RequestDelete(yt));
            Assert.Contains(yt, host.Data.Channels);
            host.Answer = true;
            ch.RequestDelete(yt);
            Assert.DoesNotContain(yt, host.Data.Channels);
            Assert.Null(vm.ChannelFilter);
            Assert.Equal(new[] { 0, 1 }, vm.Channels.Select(c => c.SortOrder));
        }
    }
}
