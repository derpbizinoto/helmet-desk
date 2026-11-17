using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using HelmetDesk.Core;
using HelmetDesk.Core.Finance;
using Xunit;

namespace HelmetDesk.Tests
{
    internal static class TestSetup
    {
        /// <summary>The older tests assert Portuguese texts and R$ amounts: run the suite in pt-BR with BRL.
        /// New tests that need English use the explicit overloads, never L.Set (tests run in parallel).</summary>
        [ModuleInitializer]
        internal static void Init()
        {
            L.Set(L.Portuguese);
            Money.SetCurrency(Money.Brl.Code);
        }
    }

    public class LocalizationTests
    {
        private static readonly string Q = "\"((?:[^\"\\\\]|\\\\.)*)\"";

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "HelmetDesk.sln"))) dir = dir.Parent;
            Assert.NotNull(dir);
            return dir.FullName;
        }

        private static string Unescape(string s) =>
            Regex.Replace(s, @"\\(.)", m => m.Groups[1].Value switch { "n" => "\n", "t" => "\t", var c => c });

        /// <summary>Every text passed to T/TF/TN (or {local:Tr} in XAML), as the app sees it at runtime.</summary>
        private static Dictionary<string, string> SourceKeys()
        {
            var keys = new Dictionary<string, string>();
            var root = RepoRoot();
            var files = new[] { "src", "core" }
                .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "*.*", SearchOption.AllDirectories))
                .Where(f => (f.EndsWith(".cs") || f.EndsWith(".xaml"))
                            && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                            && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                            && !f.EndsWith("Snapshots.cs") && !f.EndsWith("En.cs"));
            foreach (var f in files)
            {
                var s = File.ReadAllText(f);
                var name = Path.GetFileName(f);
                if (f.EndsWith(".xaml"))
                {
                    foreach (Match m in Regex.Matches(s, @"\{local:Tr '([^']*)'\}")) keys[m.Groups[1].Value] = name;
                    foreach (Match m in Regex.Matches(s, "<local:TrExtension Text=\"([^\"]*)\"")) keys[m.Groups[1].Value] = name;
                    continue;
                }
                s = Regex.Replace(s, @"^\s*//.*$", "", RegexOptions.Multiline);
                void Add(string k) => keys[Unescape(k)] = name;
                foreach (Match m in Regex.Matches(s, @"\bT\(" + Q + @"\)")) Add(m.Groups[1].Value);
                foreach (Match m in Regex.Matches(s, @"\bTF\(" + Q)) Add(m.Groups[1].Value);
                foreach (Match m in Regex.Matches(s, @"\bTF\([^""();]*?\?\s*" + Q + @"\s*:\s*" + Q)) { Add(m.Groups[1].Value); Add(m.Groups[2].Value); }
                foreach (Match m in Regex.Matches(s, @"\bTN\([^"";]*?,\s*" + Q + @",\s*" + Q + @"\)")) { Add(m.Groups[1].Value); Add(m.Groups[2].Value); }
            }
            return keys;
        }

        [Fact]
        public void Every_text_has_an_english_translation()
        {
            var keys = SourceKeys();
            Assert.True(keys.Count > 400, $"only {keys.Count} texts found: did the scan break?");
            var missing = keys.Where(k => !En.Map.ContainsKey(k.Key)).Select(k => $"{k.Value}: {k.Key}").ToList();
            Assert.True(missing.Count == 0, "Missing in core/Localization/En.cs:\n" + string.Join("\n", missing));
        }

        [Fact]
        public void Translations_keep_the_same_placeholders()
        {
            static string Holes(string s) =>
                string.Join(",", Regex.Matches(s, @"\{(\d+)(?:[:,][^}]*)?\}").Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x));
            var bad = En.Map.Where(kv => Holes(kv.Key) != Holes(kv.Value)).Select(kv => kv.Key).ToList();
            Assert.True(bad.Count == 0, "Placeholders differ:\n" + string.Join("\n", bad));
        }

        [Fact]
        public void Translations_are_not_empty_and_dates_are_valid_formats()
        {
            Assert.DoesNotContain(En.Map, kv => string.IsNullOrWhiteSpace(kv.Value));
            foreach (var f in new[] { "dd/MM", "dd/MM/yyyy", "MMMM 'de' yyyy", "dddd, d 'de' MMMM 'de' yyyy" })
                Assert.False(string.IsNullOrEmpty(new DateTime(2026, 11, 17).ToString(En.Map[f], L.Culture)));
        }

        [Fact]
        public void Language_codes_normalize()
        {
            Assert.Equal(L.Portuguese, L.Normalize("pt-BR"));
            Assert.Equal(L.Portuguese, L.Normalize("pt"));
            Assert.Equal(L.English, L.Normalize("en"));
            Assert.Equal(L.English, L.Normalize(null));
            Assert.Equal(L.English, L.Normalize("fr"));
            Assert.Equal(Money.Usd.Code, Money.DefaultFor("en"));
            Assert.Equal(Money.Brl.Code, Money.DefaultFor("pt-BR"));
        }
    }

    public class MoneyCurrencyTests
    {
        [Theory]
        [InlineData("BRL", false, "R$ 1.234,56")]
        [InlineData("USD", false, "US$ 1.234,56")]
        [InlineData("EUR", false, "€ 1.234,56")]
        [InlineData("BRL", true, "R$1,234.56")]
        [InlineData("USD", true, "$1,234.56")]
        [InlineData("EUR", true, "€1,234.56")]
        public void Format_uses_symbol_and_language_separators(string code, bool english, string expected)
        {
            Assert.Equal(expected, Money.Format(1234.56m, Money.Find(code), english));
        }

        [Theory]
        [InlineData("BRL", false)] [InlineData("USD", false)] [InlineData("EUR", false)]
        [InlineData("BRL", true)] [InlineData("USD", true)] [InlineData("EUR", true)]
        public void Format_round_trips_through_parse(string code, bool english)
        {
            foreach (var v in new[] { 0m, 0.5m, 12m, 1234.56m, 1000000m, -200.1m })
            {
                var text = Money.Format(v, Money.Find(code), english);
                Assert.True(Money.TryParse(text, english, out var back), text);
                Assert.Equal(v, back);
            }
        }

        [Theory]
        [InlineData("1,234.56", true, 1234.56)]
        [InlineData("1234.5", true, 1234.5)]
        [InlineData("$ 10", true, 10)]
        [InlineData("1.234,56", false, 1234.56)]
        [InlineData("1234,5", false, 1234.5)]
        [InlineData("R$ 10", false, 10)]
        public void Parse_follows_the_language(string text, bool english, double expected)
        {
            Assert.True(Money.TryParse(text, english, out var v));
            Assert.Equal((decimal)expected, v);
        }

        [Fact]
        public void Compact_is_short()
        {
            Assert.Equal("$1.2K", Money.Compact(1234m, Money.Usd, true));
            Assert.Equal("R$ 3,5 mi", Money.Compact(3_500_000m, Money.Brl, false));
        }

        [Fact]
        public void Unknown_currency_falls_back_to_real()
        {
            Assert.Null(Money.Find("XYZ"));
            Assert.Contains(Money.Currencies, c => c.Code == "EUR");
        }
    }
}
