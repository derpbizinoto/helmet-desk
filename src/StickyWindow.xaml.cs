using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HelmetDesk
{
    public partial class StickyWindow : Window
    {
        public Item Item { get; }
        private bool _closingByCode;
        private static int _cascade;

        public StickyWindow(Item item)
        {
            InitializeComponent();
            Item = item;
            DataContext = item;
            LabelList.ItemsSource = Store.Data.Labels;
            Topmost = item.WidgetTopmost;
            TopBtn.IsChecked = item.WidgetTopmost;

            Width = Math.Max(MinWidth, item.WidgetW > 0 ? item.WidgetW : 280);
            Height = Math.Max(MinHeight, item.WidgetH > 0 ? item.WidgetH : 300);
            var wa = SystemParameters.WorkArea;
            if (double.IsNaN(item.WidgetX) || double.IsNaN(item.WidgetY) || !OnScreen(item.WidgetX, item.WidgetY))
            {
                int k = _cascade++ % 8;
                Left = wa.Right - Width - 32 - k * 28;
                Top = wa.Top + 32 + k * 28;
            }
            else
            {
                Left = item.WidgetX;
                Top = item.WidgetY;
            }
            WindowStartupLocation = WindowStartupLocation.Manual;

            SourceInitialized += (s, e) => Native.Round(this);
            LocationChanged += (s, e) => SaveState();
            SizeChanged += (s, e) => SaveState();
            // closed from outside (Alt+F4, Windows, code calling Close): do the same bookkeeping as the X button.
            // Calling Close() again from inside Closing throws, so the close just goes ahead here.
            Closing += (s, e) =>
            {
                if (!_closingByCode && !App.Current.IsExiting) { _closingByCode = true; Forget(false); }
            };
            Loaded += (s, e) =>
            {
                if (string.IsNullOrEmpty(Item.Title) && string.IsNullOrEmpty(Item.Body)) TitleBox.Focus();
            };
        }

        private static bool OnScreen(double x, double y)
        {
            var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            return vs.Contains(new Point(x + 40, y + 10));
        }

        public void SaveState()
        {
            if (!IsLoaded || WindowState != WindowState.Normal) return;
            Item.WidgetX = Left;
            Item.WidgetY = Top;
            Item.WidgetW = ActualWidth;
            Item.WidgetH = ActualHeight;
            Store.MarkDirty();
        }

        /// <summary>Closes the widget. Empty notes are discarded, like Sticky Notes.</summary>
        public void CloseWidget(bool deleted = false)
        {
            if (_closingByCode) return; // already closing
            _closingByCode = true;
            Forget(deleted);
            Close();
        }

        /// <summary>Saves position/size and marks the widget closed (dropping an empty note).</summary>
        private void Forget(bool deleted)
        {
            SaveState();
            if (deleted) return;
            Item.WidgetOpen = false;
            bool empty = Item.IsNote && string.IsNullOrWhiteSpace(Item.Title) && string.IsNullOrWhiteSpace(Item.Body) && Item.Checklist.Count == 0;
            if (empty) Store.Data.Items.Remove(Item);
            Store.MarkDirty();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => CloseWidget();
        private void New_Click(object sender, RoutedEventArgs e) => App.Current.NewStickyNote();

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            App.Current.ShowMain();
            App.Current.MainWin.OpenItem(Item);
        }

        private void Top_Click(object sender, RoutedEventArgs e)
        {
            Item.WidgetTopmost = TopBtn.IsChecked == true;
            Topmost = Item.WidgetTopmost;
            Store.MarkDirty();
        }

        private void Color_Click(object sender, RoutedEventArgs e) => ColorPopup.IsOpen = true;

        private void LabelList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ColorPopup.IsOpen && e.AddedItems.Count > 0) ColorPopup.IsOpen = false;
        }

        private void NewCheck_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            var t = NewCheck.Text.Trim();
            if (t.Length == 0) return;
            Flip.Run(BodyScroll, () => Item.Checklist.Add(new CheckItem { Text = t }));
            NewCheck.Clear();
            e.Handled = true;
        }

        private void CheckText_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { NewCheck.Focus(); e.Handled = true; }
        }

        private void RemoveCheck_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is CheckItem c) Flip.Run(BodyScroll, () => Item.Checklist.Remove(c));
        }
    }
}
