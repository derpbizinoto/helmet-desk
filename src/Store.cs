using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Threading;
using HelmetDesk.Core.Finance;

namespace HelmetDesk
{
    public static class Store
    {
        /// <summary>The data folder and data.json (see <see cref="AppPaths"/>, which holds every path).</summary>
        private static string Dir => AppPaths.DataDir;
        private static string FilePath => AppPaths.DataFile;

        /// <summary>Data format version written in data.json ("Version"). Bump it and add a step to <see cref="Migrate"/> when the format changes.</summary>
        /// 2 = Finanças added ("Finance" section). Bumped so an older app refuses a newer backup instead of dropping Finanças on save.
        public const int CurrentSchema = 2;

        /// <summary>Held while data.json is written; the backup service takes it to read a consistent copy.</summary>
        public static readonly HelmetDesk.Core.SemaphoreGate Gate = new();

        /// <summary>Set while a backup is being imported (and until the restart): nothing is saved over the new data.</summary>
        public static bool Frozen { get; set; }

        /// <summary>Brings a data.json in <paramref name="dir"/> from <paramref name="fromSchema"/> up to <see cref="CurrentSchema"/>.</summary>
        public static void Migrate(string dir, int fromSchema)
        {
            // 1 → 2: the "Finance" section is new and optional; a file without it loads with empty Finanças
            // (seeded in Load), so nothing in data.json has to be rewritten. Future steps go here, e.g.:
            // if (fromSchema < 3) { …rewrite data.json… }
        }

        public static AppData Data { get; private set; } = new();
        public static event Action LabelsChanged;

        private static bool _loading;
        private static DispatcherTimer _saveTimer;

        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        public static void Load()
        {
            _loading = true;
            try
            {
                Directory.CreateDirectory(Dir);
                if (File.Exists(FilePath))
                {
                    var text = File.ReadAllText(FilePath);
                    int schema = 1;
                    using (var doc = JsonDocument.Parse(text))
                        if (doc.RootElement.TryGetProperty("Version", out var v) && v.TryGetInt32(out var n)) schema = n;
                    if (schema < CurrentSchema)
                    {
                        Log.Info($"Migrando dados do esquema {schema} para {CurrentSchema}");
                        Migrate(Dir, schema);
                        text = File.ReadAllText(FilePath);
                    }
                    else if (schema > CurrentSchema)
                        Log.Warn($"data.json usa o esquema {schema}, mais novo que o deste app ({CurrentSchema})");
                    Data = JsonSerializer.Deserialize<AppData>(text, Json) ?? new AppData();
                    FillMissing(Data);
                    Data.Version = Math.Max(Data.Version, CurrentSchema);
                    // data from 1.0.0 (Portuguese only, always R$): keep the user's language and currency
                    Data.Settings.Language ??= L.Portuguese;
                    Data.Settings.Currency ??= Money.Brl.Code;
                    ApplyLanguage();
                    try { File.Copy(FilePath, Path.Combine(Dir, "data.bak.json"), true); } catch { }
                    // attachment files nobody points to any more (removed, or their note was deleted): only after a good load
                    var snapshot = Data.Items.ToList();
                    var loadedAt = DateTime.Now;
                    System.Threading.Tasks.Task.Run(() => Attachments.Purge(snapshot, loadedAt));
                }
                else
                {
                    // first run: English by default; the sample note, labels and categories are created in that language
                    Data = new AppData();
                    // HELMETDESK_LANG (tooling) also picks the first-run currency
                    Data.Settings.Language = Environment.GetEnvironmentVariable("HELMETDESK_LANG") is { Length: > 0 } forcedLang ? L.Normalize(forcedLang) : L.English;
                    Data.Settings.Currency = Money.DefaultFor(Data.Settings.Language);
                    ApplyLanguage();
                    Seed();
                }
                if (Data.Labels.Count == 0) SeedLabels();
                FinanceService.Seed(Data.Finance);
            }
            catch (Exception ex)
            {
                Log.Error("Não foi possível ler data.json", ex);
                // a data file existed, so this is someone updating from 1.0.0 or later: pt-BR unless it said otherwise
                L.Set(Data?.Settings?.Language ?? L.Portuguese);
                // Keep the broken file aside instead of overwriting it.
                try { File.Copy(FilePath, Path.Combine(Dir, $"data.corrompido.{DateTime.Now:yyyyMMddHHmmss}.json"), true); } catch { }
                System.Windows.MessageBox.Show(T("Não foi possível ler os dados salvos. Uma cópia do arquivo foi guardada na pasta de dados.\n\n") + ex.Message,
                    "Helmet Desk", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                Data = new AppData();
                Data.Settings.Language = L.Lang;
                Data.Settings.Currency = Money.DefaultFor(L.Lang);
                ApplyLanguage();
                SeedLabels();
                FinanceService.Seed(Data.Finance);
            }
            finally { _loading = false; }
        }

        /// <summary>
        /// Language and currency of this run, from the settings. HELMETDESK_LANG=en|pt-BR overrides the language
        /// (snapshot/self-test tooling). Missing texts are logged once each.
        /// </summary>
        /// <summary>
        /// A data.json with a section written as null (or a null entry in a list) still loads: the gap gets an empty
        /// default instead of failing the whole load, which would start the app with no data.
        /// </summary>
        private static void FillMissing(AppData d)
        {
            d.Items ??= new();
            d.Labels ??= new();
            foreach (var i in d.Items.Where(i => i == null).ToList()) d.Items.Remove(i);
            foreach (var l in d.Labels.Where(l => l == null).ToList()) d.Labels.Remove(l);
            d.Settings ??= new();
            d.Settings.Main ??= new();
            d.Settings.TasksWidgetBounds ??= new() { W = 300, H = 380 };
            d.Settings.CollapsedGroups ??= new();
            d.Finance ??= new();
            d.Finance.Accounts ??= new();
            d.Finance.Categories ??= new();
            d.Finance.Transactions ??= new();
            d.Finance.Recurrences ??= new();
        }

        private static void ApplyLanguage()
        {
            var forced = Environment.GetEnvironmentVariable("HELMETDESK_LANG");
            L.Set(string.IsNullOrWhiteSpace(forced) ? Data.Settings.Language : forced);
            L.Missing = text => Log.Warn("Texto sem tradução para o inglês: " + text);
            Money.SetCurrency(Data.Settings.Currency);
            Log.Info($"Idioma: {L.Lang}, moeda: {Money.Current.Code}");
        }

        /// <summary>Saves the new language and restarts so every screen is rebuilt with it (like the theme).</summary>
        public static void SwitchLanguage(string lang)
        {
            lang = L.Normalize(lang);
            if (Data.Settings.Language == lang) return;
            Data.Settings.Language = lang;
            Save();
            if (lang != L.Lang) App.Current.Restart();
        }

        private static void SeedLabels()
        {
            string[] names = { T("Geral"), T("Pessoal"), T("Trabalho"), T("Estudos"), T("Ideias"), T("Urgente"), T("Casa"), T("Outros") };
            for (int i = 0; i < names.Length; i++)
                Data.Labels.Add(new LabelTag { Name = names[i], Color = Palette.LabelColors[i] });
        }

        private static void Seed()
        {
            SeedLabels();
            var welcome = new Item
            {
                Kind = ItemKind.Note,
                Title = T("Bem-vindo ao Helmet Desk"),
                Body = T("Notas, tarefas e calendário num só lugar.\n\nDicas:\n• Use o botão de alfinete para fixar uma nota na área de trabalho.\n• Informe estimativas como 1:30, 1h30, 45min ou 2.\n• Agrupe por semana ou por mês na barra de ferramentas."),
                LabelId = Data.Labels[0].Id,
            };
            welcome.Checklist.Add(new CheckItem { Text = T("Criar minha primeira tarefa") });
            welcome.Checklist.Add(new CheckItem { Text = T("Fixar uma nota na área de trabalho") });
            welcome.Checklist.Add(new CheckItem { Text = T("Ativar \"Iniciar com o Windows\" nas configurações") });
            Data.Items.Add(welcome);
            Data.Items.Add(new Item
            {
                Kind = ItemKind.Task,
                Title = T("Planejar a semana"),
                Date = DateTime.Today,
                EstimateMinutes = 30,
                Priority = 2,
                LabelId = Data.Labels[2].Id,
            });
        }

        public static LabelTag LabelById(string id) =>
            id == null ? null : Data.Labels.FirstOrDefault(l => l.Id == id);

        public static void NotifyLabelsChanged()
        {
            if (_loading) return;
            foreach (var i in Data.Items) i.RaiseLabel();
            LabelsChanged?.Invoke();
            MarkDirty();
        }

        public static void MarkDirty()
        {
            if (_loading) return;
            if (_saveTimer == null)
            {
                _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                _saveTimer.Tick += (s, e) => { _saveTimer.Stop(); Save(); };
            }
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        public static void Save()
        {
            _saveTimer?.Stop();
            if (Frozen) return;
            try
            {
                var json = JsonSerializer.Serialize(Data, Json);
                using (Gate.Enter())
                {
                    Directory.CreateDirectory(Dir);
                    var tmp = FilePath + ".tmp";
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, FilePath, true);
                }
            }
            catch (Exception ex) { Log.Warn("Falha ao salvar os dados (nova tentativa na próxima alteração)", ex); }
        }

        public static Item NewItem(ItemKind kind, DateTime? date = null)
        {
            var it = new Item { Kind = kind, Date = date, LabelId = Data.Labels.FirstOrDefault()?.Id };
            Data.Items.Insert(0, it);
            MarkDirty();
            return it;
        }
    }
}
