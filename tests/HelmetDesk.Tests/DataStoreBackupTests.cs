using System;
using System.IO;
using System.Linq;
using System.Text;
using HelmetDesk.Core;
using Xunit;

namespace HelmetDesk.Tests
{
    /// <summary>Saving data.json and the copies made at startup (DataStore.Write / BackupOnStartup).</summary>
    public class DataStoreBackupTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "hd-datastore-bak-tests-" + Guid.NewGuid().ToString("N"));
        private string DataFile => Path.Combine(_dir, "data.json");
        private static readonly DateTime Day1 = new(2026, 11, 19, 1, 5, 0);

        public DataStoreBackupTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private string[] Dated() => DataStore.DatedBackups(_dir).Select(b => Path.GetFileName(b.Path)).ToArray();
        private string Put(string name, string text) { var p = Path.Combine(_dir, name); File.WriteAllText(p, text); return p; }

        // ---------- dated copies ----------

        [Fact]
        public void First_start_makes_a_dated_copy_and_data_bak_json()
        {
            Put("data.json", "v1");

            var made = DataStore.BackupOnStartup(_dir, Day1);

            Assert.Equal(Path.Combine(_dir, "data.bak.20261119-0105.json"), made);
            Assert.Equal("v1", File.ReadAllText(made));
            Assert.Equal("v1", File.ReadAllText(Path.Combine(_dir, "data.bak.json")));
        }

        [Fact]
        public void At_most_one_dated_copy_per_day()
        {
            Put("data.json", "manhã");
            DataStore.BackupOnStartup(_dir, Day1);
            Put("data.json", "tarde (algo apagado por engano)");

            Assert.Null(DataStore.BackupOnStartup(_dir, Day1.AddHours(14)));

            // today's copy still has the state before the mistake; data.bak.json follows the last start, as before
            Assert.Equal(new[] { "data.bak.20261119-0105.json" }, Dated());
            Assert.Equal("manhã", File.ReadAllText(Path.Combine(_dir, "data.bak.20261119-0105.json")));
            Assert.Equal("tarde (algo apagado por engano)", File.ReadAllText(Path.Combine(_dir, "data.bak.json")));
        }

        [Fact]
        public void No_new_copy_when_nothing_changed_since_the_last_one()
        {
            Put("data.json", "igual");
            DataStore.BackupOnStartup(_dir, Day1);

            Assert.Null(DataStore.BackupOnStartup(_dir, Day1.AddDays(1)));
            Assert.Null(DataStore.BackupOnStartup(_dir, Day1.AddDays(5)));
            Assert.Single(Dated());
        }

        [Fact]
        public void A_new_day_with_changes_makes_a_new_copy()
        {
            Put("data.json", "dia 1");
            DataStore.BackupOnStartup(_dir, Day1);
            Put("data.json", "dia 2");

            var made = DataStore.BackupOnStartup(_dir, Day1.AddDays(1).AddMinutes(30));

            Assert.Equal(Path.Combine(_dir, "data.bak.20261120-0135.json"), made);
            Assert.Equal(new[] { "data.bak.20261119-0105.json", "data.bak.20261120-0135.json" }, Dated());
            Assert.Equal("dia 2", File.ReadAllText(made));
        }

        [Fact]
        public void Keeps_only_the_five_newest_and_deletes_the_oldest()
        {
            for (int d = 0; d < 7; d++)
            {
                Put("data.json", "versão " + d);
                DataStore.BackupOnStartup(_dir, Day1.AddDays(d));
            }

            Assert.Equal(new[]
            {
                "data.bak.20261121-0105.json", "data.bak.20261122-0105.json", "data.bak.20261123-0105.json",
                "data.bak.20261124-0105.json", "data.bak.20261125-0105.json",
            }, Dated());
            Assert.Equal("versão 6", File.ReadAllText(Path.Combine(_dir, "data.bak.20261125-0105.json")));
            Assert.Equal("versão 2", File.ReadAllText(Path.Combine(_dir, "data.bak.20261121-0105.json")));
        }

        [Fact]
        public void Pruning_leaves_other_files_alone()
        {
            for (int d = 1; d <= 6; d++) Put($"data.bak.202611{d:00}-0900.json", "antiga " + d);
            var keep = new[]
            {
                Put("data.json", "atual"),
                Put("data.bak.json", "última abertura"),
                Put("data.corrompido.20261101120000.json", "{ quebrado"),
                Put("data.bak.manual.json", "cópia do usuário"),
                Put("data.json.tmp", "x"),
            };

            DataStore.BackupOnStartup(_dir, Day1);

            Assert.Equal(new[]
            {
                "data.bak.20261103-0900.json", "data.bak.20261104-0900.json", "data.bak.20261105-0900.json",
                "data.bak.20261106-0900.json", "data.bak.20261119-0105.json",
            }, Dated());
            Assert.All(keep, f => Assert.True(File.Exists(f), f));
        }

        [Fact]
        public void A_smaller_keep_count_is_respected()
        {
            for (int d = 0; d < 4; d++) { Put("data.json", "v" + d); DataStore.BackupOnStartup(_dir, Day1.AddDays(d), keep: 2); }
            Assert.Equal(2, Dated().Length);
        }

        [Fact]
        public void Missing_data_json_makes_no_copy_and_does_not_throw()
        {
            Assert.Null(DataStore.BackupOnStartup(_dir, Day1));
            Assert.Empty(Directory.GetFiles(_dir));
        }

        // ---------- saving ----------

        [Fact]
        public void Write_produces_utf8_without_bom_and_leaves_no_temp_file()
        {
            const string json = "{ \"Title\": \"Café ☕\" }";
            DataStore.Write(_dir, json);

            var bytes = File.ReadAllBytes(DataFile);
            Assert.Equal(Encoding.UTF8.GetBytes(json), bytes);
            Assert.False(File.Exists(DataFile + ".tmp"));
        }

        [Fact]
        public void Write_replaces_a_leftover_temp_file_from_an_interrupted_save()
        {
            Put("data.json", "antigo");
            Put("data.json.tmp", "lixo de um salvamento interrompido, maior que o novo conteúdo");

            DataStore.Write(_dir, "novo");

            Assert.Equal("novo", File.ReadAllText(DataFile));
            Assert.False(File.Exists(DataFile + ".tmp"));
        }
    }
}
