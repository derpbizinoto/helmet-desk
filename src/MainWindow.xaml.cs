using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;
using System.Windows.Threading;
using HelmetDesk.Core.Finance;

namespace HelmetDesk
{
    public partial class MainWindow : Window
    {
        private string _view = "today";
        private string _search = "";
        private string _labelFilter;
        private int _taskStatus;
        private Item _editing;
        private bool _editMode;
        private DateTime _calMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
        private DateTime _calDay = DateTime.Today;
        private readonly DispatcherTimer _refreshTimer;
        private readonly DispatcherTimer _snackTimer;
        private Action _snackAction;
        private bool _ready;

        public MainWindow()
        {
            InitializeComponent();
            HookEmptyClicks();
            SyncThemeChoice();
            SyncLanguageChoice();
            BuildCurrencyChoice();
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _refreshTimer.Tick += (s, e) => { _refreshTimer.Stop(); Refresh(); };
            _snackTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            _snackTimer.Tick += (s, e) => HideSnack();

            SettingsPanel.DataContext = Store.Data.Settings;
            Sidebar.DataContext = Store.Data.Settings;
            Loaded += (s, e) => ApplySidebar(animate: false);
            LabelsEditor.ItemsSource = Store.Data.Labels;
            EdLabels.ItemsSource = Store.Data.Labels;
            DataPath.Text = AppPaths.DataFile;
            HookUpdates();
            BuildDayNames();
            BuildLabelChips();

            Item.StructureChanged += RefreshSoon;
            Store.LabelsChanged += () => { BuildLabelChips(); RefreshSoon(); };
            Store.Data.Items.CollectionChanged += (s, e) => RefreshSoon();

            switch (Store.Data.Settings.GroupMode)
            {
                case "none": GroupNone.IsChecked = true; break;
                case "month": GroupMonth.IsChecked = true; break;
                default: GroupWeek.IsChecked = true; break;
            }

            LoadBounds();
            SourceInitialized += (s, e) => Native.Round(this);
            StateChanged += OnStateChanged;
            Closing += OnClosing;
            PreviewKeyDown += OnKey;

            // Finanças: recurring bills of the months up to now (also those the app was closed in)
            if (FinanceService.GenerateUpTo(Store.Data.Finance, DateTime.Today).Count > 0) Store.MarkDirty();
            FinanceView.Changed += UpdateCounts;
            // painel "Conteúdo": in the sidebar only when Configurações › Conteúdo is on
            ApplyContentPanelSetting();
            Store.Data.Settings.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(Settings.ContentPanel)) ApplyContentPanelSetting(); };

            _ready = true;
            NavToday.IsChecked = true;

            // content fades in when the window appears (first open, or back from the tray)
            IsVisibleChanged += (s, e) => { if (IsVisible) Motion.Enter(Root, duration: Motion.Base); };
            NavList.SizeChanged += (s, e) => MoveNavMarker(CheckedNav, animate: false);
        }

        /// <summary>Shows or hides the Conteúdo item; turned off while it is open, the window goes back to Hoje. Its data stays in conteudo.json.</summary>
        private void ApplyContentPanelSetting()
        {
            bool on = Store.Data.Settings.ContentPanel;
            NavContent.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (!on && NavContent.IsChecked == true) NavToday.IsChecked = true;
            // the highlight follows on its own: NavList.SizeChanged moves it when the list grows or shrinks
        }

        private RadioButton CheckedNav =>
            new[] { NavToday, NavNotes, NavTasks, NavCalendar, NavFinance, NavContent, NavSettings }.FirstOrDefault(r => r.IsChecked == true);

        /// <summary>The highlight behind the active sidebar item glides to the newly selected one.</summary>
        private void MoveNavMarker(RadioButton target, bool animate = true)
        {
            if (target == null || !NavList.IsLoaded) return;
            NavMarker.Height = target.ActualHeight;
            NavMarker.Visibility = Visibility.Visible;
            double y = target.TranslatePoint(new Point(), NavList).Y;
            Motion.MoveTo(NavMarker, y, animate);
        }

        // ================= window chrome =================

        private void Min_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
        private void Max_Click(object s, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        private void Close_Click(object s, RoutedEventArgs e) => Close();

        private void OnStateChanged(object sender, EventArgs e)
        {
            // Maximized borderless windows overflow the screen by the resize border.
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaxIcon.Data = Ui.Geo(WindowState == WindowState.Maximized ? "I.Restore" : "I.Max");
            if (WindowState != WindowState.Minimized) RestoreState = WindowState;
            else if (Store.Data.Settings.MinimizeToTray)
            {
                // Hide while still minimized; ShowMain un-minimizes after showing. (Changing the state of a hidden
                // window doesn't reach Windows, so it used to come back minimized: taskbar button only.)
                Hide();
            }
        }

        /// <summary>The state to return to when the window comes back from the taskbar or the tray.</summary>
        public WindowState RestoreState { get; private set; } = WindowState.Normal;

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (App.Current.IsExiting) return;
            SaveBounds();
            e.Cancel = true; // the window is only hidden, or the whole app quits through ExitApp
            if (Store.Data.Settings.CloseToTray) App.Current.HideToTray();
            else App.Current.ExitApp();
        }

        private void LoadBounds()
        {
            var b = Store.Data.Settings.Main;
            if (b.W > 300 && b.H > 300 && !double.IsNaN(b.X) && !double.IsNaN(b.Y))
            {
                var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                    SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
                if (vs.IntersectsWith(new Rect(b.X, b.Y, b.W, b.H)))
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = b.X; Top = b.Y; Width = b.W; Height = b.H;
                }
            }
            if (b.Maximized) WindowState = WindowState.Maximized;
        }

        public void SaveBounds()
        {
            var b = Store.Data.Settings.Main;
            var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            if (r.Width > 0 && !double.IsInfinity(r.Left))
            {
                b.X = r.Left; b.Y = r.Top; b.W = r.Width; b.H = r.Height;
            }
            b.Maximized = WindowState == WindowState.Maximized;
            Store.MarkDirty();
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (DialogOpen)
            {
                // the dialog owns the keyboard: Esc cancels, Enter/Space act on the focused button, Tab cycles inside it
                if (e.Key == Key.Escape) { CloseDialog(false); e.Handled = true; }
                else if (Keyboard.Modifiers == ModifierKeys.Control) e.Handled = true;
                return;
            }
            if (e.Key == Key.B && Keyboard.Modifiers == ModifierKeys.Control) { ToggleSidebar(); e.Handled = true; return; }
            if (_view == "finance")
            {
                if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control) { FinanceView.NewTransaction(); e.Handled = true; }
                else if (e.Key == Key.Escape && FinanceView.PanelOpen && !e.Handled) { FinanceView.ClosePanel(); e.Handled = true; }
                return;
            }
            if (_view == "content")
            {
                if (e.Key == Key.Escape && ContentView.PanelOpen && !e.Handled) { ContentView.ClosePanel(); e.Handled = true; }
                return;
            }
            if (e.Key == Key.Escape && _editing != null) { if (_editMode) ViewMode_Click(null, null); else CloseByUser(); e.Handled = true; }
            else if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control) { CreateItem(_view == "notes" ? ItemKind.Note : ItemKind.Task); e.Handled = true; }
            else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && Toolbar.Visibility == Visibility.Visible) { SearchBox.Focus(); e.Handled = true; }
        }

        // ================= navigation =================

        private static readonly string[] ViewOrder = { "today", "notes", "tasks", "calendar", "finance", "content", "settings" };

        private void Nav_Checked(object sender, RoutedEventArgs e)
        {
            if (!_ready) return;
            var prev = _view;
            _view = (string)((FrameworkElement)sender).Tag;
            MoveNavMarker((RadioButton)sender);
            CloseEditor(false);
            ApplyView();
            if (prev != _view)
            {
                // moving down the sidebar brings the page up from below; moving up brings it from above
                int dir = Math.Sign(Array.IndexOf(ViewOrder, _view) - Array.IndexOf(ViewOrder, prev));
                Motion.Enter(CurrentView, dy: dir * Motion.Shift);
                Motion.Enter(PageHead, dy: dir * Motion.ShiftSmall);
            }
        }

        private FrameworkElement CurrentView => _view switch
        {
            "today" => TodayView,
            "notes" or "tasks" => ListArea,
            "calendar" => CalendarView,
            "finance" => FinanceView,
            "content" => ContentView,
            _ => SettingsView
        };

        private void ApplyView()
        {
            TodayView.Visibility = _view == "today" ? Visibility.Visible : Visibility.Collapsed;
            ListArea.Visibility = _view is "notes" or "tasks" ? Visibility.Visible : Visibility.Collapsed;
            CalendarView.Visibility = _view == "calendar" ? Visibility.Visible : Visibility.Collapsed;
            SettingsView.Visibility = _view == "settings" ? Visibility.Visible : Visibility.Collapsed;
            FinanceView.Visibility = _view == "finance" ? Visibility.Visible : Visibility.Collapsed;
            BtnNewTx.Visibility = _view == "finance" ? Visibility.Visible : Visibility.Collapsed;
            if (_view != "finance") FinanceView.ClosePanel();
            ContentView.Visibility = _view == "content" ? Visibility.Visible : Visibility.Collapsed;
            if (_view != "content") ContentView.ClosePanel();
            Toolbar.Visibility = _view is "notes" or "tasks" ? Visibility.Visible : Visibility.Collapsed;
            StatusBox.Visibility = _view == "tasks" ? Visibility.Visible : Visibility.Collapsed;
            NoteGroups.Visibility = _view == "notes" ? Visibility.Visible : Visibility.Collapsed;
            TaskGroups.Visibility = _view == "tasks" ? Visibility.Visible : Visibility.Collapsed;

            BtnNewNote.Visibility = _view is "today" or "notes" or "calendar" ? Visibility.Visible : Visibility.Collapsed;
            BtnNewTask.Visibility = _view is "today" or "tasks" or "calendar" ? Visibility.Visible : Visibility.Collapsed;
            BtnNewNote.Style = Ui.St(_view == "notes" ? "PrimaryButton" : "SecondaryButton");

            switch (_view)
            {
                case "today":
                    PageTitle.Text = Greeting();
                    PageSub.Text = Dates.Cap(DateTime.Today.ToString(T("dddd, d 'de' MMMM 'de' yyyy"), Dates.Culture));
                    break;
                case "notes":
                    PageTitle.Text = T("Notas");
                    PageSub.Text = T("Anotações com texto livre e checklists.");
                    break;
                case "tasks":
                    PageTitle.Text = T("Tarefas");
                    PageSub.Text = T("Sua lista de afazeres, com prazos e tempo estimado.");
                    break;
                case "calendar":
                    PageTitle.Text = T("Calendário");
                    PageSub.Text = T("Tarefas e notas organizadas por data. Clique duas vezes em um dia para criar uma tarefa.");
                    break;
                case "finance":
                    PageTitle.Text = T("Finanças");
                    PageSub.Text = T("Receitas, despesas e contas, lançadas à mão.");
                    FinanceView.Activate();
                    break;
                case "content":
                    PageTitle.Text = T("Esteira de conteúdo");
                    PageSub.Text = T("Da ideia à publicação, canal por canal.");
                    ContentView.Activate();
                    break;
                case "settings":
                    PageTitle.Text = T("Configurações");
                    PageSub.Text = T("Comportamento do app, widgets e etiquetas.");
                    break;
            }
            UpdateSidePanel();
            Refresh(animate: false);
        }

        private static string Greeting()
        {
            int h = DateTime.Now.Hour;
            return h < 12 ? T("Bom dia") : h < 18 ? T("Boa tarde") : T("Boa noite");
        }

        public void RefreshSoon()
        {
            // A task just completed: wait so its "done" animation can finish before the row is rebuilt/removed.
            bool justDone = Store.Data.Items.Any(i => i.Done && i.DoneAt.HasValue && (DateTime.Now - i.DoneAt.Value).TotalMilliseconds < 800);
            _refreshTimer.Interval = TimeSpan.FromMilliseconds(justDone ? 700 : 250);
            _refreshTimer.Stop();
            _refreshTimer.Start();
        }

        /// <param name="animate">false when the whole page is being swapped (navigation has its own transition).</param>
        public void Refresh(bool animate = true)
        {
            if (!_ready) return;
            UpdateCounts();
            void Rebuild()
            {
                switch (_view)
                {
                    case "today": RefreshToday(); break;
                    case "notes": RefreshNotes(); break;
                    case "tasks": RefreshTasks(); break;
                    case "calendar": RefreshCalendar(); break;
                    // "finance" redraws itself after its own changes (FinanceView.Commit)
                }
            }
            // lists: survivors glide to their new place, new items fade in, removed ones fade out
            FrameworkElement list = !animate ? null : _view switch { "today" => TodayView, "notes" or "tasks" => ListArea, _ => null };
            if (list != null) Flip.Run(list, Rebuild); else Rebuild();
            if (_editing != null && !Store.Data.Items.Contains(_editing)) CloseEditor();
        }

        private static IEnumerable<Item> Tasks => Store.Data.Items.Where(i => i.IsTask);
        private static IEnumerable<Item> Notes => Store.Data.Items.Where(i => i.IsNote);

        private void UpdateCounts()
        {
            var today = DateTime.Today;
            var dueToday = Tasks.Where(t => !t.Done && t.Date.HasValue && t.Date.Value <= today).ToList();
            CountToday.Text = dueToday.Count > 0 ? dueToday.Count.ToString() : "";
            CountNotes.Text = Notes.Count().ToString();
            int pending = Tasks.Count(t => !t.Done);
            CountTasks.Text = pending > 0 ? pending.ToString() : "";

            int todayOnly = Tasks.Count(t => !t.Done && t.Date == today);
            int mins = Tasks.Where(t => t.Date == today).Sum(t => t.EstimateMinutes);
            int doneMins = Tasks.Where(t => t.Date == today && t.Done).Sum(t => t.EstimateMinutes);
            SideTodayText.Text = todayOnly == 0 ? T("Nenhuma tarefa pendente") : TN(todayOnly, "{0} tarefa pendente", "{0} tarefas pendentes");
            SideTodayHours.Text = mins > 0 ? TF("{0} de {1} concluídas", Hours.Format(doneMins) switch { "" => "0h", var x => x }, Hours.Format(mins)) : T("Sem estimativas para hoje");
            SideTodayCompact.ToolTip = TF("Hoje: {0}", SideTodayText.Text) + "\n" + SideTodayHours.Text; // collapsed sidebar: summary on hover

            // Finanças: pending bills overdue or due today (empty when 0, like Hoje/Tarefas)
            var fin = Store.Data.Finance;
            var due = FinanceService.Due(fin, today).ToList();
            CountFinance.Text = due.Count > 0 ? due.Count.ToString() : "";
            CountFinance.Foreground = due.Any(t => t.Date.Date < today) ? Ui.Res("Danger") : Ui.Res("Text2");

            // Hoje card (shown once Finanças has been used)
            TodayFinance.Visibility = fin.Transactions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (fin.Transactions.Count > 0)
            {
                var billsToday = due.Where(t => t.Kind != TxKind.Income).ToList();
                int overdue = billsToday.Count(t => t.Date.Date < today);
                TodayFinanceText.Text = billsToday.Count == 0 ? T("Nenhuma conta a pagar hoje")
                    : TN(billsToday.Count, "{0} conta a pagar", "{0} contas a pagar") + ": " + Money.Format(billsToday.Sum(t => t.Amount))
                      + (overdue > 0 ? " · " + TN(overdue, "{0} vencida", "{0} vencidas") : "");
                TodayFinanceText.Foreground = overdue > 0 ? Ui.Res("Danger") : Ui.Res("Text");
                var bal = FinanceService.TotalBalance(fin);
                TodayFinanceBalance.Text = Money.Format(bal);
                TodayFinanceBalance.Foreground = bal < 0 ? Ui.Res("Danger") : Ui.Res("Text");
            }
        }

        private void TodayFinance_Click(object sender, MouseButtonEventArgs e) => NavFinance.IsChecked = true;
        private void NewTx_Click(object sender, RoutedEventArgs e) => FinanceView.NewTransaction();

        // ================= Today =================

        private void RefreshToday()
        {
            var today = DateTime.Today;
            var weekStart = Dates.StartOfWeek(today);
            var pending = Tasks.Where(t => !t.Done).ToList();

            var overdue = pending.Where(t => t.Date.HasValue && t.Date.Value < today).OrderBy(t => t.Date).ThenBy(t => t.Time).ToList();
            var todays = Tasks.Where(t => t.Date == today).OrderBy(t => t.Done).ThenBy(t => string.IsNullOrEmpty(t.Time)).ThenBy(t => t.Time).ThenByDescending(t => t.Priority).ToList();
            var next = pending.Where(t => t.Date.HasValue && t.Date.Value > today && t.Date.Value <= today.AddDays(7)).OrderBy(t => t.Date).ThenBy(t => t.Time).ToList();
            var noDate = pending.Where(t => !t.Date.HasValue).OrderByDescending(t => t.Priority).ThenByDescending(t => t.CreatedAt).ToList();
            var notesToday = Notes.Where(n => n.Date == today).ToList();

            StatToday.Text = todays.Count(t => !t.Done).ToString();
            StatTodayHours.Text = HoursOrZero(todays.Sum(t => t.EstimateMinutes));
            StatWeekHours.Text = HoursOrZero(Tasks.Where(t => t.Date.HasValue && t.Date.Value >= weekStart && t.Date.Value < weekStart.AddDays(7)).Sum(t => t.EstimateMinutes));
            StatOverdue.Text = overdue.Count.ToString();
            StatOverdue.Foreground = overdue.Count > 0 ? Ui.Res("Danger") : Ui.Res("Text");

            var groups = new List<ItemGroup>();
            void Add(string title, List<Item> items)
            {
                if (items.Count == 0) return;
                groups.Add(new ItemGroup { Title = title, Meta = GroupMeta(items), Items = items });
            }
            Add(T("Atrasadas"), overdue);
            Add(T("Hoje"), todays.Concat(notesToday).ToList());
            Add(T("Próximos 7 dias"), next);
            Add(T("Sem data"), noDate);
            TodayGroups.ItemsSource = MarkGroups(groups);
            TodayEmpty.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string HoursOrZero(int m) => m > 0 ? Hours.Format(m) : "0h";

        private static string GroupMeta(List<Item> items)
        {
            int n = items.Count;
            int mins = items.Where(i => i.IsTask).Sum(i => i.EstimateMinutes);
            var s = TN(n, "{0} item", "{0} itens");
            if (mins > 0) s += " · " + TF("{0} estimadas", Hours.Format(mins));
            return s;
        }

        // ----- quick add (Hoje): "nota:" / "tarefa:" and dates become chips -----

        private ItemKind? _quickKind;            // null = not chosen (saves as a task)
        private DateTime? _quickDate;            // null = today
        private string _quickDateWords;          // the words the date chip replaced, to give them back
        private readonly HashSet<string> _quickIgnore = new(); // phrases the user un-chipped: leave them as text
        private bool _quickBusy;
        private static readonly Regex KindPrefix = new(@"(?<![\p{L}\d])(nota|tarefa|note|task)\s*:\s?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private void QuickAdd_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_quickBusy) return;
            if (QuickAdd.Text.Length == 0) _quickIgnore.Clear();
            ParseQuick(finished: false);
        }

        /// <summary>Turns recognised words into chips. <paramref name="finished"/>: Enter was pressed, so a date at the very end counts too.</summary>
        private void ParseQuick(bool finished)
        {
            string text = QuickAdd.Text;
            int caret = QuickAdd.CaretIndex;
            bool changed = false;
            if (text.Length > 0 && text[0] == ' ') // e.g. the space typed right after "nota:"
            {
                int n = text.Length - text.TrimStart(' ').Length;
                text = text.Substring(n);
                caret = Math.Max(0, caret - n);
                changed = true;
            }

            var km = KindPrefix.Match(SmartDate.Normalize(text));
            if (km.Success)
            {
                SetQuickKind(km.Groups[1].Value is "nota" or "note" ? ItemKind.Note : ItemKind.Task);
                text = SmartDate.Remove(text, km.Index, km.Length, out caret);
                if (text.Trim().Length == 0) { text = ""; caret = 0; }
                changed = true;
            }

            var hit = SmartDate.Find(text, DateTime.Today, finished, _quickIgnore);
            if (hit != null)
            {
                SetQuickDate(hit.Date, hit.Text.Trim());
                text = SmartDate.Remove(text, hit.Index, hit.Length, out caret);
                if (text.Trim().Length == 0) { text = ""; caret = 0; }
                changed = true;
            }

            var th = SmartDate.FindTime(text, finished, _quickIgnore);
            if (th != null)
            {
                SetQuickTime(th.Time, th.Text.Trim());
                text = SmartDate.Remove(text, th.Index, th.Length, out caret);
                if (text.Trim().Length == 0) { text = ""; caret = 0; }
                changed = true;
            }

            if (!changed) return;
            _quickBusy = true;
            QuickAdd.Text = text;
            QuickAdd.CaretIndex = Math.Min(caret, text.Length);
            _quickBusy = false;
        }

        private void SetQuickKind(ItemKind? kind)
        {
            bool wasShown = _quickKind != null;
            _quickKind = kind;
            if (kind == null) { QuickKindChip.Visibility = Visibility.Collapsed; UpdateQuickHint(); return; }
            bool note = kind == ItemKind.Note;
            QuickKindText.Text = note ? T("Nota") : T("Tarefa");
            QuickKindIcon.Data = Ui.Geo(note ? "I.Note" : "I.Tasks");
            QuickKindChip.Background = note ? Ui.Res("Accent") : Ui.Res("Primary");
            var fg = note ? Ui.Res("OnAccent") : Ui.Res("OnPrimary");
            QuickKindText.Foreground = fg;
            QuickKindIcon.Foreground = fg;
            QuickKindChip.Visibility = Visibility.Visible;
            if (!wasShown) Motion.Enter(QuickKindChip, scale: Motion.PopScale, origin: new Point(0.5, 0.5), duration: Motion.Fast);
            UpdateQuickHint();
        }

        private void SetQuickDate(DateTime? date, string words)
        {
            bool wasShown = _quickDate != null;
            _quickDate = date;
            _quickDateWords = words;
            if (date == null) { QuickDateChip.Visibility = Visibility.Collapsed; UpdateQuickHint(); return; }
            QuickDateText.Text = SmartDate.Label(date.Value);
            QuickDateChip.ToolTip = Dates.Cap(date.Value.ToString(T("dddd, d 'de' MMMM 'de' yyyy"), Dates.Culture)) + "\n" + T("Clique para remover a data");
            QuickDateChip.Visibility = Visibility.Visible;
            if (!wasShown) Motion.Enter(QuickDateChip, scale: Motion.PopScale, origin: new Point(0.5, 0.5), duration: Motion.Fast);
            UpdateQuickHint();
        }

        private TimeSpan? _quickTime;
        private string _quickTimeWords;

        private void SetQuickTime(TimeSpan? time, string words)
        {
            bool wasShown = _quickTime != null;
            _quickTime = time;
            _quickTimeWords = words;
            if (time == null) { QuickTimeChip.Visibility = Visibility.Collapsed; UpdateQuickHint(); return; }
            QuickTimeText.Text = $"{time.Value.Hours:00}:{time.Value.Minutes:00}";
            QuickTimeChip.Visibility = Visibility.Visible;
            if (!wasShown) Motion.Enter(QuickTimeChip, scale: Motion.PopScale, origin: new Point(0.5, 0.5), duration: Motion.Fast);
            UpdateQuickHint();
        }

        private void UpdateQuickHint()
        {
            QuickAdd.Tag = _quickKind == null && _quickDate == null && _quickTime == null ? T("Adicionar rápido")
                : _quickKind == ItemKind.Note ? T("Título da nota") : T("Título da tarefa");
        }

        private enum QuickChip { Kind, Date, Time }

        /// <summary>Takes a chip back out. A date's or time's words return as plain text and are not turned into a chip again.</summary>
        private void RemoveQuickChip(QuickChip chip)
        {
            string words = null;
            if (chip == QuickChip.Date && _quickDate != null) { words = _quickDateWords; SetQuickDate(null, null); }
            else if (chip == QuickChip.Time && _quickTime != null) { words = _quickTimeWords; SetQuickTime(null, null); }
            else if (chip == QuickChip.Kind) SetQuickKind(null);
            if (!string.IsNullOrEmpty(words))
            {
                _quickIgnore.Add(SmartDate.Normalize(words).Trim());
                _quickBusy = true;
                string t = QuickAdd.Text;
                QuickAdd.Text = words + (t.Length > 0 ? " " : "") + t;
                QuickAdd.CaretIndex = words.Length;
                _quickBusy = false;
            }
            QuickAdd.Focus();
        }

        private void QuickKindChip_MouseDown(object sender, MouseButtonEventArgs e) { RemoveQuickChip(QuickChip.Kind); e.Handled = true; }
        private void QuickDateChip_MouseDown(object sender, MouseButtonEventArgs e) { RemoveQuickChip(QuickChip.Date); e.Handled = true; }
        private void QuickTimeChip_MouseDown(object sender, MouseButtonEventArgs e) { RemoveQuickChip(QuickChip.Time); e.Handled = true; }

        private void QuickBox_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && QuickAdd.IsAncestorOf(d)) return;
            QuickAdd.Focus();
            QuickAdd.CaretIndex = QuickAdd.Text.Length;
            e.Handled = true;
        }

        private void QuickAdd_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // Backspace at the very start steps back into the chips (date first, then the kind)
            bool anyChip = _quickDate != null || _quickKind != null || _quickTime != null;
            if (e.Key == Key.Back && QuickAdd.CaretIndex == 0 && QuickAdd.SelectionLength == 0 && anyChip)
            {
                // the chip right before the text goes first: time, then date, then kind
                RemoveQuickChip(_quickTime != null ? QuickChip.Time : _quickDate != null ? QuickChip.Date : QuickChip.Kind);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && (QuickAdd.Text.Length > 0 || anyChip))
            {
                ClearQuick();
                e.Handled = true;
            }
        }

        private void ClearQuick()
        {
            _quickBusy = true;
            QuickAdd.Clear();
            _quickBusy = false;
            _quickIgnore.Clear();
            SetQuickKind(null);
            SetQuickDate(null, null);
            SetQuickTime(null, null);
        }

        private void QuickAdd_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            SubmitQuick();
        }

        /// <summary>Saves what is in the quick-add box. Returns the new item (null if there was no title).</summary>
        public Item SubmitQuick()
        {
            ParseQuick(finished: true);
            var text = QuickAdd.Text.Trim();
            if (text.Length == 0) return null;
            var kind = _quickKind ?? ItemKind.Task;
            int minutes = 0;
            if (kind == ItemKind.Task) text = Hours.SplitEstimate(text, out minutes);
            var date = _quickDate ?? DateTime.Today;
            var it = Store.NewItem(kind, date);
            it.Title = text;
            it.EstimateMinutes = minutes;
            if (_quickTime is TimeSpan tm) it.Time = $"{tm.Hours:00}:{tm.Minutes:00}";
            ClearQuick();
            string when = date == DateTime.Today ? T("hoje") : date == DateTime.Today.AddDays(1) ? T("amanhã") : SmartDate.Label(date);
            if (it.Time.Length > 0) when += T(" às ") + it.Time;
            ShowSnack(TF(kind == ItemKind.Note ? "Nota adicionada para {0}" : "Tarefa adicionada para {0}", when), T("Abrir"), () => OpenItem(it));
            return it;
        }

        // ================= Notes / Tasks lists =================

        private IEnumerable<Item> Filtered(IEnumerable<Item> src)
        {
            if (_labelFilter != null) src = src.Where(i => i.LabelId == _labelFilter);
            if (!string.IsNullOrWhiteSpace(_search)) src = src.Where(i => i.Matches(_search));
            return src;
        }

        private List<ItemGroup> Group(List<Item> items, bool newestFirst)
        {
            string mode = Store.Data.Settings.GroupMode;
            if (mode == "none")
                return items.Count == 0 ? new() : new() { new ItemGroup { Title = T("Tudo"), Meta = GroupMeta(items), Items = items } };

            var dated = items.Where(i => i.GroupDate.HasValue);
            var groups = dated.GroupBy(i => mode == "month"
                    ? new DateTime(i.GroupDate.Value.Year, i.GroupDate.Value.Month, 1)
                    : Dates.StartOfWeek(i.GroupDate.Value));
            groups = newestFirst ? groups.OrderByDescending(g => g.Key) : groups.OrderBy(g => g.Key);
            var result = groups.Select(g => new ItemGroup
            {
                Title = mode == "month" ? Dates.MonthTitle(g.Key) : Dates.WeekTitle(g.Key),
                Meta = GroupMeta(g.ToList()),
                Items = g.ToList()
            }).ToList();
            var noDate = items.Where(i => !i.GroupDate.HasValue).ToList();
            if (noDate.Count > 0) result.Add(new ItemGroup { Title = T("Sem data"), Meta = GroupMeta(noDate), Items = noDate });
            return result;
        }

        private void RefreshNotes()
        {
            var items = Filtered(Notes).OrderByDescending(n => n.GroupDate).ThenByDescending(n => n.UpdatedAt).ToList();
            var groups = Group(items, true);
            NoteGroups.ItemsSource = MarkGroups(groups);
            ShowEmpty(groups.Count == 0, "I.Note",
                string.IsNullOrWhiteSpace(_search) && _labelFilter == null ? T("Nenhuma nota ainda. Crie a primeira com “Nova nota”.") : T("Nenhuma nota encontrada com esses filtros."));
        }

        private void RefreshTasks()
        {
            var src = Filtered(Tasks);
            src = _taskStatus switch { 0 => src.Where(t => !t.Done), 1 => src.Where(t => t.Done), _ => src };
            var items = src.OrderBy(t => t.Done)
                .ThenBy(t => t.Date ?? DateTime.MaxValue)
                .ThenBy(t => string.IsNullOrEmpty(t.Time)).ThenBy(t => t.Time)
                .ThenByDescending(t => t.Priority)
                .ThenByDescending(t => t.CreatedAt).ToList();
            var groups = Group(items, _taskStatus == 1);
            TaskGroups.ItemsSource = MarkGroups(groups);
            ShowEmpty(groups.Count == 0, "I.Tasks",
                _taskStatus == 0 && string.IsNullOrWhiteSpace(_search) && _labelFilter == null ? T("Nenhuma tarefa pendente.") : T("Nenhuma tarefa encontrada com esses filtros."));
        }

        private List<ItemGroup> MarkGroups(List<ItemGroup> groups)
        {
            var collapsed = Store.Data.Settings.CollapsedGroups;
            foreach (var g in groups)
            {
                g.Key = _view + ":" + g.Title;
                g.Collapsed = collapsed.Contains(g.Key);
            }
            return groups;
        }

        /// <summary>Group title clicked: the group's list opens/closes with a height animation; the choice is remembered.</summary>
        private void GroupToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement header || header.DataContext is not ItemGroup g) return;
            var container = FindParent<ContentPresenter>(VisualTreeHelper.GetParent(header));
            while (container != null && !(container.Content == g && ItemsControl.ItemsControlFromItemContainer(container) != null))
                container = FindParent<ContentPresenter>(VisualTreeHelper.GetParent(container));
            if (container?.ContentTemplate?.FindName("items", container) is not FrameworkElement items) return;

            g.Collapsed = !g.Collapsed;
            var list = Store.Data.Settings.CollapsedGroups;
            list.Remove(g.Key);
            if (g.Collapsed) list.Add(g.Key);
            while (list.Count > 100) list.RemoveAt(0);
            Store.MarkDirty();

            Motion.Collapse(items, g.Collapsed);
            if (FindAll<Icon>(header).Find(i => i.Name == "chev") is Icon chev)
                Motion.Rotate(chev, g.Collapsed ? 0 : -90, g.Collapsed ? -90 : 0);
        }

        private void ShowEmpty(bool empty, string icon, string text)
        {
            ListEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            EmptyIcon.Data = Ui.Geo(icon);
            EmptyText.Text = text;
        }

        private void Search_Changed(object sender, TextChangedEventArgs e)
        {
            _search = SearchBox.Text;
            RefreshSoon();
        }

        private void Group_Checked(object sender, RoutedEventArgs e)
        {
            Store.Data.Settings.GroupMode = (string)((FrameworkElement)sender).Tag;
            Store.MarkDirty();
            if (_ready) Refresh();
        }

        private void Status_Checked(object sender, RoutedEventArgs e)
        {
            _taskStatus = int.Parse((string)((FrameworkElement)sender).Tag);
            if (_ready) Refresh();
        }

        private void BuildLabelChips()
        {
            LabelChips.Children.Clear();
            RadioButton Chip(string text, string id, Brush dot)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal };
                if (dot != null) sp.Children.Add(new Ellipse { Width = 8, Height = 8, Fill = dot, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                sp.Children.Add(new TextBlock { Text = text });
                var rb = new RadioButton { Style = Ui.St("Chip"), GroupName = "labels", Content = sp, IsChecked = _labelFilter == id };
                rb.Checked += (s, e) => { _labelFilter = id; Refresh(); };
                return rb;
            }
            if (_labelFilter != null && Store.LabelById(_labelFilter) == null) _labelFilter = null;
            LabelChips.Children.Add(Chip(T("Todas as etiquetas"), null, null));
            foreach (var l in Store.Data.Labels) LabelChips.Children.Add(Chip(l.Name, l.Id, l.Brush));
        }

        // ================= item actions =================

        private static Item ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as Item;

        private void Card_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && FindParent<ButtonBase>(d) != null) return;
            if (ItemOf(sender) is Item it) ToggleItem(it);
        }

        /// <summary>Card click: opens the item's panel, or closes it if that same item is already open.</summary>
        private void ToggleItem(Item it)
        {
            if (_editing == it) CloseByUser();
            else OpenItem(it);
        }

        private static T FindParent<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null)
            {
                if (d is T t) return t;
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }

        private static List<T> FindAll<T>(DependencyObject root) where T : DependencyObject
        {
            var list = new List<T>();
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

        private void PinWidget_Click(object sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is Item it) App.Current.OpenWidget(it);
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is Item it) RequestDelete(it);
        }

        // ================= delete confirmation =================

        private Action<bool> _dialogDone;   // what the open dialog does with the answer
        private UIElement _dialogReturn;
        private bool DialogOpen => DialogLayer.Visibility == Visibility.Visible && _dialogDone != null;
        /// <summary>State of the dialog's check box, read inside the <c>done</c> callback.</summary>
        public bool DialogChecked => DialogDontAsk.IsChecked == true;

        public enum DialogKind { Danger, Info, Error }

        // ---------- theme (Configurações › Geral › Tema) ----------

        private void SyncThemeChoice()
        {
            ThemeAero.IsChecked = ThemeManager.Current == ThemeManager.Aero;
            ThemeClassic.IsChecked = ThemeManager.Current == ThemeManager.Classic;
        }

        private void Theme_Click(object sender, RoutedEventArgs e)
        {
            var theme = ThemeManager.Normalize((sender as FrameworkElement)?.Tag as string);
            if (theme == ThemeManager.Current) return;
            OpenDialog(TF("Trocar para o tema {0}?", ThemeManager.DisplayName(theme)),
                T("O Helmet Desk vai fechar e abrir de novo com o novo visual. Nada dos seus dados muda."),
                T("Trocar e reiniciar"), T("Cancelar"), DialogKind.Info, false, ok =>
                {
                    if (ok) ThemeManager.Switch(theme);
                    else SyncThemeChoice();
                });
        }

        // ---------- language (Configurações › Geral › Idioma) ----------

        private void SyncLanguageChoice()
        {
            LangEnglish.IsChecked = L.IsEnglish;
            LangPortuguese.IsChecked = !L.IsEnglish;
        }

        private void Language_Click(object sender, RoutedEventArgs e)
        {
            var lang = L.Normalize((sender as FrameworkElement)?.Tag as string);
            if (lang == L.Lang) return;
            OpenDialog(TF("Trocar o idioma para {0}?", L.DisplayName(lang)),
                T("O Helmet Desk vai fechar e abrir de novo no novo idioma. Nada dos seus dados muda."),
                T("Trocar e reiniciar"), T("Cancelar"), DialogKind.Info, false, ok =>
                {
                    if (ok) Store.SwitchLanguage(lang);
                    else SyncLanguageChoice();
                });
        }

        // ---------- currency (Configurações › Finanças › Moeda) ----------

        private void BuildCurrencyChoice()
        {
            CurrencyChoice.Children.Clear();
            foreach (var c in Money.Currencies)
            {
                var b = new RadioButton
                {
                    Style = Ui.St("SegButton"), GroupName = "currency", Tag = c.Code,
                    Content = $"{T(c.NamePt)} ({c.Symbol(L.IsEnglish)})",
                    IsChecked = c == Money.Current,
                };
                b.Click += Currency_Click;
                CurrencyChoice.Children.Add(b);
            }
        }

        /// <summary>Applies right away (no restart): only the symbol changes, amounts stay as they are.</summary>
        private void Currency_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string code || code == Money.Current.Code) return;
            Money.SetCurrency(code);
            Store.Data.Settings.Currency = code;
            FinanceView.CurrencyChanged();
            UpdateCounts(); // the Hoje card shows money too
            ShowSnack(TF("Moeda alterada para {0}", T(Money.Current.NamePt)), null, null);
        }

        /// <summary>
        /// The in-app dialog (scrim + card). <paramref name="cancel"/> = null shows a single button (a message).
        /// <paramref name="done"/> runs synchronously with true (confirm) or false (cancel / Esc / click outside).
        /// </summary>
        /// <param name="checkText">Shows the check box with this text instead of "Não perguntar novamente" (read it with <see cref="DialogChecked"/>).</param>
        public void OpenDialog(string title, string text, string confirm, string cancel, DialogKind kind, bool dontAsk, Action<bool> done, string checkText = null)
        {
            if (DialogOpen) CloseDialog(false);
            _dialogDone = done ?? (_ => { });
            _dialogReturn = FocusManager.GetFocusedElement(this) as UIElement;
            DialogTitle.Text = title;
            DialogText.Text = text;
            DialogDontAsk.IsChecked = false;
            DialogDontAsk.Visibility = dontAsk || checkText != null ? Visibility.Visible : Visibility.Collapsed;
            DialogCheckText.Text = checkText ?? T("Não perguntar novamente");
            DialogConfirm.Content = confirm;
            DialogConfirm.Style = Ui.St(kind == DialogKind.Danger ? "DangerButton" : "PrimaryButton");
            DialogCancel.Content = cancel ?? "";
            DialogCancel.Visibility = cancel == null ? Visibility.Collapsed : Visibility.Visible;
            DialogIcon.Data = Ui.Geo(kind switch { DialogKind.Danger => "I.Trash", DialogKind.Error => "I.Bug", _ => "I.Download" });
            bool red = kind != DialogKind.Info;
            DialogIcon.Foreground = red ? Ui.Res("Danger") : Ui.Res("Accent");
            DialogIconBg.Background = Ui.Res(red ? "DangerSoft" : "AccentSoft");
            AutomationProperties.SetName(DialogCard, title);
            DialogLayer.Visibility = Visibility.Visible;
            Motion.Enter(DialogScrim, duration: Motion.Base);                       // backdrop darkens
            Motion.Enter(DialogCard, scale: Motion.PopScale, origin: new Point(0.5, 0.5)); // card: fade + 0.96 → 1
            Dispatcher.BeginInvoke(new Action(() => DialogConfirm.Focus()), DispatcherPriority.Input);
        }

        /// <summary>Deletes after "tem certeza?" (unless the user turned the question off).</summary>
        public void RequestDelete(Item it)
        {
            if (it == null) return;
            if (!Store.Data.Settings.ConfirmDelete) { DeleteItem(it); return; }
            OpenDialog(it.IsTask ? T("Excluir tarefa?") : T("Excluir nota?"),
                TF("“{0}” será excluída. Logo depois, você ainda pode desfazer pelo aviso no rodapé.", it.DisplayTitle),
                T("Excluir"), T("Cancelar"), DialogKind.Danger, dontAsk: true, ok =>
                {
                    if (!ok) return;
                    if (DialogChecked) Store.Data.Settings.ConfirmDelete = false;
                    DeleteItem(it);
                });
        }

        private void CloseDialog(bool confirm)
        {
            if (!DialogOpen) return;
            var done = _dialogDone;
            _dialogDone = null;
            // exit is quicker than the entrance and never blocks: the action happens right away
            Motion.Exit(DialogScrim);
            Motion.Exit(DialogCard, scale: 0.98, done: () => { if (_dialogDone == null) DialogLayer.Visibility = Visibility.Collapsed; });
            done(confirm);
            var back = _dialogReturn;
            _dialogReturn = null;
            if (back != null && back.IsVisible && PresentationSource.FromVisual(back) != null) back.Focus();
            else Keyboard.ClearFocus();
        }

        private void DialogConfirm_Click(object sender, RoutedEventArgs e) => CloseDialog(true);
        private void DialogCancel_Click(object sender, RoutedEventArgs e) => CloseDialog(false);
        private void DialogScrim_Click(object sender, MouseButtonEventArgs e) => CloseDialog(false);

        public void CreateItem(ItemKind kind, DateTime? date = null)
        {
            if (date == null && _view == "today") date = DateTime.Today;
            if (date == null && _view == "calendar") date = _calDay;
            if (kind == ItemKind.Note && _view == "today") date = null;
            if (_view == "settings") NavFor(kind).IsChecked = true;
            var it = Store.NewItem(kind, date);
            if (_labelFilter != null) it.LabelId = _labelFilter;
            Refresh();
            OpenEditor(it);
            Dispatcher.BeginInvoke(new Action(() => { EdTitle.Focus(); }), DispatcherPriority.Input);
        }

        private RadioButton NavFor(ItemKind k) => k == ItemKind.Note ? NavNotes : NavTasks;

        private void NewNote_Click(object sender, RoutedEventArgs e) => CreateItem(ItemKind.Note);
        private void NewTask_Click(object sender, RoutedEventArgs e) => CreateItem(ItemKind.Task);
        private void NewSticky_Click(object sender, RoutedEventArgs e) => App.Current.NewStickyNote();
        private void ShowWidgets_Click(object sender, RoutedEventArgs e) => App.Current.BringWidgetsToFront();

        public void DeleteItem(Item it)
        {
            int index = Store.Data.Items.IndexOf(it);
            if (index < 0) return;
            bool hadWidget = App.Current.IsWidgetOpen(it);
            App.Current.OnItemDeleted(it);
            Store.Data.Items.Remove(it);
            Store.MarkDirty();
            if (_editing == it) CloseEditor();
            ShowSnack(it.IsTask ? T("Tarefa excluída") : T("Nota excluída"), T("Desfazer"), () =>
            {
                Store.Data.Items.Insert(Math.Min(index, Store.Data.Items.Count), it);
                Store.MarkDirty();
                if (hadWidget) App.Current.OpenWidget(it, false);
                Refresh();
            });
        }

        // ================= editor =================

        /// <summary>Opens the side panel for an item: read-only view by default, or the edit form.</summary>
        public void OpenItem(Item it, bool edit = false)
        {
            if (it == null) return;
            if (!IsVisible) App.Current.ShowMain();
            if (_editing == null)
            {
                // remember where focus was, to hand it back when the panel closes
                _keyboardOpen = ViaKeyboard;
                _returnFocus = FocusManager.GetFocusedElement(this) as UIElement;
                _returnItem = it;
            }
            if (_editing != null && _editMode) Keyboard.ClearFocus(); // commit LostFocus bindings
            _editing = it;
            _editMode = edit;
            Viewer.DataContext = it;
            Editor.DataContext = it;
            NewCheck.Clear();
            EditorHeader.Text = it.IsTask ? T("Editando tarefa") : T("Editando nota");
            ConvertBtn.ToolTip = it.IsTask ? T("Converter em nota") : T("Converter em tarefa");
            ViewEmpty.Visibility = !it.HasPreview && !it.HasChecklist ? Visibility.Visible : Visibility.Collapsed;
            BindAttachments(it);
            UpdateSidePanel();
            if (ViaKeyboard)
            {
                // keyboard users land inside the panel
                IInputElement target = edit ? EdTitle : ViewEditBtn;
                Dispatcher.BeginInvoke(new Action(() => target.Focus()), DispatcherPriority.Input);
            }
        }

        public void OpenEditor(Item it) => OpenItem(it, true);

        public void CloseEditor(bool refresh = true)
        {
            if (_editing == null) return;
            // commit pending LostFocus bindings
            Keyboard.ClearFocus();
            _editing = null;
            _editMode = false;
            UpdateSidePanel(); // before clearing the data, so the closing panel still shows its content while it fades
            Editor.DataContext = null;
            Viewer.DataContext = null;
            BindAttachments(null);
            if (refresh) Refresh();
        }

        // ----- focus handling for the side panel -----
        private bool _keyboardOpen;
        private UIElement _returnFocus;
        private Item _returnItem;
        /// <summary>Test hook: pretend the last input came from the keyboard (true) or mouse (false).</summary>
        internal static bool? KeyboardOverride { get; set; }
        private static bool ViaKeyboard => KeyboardOverride ?? InputManager.Current.MostRecentInputDevice is KeyboardDevice;

        /// <summary>Close requested by the user (X button or Esc): focus goes back to what opened the panel.</summary>
        private void CloseByUser()
        {
            bool restore = _keyboardOpen || ViaKeyboard;
            var back = _returnFocus;
            var item = _returnItem;
            CloseEditor();
            if (!restore) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (back != null && back.IsVisible && PresentationSource.FromVisual(back) != null && !SidePanel.IsAncestorOf(back) && back.Focus()) return;
                // the opener was rebuilt by the list refresh: focus that item's edit button instead
                var editStyle = (Style)FindResource("EditButton");
                FindAll<Button>(CurrentView).Find(b => b.Style == editStyle && b.DataContext == item)?.Focus();
            }), DispatcherPriority.Loaded);
        }

        private string _panelState = "closed";
        private Item _panelItem;

        private void UpdateSidePanel()
        {
            bool open = _editing != null;
            string state = open ? (_editMode ? "edit" : "view") : _view == "calendar" ? "day" : "closed";
            string prev = _panelState;
            var prevItem = _panelItem;
            _panelState = state;
            _panelItem = _editing;

            // closing: a snapshot of the panel slides back to the edge while the content reflows immediately
            if (prev != "closed" && state == "closed") Motion.Ghost(SidePanel, BodyGrid, dx: Motion.ShiftPanel);

            Editor.Visibility = open && _editMode ? Visibility.Visible : Visibility.Collapsed;
            Viewer.Visibility = open && !_editMode ? Visibility.Visible : Visibility.Collapsed;
            DayPanel.Visibility = !open && _view == "calendar" ? Visibility.Visible : Visibility.Collapsed;
            SidePanel.Visibility = open || _view == "calendar" ? Visibility.Visible : Visibility.Collapsed;
            SidePanel.Width = open ? (_editMode ? 410 : 380) : 320;

            if (prev == "closed" && state != "closed")
                Motion.Enter(SidePanel, dx: Motion.ShiftPanel); // opening: slides in from the right edge
            else if (state != "closed" && (prev != state || prevItem != _editing))
                Motion.Enter(state switch { "edit" => Editor, "view" => Viewer, _ => DayPanel }, duration: Motion.Fast); // view ↔ edit, other item: crossfade
        }

        private void EditItem_Click(object sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is Item it) OpenItem(it, true);
        }

        private void EditMode_Click(object sender, RoutedEventArgs e)
        {
            if (_editing == null) return;
            OpenItem(_editing, true);
            Dispatcher.BeginInvoke(new Action(() => EdTitle.Focus()), DispatcherPriority.Input);
        }

        private void ViewMode_Click(object sender, RoutedEventArgs e)
        {
            if (_editing == null) return;
            Keyboard.ClearFocus();
            OpenItem(_editing, false);
            Refresh();
        }

        private void EditorClose_Click(object sender, RoutedEventArgs e) => CloseByUser();
        private void EditorDelete_Click(object sender, RoutedEventArgs e) { if (_editing != null) RequestDelete(_editing); }
        private void EditorPin_Click(object sender, RoutedEventArgs e) { if (_editing != null) App.Current.OpenWidget(_editing); }

        private void CommitOnEnter(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            ((TextBox)sender).GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            e.Handled = true;
        }

        private void NewCheck_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || _editing == null) return;
            var t = NewCheck.Text.Trim();
            if (t.Length == 0) return;
            Flip.Run(EdScroll, () => _editing.Checklist.Add(new CheckItem { Text = t }));
            NewCheck.Clear();
            e.Handled = true;
        }

        private void CheckText_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { NewCheck.Focus(); e.Handled = true; }
        }

        private void RemoveCheck_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is CheckItem c && _editing != null) Flip.Run(EdScroll, () => _editing.Checklist.Remove(c));
        }

        private void Convert_Click(object sender, RoutedEventArgs e)
        {
            if (_editing == null) return;
            _editing.Kind = _editing.IsTask ? ItemKind.Note : ItemKind.Task;
            ConvertBtn.ToolTip = _editing.IsTask ? T("Converter em nota") : T("Converter em tarefa");
            EditorHeader.Text = _editing.IsTask ? T("Editando tarefa") : T("Editando nota");
            ShowSnack(_editing.IsTask ? T("Convertida em tarefa") : T("Convertida em nota"), null, null);
        }

        private void Duplicate_Click(object sender, RoutedEventArgs e)
        {
            if (_editing == null) return;
            var src = _editing;
            var copy = Store.NewItem(src.Kind, src.Date);
            copy.Title = src.Title + T(" (cópia)");
            copy.Body = src.Body;
            copy.LabelId = src.LabelId;
            copy.Time = src.Time;
            copy.EstimateMinutes = src.EstimateMinutes;
            copy.Priority = src.Priority;
            foreach (var c in src.Checklist) copy.Checklist.Add(new CheckItem { Text = c.Text, Done = c.Done });
            Attachments.CopyAll(src, copy);
            Refresh();
            OpenEditor(copy);
        }

        // ================= calendar =================

        private void BuildDayNames()
        {
            foreach (var n in new[] { T("Dom"), T("Seg"), T("Ter"), T("Qua"), T("Qui"), T("Sex"), T("Sáb") })
                CalDow.Children.Add(new TextBlock { Text = n, Style = Ui.St("Muted"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 0) });
        }

        private void CalPrev_Click(object s, RoutedEventArgs e) => ShowMonth(_calMonth.AddMonths(-1));
        private void CalNext_Click(object s, RoutedEventArgs e) => ShowMonth(_calMonth.AddMonths(1));
        private void CalToday_Click(object s, RoutedEventArgs e)
        {
            _calDay = DateTime.Today;
            ShowMonth(new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1));
        }

        /// <summary>Changes month: the next month slides in from the right, the previous one from the left.</summary>
        private void ShowMonth(DateTime month)
        {
            int dir = month.CompareTo(_calMonth);
            _calMonth = month;
            RefreshCalendar();
            if (dir != 0)
            {
                Motion.Enter(CalGrid, dx: dir * Motion.Shift);
                Motion.Enter(CalTitle, dx: dir * Motion.ShiftSmall);
            }
        }

        private static List<Item> ItemsOn(DateTime d) =>
            Store.Data.Items.Where(i => i.Date == d)
                .OrderBy(i => i.IsNote).ThenBy(i => i.Done).ThenBy(i => string.IsNullOrEmpty(i.Time)).ThenBy(i => i.Time).ToList();

        private void RefreshCalendar()
        {
            CalTitle.Text = Dates.MonthTitle(_calMonth);
            var monthItems = Store.Data.Items.Where(i => i.Date.HasValue && i.Date.Value.Year == _calMonth.Year && i.Date.Value.Month == _calMonth.Month).ToList();
            int monthMins = monthItems.Where(i => i.IsTask).Sum(i => i.EstimateMinutes);
            CalMeta.Text = TN(monthItems.Count(i => i.IsTask), "{0} tarefa", "{0} tarefas") + " · " + TN(monthItems.Count(i => i.IsNote), "{0} nota", "{0} notas")
                           + (monthMins > 0 ? " · " + TF("{0} estimadas", Hours.Format(monthMins)) : "");

            CalGrid.Children.Clear();
            var start = Dates.StartOfWeek(_calMonth);
            for (int i = 0; i < 42; i++)
            {
                var day = start.AddDays(i);
                CalGrid.Children.Add(BuildDayCell(day));
            }
            RefreshDayPanel();
        }

        private static Brush HoverLine => Ui.Res("HoverLine");

        private UIElement BuildDayCell(DateTime day)
        {
            var items = ItemsOn(day);
            bool inMonth = day.Month == _calMonth.Month;
            bool isToday = day == DateTime.Today;
            bool selected = day == _calDay;

            var cell = new Border
            {
                Background = Ui.Res(inMonth ? "RowFill" : "Bg"),
                BorderBrush = selected ? Ui.Res("Accent") : Ui.Res("Line"),
                BorderThickness = new Thickness(selected ? 1.5 : 1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(3),
                Padding = new Thickness(6, 5, 6, 5),
                Cursor = Cursors.Hand,
                ClipToBounds = true
            };
            var dock = new DockPanel { LastChildFill = true };

            // day badge | count and hours (trimmed in narrow cells instead of running under the badge)
            var head = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            DockPanel.SetDock(head, Dock.Top);
            int mins = items.Where(x => x.IsTask).Sum(x => x.EstimateMinutes);
            var headText = string.Join(" · ", new[] { items.Count > 2 ? $"+{items.Count - 2}" : null, mins > 0 ? Hours.Format(mins) : null }.Where(x => x != null));
            if (headText.Length > 0)
            {
                var h = new TextBlock
                {
                    Text = headText, Style = Ui.St("Muted"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(4, 0, 0, 0), ToolTip = headText
                };
                Grid.SetColumn(h, 1);
                head.Children.Add(h);
            }
            var num = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Left,
                Background = isToday ? Ui.Res("Primary") : Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = day.Day.ToString(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = isToday ? FontWeights.Bold : FontWeights.Medium, FontSize = 12.5,
                    Foreground = isToday ? Brushes.White : Ui.Res(inMonth ? "Text" : "Text2")
                }
            };
            head.Children.Add(num);
            dock.Children.Add(head);

            var list = new StackPanel();
            foreach (var it in items.Take(2))
            {
                var chip = new Border
                {
                    Background = Ui.Res("Elevated"), CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 0, 3),
                    Padding = new Thickness(0, 2, 4, 2), ToolTip = it.DisplayTitle + (it.HasEstimate ? $" · {it.EstimateDisplay}" : "")
                };
                var g = new DockPanel();
                var bar = new Border { Width = 3, Background = it.LabelBrush, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 5, 0) };
                DockPanel.SetDock(bar, Dock.Left);
                g.Children.Add(bar);
                g.Children.Add(new TextBlock
                {
                    Text = (string.IsNullOrEmpty(it.Time) ? "" : it.Time + " ") + it.DisplayTitle,
                    FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = Ui.Res(it.Done ? "Text2" : "Text"),
                    TextDecorations = it.Done ? TextDecorations.Strikethrough : null
                });
                chip.Child = g;
                chip.MouseLeftButtonUp += (s, e) => { e.Handled = true; _calDay = day; ToggleItem(it); RefreshCalendar(); };
                list.Children.Add(chip);
            }
            dock.Children.Add(list);
            cell.Child = dock;
            if (!inMonth) cell.Opacity = 0.6;

            Motion.SetBorderBrush(cell, cell.BorderBrush);
            cell.MouseEnter += (s, e) => { if (day != _calDay) Motion.SetBorderBrush(cell, HoverLine); };
            cell.MouseLeave += (s, e) => { if (day != _calDay) Motion.SetBorderBrush(cell, Ui.Res("Line")); };
            cell.MouseLeftButtonUp += (s, e) =>
            {
                _calDay = day;
                CloseEditor(false);
                if (day.Month != _calMonth.Month) ShowMonth(new DateTime(day.Year, day.Month, 1));
                else RefreshCalendar();
            };
            cell.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                {
                    _calDay = day;
                    e.Handled = true;
                    CreateItem(ItemKind.Task, day);
                }
            };
            return cell;
        }

        private void RefreshDayPanel()
        {
            var items = ItemsOn(_calDay);
            DayTitle.Text = Dates.Cap(_calDay.ToString(T("dddd, d 'de' MMMM"), Dates.Culture));
            int mins = items.Where(i => i.IsTask).Sum(i => i.EstimateMinutes);
            int pend = items.Count(i => i.IsTask && !i.Done);
            DayMeta.Text = (items.Count == 0 ? T("Dia livre") : TN(pend, "{0} pendente", "{0} pendentes") + " · " + TN(items.Count, "{0} item", "{0} itens"))
                           + (mins > 0 ? " · " + TF("{0} estimadas", Hours.Format(mins)) : "");
            Flip.Run(DayScroll, () => DayItems.ItemsSource = items);
            DayEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void DayNewTask_Click(object sender, RoutedEventArgs e) => CreateItem(ItemKind.Task, _calDay);
        private void DayNewNote_Click(object sender, RoutedEventArgs e) => CreateItem(ItemKind.Note, _calDay);

        // ================= settings =================

        private void StartupToggle_Click(object sender, RoutedEventArgs e) => App.Current.SyncStartupRegistration();

        private void TasksWidgetToggle_Click(object sender, RoutedEventArgs e)
        {
            App.Current.ShowTasksWidget(((CheckBox)sender).IsChecked == true);
        }

        private void SideWidgetCompact_Click(object sender, RoutedEventArgs e) =>
            App.Current.ShowTasksWidget(((ToggleButton)sender).IsChecked == true);

        // ================= sidebar: collapse to icons / resize =================

        private const double MinSideWidth = 180, MaxSideWidth = 380;
        // 64 px of content; the theme may add room for its own panel margins
        private static double CompactWidth => 64 + (Application.Current.TryFindResource("Sidebar.CompactExtra") is double extra ? extra : 0);

        private void SideToggle_Click(object sender, RoutedEventArgs e) => ToggleSidebar();

        public void ToggleSidebar()
        {
            Store.Data.Settings.SidebarCollapsed = !Store.Data.Settings.SidebarCollapsed;
            Store.MarkDirty();
            ApplySidebar();
        }

        private void ApplySidebar(bool animate = true)
        {
            var st = Store.Data.Settings;
            bool c = st.SidebarCollapsed;
            if (c)
            {
                SideCol.MinWidth = SideCol.MaxWidth = CompactWidth;
                SideCol.Width = new GridLength(CompactWidth);
            }
            else
            {
                SideCol.MaxWidth = MaxSideWidth;
                SideCol.MinWidth = MinSideWidth;
                SideCol.Width = new GridLength(Math.Clamp(st.SidebarWidth, MinSideWidth, MaxSideWidth));
            }
            SideDock.Margin = c ? new Thickness(10, 16, 10, 16) : new Thickness(14, 16, 14, 16);
            SideTop.Margin = c ? new Thickness(0, 0, 0, 22) : new Thickness(4, 0, 0, 22);
            Grid.SetColumn(SideToggle, c ? 0 : 1);
            SideToggle.HorizontalAlignment = c ? HorizontalAlignment.Center : HorizontalAlignment.Right;
            SideToggle.ToolTip = c ? T("Expandir barra lateral (Ctrl+B)") : T("Recolher barra lateral (Ctrl+B)");
            SideSplitter.Visibility = c ? Visibility.Collapsed : Visibility.Visible;

            foreach (var el in FindAll<FrameworkElement>(Sidebar))
            {
                if (Ui.GetWideOnly(el)) el.Visibility = c ? Visibility.Collapsed : Visibility.Visible;
                if (Ui.GetCompactOnly(el)) el.Visibility = c ? Visibility.Visible : Visibility.Collapsed;
                if (Ui.GetCompactTip(el) is string tip)
                {
                    if (c) el.ToolTip = tip; else el.ClearValue(ToolTipProperty);
                }
                if (el is RadioButton rb && rb.Style == NavStyle)
                {
                    // icon centred in the narrow button
                    if (c) rb.Padding = new Thickness(14, 9, 0, 9); else rb.ClearValue(PaddingProperty);
                }
            }
            if (animate) Motion.Enter(SideDock, duration: Motion.Fast); // contents cross-fade into the new layout
        }

        private Style NavStyle => (Style)FindResource("NavButton");

        private void SideSplitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
        {
            UpdateLayout(); // the last drag step may not be laid out yet
            Store.Data.Settings.SidebarWidth = Math.Round(SideCol.ActualWidth);
            Store.MarkDirty();
        }

        /// <summary>Clicking anywhere on the sidebar row (not just the switch) toggles the widget.</summary>
        private void SideWidgetRow_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && FindParent<CheckBox>(d) != null) return; // the switch handles itself
            App.Current.ShowTasksWidget(!Store.Data.Settings.TasksWidget);
        }

        private void AddLabel_Click(object sender, RoutedEventArgs e)
        {
            var used = Store.Data.Labels.Select(l => l.Color).ToHashSet();
            var color = Palette.LabelColors.FirstOrDefault(c => !used.Contains(c)) ?? Palette.LabelColors[Store.Data.Labels.Count % Palette.LabelColors.Length];
            Store.Data.Labels.Add(new LabelTag { Name = T("Nova etiqueta"), Color = color });
            Store.NotifyLabelsChanged();
        }

        private void DeleteLabel_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not LabelTag l) return;
            if (Store.Data.Labels.Count <= 1) { ShowSnack(T("É preciso manter pelo menos uma etiqueta"), null, null); return; }
            int idx = Store.Data.Labels.IndexOf(l);
            var fallback = Store.Data.Labels.First(x => x != l).Id;
            var affected = Store.Data.Items.Where(i => i.LabelId == l.Id).ToList();
            Store.Data.Labels.Remove(l);
            foreach (var i in affected) i.LabelId = fallback;
            Store.NotifyLabelsChanged();
            ShowSnack(TF("Etiqueta “{0}” removida", l.Name), T("Desfazer"), () =>
            {
                Store.Data.Labels.Insert(Math.Min(idx, Store.Data.Labels.Count), l);
                foreach (var i in affected) i.LabelId = l.Id;
                Store.NotifyLabelsChanged();
            });
        }

        private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => Explorer.OpenFolder(AppPaths.DataDir);

        // ================= snackbar =================

        public void ShowSnack(string text, string action, Action onAction, int seconds = 6)
        {
            _snackTimer.Interval = TimeSpan.FromSeconds(seconds);
            SnackText.Text = text;
            _snackAction = onAction;
            // the action reads as a link: Roboto Bold, underlined
            SnackAction.Content = action == null ? null
                : new TextBlock { Text = action, FontWeight = FontWeights.Bold, TextDecorations = TextDecorations.Underline };
            SnackAction.Visibility = action == null ? Visibility.Collapsed : Visibility.Visible;
            Snack.Padding = action == null ? new Thickness(16, 10, 16, 10) : new Thickness(16, 6, 6, 6);
            bool wasShown = _snackShown;
            _snackShown = true;
            Snack.Visibility = Visibility.Visible;
            if (!wasShown) Motion.Enter(Snack, dy: Motion.Shift); // slides up into place
            _snackTimer.Stop();
            _snackTimer.Start();
        }

        private bool _snackShown;

        private void HideSnack()
        {
            _snackTimer.Stop();
            _snackAction = null;
            if (!_snackShown) return;
            _snackShown = false;
            // fades out; stops taking clicks right away, collapses when the fade ends (unless shown again meanwhile)
            Motion.Exit(Snack, done: () => { if (!_snackShown) Snack.Visibility = Visibility.Collapsed; });
        }

        private void SnackAction_Click(object sender, RoutedEventArgs e)
        {
            var a = _snackAction;
            HideSnack();
            a?.Invoke();
        }
    }
}
