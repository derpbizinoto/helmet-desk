using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace HelmetDesk
{
    public partial class App : Application
    {
        public static new App Current => (App)Application.Current;

        private const string MutexName = "HelmetDesk.SingleInstance.v1";
        private const string ShowEventName = "HelmetDesk.ShowMain.v1";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "HelmetDesk";

        private Mutex _mutex;
        private EventWaitHandle _showEvent;
        private Forms.NotifyIcon _tray;
        private DispatcherTimer _clock;
        private DateTime _lastDay = DateTime.Today;

        public MainWindow MainWin { get; private set; }
        private readonly Dictionary<string, StickyWindow> _widgets = new();
        private TasksWidget _tasksWidget;
        private bool _exiting;
        public bool IsExiting => _exiting;

        protected override void OnStartup(StartupEventArgs e)
        {
            _mutex = new Mutex(true, MutexName, out bool first);
            if (!first && e.Args.Contains("--restarted"))
            {
                // started by Restart(): wait for the old instance to finish closing
                try { first = _mutex.WaitOne(TimeSpan.FromSeconds(15)); }
                catch (AbandonedMutexException) { first = true; }
            }
            if (!first)
            {
                try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
                Shutdown();
                return;
            }
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            var listener = new Thread(() =>
            {
                while (_showEvent.WaitOne())
                    Dispatcher.BeginInvoke(new Action(ShowMain));
            }) { IsBackground = true };
            listener.Start();

            base.OnStartup(e);
            // folders first (and the one-time move of the old %LocalAppData%\HelmetDesk), so the log starts in the right place
            string migrated = null, pathError = null;
            try { migrated = AppPaths.MigrateLocal(); } catch (Exception ex) { pathError = "migração da pasta local falhou: " + ex.Message; }
            try { AppPaths.EnsureCreated(); } catch (Exception ex) { pathError = "não foi possível criar as pastas do app: " + ex.Message; }
            Log.Init();
            Log.Info($"Helmet Desk {AppVersion} iniciado ({Environment.OSVersion.VersionString}, {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription})");
            if (migrated != null) Log.Info(migrated);
            if (pathError != null) Log.Warn(pathError);
            HookUnhandledErrors();

            // the self-test and the screenshots look for Portuguese texts and seed names: pt-BR unless HELMETDESK_LANG says otherwise
            if ((e.Args.Contains("--selftest") || e.Args.Contains("--snapshot")) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HELMETDESK_LANG")))
                Environment.SetEnvironmentVariable("HELMETDESK_LANG", L.Portuguese);

            Store.Load();
            SoundService.Preload(); // decodes the two sounds in the background
            ThemeManager.Apply(Store.Data.Settings.Theme);
            SyncStartupRegistration();
            SetupTray();

            MainWin = new MainWindow();
            bool fromStartup = e.Args.Any(a => a.Equals("--startup", StringComparison.OrdinalIgnoreCase));
            bool hidden = e.Args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase))
                          || (fromStartup && Store.Data.Settings.StartMinimized);
            if (!hidden) MainWin.Show();

            foreach (var it in Store.Data.Items.Where(i => i.WidgetOpen).ToList()) OpenWidget(it, false);
            if (Store.Data.Settings.TasksWidget) ShowTasksWidget(true);

            if (ArgValue(e.Args, "--selftest") is string outFile)
            {
                MainWin.Show();
                MainWin.Dispatcher.BeginInvoke(new Action(async () =>
                {
                    string result;
                    try { result = await MainWin.SelfTest(); }
                    catch (Exception ex) { result = "EXCEPTION " + ex; }
                    System.IO.File.WriteAllText(outFile, result);
                    ExitApp();
                }), DispatcherPriority.ApplicationIdle);
            }

            if (ArgValue(e.Args, "--snapshot") is string snapDir)
            {
                var dir = snapDir;
                MainWin.Show();
                MainWin.Dispatcher.BeginInvoke(new Action(async () =>
                {
                    await MainWin.Snapshot(dir);
                    foreach (var w in _widgets.Values) Snapshots.Save(w, System.IO.Path.Combine(dir, "widget-" + w.Item.Id.Substring(0, 6) + ".png"));
                    if (_tasksWidget != null) Snapshots.Save(_tasksWidget, System.IO.Path.Combine(dir, "widget-tasks.png"));
                    ExitApp();
                }), DispatcherPriority.ApplicationIdle);
            }

            if (ArgValue(e.Args, "--readme-shots") is string shotsDir)
            {
                var dir = shotsDir;
                MainWin.Show();
                MainWin.Dispatcher.BeginInvoke(new Action(async () =>
                {
                    try { await MainWin.ReadmeShots(dir); }
                    catch (Exception ex) { System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "error.txt"), ex.ToString()); }
                    ExitApp();
                }), DispatcherPriority.ApplicationIdle);
            }

            _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _clock.Tick += (s, ev) => Tick();
            _clock.Start();

            // test of Restart(): 1st run restarts, the restarted instance records it and exits
            if (ArgValue(e.Args, "--selftest-restart") is string f)
            {
                bool second = System.IO.File.Exists(f);
                System.IO.File.AppendAllText(f, (second ? "reiniciado" : "primeira") + (e.Args.Contains("--restarted") ? "+restarted" : "") + "\n");
                MainWin.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (second) ExitApp();
                    else Restart(extraArgs: $"--selftest-restart \"{f}\"");
                }), DispatcherPriority.ApplicationIdle);
            }

            if (IsAutomation) return; // no crash report offer, no update check

            // the app died last time: offer to report it
            if (TakeCrashMarker() is string crash)
                MainWin.Dispatcher.BeginInvoke(new Action(() => MainWin.OfferCrashReport(crash)), DispatcherPriority.ApplicationIdle);

            // updates: checked in the background a little after the window is up; nothing at all when not installed
            UpdateService.Instance.StartBackgroundCheck();
        }

        // ---------- automated runs (self-test, screenshots) ----------

        private static readonly string[] AutomationArgs = { "--selftest", "--snapshot", "--readme-shots", "--selftest-restart" };

        /// <summary>Started by a tool, not a person: no dialogs on screen, no crash report offer, no update check.</summary>
        public static bool IsAutomation { get; } = Environment.GetCommandLineArgs().Any(a => AutomationArgs.Contains(a));

        /// <summary>The value after <paramref name="name"/> on the command line ("--selftest out.txt" → "out.txt"), or null.</summary>
        private static string ArgValue(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        // ---------- errors, crash report, restart ----------

        public static string AppVersion =>
            (Assembly.GetEntryAssembly() ?? typeof(App).Assembly).GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0";

        private void HookUnhandledErrors()
        {
            // UI thread: log, tell the user, keep running, offer to report
            DispatcherUnhandledException += (s, ex) =>
            {
                ex.Handled = true;
                Log.Error("Erro não tratado na interface", ex.Exception);
                // automated runs: never put a dialog on the user's screen; write it down and quit
                if (IsAutomation)
                {
                    try { System.IO.File.WriteAllText(System.IO.Path.Combine(AppPaths.DataDir, "automation-error.txt"), ex.Exception.ToString()); }
                    catch { }
                    Environment.Exit(3);
                }
                var r = MessageBox.Show(T("Ocorreu um erro inesperado:\n\n") + ex.Exception.Message + T("\n\nDeseja reportar este problema?"),
                    "Helmet Desk", MessageBoxButton.YesNo, MessageBoxImage.Error);
                if (r == MessageBoxResult.Yes) MainWin?.OpenBugReport(ex.Exception.ToString());
            };
            // any other thread: the process is going down; leave a marker so the next start can offer a report
            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
            {
                var exc = ex.ExceptionObject as Exception;
                try
                {
                    System.IO.Directory.CreateDirectory(AppPaths.LocalDir);
                    System.IO.File.WriteAllText(AppPaths.CrashMarker, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{exc}");
                }
                catch { }
                Log.Fatal("Erro fatal — o app vai fechar", exc);
            };
            // forgotten Tasks that failed: log, don't crash
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, ex) =>
            {
                Log.Error("Erro em tarefa em segundo plano", ex.Exception);
                ex.SetObserved();
            };
        }

        private static string TakeCrashMarker()
        {
            try
            {
                if (!System.IO.File.Exists(AppPaths.CrashMarker)) return null;
                var text = System.IO.File.ReadAllText(AppPaths.CrashMarker);
                System.IO.File.Delete(AppPaths.CrashMarker);
                return text;
            }
            catch { return null; }
        }

        /// <summary>Closes cleanly and starts again (after importing a backup). Works with single-file publishing too.</summary>
        public void Restart(string extraArgs = null)
        {
            var exe = Environment.ProcessPath;
            Log.Info("Reiniciando o app");
            ExitApp(() =>
            {
                try { _mutex?.ReleaseMutex(); } catch { }
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, ("--restarted " + extraArgs).Trim()) { UseShellExecute = false }); }
                catch (Exception ex) { Log.Error("Não foi possível reabrir o app", ex); }
            });
        }

        // ---------- tray ----------

        private void SetupTray()
        {
            System.Drawing.Icon icon;
            try
            {
                var res = GetResourceStream(new Uri("pack://application:,,,/Assets/icon.ico"));
                icon = new System.Drawing.Icon(res.Stream, Forms.SystemInformation.SmallIconSize);
            }
            catch { icon = System.Drawing.SystemIcons.Application; }

            _tray = new Forms.NotifyIcon { Icon = icon, Text = "Helmet Desk", Visible = true };
            _tray.MouseClick += (s, e) => { if (e.Button == Forms.MouseButtons.Left) ShowMain(); };

            var menu = new Forms.ContextMenuStrip
            {
                ShowImageMargin = false,
                BackColor = TrayColor("Tray.Back"),
                ForeColor = TrayColor("Tray.Text"),
                Renderer = new TrayRenderer()
            };
            menu.Items.Add(T("Abrir Helmet Desk"), null, (s, e) => ShowMain());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(T("Nova nota"), null, (s, e) => { ShowMain(); MainWin.CreateItem(ItemKind.Note); });
            menu.Items.Add(T("Nova tarefa"), null, (s, e) => { ShowMain(); MainWin.CreateItem(ItemKind.Task); });
            menu.Items.Add(T("Nova nota na área de trabalho"), null, (s, e) => NewStickyNote());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(T("Mostrar widgets"), null, (s, e) => BringWidgetsToFront());
            menu.Items.Add(T("Lista de tarefas na área de trabalho"), null, (s, e) => ShowTasksWidget(!Store.Data.Settings.TasksWidget));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(T("Sair"), null, (s, e) => ExitApp());
            _tray.ContextMenuStrip = menu;
        }

        /// <summary>Tray menu colors come from the theme (Tray.* keys), since the menu is WinForms.</summary>
        private static System.Drawing.Color TrayColor(string key)
        {
            var c = Current.Resources[key] is System.Windows.Media.Color m ? m : System.Windows.Media.Color.FromRgb(0x22, 0x18, 0x31);
            return System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
        }

        private class TrayRenderer : Forms.ToolStripProfessionalRenderer
        {
            public TrayRenderer() : base(new TrayColors()) { RoundedEdges = false; }
            protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = TrayColor("Tray.Text");
                base.OnRenderItemText(e);
            }
        }

        private class TrayColors : Forms.ProfessionalColorTable
        {
            private static readonly System.Drawing.Color Elevated = TrayColor("Tray.Back");
            private static readonly System.Drawing.Color Line = TrayColor("Tray.Line");
            public override System.Drawing.Color ToolStripDropDownBackground => Elevated;
            public override System.Drawing.Color MenuBorder => Line;
            public override System.Drawing.Color MenuItemBorder => Line;
            public override System.Drawing.Color MenuItemSelected => Line;
            public override System.Drawing.Color SeparatorDark => Line;
            public override System.Drawing.Color SeparatorLight => Elevated;
            public override System.Drawing.Color ImageMarginGradientBegin => Elevated;
            public override System.Drawing.Color ImageMarginGradientMiddle => Elevated;
            public override System.Drawing.Color ImageMarginGradientEnd => Elevated;
        }

        public void ShowMain()
        {
            if (MainWin == null) return;
            if (!MainWin.IsVisible) MainWin.Show();
            if (MainWin.WindowState == WindowState.Minimized) MainWin.WindowState = MainWin.RestoreState;
            // belt and braces: if Windows still has it iconic (minimized while hidden), restore it there too
            var hwnd = new System.Windows.Interop.WindowInteropHelper(MainWin).Handle;
            if (hwnd != IntPtr.Zero && Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
            MainWin.Activate();
            MainWin.Topmost = true; MainWin.Topmost = false;
            MainWin.Focus();
        }

        public void HideToTray()
        {
            MainWin.Hide();
            var st = Store.Data.Settings;
            if (!st.TrayHintShown)
            {
                st.TrayHintShown = true;
                Store.MarkDirty();
                Notify(T("O Helmet Desk continua aberto aqui na bandeja. Clique no ícone para abrir."));
            }
        }

        public void Notify(string text, string title = "Helmet Desk")
        {
            _tray?.ShowBalloonTip(5000, title, text, Forms.ToolTipIcon.None);
        }

        /// <summary>Quits. A downloaded update the user did not apply yet is installed once the process has exited.</summary>
        public void ExitApp() => ExitApp(UpdateService.Instance.ApplyOnExit);

        /// <summary>Saves and quits, running <paramref name="beforeShutdown"/> right before the process ends.</summary>
        internal void ExitApp(Action beforeShutdown)
        {
            if (_exiting) return;
            _exiting = true;
            MainWin?.SaveBounds();
            foreach (var w in _widgets.Values.ToList()) w.SaveState();
            _tasksWidget?.SaveState();
            Store.Save(); // no-op while Store.Frozen (right after an import)
            RemoveTray();
            beforeShutdown?.Invoke();
            Shutdown();
        }

        /// <summary>Takes the icon out of the notification area (once; ExitApp and OnExit both call it).</summary>
        private void RemoveTray()
        {
            if (_tray == null) return;
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            RemoveTray();
            try { _mutex?.ReleaseMutex(); } catch { }
            Log.Flush();
            base.OnExit(e);
        }

        // ---------- start with Windows ----------

        public void SyncStartupRegistration()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
                if (Store.Data.Settings.StartWithWindows)
                    key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" --startup");
                else if (key.GetValue(RunValue) != null)
                    key.DeleteValue(RunValue, false);
            }
            catch { }
        }

        /// <summary>Uninstall hook (Program.Main): leave no "start with Windows" entry pointing at a deleted exe.</summary>
        public static void RemoveStartupRegistration()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
                if (key?.GetValue(RunValue) != null) key.DeleteValue(RunValue, false);
            }
            catch { }
        }

        // ---------- widgets ----------

        public bool IsWidgetOpen(Item it) => _widgets.ContainsKey(it.Id);

        public void OpenWidget(Item it, bool activate = true)
        {
            if (_widgets.TryGetValue(it.Id, out var w))
            {
                if (activate) w.Activate();
                return;
            }
            w = new StickyWindow(it);
            _widgets[it.Id] = w;
            w.Closed += (s, e) => _widgets.Remove(it.Id);
            it.WidgetOpen = true;
            Store.MarkDirty();
            w.Show();
            if (activate) w.Activate();
        }

        public void CloseWidget(Item it)
        {
            if (_widgets.TryGetValue(it.Id, out var w)) w.CloseWidget();
        }

        public void NewStickyNote()
        {
            var it = Store.NewItem(ItemKind.Note);
            MainWin?.RefreshSoon();
            OpenWidget(it);
        }

        public void BringWidgetsToFront()
        {
            foreach (var w in _widgets.Values)
            {
                if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                w.Show();
                w.Activate();
            }
            if (_tasksWidget != null) _tasksWidget.Activate();
        }

        public void ShowTasksWidget(bool show)
        {
            Store.Data.Settings.TasksWidget = show;
            if (show)
            {
                if (_tasksWidget == null)
                {
                    _tasksWidget = new TasksWidget();
                    _tasksWidget.Closed += (s, e) => _tasksWidget = null;
                    _tasksWidget.Show();
                }
                _tasksWidget.Activate();
            }
            else
            {
                _tasksWidget?.CloseWidget();
            }
        }

        public void OnItemDeleted(Item it)
        {
            if (_widgets.TryGetValue(it.Id, out var w)) w.CloseWidget(true);
        }

        // ---------- reminders / day change ----------

        private void Tick()
        {
            if (DateTime.Today != _lastDay)
            {
                _lastDay = DateTime.Today;
                foreach (var it in Store.Data.Items) it.RaiseDates();
                MainWin?.RefreshSoon();
            }
            if (!Store.Data.Settings.Reminders) return;
            var now = DateTime.Now;
            foreach (var it in Store.Data.Items)
            {
                if (!it.IsTask || it.Done || it.Reminded || string.IsNullOrWhiteSpace(it.Time)) continue;
                var due = it.DueMoment;
                if (!due.HasValue || due.Value > now) continue;
                it.Reminded = true;
                Store.MarkDirty();
                if (now - due.Value < TimeSpan.FromHours(6))
                {
                    var extra = it.HasEstimate ? " · " + TF("estimativa {0}", it.EstimateDisplay) : "";
                    Notify($"{it.DisplayTitle}{extra}", T("Lembrete de tarefa"));
                }
            }
        }
    }
}
