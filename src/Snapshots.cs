using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HelmetDesk.Core;

namespace HelmetDesk
{
    /// <summary>Developer aid: renders windows to PNG (run with --snapshot &lt;folder&gt;).</summary>
    public static class Snapshots
    {
        public static void Save(Window w, string file)
        {
            if (w.Content is not FrameworkElement root) return;
            root.UpdateLayout();
            int width = (int)Math.Ceiling(root.ActualWidth), height = (int)Math.Ceiling(root.ActualHeight);
            if (width <= 0 || height <= 0) return;
            var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
                dc.DrawRectangle(w.Background, null, new Rect(0, 0, width, height));
            rtb.Render(dv);
            // the window's template root also holds the adorner layer (fading ghosts of removed items/panels)
            rtb.Render(VisualTreeHelper.GetChildrenCount(w) > 0 ? (Visual)VisualTreeHelper.GetChild(w, 0) : root);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            using var fs = File.Create(file);
            enc.Save(fs);
        }

        public static async Task Idle(Dispatcher d) => await d.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    public partial class MainWindow
    {
        /// <summary>Developer aid: exercises the main flows (run with --selftest &lt;file&gt;).</summary>
        public async Task<string> SelfTest()
        {
            var log = new System.Text.StringBuilder();
            void Check(string name, bool ok) => log.AppendLine((ok ? "OK   " : "FAIL ") + name);
            Motion.Off = true; // flows below check state, not transitions (those are tested at the end)
            SoundService.Muted = true;
            await SoundTests(Check);

            // embedded Google Fonts resolve (not a silent fallback to Segoe UI)
            bool FontLoads(string key, string family, FontWeight w, FontStyle s)
            {
                var ff = (FontFamily)FindResource(key);
                return new Typeface(ff, s, w, FontStretches.Normal).TryGetGlyphTypeface(out var g)
                    && g.FamilyNames.Values.Contains(family) && g.Weight == w && g.Style == s && g.CharacterToGlyphMap.ContainsKey('ç');
            }
            Check("fonte Roboto embutida", FontLoads("UiFont", "Roboto", FontWeights.Normal, FontStyles.Normal));
            Check("Roboto negrito e itálico", FontLoads("UiFont", "Roboto", FontWeights.Bold, FontStyles.Italic) && FontLoads("UiFont", "Roboto", FontWeights.Light, FontStyles.Normal));
            Check("fonte Geo embutida", FontLoads("BrandFont", "Geo", FontWeights.Medium, FontStyles.Normal));

            // hours parsing
            Check("parse 1:30", Hours.TryParse("1:30", out var m) && m == 90);
            Check("parse 1h30", Hours.TryParse("1h30", out m) && m == 90);
            Check("parse 45min", Hours.TryParse("45min", out m) && m == 45);
            Check("parse 1,5", Hours.TryParse("1,5", out m) && m == 90);
            Check("parse 2", Hours.TryParse("2", out m) && m == 120);
            Check("parse lixo falha", !Hours.TryParse("abc", out _));
            Check("format 150", Hours.Format(150) == "2h30");
            Check("time 9h", Dates.NormalizeTime("9h") == "09:00");
            Check("date 25/12", Dates.TryParseDate("25/12", out var dd) && dd.Value.Month == 12 && dd.Value.Day == 25);

            int before = Store.Data.Items.Count;
            NavNotes.IsChecked = true;
            CreateItem(ItemKind.Note);
            var note = _editing;
            Check("cria nota e abre editor", note != null && note.IsNote && Store.Data.Items.Count == before + 1);
            note.Title = "Teste";
            note.EstimateText = "1h15";
            Check("estimativa via texto", note.EstimateMinutes == 75);
            NewCheck.Text = "item A";
            NewCheck_KeyDown(null, new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
            Check("checklist adiciona", note.Checklist.Count == 1 && note.ChecklistText == "0/1");
            note.Checklist[0].Done = true;
            Check("checklist marca", note.ChecklistText == "1/1");

            Convert_Click(null, null);
            Check("converte em tarefa", note.IsTask);
            Convert_Click(null, null);

            App.Current.OpenWidget(note, false);
            await Snapshots.Idle(Dispatcher);
            Check("widget abre", App.Current.IsWidgetOpen(note) && note.WidgetOpen);
            App.Current.CloseWidget(note);
            await Snapshots.Idle(Dispatcher);
            Check("widget fecha e nota continua", !App.Current.IsWidgetOpen(note) && Store.Data.Items.Contains(note));

            // closed from outside (Alt+F4 / Window.Close) — used to throw "cannot Close while closing"
            App.Current.OpenWidget(note, false);
            await Snapshots.Idle(Dispatcher);
            var stickyWin = Application.Current.Windows.OfType<StickyWindow>().First(x => x.Item == note);
            bool closedOk;
            try { stickyWin.Close(); closedOk = true; } catch (InvalidOperationException) { closedOk = false; }
            await Snapshots.Idle(Dispatcher);
            Check("widget fechado por fora (Alt+F4) sem erro", closedOk && !App.Current.IsWidgetOpen(note) && !note.WidgetOpen && Store.Data.Items.Contains(note));

            App.Current.NewStickyNote();
            await Snapshots.Idle(Dispatcher);
            var empty = Store.Data.Items[0];
            App.Current.CloseWidget(empty);
            await Snapshots.Idle(Dispatcher);
            Check("nota adesiva vazia é descartada", !Store.Data.Items.Contains(empty));

            DeleteItem(note);
            Check("exclui", !Store.Data.Items.Contains(note) && _editing == null);
            SnackAction_Click(null, null);
            Check("desfazer restaura", Store.Data.Items.Contains(note));

            NavTasks.IsChecked = true;
            CreateItem(ItemKind.Task);
            var task = _editing;
            task.Title = "Tarefa teste"; task.Date = DateTime.Today; task.EstimateMinutes = 30;
            CloseEditor();
            Refresh();
            var groups = (System.Collections.Generic.List<ItemGroup>)TaskGroups.ItemsSource;
            Check("tarefa aparece agrupada na semana", groups.Exists(g => g.Items.Contains(task) && g.Title.StartsWith("Esta semana")));
            GroupMonth.IsChecked = true;
            groups = (System.Collections.Generic.List<ItemGroup>)TaskGroups.ItemsSource;
            Check("agrupamento por mês", groups.Exists(g => g.Items.Contains(task) && g.Title == Dates.MonthTitle(DateTime.Today)));
            GroupWeek.IsChecked = true;
            task.Done = true;
            Refresh();
            groups = (System.Collections.Generic.List<ItemGroup>)TaskGroups.ItemsSource;
            Check("concluída sai de pendentes", !groups.Exists(g => g.Items.Contains(task)));

            var lbl = Store.Data.Labels[1];
            task.LabelId = lbl.Id;
            Check("etiqueta aplica cor", task.LabelBrush == lbl.Brush);

            Store.Save();
            var json = File.ReadAllText(Store.FilePath);
            Check("salva em disco", json.Contains("Tarefa teste") && json.Contains("\"Teste\""));

            // ----- Lottie component -----
            var missing = new LottieView { Source = "nao-existe-teste", AutoPlay = false };
            bool missingDone = false;
            missing.Completed += (s, e) => missingDone = true;
            missing.Play();
            Check("lottie: arquivo ausente falha em silêncio", missing.LoadFailed && !missing.HasAnimation && missingDone);
            Check("lottie: falha registrada no log", HelmetDesk.Core.LogTail.Read(Log.Dir, 400).Any(l => l.Contains("nao-existe-teste")));

            var bundled = new LottieView { Source = "tarefa-concluida", AutoPlay = false };
            bundled.Play();
            Check("lottie: tarefa-concluida embutida carrega", bundled.HasAnimation && !bundled.LoadFailed);
            bundled.Stop();

            // TaskCheck animates on user click (side panel "Concluída"), not on programmatic/data changes
            {
                LottieView.ReducedMotionOverride = false;
                var t2 = Store.NewItem(ItemKind.Task, DateTime.Today);
                t2.Title = "Painel teste";
                NavTasks.IsChecked = true;
                OpenItem(t2);
                await Snapshots.Idle(Dispatcher);
                var vcb = FindAll<CheckBox>(Viewer).Find(c => c.Style == Ui.St("TaskCheck"));
                var vfx = vcb?.Template.FindName("fx", vcb) as LottieView;
                Check("painel: checkbox redondo tem animação", vfx != null);
                if (vfx != null)
                {
                    t2.Done = true; // data change, e.g. opening an already-done task
                    await Snapshots.Idle(Dispatcher);
                    Check("painel: marcar sem clique não anima", vcb.IsChecked == true && !vfx.IsAnimating);
                    t2.Done = false;
                    await Snapshots.Idle(Dispatcher);
                    vcb.IsChecked = true; // what a click does…
                    vcb.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); // …plus the Click event
                    Check("painel: clique em Concluída anima", vfx.IsAnimating && t2.Done);
                    await Task.Delay(500);
                    Check("painel: depois volta ao desenho estático", !vfx.IsAnimating);
                }
                CloseEditor();
                ToggleItem(t2);
                Check("painel: clicar no cartão abre o painel", _editing == t2);
                ToggleItem(t2);
                Check("painel: clicar de novo no mesmo cartão fecha", _editing == null);
                Store.Data.Items.Remove(t2);
                LottieView.ReducedMotionOverride = null;
            }

            // segment 14–20 (the "press"): starts right away and lasts ~200 ms
            {
                var segHost = new Window { Width = 60, Height = 60, ShowInTaskbar = false, Left = -2000, Top = -2000, WindowStyle = WindowStyle.None };
                var seg = new LottieView { Source = "tarefa-concluida", StartFrame = 14, EndFrame = 20, Size = 24, AutoPlay = false };
                segHost.Content = seg;
                segHost.Show();
                await Snapshots.Idle(Dispatcher);
                LottieView.ReducedMotionOverride = false;
                var segDone = new TaskCompletionSource<bool>();
                seg.Completed += (s, e) => segDone.TrySetResult(true);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                seg.Play();
                double startPos = seg.Progress;
                bool segFinished = await Task.WhenAny(segDone.Task, Task.Delay(2000)) == segDone.Task;
                sw.Stop();
                double total = 30.0000012219251; // frames in the file
                Check($"lottie trecho: começa no quadro 14 ({startPos * total:0.0})", Math.Abs(startPos * total - 14) < 0.5);
                Check($"lottie trecho: dura ~200 ms ({sw.ElapsedMilliseconds} ms)", segFinished && sw.ElapsedMilliseconds >= 150 && sw.ElapsedMilliseconds < 450);
                Check($"lottie trecho: termina no quadro 20 ({seg.Progress * total:0.0})", Math.Abs(seg.Progress * total - 20) < 0.5);
                LottieView.ReducedMotionOverride = null;
                segHost.Close();
            }

            foreach (var (envVar, label) in new[] { ("HELMETDESK_TEST_LOTTIE_JSON", ".json"), ("HELMETDESK_TEST_LOTTIE_ZIP", ".lottie") })
            {
                var path = Environment.GetEnvironmentVariable(envVar);
                if (string.IsNullOrEmpty(path)) continue;
                var host = new Window { Width = 120, Height = 120, ShowInTaskbar = false, Left = -2000, Top = -2000, WindowStyle = WindowStyle.None };
                var lv = new LottieView { Source = path, Size = 60, AutoPlay = false };
                host.Content = lv;
                host.Show();
                await Snapshots.Idle(Dispatcher);

                LottieView.ReducedMotionOverride = false;
                var done = new TaskCompletionSource<bool>();
                EventHandler h = (s, e) => done.TrySetResult(true);
                lv.Completed += h;
                lv.Play();
                bool started = lv.HasAnimation && lv.IsPlaying;
                bool finished = await Task.WhenAny(done.Task, Task.Delay(3000)) == done.Task;
                Check($"lottie {label}: carrega e toca uma vez", started && finished && !lv.IsPlaying && lv.Progress == 1);
                var shotDir = Environment.GetEnvironmentVariable("HELMETDESK_TEST_SHOTS");
                if (!string.IsNullOrEmpty(shotDir)) Snapshots.Save(host, Path.Combine(shotDir, $"lottie{label}.png"));

                LottieView.ReducedMotionOverride = true;
                done = new TaskCompletionSource<bool>();
                lv.Play();
                Check($"lottie {label}: movimento reduzido mostra quadro final parado", !lv.IsPlaying && lv.Progress == 1);
                finished = await Task.WhenAny(done.Task, Task.Delay(2000)) == done.Task;
                Check($"lottie {label}: movimento reduzido ainda avisa o fim", finished);
                lv.Completed -= h;
                LottieView.ReducedMotionOverride = null;

                lv.Loop = true;
                lv.Play();
                await Task.Delay(900);
                Check($"lottie {label}: loop continua tocando", lv.IsPlaying);
                lv.Stop();
                host.Close();
            }

            // sidebar shortcut for the tasks widget, kept in sync with the switch in Configurações
            {
                App.Current.ShowTasksWidget(false);
                await Snapshots.Idle(Dispatcher);
                var settingsSwitch = FindAll<CheckBox>(SettingsPanel).Find(c => c.GetBindingExpression(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)?.ParentBinding.Path.Path == "TasksWidget");
                void ClickRow() => SideWidgetRow.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
                ClickRow();
                await Snapshots.Idle(Dispatcher);
                bool widgetOpen = Application.Current.Windows.OfType<TasksWidget>().Any();
                Check("atalho lateral: liga o widget de tarefas", Store.Data.Settings.TasksWidget && widgetOpen && SideWidgetSwitch.IsChecked == true);
                Check("atalho lateral: chave em Configurações acompanha", settingsSwitch?.IsChecked == true);
                ClickRow();
                await Snapshots.Idle(Dispatcher);
                Check("atalho lateral: desliga o widget", !Store.Data.Settings.TasksWidget && !Application.Current.Windows.OfType<TasksWidget>().Any()
                    && SideWidgetSwitch.IsChecked == false && settingsSwitch?.IsChecked == false);
            }

            // estimate belongs to tasks only
            {
                var n = Store.NewItem(ItemKind.Note, null);
                n.EstimateMinutes = 60;
                Check("nota não mostra estimativa", !n.HasEstimate);
                n.Kind = ItemKind.Task;
                Check("tarefa mostra estimativa", n.HasEstimate);
                Store.Data.Items.Remove(n);
            }
            Check("descrição começa no topo (alinhada ao aviso)", ((Style)FindResource("BareTextBox")).Setters.OfType<Setter>()
                .Any(s => s.Property == Control.VerticalContentAlignmentProperty && (VerticalAlignment)s.Value == VerticalAlignment.Top));

            // delete confirmation
            {
                Store.Data.Settings.ConfirmDelete = true;
                var d = Store.NewItem(ItemKind.Task, DateTime.Today); d.Title = "Excluir teste";
                RequestDelete(d);
                Check("excluir: pergunta antes", DialogOpen && Store.Data.Items.Contains(d) && DialogTitle.Text == "Excluir tarefa?");
                CloseDialog(false);
                Check("excluir: cancelar mantém o item", !DialogOpen && Store.Data.Items.Contains(d) && Store.Data.Settings.ConfirmDelete);
                RequestDelete(d);
                DialogDontAsk.IsChecked = true;
                CloseDialog(true);
                Check("excluir: confirmar exclui e 'não perguntar' desliga a opção", !Store.Data.Items.Contains(d) && !Store.Data.Settings.ConfirmDelete && ConfirmDeleteSwitch.IsChecked == false);
                var d2 = Store.NewItem(ItemKind.Note, null);
                RequestDelete(d2);
                Check("excluir: sem a pergunta, exclui na hora", !Store.Data.Items.Contains(d2) && !DialogOpen);
                Store.Data.Settings.ConfirmDelete = true;
                Check("excluir: opção nas configurações volta a ligar", ConfirmDeleteSwitch.IsChecked == true);
                HideSnack();
            }

            // sidebar collapse / expand
            {
                bool startCollapsed = Store.Data.Settings.SidebarCollapsed;
                if (startCollapsed) ToggleSidebar();
                await Snapshots.Idle(Dispatcher);
                ToggleSidebar();
                await Snapshots.Idle(Dispatcher);
                var label = FindAll<TextBlock>(NavTasks).Find(t => t.Text == "Tarefas");
                Check($"barra lateral: recolhe só com ícones ({SideCol.ActualWidth})", SideCol.ActualWidth == CompactWidth && label?.Visibility == Visibility.Collapsed
                    && SideSplitter.Visibility == Visibility.Collapsed);
                Check("barra lateral: nome aparece ao passar o mouse", (NavTasks.ToolTip as string) == "Tarefas" && SideTodayCompact.Visibility == Visibility.Visible);
                ToggleSidebar();
                await Snapshots.Idle(Dispatcher);
                Check($"barra lateral: expande de volta ({SideCol.ActualWidth})", SideCol.ActualWidth >= MinSideWidth && label?.Visibility == Visibility.Visible
                    && NavTasks.ToolTip == null && SideSplitter.Visibility == Visibility.Visible);
                Check("barra lateral: largura ajustável entre 180 e 380", SideCol.MinWidth == MinSideWidth && SideCol.MaxWidth == MaxSideWidth);
                // drag the edge: +60 px, then far beyond the maximum
                double w0 = SideCol.ActualWidth;
                void Drag(double dx)
                {
                    SideSplitter.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0) { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragStartedEvent });
                    SideSplitter.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(dx, 0) { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragDeltaEvent });
                    SideSplitter.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(dx, 0, false) { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragCompletedEvent });
                    UpdateLayout();
                }
                Drag(60);
                Check($"barra lateral: arrastar a borda aumenta ({w0:0} → {SideCol.ActualWidth:0}) e guarda a largura",
                    Math.Abs(SideCol.ActualWidth - (w0 + 60)) < 1 && Math.Abs(Store.Data.Settings.SidebarWidth - SideCol.ActualWidth) < 1);
                Drag(1000);
                Check($"barra lateral: não passa do máximo ({SideCol.ActualWidth:0})", SideCol.ActualWidth <= MaxSideWidth + 0.5);
                ToggleSidebar(); ToggleSidebar(); UpdateLayout();
                Check("barra lateral: ao reabrir mantém a largura escolhida", Math.Abs(SideCol.ActualWidth - Store.Data.Settings.SidebarWidth) < 1);
                Drag(-1000);
                Store.Data.Settings.SidebarWidth = 228; ApplySidebar(false);
            }

            await QuickAddTests(Check);
            await SupportTests(Check);
            await FinanceTests(Check);
            await AttachTests(Check);
            await MotionTests(Check);
            return log.ToString();
        }

        private async Task SupportTests(Action<string, bool> Check)
        {
            // ---- log ----
            Log.Info("autoteste: linha de log");
            Check("log: Serilog grava em %LocalAppData%\\HelmetDesk\\logs", LogTail.Read(Log.Dir, 50).Any(l => l.Contains("autoteste: linha de log")));

            // ---- export → import inside the running app ----
            var tmpDir = Path.Combine(Path.GetTempPath(), "hd-selftest-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmpDir);
            try
            {
                var svc = CreateBackupService();
                var zip = Path.Combine(tmpDir, svc.SuggestedFileName(DateTimeOffset.Now));
                Store.Save();
                var before = File.ReadAllBytes(Store.FilePath);
                bool exported = await ExportTo(svc, zip);
                bool hasEntries = false;
                if (File.Exists(zip))
                    using (var z = System.IO.Compression.ZipFile.OpenRead(zip))
                        hasEntries = z.GetEntry("manifest.json") != null && z.GetEntry("data.json") != null;
                Check("backup: exportar cria o .zip com manifest", exported && hasEntries);
                HideSnack();

                bool imported = await ImportFrom(svc, zip, confirm: false, restart: false);
                Check("backup: importar o mesmo arquivo deixa os dados idênticos e trava gravações até reiniciar",
                    imported && Store.Frozen && before.SequenceEqual(File.ReadAllBytes(Store.FilePath)));
                Check("backup: cópia de segurança pre-import guardada",
                    Directory.GetFiles(Path.Combine(Store.LocalDir, "backups"), "pre-import-*.zip").Length > 0);
                Store.Frozen = false;

                var bad = Path.Combine(tmpDir, "ruim.zip");
                File.WriteAllText(bad, "isto não é um zip");
                bool ok = await ImportFrom(svc, bad, confirm: false, restart: false);
                Check("backup: zip inválido mostra erro e não mexe nos dados",
                    !ok && DialogOpen && DialogTitle.Text == "Backup não pode ser importado" && DialogCancel.Visibility == Visibility.Collapsed
                    && before.SequenceEqual(File.ReadAllBytes(Store.FilePath)) && ExportBtn.IsEnabled && ImportBtn.IsEnabled);
                CloseDialog(true);
            }
            finally { try { Directory.Delete(tmpDir, true); } catch { } }

            // ---- bug report window: form → preview ----
            var w = new BugReportWindow(ReportService, "System.Exception: teste") { Owner = this, ShowActivated = false };
            w.Show();
            await Snapshots.Idle(Dispatcher);
            w.DescBox.Text = "";
            w.NextBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check("report: pede a descrição antes de seguir", w.FormPage.Visibility == Visibility.Visible && w.Status.Text.Contains("Descreva"));
            w.DescBox.Text = "Passos para reproduzir.";
            w.NextBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check("report: prévia mostra o conteúdo exato (sistema, erro, sem dados do usuário)",
                w.PreviewPage.Visibility == Visibility.Visible && w.PreviewBox.Text.Contains("Versão do app") && w.PreviewBox.Text.Contains("System.Exception: teste")
                && w.PreviewBox.Text.Contains("Dados do usuário: não incluídos.") && w.PreviewBox.Text.Contains("Windows"));
            w.Close();
        }

        /// <summary>Finanças screenshots with a sample month (restored afterwards).</summary>
        private async Task FinanceShots(Func<string, Task> Shot)
        {
            var saved = System.Text.Json.JsonSerializer.Serialize(Store.Data.Finance);
            try
            {
                var D = Store.Data.Finance;
                var today = DateTime.Today;
                var m = new DateTime(today.Year, today.Month, 1);
                var wallet = D.Accounts[0];
                wallet.InitialBalance = 180m;
                var bank = new HelmetDesk.Core.Finance.Account { Name = "Nubank", Type = HelmetDesk.Core.Finance.AccountType.Checking, InitialBalance = 2350m, Color = "#8950D9" };
                var card = new HelmetDesk.Core.Finance.Account { Name = "Cartão Inter", Type = HelmetDesk.Core.Finance.AccountType.CreditCard, Color = "#F2A65A" };
                var save = new HelmetDesk.Core.Finance.Account { Name = "Reserva", Type = HelmetDesk.Core.Finance.AccountType.Savings, InitialBalance = 5000m, Color = "#64D6A2" };
                D.Accounts.AddRange(new[] { bank, card, save });
                D.LastAccountId = bank.Id;
                string C(string n) => D.Categories.First(c => c.Name == n).Id;
                HelmetDesk.Core.Finance.FinanceTransaction T(HelmetDesk.Core.Finance.TxKind k, decimal v, int day, string desc, string cat, HelmetDesk.Core.Finance.Account a, bool paid)
                {
                    var t = new HelmetDesk.Core.Finance.FinanceTransaction { Kind = k, Amount = v, Date = m.AddDays(Math.Min(day, DateTime.DaysInMonth(m.Year, m.Month)) - 1), Description = desc, CategoryId = cat, AccountId = a.Id, Paid = paid };
                    D.Transactions.Add(t);
                    return t;
                }
                var E = HelmetDesk.Core.Finance.TxKind.Expense; var I = HelmetDesk.Core.Finance.TxKind.Income;
                T(I, 6500m, 5, "Salário", C("Salário"), bank, true);
                T(I, 1200m, 12, "Freela edição de vídeo", C("Freelance"), bank, today.Day >= 12);
                T(E, 312.47m, 3, "Mercado", C("Alimentação"), bank, true);
                T(E, 89.90m, Math.Max(1, today.Day - 1), "Padaria e feira", C("Alimentação"), wallet, true);
                T(E, 64.00m, today.Day, "Uber", C("Transporte"), card, true);
                T(E, 187.35m, Math.Max(1, today.Day - 3), "Conta de luz", C("Moradia"), bank, false);
                T(E, 120.00m, today.Day + 4, "Academia", C("Saúde"), bank, false);
                var rent = new HelmetDesk.Core.Finance.FinanceTransaction { Kind = E, Amount = 1800m, Date = m.AddDays(9), Description = "Aluguel", CategoryId = C("Moradia"), AccountId = bank.Id, Paid = today.Day >= 10 };
                D.Transactions.Add(rent); HelmetDesk.Core.Finance.FinanceService.StartRecurrence(D, rent);
                var stream = new HelmetDesk.Core.Finance.FinanceTransaction { Kind = E, Amount = 55.90m, Date = m.AddDays(14), Description = "Streaming", CategoryId = C("Assinaturas"), AccountId = card.Id, Paid = today.Day >= 15 };
                D.Transactions.Add(stream); HelmetDesk.Core.Finance.FinanceService.StartRecurrence(D, stream);
                HelmetDesk.Core.Finance.FinanceService.AddInstallments(D, new HelmetDesk.Core.Finance.FinanceTransaction { Kind = E, Amount = 2399m, Date = m.AddDays(7), Description = "Celular", CategoryId = C("Outros"), AccountId = card.Id, Paid = true }, 10);
                D.Transactions.Add(new HelmetDesk.Core.Finance.FinanceTransaction { Kind = HelmetDesk.Core.Finance.TxKind.Transfer, Amount = 500m, Date = m.AddDays(5), Description = "Guardar na reserva", AccountId = bank.Id, ToAccountId = save.Id, Paid = true });
                T(E, 230m, 18, "Cinema e jantar", C("Lazer"), card, today.Day >= 18);

                NavFinance.IsChecked = true;
                FinanceView.ShowTab("tx");
                await Shot("19-financas");
                FinanceView.NewTransaction();
                FinanceView.TxAmount.Text = "45,90";
                FinanceView.TxDesc.Focus();
                FinanceView.TxDesc.Text = "Pa";
                FinanceView.TxDesc.CaretIndex = 2;
                await Shot("19b-lancamento");
                FinanceView.SuggestPopup.IsOpen = false;
                FinanceView.ClosePanel();
                FinanceView.OpenTxForm(D.Transactions.First(t => t.Description == "Celular (1/10)"));
                FinanceView.SetMore(true);
                await Shot("19c-editar-parcela");
                FinanceView.ClosePanel();
                FinanceView.ShowTab("acc");
                await Shot("19d-contas");
                FinanceView.OpenCheckForm(bank);
                FinanceView.CheckReal.Text = "5.000,00";
                await Shot("19e-conferir-saldo");
                FinanceView.ClosePanel();
                FinanceView.ShowTab("rec");
                await Shot("19f-recorrentes");
                FinanceView.ShowTab("cat");
                await Shot("19g-categorias");
                // five past months, so the statistics have some history
                var mm = m;
                for (int k = 1; k <= 5; k++)
                {
                    m = mm.AddMonths(-k);
                    T(I, 6500m, 5, "Salário", C("Salário"), bank, true);
                    if (k % 2 == 1) T(I, 800m + k * 150m, 18, "Freela", C("Freelance"), bank, true);
                    T(E, 1800m, 10, "Aluguel", C("Moradia"), bank, true);
                    T(E, 900m + k * 85m, 8, "Mercado do mês", C("Alimentação"), card, true);
                    T(E, 210m + k * 40m, 15, "Combustível", C("Transporte"), card, true);
                    T(E, k == 3 ? 1450m : 160m, 21, k == 3 ? "Show + viagem" : "Cinema", C("Lazer"), card, true);
                    T(E, 120m, 2, "Academia", C("Saúde"), bank, true);
                }
                m = mm;
                FinanceView.ShowTab("stats");
                FinanceView.SetPeriod(6);
                await Shot("19k-estatisticas");
                FinanceView.Scroll.ScrollToEnd();
                await Shot("19k2-estatisticas-barras");
                FinanceView.Scroll.ScrollToTop();
                FinanceView.SelectPieSlice(1);
                await Shot("19l-estatisticas-fatia");
                FinanceView.SetPeriod(1);
                FinanceView.SetPieKind(HelmetDesk.Core.Finance.TxKind.Income);
                await Shot("19m-estatisticas-ganhos-mes");
                FinanceView.SetPieKind(E);
                FinanceView.ShowTab("tx");
                FinanceView.RequestDeleteTx(D.Transactions.First(t => t.Description == "Celular (3/10)"));
                await Shot("19h-excluir-parcela");
                CloseDialog(false);
                NavToday.IsChecked = true;
                await Shot("19i-hoje-financas");
                ToggleSidebar(); await Shot("19j-barra-recolhida"); ToggleSidebar();
            }
            finally
            {
                if (DialogOpen) CloseDialog(false);
                FinanceView.ClosePanel();
                Store.Data.Finance = System.Text.Json.JsonSerializer.Deserialize<HelmetDesk.Core.Finance.FinanceData>(saved);
                NavToday.IsChecked = true;
            }
        }

        private async Task FinanceTests(Action<string, bool> Check)
        {
            var saved = System.Text.Json.JsonSerializer.Serialize(Store.Data.Finance);
            try
            {
                var D = Store.Data.Finance;
                var fv = FinanceView;
                Check("finanças: categorias padrão e conta inicial existem", D.Categories.Count >= 10 && D.Accounts.Count >= 1 && D.Seeded);
                NavFinance.IsChecked = true;
                await Snapshots.Idle(Dispatcher);
                Check("finanças: item na barra lateral abre o painel", _view == "finance" && FinanceView.Visibility == Visibility.Visible
                    && PageTitle.Text == "Finanças" && BtnNewTx.Visibility == Visibility.Visible);
                Check("finanças: item fica entre Calendário e Configurações",
                    NavList.Children.IndexOf(NavFinance) == NavList.Children.IndexOf(NavCalendar) + 1
                    && NavList.Children.IndexOf(NavSettings) > NavList.Children.IndexOf(NavFinance));

                // Ctrl+N → form with the value focused
                fv.ShowTab("tx");
                fv.NewTransaction(); // what Ctrl+N and "Novo lançamento" call
                await Snapshots.Idle(Dispatcher);
                Check("finanças: novo lançamento abre o formulário com foco no valor", fv.PanelOpen && fv.TxForm.Visibility == Visibility.Visible && fv.TxAmount.IsKeyboardFocused);
                Check("finanças: conta padrão = a última usada", fv.TxAccount.SelectedItem is ComboBoxItem ci && (string)ci.Tag == D.LastAccountId);

                int n0 = D.Transactions.Count;
                fv.TxAmount.Text = "1.234,56";
                fv.TxDesc.Text = "Mercado teste";
                fv.TxSaveBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                var t1 = D.Transactions.LastOrDefault();
                Check("finanças: salvar cria a despesa paga de hoje (vírgula como decimal)", D.Transactions.Count == n0 + 1 && t1.Amount == 1234.56m
                    && t1.Kind == HelmetDesk.Core.Finance.TxKind.Expense && t1.Paid && t1.Date == DateTime.Today && !fv.PanelOpen);

                fv.NewTransaction();
                fv.TxAmount.Text = "abc";
                bool okBad = fv.SaveTx();
                Check("finanças: valor inválido mostra erro e não salva", !okBad && fv.TxError.Visibility == Visibility.Visible && D.Transactions.Count == n0 + 1);

                // autocomplete from the same description
                fv.TxAmount.Text = "";
                fv.TxDesc.Text = "mercado TESTE";
                fv.ApplySuggestion();
                Check("finanças: autocompletar sugere valor e conta do último igual", fv.TxAmount.Text == "1.234,56");
                fv.ClosePanel();

                // installments through the form
                fv.NewTransaction();
                fv.TxAmount.Text = "100";
                fv.TxDesc.Text = "Fone";
                fv.TxInstallments.Text = "3";
                fv.SaveTx();
                var parts = D.Transactions.Where(t => t.Description.StartsWith("Fone (")).OrderBy(t => t.InstallmentNumber).ToList();
                Check("finanças: 3 parcelas de 100 = 33,33 + 33,33 + 33,34, só a 1ª paga",
                    parts.Count == 3 && parts.Sum(p => p.Amount) == 100m && parts[2].Amount == 33.34m && parts[0].Paid && !parts[1].Paid && parts[1].Description == "Fone (2/3)");

                // sidebar counter: a pending bill due today
                fv.NewTransaction();
                fv.TxAmount.Text = "50";
                fv.TxDesc.Text = "Conta de luz";
                fv.TxPaid.IsChecked = false;
                fv.SaveTx();
                var luz = D.Transactions.Last(t => t.Description == "Conta de luz");
                UpdateCounts();
                int due = HelmetDesk.Core.Finance.FinanceService.Due(D, DateTime.Today).Count();
                Check("finanças: contador da barra lateral = pendentes vencidos ou de hoje", CountFinance.Text == due.ToString() && due >= 1);
                fv.TogglePaid(luz);
                Check("finanças: clicar no status alterna pago/pendente", luz.Paid);

                // delete: "this and following" through the dialog
                fv.RequestDeleteTx(parts[1]);
                Check("finanças: excluir parcela pergunta e oferece 'as seguintes'", DialogOpen && DialogDontAsk.Visibility == Visibility.Visible && DialogCheckText.Text.Contains("1 parcela seguinte"));
                DialogDontAsk.IsChecked = true;
                CloseDialog(true);
                Check("finanças: excluir esta e as seguintes", !D.Transactions.Contains(parts[1]) && !D.Transactions.Contains(parts[2]) && D.Transactions.Contains(parts[0]));
                SnackAction_Click(null, null);
                Check("finanças: desfazer restaura as parcelas", Store.Data.Finance.Transactions.Count(t => t.Description.StartsWith("Fone (")) == 3);
                D = Store.Data.Finance;

                // recurrence through the form, then opening the next month generates it once
                fv.NewTransaction();
                fv.TxAmount.Text = "39,90";
                fv.TxDesc.Text = "Streaming";
                fv.TxRepeat.IsChecked = true;
                fv.SaveTx();
                var rec = D.Recurrences.LastOrDefault();
                Check("finanças: 'repetir todo mês' cria a recorrência", rec != null && rec.Amount == 39.90m && rec.DayOfMonth == DateTime.Today.Day);
                fv.NextMonth_Click(null, null);
                fv.NextMonth_Click(null, null); fv.PrevMonth_Click(null, null); // reopening the month must not duplicate
                var next = HelmetDesk.Core.Finance.FinanceService.MonthKey(DateTime.Today.AddMonths(1));
                Check("finanças: abrir o mês seguinte gera 1 pendente da recorrência (sem duplicar)",
                    D.Transactions.Count(t => t.RecurrenceId == rec.Id && t.RecurrenceMonth == next) == 1
                    && !D.Transactions.First(t => t.RecurrenceId == rec.Id && t.RecurrenceMonth == next).Paid);
                fv.ThisMonth_Click(null, null);

                // check balance → adjustment
                var acc = D.Accounts.First(a => !a.Archived);
                fv.OpenCheckForm(acc);
                fv.CheckReal.Text = "1000";
                fv.CheckSave_Click(null, null);
                Check("finanças: conferir saldo cria o ajuste e o saldo bate", HelmetDesk.Core.Finance.FinanceService.Balance(D, acc) == 1000m
                    && D.Transactions.Any(t => t.IsAdjustment));
                HideSnack();

                // category in use: delete moving its transactions
                var food = D.Categories.First(c => c.Name == "Alimentação");
                var other = D.Categories.First(c => c.Name == "Outros" && c.Kind == HelmetDesk.Core.Finance.CategoryKind.Expense);
                var usedTx = D.Transactions.First(t => t.Description == "Mercado teste");
                usedTx.CategoryId = food.Id;
                fv.ShowTab("cat");
                fv.OpenCatForm(food);
                Check("finanças: categoria em uso pede para onde mover", fv.CatMoveField.Visibility == Visibility.Visible);
                fv.CatMoveTo.SelectedItem = fv.CatMoveTo.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == other.Id);
                fv.CatDelete_Click(null, null);
                CloseDialog(true);
                Check("finanças: excluir categoria move os lançamentos", !D.Categories.Contains(food) && usedTx.CategoryId == other.Id);
                HideSnack();

                // recurrence: "encerrar já neste mês" through the dialog (box ticked), then "excluir" keeping paid history
                {
                    var endAcc = D.Accounts.First();
                    var endRec = new HelmetDesk.Core.Finance.Recurrence { Kind = HelmetDesk.Core.Finance.TxKind.Expense, Description = "Clube teste", Amount = 7.95m,
                        AccountId = endAcc.Id, DayOfMonth = 28, StartMonth = HelmetDesk.Core.Finance.FinanceService.MonthStart(DateTime.Today.AddMonths(-1)) };
                    D.Recurrences.Add(endRec);
                    HelmetDesk.Core.Finance.FinanceService.GenerateRecurring(D, DateTime.Today.AddMonths(-1)).Single().Paid = true;
                    var cur = HelmetDesk.Core.Finance.FinanceService.GenerateRecurring(D, DateTime.Today).Single();
                    fv.ShowTab("endRec");
                    fv.RequestEndRecurrence(endRec);
                    DialogDontAsk.IsChecked = true;
                    CloseDialog(true);
                    Check("finanças: encerrar recorrência já neste mês remove o pendente do mês", !D.Transactions.Contains(cur) && D.Recurrences.Contains(endRec)
                        && HelmetDesk.Core.Finance.FinanceService.NextOccurrence(endRec, DateTime.Today) == null);
                    HideSnack();
                    fv.RequestDeleteRecurrence(endRec);
                    DialogDontAsk.IsChecked = false;
                    CloseDialog(true);
                    var paidLeft = D.Transactions.SingleOrDefault(t => t.Description == "Clube teste");
                    Check("finanças: excluir recorrência mantém o já pago como lançamento comum", !D.Recurrences.Contains(endRec) && paidLeft != null && !paidLeft.IsRecurring);
                    HideSnack();
                    if (paidLeft != null) D.Transactions.Remove(paidLeft);
                }

                fv.ShowTab("tx");
                await Snapshots.Idle(Dispatcher);
                Check("finanças: lista agrupa por dia e o gráfico mostra categorias", fv.TxList.Children.Count > 1 && fv.Chart.Children.Count > 0);

                NavToday.IsChecked = true;
                await Snapshots.Idle(Dispatcher);
                Check("finanças: card no Hoje com saldo atual", TodayFinance.Visibility == Visibility.Visible
                    && TodayFinanceBalance.Text == HelmetDesk.Core.Finance.Money.Format(HelmetDesk.Core.Finance.FinanceService.TotalBalance(D)));

                // statistics tab
                fv.ShowTab("stats");
                fv.SetPeriod(1);
                await Snapshots.Idle(Dispatcher);
                var st = HelmetDesk.Core.Finance.FinanceService.Totals(D, fv.Month, 1);
                Check("finanças: aba Estatísticas abre com resumo do mês", fv.StatsTab.Visibility == Visibility.Visible && fv.SumGrid.Visibility == Visibility.Collapsed
                    && fv.StExpense.Text == HelmetDesk.Core.Finance.Money.Format(st.Expense) && fv.StIncome.Text == HelmetDesk.Core.Finance.Money.Format(st.Income));
                Check("finanças: pizza tem uma fatia por categoria com gasto", fv.PieSliceCount == HelmetDesk.Core.Finance.FinanceService.ByCategory(D, HelmetDesk.Core.Finance.TxKind.Expense, fv.Month, 1).Count
                    && fv.PieLegend.Children.Count == Math.Max(1, fv.PieSliceCount));
                if (fv.PieSliceCount > 0)
                {
                    fv.SelectPieSlice(0);
                    Check("finanças: clicar na fatia filtra os maiores lançamentos", fv.TopMeta.Text != "Todas as categorias" && fv.PieCenterLabel.Text != "Total");
                    fv.SelectPieSlice(-1);
                }
                fv.SetPeriod(6);
                await Snapshots.Idle(Dispatcher);
                Check("finanças: barras mostram 6 meses", fv.Bars.Children.OfType<TextBlock>().Count(t => t.Text.Length <= 6 && !t.Text.StartsWith("R$") && !t.Text.StartsWith("-R$")) == 6);
                fv.SetPeriod(1);
                fv.ShowTab("tx");
                Check("finanças: resumo do mês volta fora das estatísticas", fv.SumGrid.Visibility == Visibility.Visible);

                Store.Save();
                var json = File.ReadAllText(Store.FilePath);
                Check("finanças: salvo dentro do data.json (entra no backup) com esquema 2", json.Contains("\"Finance\"") && json.Contains("\"Version\": 2") && json.Contains("1234.56"));
            }
            finally
            {
                if (DialogOpen) CloseDialog(false);
                FinanceView.ClosePanel();
                HideSnack();
                Store.Data.Finance = System.Text.Json.JsonSerializer.Deserialize<HelmetDesk.Core.Finance.FinanceData>(saved);
                NavToday.IsChecked = true;
                Refresh(false);
            }
        }

        private async Task QuickAddTests(Action<string, bool> Check)
        {
            // ---- date phrases (a fixed "today": Thursday 12/11/2026) ----
            var t0 = new DateTime(2026, 11, 12);
            DateTime? D(string s) => SmartDate.Find(s, t0, finished: true)?.Date;
            var cases = new (string text, DateTime? want)[]
            {
                ("Ligar hoje", t0), ("Ligar amanhã", t0.AddDays(1)), ("Ligar amanha", t0.AddDays(1)),
                ("Ligar no próximo dia", t0.AddDays(1)), ("Ligar depois de amanhã", t0.AddDays(2)),
                ("Revisar semana que vem", new DateTime(2026, 11, 16)), ("Revisar na próxima semana", new DateTime(2026, 11, 16)),
                ("Pagar daqui 3 dias", t0.AddDays(3)), ("Pagar daqui a 3 dias", t0.AddDays(3)), ("Pagar em 2 semanas", t0.AddDays(14)),
                ("Pagar daqui a uma semana", t0.AddDays(7)), ("Pagar daqui a dois dias", t0.AddDays(2)), ("Pagar dentro de 1 mês", t0.AddMonths(1)),
                ("Entrega 20/11/26", new DateTime(2026, 11, 20)), ("Entrega 20/11/2027", new DateTime(2027, 11, 20)),
                ("Entrega 05/01", new DateTime(2026, 1, 5)), ("Entrega 15 de dezembro", new DateTime(2026, 12, 15)),
                ("Entrega dia 20", new DateTime(2026, 11, 20)), ("Entrega dia 5", new DateTime(2026, 12, 5)),
                ("Reunião na sexta", new DateTime(2026, 11, 13)), ("Reunião sexta-feira", new DateTime(2026, 11, 13)),
                ("Reunião até quinta", new DateTime(2026, 11, 19)), ("Feira no sábado", new DateTime(2026, 11, 14)),
                ("Fechar no fim do mês", new DateTime(2026, 11, 30)), ("Viagem mês que vem", t0.AddMonths(1)),
                ("Viagem no fim de semana", new DateTime(2026, 11, 14)),
                ("Ler a segunda parte", null), ("Comprar 2 dias de ração", null), ("Revisar 31/02", null), ("sem data aqui", null),
            };
            var bad = cases.Where(c => D(c.text) != c.want).Select(c => $"{c.text} → {D(c.text):dd/MM/yyyy}").ToList();
            Check($"datas em texto: {cases.Length - bad.Count}/{cases.Length} reconhecidas{(bad.Count > 0 ? " — " + string.Join("; ", bad) : "")}", bad.Count == 0);
            Check("datas: DD/MM sem ano usa o ano atual (acompanha a virada do ano)",
                SmartDate.Find("x 03/02 ", new DateTime(2027, 1, 2), false)?.Date == new DateTime(2027, 2, 3));
            Check("datas: só vira etiqueta depois de terminar de escrever",
                SmartDate.Find("Ligar amanhã", t0, false) == null && SmartDate.Find("Ligar amanhã ", t0, false) != null
                && SmartDate.Find("Entrega 15/1", t0, false) == null);
            var h = SmartDate.Find("Ligar para João amanhã cedo", t0, false);
            Check("datas: tira as palavras da data do título",
                h != null && SmartDate.Remove("Ligar para João amanhã cedo", h.Index, h.Length, out _) == "Ligar para João cedo");

            // ---- times of day ----
            string T(string s) => SmartDate.FindTime(s, finished: true) is { } th ? $"{th.Time.Hours:00}:{th.Time.Minutes:00}" : null;
            var times = new (string text, string want)[]
            {
                ("Reunião 13:00", "13:00"), ("Reunião 09:30", "09:30"), ("Reunião 13h", "13:00"), ("Reunião 13h30", "13:30"),
                ("Reunião 13h30m", "13:30"), ("Reunião 13h30min", "13:30"), ("Reunião 9h", "09:00"), ("Reunião 14 horas", "14:00"),
                ("Reunião às 9", "09:00"), ("Reunião às 3h", "03:00"), ("Reunião as 15:45", "15:45"), ("Reunião pelas 8", "08:00"),
                ("Reunião a partir das 14h", "14:00"), ("Reunião 3 da tarde", "15:00"), ("Reunião às 8 da noite", "20:00"),
                ("Reunião 10 da manhã", "10:00"), ("Almoço ao meio-dia", "12:00"), ("Backup meia-noite", "00:00"),
                ("Revisar roteiro 1h30", null), ("Revisar 2h", null), ("Comprar as 3 camisas", null), ("Ler 2 horas", null),
                ("Reunião 25h", null), ("Ver capítulo 12", null),
            };
            var badT = times.Where(c => T(c.text) != c.want).Select(c => $"{c.text} → {T(c.text) ?? "nada"}").ToList();
            Check($"horários em texto: {times.Length - badT.Count}/{times.Length}{(badT.Count > 0 ? " — " + string.Join("; ", badT) : "")}", badT.Count == 0);

            // ---- quick add box on Hoje ----
            NavToday.IsChecked = true;
            await Snapshots.Idle(Dispatcher);
            ClearQuick();
            QuickAdd.Focus();
            void Type(string s) { foreach (char c in s) { QuickAdd.SelectedText = c.ToString(); QuickAdd.CaretIndex = QuickAdd.Text.Length; } }

            Type("nota:");
            Check("adição rápida: 'nota:' vira etiqueta Nota e some do texto",
                QuickKindChip.Visibility == Visibility.Visible && QuickKindText.Text == "Nota" && QuickAdd.Text == "");
            Type("Ideias de vídeo amanhã ");
            Check($"adição rápida: data vira etiqueta ao terminar a palavra ('{QuickAdd.Text}' · {QuickDateText.Text})",
                QuickDateChip.Visibility == Visibility.Visible && QuickDateText.Text == "Amanhã" && QuickAdd.Text == "Ideias de vídeo ");
            var note = SubmitQuick();
            Check("adição rápida: Enter salva como nota com a data",
                note != null && note.IsNote && note.Title == "Ideias de vídeo" && note.Date == DateTime.Today.AddDays(1)
                && QuickKindChip.Visibility == Visibility.Collapsed && QuickDateChip.Visibility == Visibility.Collapsed && QuickAdd.Text == "");

            Type("tarefa: Revisar roteiro 1h30");
            var task = SubmitQuick();
            Check("adição rápida: 'tarefa:' salva tarefa para hoje com estimativa",
                task != null && task.IsTask && task.Title == "Revisar roteiro" && task.EstimateMinutes == 90 && task.Date == DateTime.Today);

            Check("adição rápida: aviso sem texto de exemplo", (QuickAdd.Tag as string) == "Adicionar rápido");
            Type("tarefa: Reunião amanhã às 15h30 ");
            Check($"adição rápida: horário vira etiqueta ({QuickTimeText.Text}, '{QuickAdd.Text}')",
                QuickTimeChip.Visibility == Visibility.Visible && QuickTimeText.Text == "15:30" && QuickAdd.Text == "Reunião ");
            Type("1h");
            var timed = SubmitQuick();
            Check("adição rápida: salva data, horário e estimativa juntos",
                timed != null && timed.Title == "Reunião" && timed.Time == "15:30" && timed.Date == DateTime.Today.AddDays(1) && timed.EstimateMinutes == 60);
            Store.Data.Items.Remove(timed);
            Type("Call 14h ");
            QuickAdd.CaretIndex = 0;
            QuickAdd.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(QuickAdd), 0, Key.Back) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Check($"adição rápida: Backspace desfaz o horário ('{QuickAdd.Text}')", QuickTimeChip.Visibility == Visibility.Collapsed && QuickAdd.Text == "14h Call ");
            ClearQuick();

            Type("Comprar café");
            var plain = SubmitQuick();
            Check("adição rápida: sem prefixo continua salvando tarefa de hoje", plain != null && plain.IsTask && plain.Date == DateTime.Today);

            Type("nota: Pauta 20/12 ");
            Check("adição rápida: DD/MM vira etiqueta", QuickDateChip.Visibility == Visibility.Visible && _quickDate == new DateTime(DateTime.Today.Year, 12, 20));
            QuickAdd.CaretIndex = 0;
            QuickAdd.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(QuickAdd), 0, Key.Back) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            Check($"adição rápida: Backspace no início desfaz a etiqueta da data ('{QuickAdd.Text}')",
                QuickDateChip.Visibility == Visibility.Collapsed && QuickAdd.Text == "20/12 Pauta " && QuickKindChip.Visibility == Visibility.Visible);
            QuickAdd.CaretIndex = QuickAdd.Text.Length;
            Type("x ");
            Check("adição rápida: data desfeita não volta a virar etiqueta", QuickDateChip.Visibility == Visibility.Collapsed);
            var noDate = SubmitQuick();
            Check("adição rápida: nota sem data no painel Hoje fica para hoje", noDate != null && noDate.IsNote && noDate.Date == DateTime.Today);
            Check("widget de tarefas: adição rápida continua igual (sem etiquetas)",
                typeof(TasksWidget).GetMethod("Quick_KeyDown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) != null);

            // ---- notes dated today show up on Hoje ----
            var dated = Store.NewItem(ItemKind.Note, null); dated.Title = "Nota datada";
            Refresh(false);
            bool InToday(Item it) => (TodayGroups.ItemsSource as System.Collections.Generic.IEnumerable<ItemGroup>)?.Any(g => g.Title == "Hoje" && g.Items.Contains(it)) == true;
            Check("Hoje: nota sem data não aparece", !InToday(dated));
            dated.Date = DateTime.Today;
            Refresh(false);
            Check("Hoje: nota com a data de hoje aparece", InToday(dated) && InToday(noDate));
            HideSnack();
            foreach (var it in new[] { note, task, plain, noDate, dated }) Store.Data.Items.Remove(it);
            Refresh(false);

            // ---- row actions: pin · delete · edit together, only on hover ----
            {
                var row = (DataTemplate)FindResource("ItemRow");
                var probe = Store.NewItem(ItemKind.Task, DateTime.Today); probe.Title = "Ações";
                var cp = new ContentPresenter { Content = probe, ContentTemplate = row };
                var host = new Border { Child = cp, Width = 500 };
                host.Measure(new Size(500, 200)); host.Arrange(new Rect(0, 0, 500, 80)); host.UpdateLayout();
                var actions = row.FindName("actions", cp) as StackPanel;
                var buttons = actions?.Children.OfType<Button>().ToList();
                var editStyle = (Style)FindResource("EditButton");
                Check("tarefas: fixar, excluir e editar lado a lado", buttons?.Count == 3 && buttons[2].Style == editStyle);
                Check("tarefas: ações escondidas até passar o mouse", actions?.Opacity == 0);
                var iconBox = FindAll<CheckBox>(cp).FirstOrDefault()?.Parent as Grid;
                Check("linhas: ícone de tarefa e de nota na mesma coluna fixa", iconBox != null && iconBox.Width == 24);
                var card = (DataTemplate)FindResource("NoteCard");
                var cp2 = new ContentPresenter { Content = probe, ContentTemplate = card };
                var host2 = new Border { Child = cp2 };
                host2.Measure(new Size(300, 300)); host2.Arrange(new Rect(0, 0, 300, 200)); host2.UpdateLayout();
                Check("notas: editar fica no lugar e só aparece ao passar o mouse",
                    card.FindName("edit", cp2) is Button eb && eb.Opacity == 0 && DockPanel.GetDock(eb) == Dock.Right
                    && card.FindName("actions", cp2) is StackPanel a2 && a2.Opacity == 0);
                Check("notas: etiquetas e editar fixos no rodapé do cartão",
                    card.FindName("footer", cp2) is DockPanel ft && DockPanel.GetDock(ft) == Dock.Bottom);
                Store.Data.Items.Remove(probe);
            }

            // ---- minimize to tray, then open from the tray ----
            {
                bool old = Store.Data.Settings.MinimizeToTray;
                Store.Data.Settings.MinimizeToTray = true;
                WindowState = WindowState.Minimized;
                await Snapshots.Idle(Dispatcher);
                bool hidden = !IsVisible;
                App.Current.ShowMain();
                await Snapshots.Idle(Dispatcher);
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                Check($"bandeja: minimizar esconde e abrir pela bandeja restaura a janela (escondida={hidden}, estado={WindowState})",
                    hidden && IsVisible && WindowState == WindowState.Normal && !Native.IsIconic(hwnd));
                Store.Data.Settings.MinimizeToTray = old;
            }
            _refreshTimer.Stop();
        }

        private async Task MotionTests(Action<string, bool> Check)
        {
            Motion.Off = false;
            Motion.ReducedOverride = false;
            async Task Settle(int ms = 450) { await Task.Delay(ms); await Snapshots.Idle(Dispatcher); _refreshTimer.Stop(); }
            // animations start on the next render tick: look a few frames in
            async Task Frames() { await Task.Delay(60); _refreshTimer.Stop(); }
            ContentPresenter Cont(FrameworkElement root, object item) =>
                FindAll<ContentPresenter>(root).Find(c => c.Content == item && ItemsControl.ItemsControlFromItemContainer(c) != null);
            bool Mode(IEasingFunction f, EasingMode m) => f is EasingFunctionBase b && b.EasingMode == m;

            Check("movimento: tokens 150/220/300 ms e easings entrada/saída/padrão",
                Motion.Fast.TimeSpan.TotalMilliseconds == 150 && Motion.Base.TimeSpan.TotalMilliseconds == 220 && Motion.Slow.TimeSpan.TotalMilliseconds == 300
                && Mode(Motion.EaseOut, EasingMode.EaseOut) && Mode(Motion.EaseIn, EasingMode.EaseIn) && Mode(Motion.EaseStandard, EasingMode.EaseInOut));

            // navigation
            NavToday.IsChecked = true;
            await Settle();
            NavTasks.IsChecked = true;
            await Frames();
            Check("navegação: nova tela entra com deslocamento e fade", Motion.IsDisplaced(ListArea) && ListArea.HasAnimatedProperties);
            Check("navegação: tela clicável durante a transição", ListArea.IsHitTestVisible && ListArea.IsEnabled);
            await Settle();
            Check("navegação: termina parada no lugar", !Motion.IsDisplaced(ListArea) && !ListArea.HasAnimatedProperties && ListArea.Opacity == 1);
            double markerY = Motion.Offset(NavMarker).Y, navY = NavTasks.TranslatePoint(new Point(), NavList).Y;
            Check($"barra lateral: marcador foi até o item ativo ({markerY:0}/{navY:0})", Math.Abs(markerY - navY) < 1 && NavMarker.IsVisible);

            Motion.ReducedOverride = true;
            NavNotes.IsChecked = true;
            Check("movimento reduzido: navegação vira só fade (sem deslocamento)", !Motion.IsDisplaced(ListArea) && ListArea.HasAnimatedProperties);
            await Settle();
            Motion.ReducedOverride = false;

            // list FLIP
            NavTasks.IsChecked = true;
            StatusPending.IsChecked = true;
            var made = new System.Collections.Generic.List<Item>();
            foreach (var t in new[] { "FLIP A", "FLIP B", "FLIP C" })
            {
                var it = Store.NewItem(ItemKind.Task, DateTime.Today); it.Title = t; it.Time = "0" + (made.Count + 1) + ":00"; made.Add(it);
            }
            Refresh(animate: false);
            await Settle();
            made[0].Done = true;
            await Settle(10);
            Refresh();
            var bCont = Cont(ListArea, made[1]);
            await Frames();
            Check("lista: item removido sai como fantasma", Motion.GhostCount(ListArea) > 0);
            Check("lista: os restantes deslizam para o novo lugar", bCont != null && Motion.IsDisplaced(bCont));
            if (bCont != null)
            {
                var center = bCont.TranslatePoint(new Point(bCont.ActualWidth / 2, bCont.ActualHeight / 2), ListArea);
                var hit = ListArea.InputHitTest(center) as DependencyObject;
                Check("lista: item continua clicável durante a animação", hit != null && FindParent<ContentPresenter>(hit) is ContentPresenter hc && (hc == bCont || bCont.IsAncestorOf(hc)));
            }
            await Settle();
            bool anyDisplaced = FindAll<ContentPresenter>(ListArea).Exists(c => Motion.IsDisplaced(c));
            Check("lista: depois tudo para no lugar e o fantasma some", Motion.GhostCount(ListArea) == 0 && !anyDisplaced);
            var nw = Store.NewItem(ItemKind.Task, DateTime.Today); nw.Title = "FLIP novo"; made.Add(nw);
            await Settle(10);
            Refresh();
            var nCont = Cont(ListArea, nw);
            Check("lista: item novo entra com fade", nCont != null && nCont.HasAnimatedProperties);
            await Settle();

            // group expand/collapse
            var header = FindAll<Button>(TaskGroups).Find(b => b.Style == (Style)FindResource("GroupToggle"));
            if (header?.DataContext is ItemGroup grp)
            {
                var gc = Cont(TaskGroups, grp);
                var items = gc?.ContentTemplate.FindName("items", gc) as FrameworkElement;
                GroupToggle_Click(header, null);
                Check("grupo: recolhe animando a altura", items != null && items.HasAnimatedProperties && Store.Data.Settings.CollapsedGroups.Contains(grp.Key));
                await Settle();
                Check("grupo: fica recolhido", items?.Visibility == Visibility.Collapsed);
                GroupToggle_Click(header, null);
                await Settle();
                Check($"grupo: expande e volta à altura automática ({items?.Visibility} h={items?.Height} ah={items?.ActualHeight})",
                    items?.Visibility == Visibility.Visible && double.IsNaN(items.Height) && items.ActualHeight > 0
                    && !Store.Data.Settings.CollapsedGroups.Contains(grp.Key));
            }
            else Check("grupo: cabeçalho encontrado", false);

            // side panel + focus return
            KeyboardOverride = true;
            var editStyle = (Style)FindResource("EditButton");
            var editBtn = FindAll<Button>(ListArea).Find(b => b.Style == editStyle && b.DataContext == made[1]);
            editBtn?.Focus();
            EditItem_Click(editBtn, null);
            await Frames();
            Check($"painel: abre deslizando da borda (btn={editBtn != null})", Motion.IsDisplaced(SidePanel) && SidePanel.Visibility == Visibility.Visible);
            await Settle();
            Check("painel: foco vai para dentro do painel", FocusManager.GetFocusedElement(this) == EdTitle);
            EditorClose_Click(null, null);
            Check($"painel: fecha na hora e sai como fantasma ({SidePanel.Visibility}, {Motion.GhostCount(BodyGrid)})", SidePanel.Visibility == Visibility.Collapsed && Motion.GhostCount(BodyGrid) > 0);
            await Settle();
            var fe = FocusManager.GetFocusedElement(this);
            Check($"painel: foco volta para o botão que abriu ({fe})", fe is Button fb && fb.DataContext == made[1] && fb.Style == editStyle);
            Check("painel: fantasma removido", Motion.GhostCount(BodyGrid) == 0);
            KeyboardOverride = null;

            // snackbar
            ShowSnack("Teste", null, null);
            await Frames();
            Check("aviso: aparece na hora e entra deslizando", Snack.Visibility == Visibility.Visible && Motion.IsDisplaced(Snack));
            await Settle();
            HideSnack();
            Check("aviso: ao sair para de receber clique na hora", !Snack.IsHitTestVisible && Snack.Visibility == Visibility.Visible);
            await Settle();
            Check("aviso: some com fade e volta a aceitar clique", Snack.Visibility == Visibility.Collapsed && Snack.IsHitTestVisible);

            // press
            Motion.Press(BtnNewTask, true);
            await Settle(200);
            Check($"botão: pressionar reduz para {Motion.PressScale}", Math.Abs(((TransformGroup)BtnNewTask.RenderTransform).Value.M11 - Motion.PressScale) < 0.005);
            Motion.Press(BtnNewTask, false);
            await Settle();
            Check("botão: soltar volta ao tamanho normal", !Motion.IsDisplaced(BtnNewTask));

            foreach (var it in made) Store.Data.Items.Remove(it);
            Motion.ReducedOverride = null;
            Motion.Off = true;
            Refresh(animate: false);
        }

        private static System.Collections.Generic.List<T> FindAll<T>(DependencyObject root) where T : DependencyObject
        {
            var list = new System.Collections.Generic.List<T>();
            void Walk(DependencyObject d)
            {
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
                {
                    var c = VisualTreeHelper.GetChild(d, i);
                    if (c is T t) list.Add(t);
                    Walk(c);
                }
            }
            Walk(root);
            return list;
        }

        public async Task Snapshot(string dir)
        {
            async Task Shot(string name)
            {
                await Snapshots.Idle(Dispatcher);
                await Task.Delay(150);
                await Snapshots.Idle(Dispatcher);
                Snapshots.Save(this, Path.Combine(dir, name + ".png"));
            }
            Motion.Off = true; // still frames; transition frames are captured separately below
            SoundService.Muted = true;
            NavToday.IsChecked = true; await Shot("1-hoje");
            NavNotes.IsChecked = true; await Shot("2-notas");
            NavTasks.IsChecked = true; await Shot("3-tarefas");
            var fxs = FindAll<LottieView>(TaskGroups);
            if (fxs.Count > 0)
            {
                foreach (var (p, name) in new[] { (0.25, "8a-anim-inicio"), (0.55, "8b-anim-meio"), (1.0, "8c-anim-fim") })
                {
                    fxs[0].ShowFrame(p);
                    await Shot(name);
                }
                fxs[0].Stop();
            }

            // static checked state vs. the animation's last frame, side by side, 8x
            {
                var panel = new StackPanel { Orientation = Orientation.Horizontal, Background = Ui.Res("Surface") };
                var cb = new CheckBox { Style = Ui.St("TaskCheck"), IsChecked = true, Margin = new Thickness(4) };
                var fx = new LottieView { Source = "tarefa-concluida", Size = 24, AutoPlay = false, Margin = new Thickness(4) };
                var cbOff = new CheckBox { Style = Ui.St("TaskCheck"), IsChecked = false, Margin = new Thickness(4) };
                panel.Children.Add(cbOff); panel.Children.Add(cb); panel.Children.Add(fx);
                var w = new Window { Content = panel, SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None, ShowInTaskbar = false, Left = -3000, Top = -3000 };
                w.Show();
                await Snapshots.Idle(Dispatcher);
                fx.ShowFrame(1);
                await Snapshots.Idle(Dispatcher);
                panel.UpdateLayout();
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(panel.ActualWidth * 8), (int)(panel.ActualHeight * 8), 768, 768, PixelFormats.Pbgra32);
                rtb.Render(panel);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using (var fs = File.Create(Path.Combine(dir, "9-comparacao.png"))) enc.Save(fs);
                w.Close();
            }
            // desktop sticky: a labelled task with a checklist (the item is not changed)
            {
                var it = Store.Data.Items.FirstOrDefault(i => !i.IsNote && i.Checklist.Count > 0)
                      ?? Store.Data.Items.FirstOrDefault(i => !i.IsNote) ?? Store.Data.Items.First();
                // temporary body + checklist so the spacing between every block shows; restored right after
                var (oldBody, oldList) = (it.Body, it.Checklist.ToList());
                it.Body = "Descrição da tarefa\n\nsegundo parágrafo";
                it.Checklist.Clear();
                it.Checklist.Add(new CheckItem { Text = "primeiro item", Done = true });
                it.Checklist.Add(new CheckItem { Text = "segundo item" });
                var sw = new StickyWindow(it) { ShowActivated = false, ShowInTaskbar = false, Left = -3000, Top = -3000, Height = 380 };
                sw.Show();
                await Snapshots.Idle(Dispatcher); await Task.Delay(150); await Snapshots.Idle(Dispatcher);
                Snapshots.Save(sw, Path.Combine(dir, "20-nota-adesiva.png"));
                sw.Close();
                it.Body = oldBody;
                it.Checklist.Clear();
                foreach (var c in oldList) it.Checklist.Add(c);
            }
            GroupMonth.IsChecked = true; await Shot("3b-tarefas-mes");
            GroupWeek.IsChecked = true;
            NavCalendar.IsChecked = true; await Shot("4-calendario");
            NavSettings.IsChecked = true; await Shot("5-config");
            NavNotes.IsChecked = true;
            foreach (var it in Store.Data.Items) { if (it.IsNote) { OpenItem(it); await Shot("6a-ver-nota"); OpenEditor(it); break; } }
            await Shot("6-editor-nota");
            NavTasks.IsChecked = true;
            StatusPending.IsChecked = false;
            foreach (var it in Store.Data.Items) { if (it.IsTask) { OpenItem(it); await Shot("7a-ver-tarefa"); OpenEditor(it); break; } }
            await Shot("7-editor-tarefa");
            foreach (var it in Store.Data.Items) { if (it.IsTask) { RequestDelete(it); await Shot("14-confirmar-exclusao"); CloseDialog(false); break; } }
            CloseEditor();
            ToggleSidebar(); await Shot("15-barra-recolhida"); ToggleSidebar();
            NavToday.IsChecked = true;
            ClearQuick();
            QuickAdd.Focus();
            var probeNote = Store.NewItem(ItemKind.Note, DateTime.Today); probeNote.Title = "Nota de hoje";
            Refresh(false);
            foreach (char c in "nota: Ideias de vídeo amanhã às 14h ") { QuickAdd.SelectedText = c.ToString(); QuickAdd.CaretIndex = QuickAdd.Text.Length; }
            await Shot("16-adicao-rapida");
            {
                var rw = new BugReportWindow(ReportService) { Owner = this, ShowActivated = false };
                rw.Show();
                rw.TitleBox.Text = "Widget some ao reiniciar"; rw.DescBox.Text = "Fixei o widget de tarefas, reiniciei o PC e ele não voltou.";
                await Snapshots.Idle(Dispatcher); await Task.Delay(400);
                Snapshots.Save(rw, Path.Combine(dir, "17-reportar.png"));
                rw.NextBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                await Snapshots.Idle(Dispatcher); await Task.Delay(400);
                Snapshots.Save(rw, Path.Combine(dir, "17b-revisar.png"));
                rw.Close();
                NavSettings.IsChecked = true;
                await Snapshots.Idle(Dispatcher);
                SettingsView.ScrollToEnd();
                await Shot("18-config-dados-suporte");
                NavToday.IsChecked = true;
            }
            ClearQuick();
            Store.Data.Items.Remove(probeNote);
            Keyboard.ClearFocus();
            await AttachShots(Shot, dir);
            await FinanceShots(Shot);

            // transition frames, captured mid-animation
            async Task Mid(string name, int ms)
            {
                await Task.Delay(ms);
                Snapshots.Save(this, Path.Combine(dir, name + ".png"));
                await Task.Delay(400);
            }
            CloseEditor();
            StatusPending.IsChecked = true;
            var temp = new System.Collections.Generic.List<Item>();
            foreach (var t in new[] { "Responder e-mails", "Revisar roteiro", "Gravar vídeo" })
            {
                var it = Store.NewItem(ItemKind.Task, DateTime.Today); it.Title = t; it.Time = $"{temp.Count + 8:00}:00"; it.EstimateMinutes = 30; temp.Add(it);
            }
            Refresh(animate: false);
            await Shot("10a-lista-antes");
            Motion.Off = false;
            Motion.ReducedOverride = false;
            temp[0].Done = true;
            Refresh();
            _refreshTimer.Stop();
            await Mid("10b-lista-removendo", 70);
            OpenItem(temp[1]);
            await Mid("11-painel-abrindo", 70);
            CloseEditor();
            await Mid("11b-painel-fechando", 40);
            NavToday.IsChecked = true;
            await Task.Delay(400);
            NavNotes.IsChecked = true;
            await Mid("12-navegacao", 70);
            ShowSnack("Tarefa excluída", "Desfazer", null);
            await Mid("13-aviso-entrando", 70);
            Motion.Off = true;
            Motion.ReducedOverride = null;
            foreach (var it in temp) Store.Data.Items.Remove(it);
        }
    }
}
