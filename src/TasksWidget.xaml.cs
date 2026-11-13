using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace HelmetDesk
{
    public partial class TasksWidget : Window
    {
        private readonly DispatcherTimer _refresh;
        private bool _closingByCode;

        public TasksWidget()
        {
            InitializeComponent();
            var st = Store.Data.Settings;
            var b = st.TasksWidgetBounds;
            Width = b.W > 0 ? b.W : 300;
            Height = b.H > 0 ? b.H : 380;
            var wa = SystemParameters.WorkArea;
            WindowStartupLocation = WindowStartupLocation.Manual;
            if (double.IsNaN(b.X) || double.IsNaN(b.Y))
            {
                Left = wa.Right - Width - 32;
                Top = wa.Bottom - Height - 32;
            }
            else { Left = b.X; Top = b.Y; }
            Topmost = st.TasksWidgetTopmost;
            TopBtn.IsChecked = Topmost;

            // Checked tasks stay visible for a moment before leaving the list.
            _refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            _refresh.Tick += (s, e) => { _refresh.Stop(); Refresh(); };
            Item.StructureChanged += Soon;
            Store.Data.Items.CollectionChanged += OnItemsChanged;

            SourceInitialized += (s, e) => Native.Round(this);
            LocationChanged += (s, e) => SaveState();
            SizeChanged += (s, e) => SaveState();
            Closing += (s, e) =>
            {
                if (!_closingByCode && !App.Current.IsExiting) { e.Cancel = true; CloseWidget(); }
            };
            Closed += (s, e) =>
            {
                Item.StructureChanged -= Soon;
                Store.Data.Items.CollectionChanged -= OnItemsChanged;
            };
            Refresh();
        }

        private void OnItemsChanged(object s, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Soon();

        private void Soon()
        {
            _refresh.Stop();
            _refresh.Start();
        }

        private void Refresh()
        {
            var today = DateTime.Today;
            var items = Store.Data.Items.Where(i => i.IsTask && !i.Done)
                .OrderBy(t => t.Date ?? DateTime.MaxValue)
                .ThenBy(t => string.IsNullOrEmpty(t.Time)).ThenBy(t => t.Time)
                .ThenByDescending(t => t.Priority).ToList();
            Flip.Run(ListScroll, () => List.ItemsSource = items); // done tasks fade out, the rest move up
            Empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            int mins = items.Sum(i => i.EstimateMinutes);
            int todayCount = items.Count(i => i.Date.HasValue && i.Date.Value <= today);
            Meta.Text = $"{items.Count} pendente(s) · {todayCount} para hoje" + (mins > 0 ? $" · {Hours.Format(mins)}" : "");
        }

        public void SaveState()
        {
            if (!IsLoaded) return;
            var b = Store.Data.Settings.TasksWidgetBounds;
            b.X = Left; b.Y = Top; b.W = ActualWidth; b.H = ActualHeight;
            Store.MarkDirty();
        }

        public void CloseWidget()
        {
            SaveState();
            Store.Data.Settings.TasksWidget = false;
            _closingByCode = true;
            Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => CloseWidget();

        private void Open_Click(object sender, RoutedEventArgs e) => App.Current.ShowMain();

        private void Top_Click(object sender, RoutedEventArgs e)
        {
            Topmost = TopBtn.IsChecked == true;
            Store.Data.Settings.TasksWidgetTopmost = Topmost;
            Store.MarkDirty();
        }

        private void Quick_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            var text = Quick.Text.Trim();
            if (text.Length == 0) return;
            int minutes = 0;
            var m = Regex.Match(text, @"\s+(\d{1,3}:\d{2}|\d{1,3}h\d{0,2}|\d{1,4}\s?min)$", RegexOptions.IgnoreCase);
            if (m.Success && Hours.TryParse(m.Groups[1].Value, out minutes)) text = text.Substring(0, m.Index).Trim();
            var it = Store.NewItem(ItemKind.Task, DateTime.Today);
            it.Title = text;
            it.EstimateMinutes = minutes;
            Quick.Clear();
            Refresh();
        }

        private void Row_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2 && (sender as FrameworkElement)?.DataContext is Item it)
            {
                App.Current.ShowMain();
                App.Current.MainWin.OpenItem(it);
            }
        }
    }

    internal static class Native
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hwnd, int cmd);

        public const int SW_RESTORE = 9;

        /// <summary>Asks Windows 11 to draw rounded corners on a borderless window (ignored on Windows 10).</summary>
        public static void Round(Window w)
        {
            try
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                int pref = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int));
            }
            catch { }
        }
    }
}
