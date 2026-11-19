using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using HelmetDesk.Core;
using HelmetDesk.Core.Finance;
using Xunit;

namespace HelmetDesk.Tests
{
    /// <summary>Loading, migrating and saving data.json (core/DataStore.cs, used by src/Store.cs).</summary>
    public class DataStoreTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "hd-datastore-tests-" + Guid.NewGuid().ToString("N"));
        private string DataFile => Path.Combine(_dir, "data.json");

        public DataStoreTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        /// <summary>The persisted shape of src/Models.cs AppData, enough to check what the app would get.</summary>
        private sealed class Shape
        {
            public int Version { get; set; } = DataStore.CurrentSchema;
            public List<JsonElement?> Items { get; set; }
            public List<JsonElement?> Labels { get; set; }
            public SettingsShape Settings { get; set; }
            public FinanceData Finance { get; set; }
        }

        private sealed class SettingsShape
        {
            public string Theme { get; set; }
            public BoundsShape Main { get; set; }
            public BoundsShape TasksWidgetBounds { get; set; }
            public List<string> CollapsedGroups { get; set; }
        }

        private sealed class BoundsShape
        {
            public double X { get; set; } = double.NaN;
            public double Y { get; set; } = double.NaN;
            public double W { get; set; }
            public double H { get; set; }
        }

        private Shape Load()
        {
            var content = DataStore.Read(_dir);
            Assert.NotNull(content);
            return JsonSerializer.Deserialize<Shape>(content.Json, DataStore.JsonOptions);
        }

        // ---------- schema ----------

        [Fact]
        public void No_file_is_a_first_run()
        {
            Assert.Null(DataStore.Read(_dir));
        }

        [Fact]
        public void Schema_1_file_loads_with_empty_finance()
        {
            // 1.0.0 file: no "Version", no "Finance"
            const string v1 = """
            {
              "Items": [ { "Id": "a1", "Title": "Comprar pão", "Kind": 1 } ],
              "Labels": [ { "Id": "l1", "Name": "Geral", "Color": "#8950D9" } ],
              "Settings": { "Theme": "classic" }
            }
            """;
            File.WriteAllText(DataFile, v1);
            var log = new List<string>();

            var content = DataStore.Read(_dir, log.Add, log.Add);
            var d = JsonSerializer.Deserialize<Shape>(content.Json, DataStore.JsonOptions);

            Assert.Equal(1, content.Schema);
            Assert.Contains(log, m => m.Contains("esquema 1 para 2"));
            Assert.Single(d.Items);
            Assert.Single(d.Labels);
            Assert.Equal("classic", d.Settings.Theme);
            Assert.NotNull(d.Finance);
            Assert.Empty(d.Finance.Accounts);
            Assert.Empty(d.Finance.Categories);
            Assert.Empty(d.Finance.Transactions);
            Assert.Empty(d.Finance.Recurrences);
            Assert.False(d.Finance.Seeded);
            // loading never rewrites the file
            Assert.Equal(v1, File.ReadAllText(DataFile));
        }

        [Fact]
        public void Current_schema_is_read_and_not_migrated()
        {
            File.WriteAllText(DataFile, """{ "Version": 2, "Items": [] }""");
            var log = new List<string>();
            Assert.Equal(2, DataStore.Read(_dir, log.Add, log.Add).Schema);
            Assert.Empty(log);
        }

        [Fact]
        public void Newer_schema_loads_with_a_warning()
        {
            File.WriteAllText(DataFile, """{ "Version": 9, "Items": [] }""");
            var warnings = new List<string>();
            var content = DataStore.Read(_dir, null, warnings.Add);
            Assert.Equal(9, content.Schema);
            Assert.Contains(warnings, m => m.Contains("esquema 9"));
        }

        // ---------- null / missing sections ----------

        [Fact]
        public void Null_sections_and_items_do_not_break_the_load()
        {
            File.WriteAllText(DataFile, """
            {
              "Version": 2,
              "Items": [ null, { "Id": "a1" }, null ],
              "Labels": null,
              "Settings": { "Theme": "aero", "Main": null, "TasksWidgetBounds": null, "CollapsedGroups": null },
              "Finance": { "Accounts": null, "Categories": [ { "Id": "c1", "Name": "Lazer" } ], "Transactions": null, "Recurrences": null }
            }
            """);

            var d = Load();

            Assert.Single(d.Items);
            Assert.Equal("a1", d.Items[0].Value.GetProperty("Id").GetString());
            Assert.NotNull(d.Labels);
            Assert.Empty(d.Labels);
            Assert.Equal("aero", d.Settings.Theme);
            Assert.NotNull(d.Settings.Main);
            Assert.True(double.IsNaN(d.Settings.Main.X));
            Assert.Equal(300, d.Settings.TasksWidgetBounds.W);
            Assert.Equal(380, d.Settings.TasksWidgetBounds.H);
            Assert.True(double.IsNaN(d.Settings.TasksWidgetBounds.X));
            Assert.Empty(d.Settings.CollapsedGroups);
            Assert.Empty(d.Finance.Accounts);
            Assert.Equal("Lazer", Assert.Single(d.Finance.Categories).Name);
            Assert.Empty(d.Finance.Transactions);
            Assert.Empty(d.Finance.Recurrences);
        }

        [Fact]
        public void Whole_sections_written_as_null_get_their_defaults()
        {
            File.WriteAllText(DataFile, """{ "Version": 2, "Items": null, "Settings": null, "Finance": null }""");

            var d = Load();

            Assert.Empty(d.Items);
            Assert.Empty(d.Labels); // missing entirely
            Assert.NotNull(d.Settings.Main);
            Assert.Equal(300, d.Settings.TasksWidgetBounds.W);
            Assert.Empty(d.Settings.CollapsedGroups);
            Assert.Empty(d.Finance.Accounts);
            Assert.Empty(d.Finance.Transactions);
        }

        [Fact]
        public void Values_that_are_present_are_kept_as_they_are()
        {
            File.WriteAllText(DataFile, """
            {
              "Settings": { "Main": { "X": "NaN", "Y": 12.5, "W": 1024, "H": 700 }, "TasksWidgetBounds": { "X": 5, "Y": 6, "W": 250, "H": 410 } },
              "Finance": { "LastAccountId": "acc", "Seeded": true, "Transactions": [ { "Amount": 1234.56, "Description": "Café ☕ \"forte\"" } ] }
            }
            """);

            var d = Load();

            Assert.True(double.IsNaN(d.Settings.Main.X));
            Assert.Equal(12.5, d.Settings.Main.Y);
            Assert.Equal(1024, d.Settings.Main.W);
            Assert.Equal(250, d.Settings.TasksWidgetBounds.W);
            Assert.Equal(410, d.Settings.TasksWidgetBounds.H);
            Assert.Equal("acc", d.Finance.LastAccountId);
            Assert.True(d.Finance.Seeded);
            var tx = Assert.Single(d.Finance.Transactions);
            Assert.Equal(1234.56m, tx.Amount);
            Assert.Equal("Café ☕ \"forte\"", tx.Description);
        }

        // ---------- corrupt file ----------

        [Theory]
        [InlineData("{ isto não é json")]
        [InlineData("[1, 2, 3]")]
        [InlineData("null")]
        [InlineData("""{ "Version": "dois" }""")]
        [InlineData("")]
        public void Corrupt_file_throws_and_is_kept_aside_without_touching_the_original(string text)
        {
            File.WriteAllText(DataFile, text);
            var before = File.ReadAllBytes(DataFile);

            Assert.ThrowsAny<Exception>(() => DataStore.Read(_dir));
            var copy = DataStore.KeepCorruptCopy(_dir, new DateTime(2026, 11, 19, 1, 5, 9));

            Assert.Equal(Path.Combine(_dir, "data.corrompido.20261119010509.json"), copy);
            Assert.Equal(before, File.ReadAllBytes(copy));
            Assert.Equal(before, File.ReadAllBytes(DataFile));
        }

        // ---------- writing ----------

        [Fact]
        public void Save_then_load_gives_the_same_data()
        {
            var finance = new FinanceData { LastAccountId = "acc1", Seeded = true };
            finance.Accounts.Add(new Account { Id = "acc1", Name = "Carteira", Type = AccountType.Wallet, InitialBalance = 10.5m });
            finance.Categories.Add(new Category { Id = "cat1", Name = "Alimentação", Kind = CategoryKind.Expense });
            finance.Transactions.Add(new FinanceTransaction
            {
                Id = "t1", Kind = TxKind.Expense, Amount = 19.99m, Date = new DateTime(2026, 11, 18),
                Description = "Almoço — açaí", CategoryId = "cat1", AccountId = "acc1", Paid = true,
            });
            finance.Recurrences.Add(new Recurrence { Id = "r1", Description = "Aluguel", Amount = 1500m, StartMonth = new DateTime(2026, 1, 1) });
            var saved = new Shape
            {
                Items = new() { JsonDocument.Parse("""{ "Id": "a1", "Title": "Nota", "Body": "linha 1\nlinha 2" }""").RootElement },
                Labels = new() { JsonDocument.Parse("""{ "Id": "l1", "Name": "Geral" }""").RootElement },
                Settings = new SettingsShape
                {
                    Theme = "aero",
                    Main = new BoundsShape { W = 1100, H = 720 },
                    TasksWidgetBounds = new BoundsShape { X = 40, Y = 50, W = 300, H = 380 },
                    CollapsedGroups = new() { "2026-W47" },
                },
                Finance = finance,
            };
            var json = JsonSerializer.Serialize(saved, DataStore.JsonOptions);

            DataStore.Write(_dir, json);
            var loaded = Load();

            Assert.Equal(json, JsonSerializer.Serialize(loaded, DataStore.JsonOptions));
            Assert.Equal(json, File.ReadAllText(DataFile));
            Assert.False(File.Exists(DataFile + ".tmp"));
        }

        [Fact]
        public void Write_replaces_the_previous_file_and_creates_the_folder()
        {
            Directory.Delete(_dir, true);
            DataStore.Write(_dir, """{ "Version": 2, "Items": [] }""");
            DataStore.Write(_dir, """{ "Version": 2, "Items": [ { "Id": "x" } ] }""");

            Assert.Single(Load().Items);
            Assert.Equal(new[] { "data.json" }, Directory.GetFiles(_dir).Select(Path.GetFileName));
        }

        [Fact]
        public void Duplicate_keys_still_load_like_the_serializer_reads_them()
        {
            // hand-edited files: the last value wins, as before DataStore existed
            File.WriteAllText(DataFile, """{ "Settings": { "Theme": "classic" }, "Settings": { "Theme": "aero" } }""");
            Assert.Equal("aero", Load().Settings.Theme);
        }
    }
}
