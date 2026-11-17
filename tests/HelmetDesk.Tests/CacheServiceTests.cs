using System;
using System.IO;
using HelmetDesk.Core;
using Xunit;

namespace HelmetDesk.Tests
{
    public class CacheServiceTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "hd-cache-tests-" + Guid.NewGuid().ToString("N"));

        public CacheServiceTests() => Directory.CreateDirectory(_root);
        public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

        private string Put(string rel, int bytes)
        {
            var p = Path.Combine(_root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p));
            File.WriteAllBytes(p, new byte[bytes]);
            return p;
        }

        [Fact]
        public void Clears_only_matching_entries_and_reports_size()
        {
            Put("links/index.json", 100);
            Put("links/a.png", 50);
            Put("relatorios/r1.zip", 30);
            var keep = Put("relatorios/notes.txt", 7);
            var data = Put("data.json", 999);
            Put("tmp/hd-selftest-1/x/y.bin", 20);
            var other = Put("tmp/other.bin", 5);

            var targets = new[]
            {
                new CacheTarget(Path.Combine(_root, "links")),
                new CacheTarget(Path.Combine(_root, "relatorios"), "*.zip"),
                new CacheTarget(Path.Combine(_root, "tmp"), "hd-selftest-*"),
                new CacheTarget(Path.Combine(_root, "missing")),
            };

            Assert.Equal(200, CacheService.Measure(targets));
            var r = CacheService.Clear(targets);

            Assert.Equal(200, r.FreedBytes);
            Assert.Equal(4, r.Deleted);
            Assert.Equal(0, r.Skipped);
            Assert.True(File.Exists(keep));
            Assert.True(File.Exists(data));
            Assert.True(File.Exists(other));
            Assert.True(Directory.Exists(Path.Combine(_root, "links"))); // the folder itself stays
            Assert.False(Directory.Exists(Path.Combine(_root, "tmp", "hd-selftest-1")));
            Assert.Equal(0, CacheService.Measure(targets));
        }

        [Fact]
        public void File_in_use_is_skipped_not_fatal()
        {
            var busy = Put("links/busy.png", 10);
            Put("links/free.png", 10);
            var t = new[] { new CacheTarget(Path.Combine(_root, "links")) };
            CacheResult r;
            using (new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.None))
                r = CacheService.Clear(t);
            Assert.Equal(1, r.Deleted);
            Assert.Equal(1, r.Skipped);
            Assert.True(File.Exists(busy));
        }

        [Theory]
        [InlineData(512, "512 bytes")]
        [InlineData(1536 * 1024, "1,5 MB")]
        public void Formats_in_pt_BR(long bytes, string expected) => Assert.Equal(expected, CacheService.FormatSize(bytes));
    }
}
