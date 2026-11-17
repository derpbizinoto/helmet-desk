using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace HelmetDesk
{
    /// <summary>Attached helpers used by the theme.</summary>
    public static class Ui
    {
        public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached(
            "Radius", typeof(CornerRadius), typeof(Ui), new FrameworkPropertyMetadata(new CornerRadius(8)));
        public static CornerRadius GetRadius(DependencyObject o) => (CornerRadius)o.GetValue(RadiusProperty);
        public static void SetRadius(DependencyObject o, CornerRadius v) => o.SetValue(RadiusProperty, v);

        // sidebar compact mode markers (see MainWindow.ApplySidebar)
        public static readonly DependencyProperty WideOnlyProperty = DependencyProperty.RegisterAttached("WideOnly", typeof(bool), typeof(Ui));
        public static bool GetWideOnly(DependencyObject o) => (bool)o.GetValue(WideOnlyProperty);
        public static void SetWideOnly(DependencyObject o, bool v) => o.SetValue(WideOnlyProperty, v);
        public static readonly DependencyProperty CompactOnlyProperty = DependencyProperty.RegisterAttached("CompactOnly", typeof(bool), typeof(Ui));
        public static bool GetCompactOnly(DependencyObject o) => (bool)o.GetValue(CompactOnlyProperty);
        public static void SetCompactOnly(DependencyObject o, bool v) => o.SetValue(CompactOnlyProperty, v);
        /// <summary>Tooltip shown only while the sidebar is collapsed to icons.</summary>
        public static readonly DependencyProperty CompactTipProperty = DependencyProperty.RegisterAttached("CompactTip", typeof(string), typeof(Ui));
        public static string GetCompactTip(DependencyObject o) => (string)o.GetValue(CompactTipProperty);
        public static void SetCompactTip(DependencyObject o, string v) => o.SetValue(CompactTipProperty, v);

        public static Brush Res(string key) => (Brush)Application.Current.Resources[key];
        public static Geometry Geo(string key) => (Geometry)Application.Current.Resources[key];
        public static Style St(string key) => (Style)Application.Current.Resources[key];
    }

    /// <summary>Draws a Lucide-style stroked icon (24x24 path data) using the inherited Foreground.</summary>
    public class Icon : FrameworkElement
    {
        public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
            nameof(Data), typeof(Geometry), typeof(Icon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
            typeof(Icon), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
            nameof(Thickness), typeof(double), typeof(Icon), new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public Geometry Data { get => (Geometry)GetValue(DataProperty); set => SetValue(DataProperty, value); }
        public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
        public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

        static Icon()
        {
            WidthProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(16.0));
            HeightProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(16.0));
            SnapsToDevicePixelsProperty.OverrideMetadata(typeof(Icon), new FrameworkPropertyMetadata(false));
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (Data == null || ActualWidth <= 0) return;
            double s = Math.Min(ActualWidth, ActualHeight) / 24.0;
            var pen = new Pen(Foreground, Thickness)
            {
                StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round
            };
            dc.PushTransform(new TranslateTransform((ActualWidth - 24 * s) / 2, (ActualHeight - 24 * s) / 2));
            dc.PushTransform(new ScaleTransform(s, s));
            dc.DrawGeometry(null, pen, Data);
            dc.Pop();
            dc.Pop();
        }
    }

    /// <summary>Generic visibility converter. Parameter "not" inverts. Works with bool, null, string and numbers.</summary>
    public class VisConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool v = value switch
            {
                null => false,
                bool b => b,
                string s => !string.IsNullOrWhiteSpace(s),
                int i => i != 0,
                _ => true
            };
            if (parameter as string == "not") v = !v;
            return v ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    public class HexBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Palette.BrushFrom(value as string);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }

    /// <summary>Month grid used by the date picker popup.</summary>
    public class MiniCalendar : StackPanel
    {
        private DateTime _month;
        private UniformGrid _grid;
        public DateTime? Selected { get; set; }
        public event Action<DateTime> Picked;

        public MiniCalendar()
        {
            _month = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            Width = 252;
        }

        public void Show(DateTime? selected)
        {
            Selected = selected;
            var d = selected ?? DateTime.Today;
            _month = new DateTime(d.Year, d.Month, 1);
            Build();
        }

        private void Build()
        {
            Children.Clear();
            var head = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var prev = IconButton("I.Left", () => { _month = _month.AddMonths(-1); Build(); Motion.Enter(_grid, dx: -Motion.ShiftSmall, duration: Motion.Fast); });
            var next = IconButton("I.Right", () => { _month = _month.AddMonths(1); Build(); Motion.Enter(_grid, dx: Motion.ShiftSmall, duration: Motion.Fast); });
            var title = new TextBlock
            {
                Text = Dates.MonthTitle(_month), FontWeight = FontWeights.SemiBold, FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Foreground = Ui.Res("Text")
            };
            Grid.SetColumn(title, 1);
            Grid.SetColumn(next, 2);
            head.Children.Add(prev); head.Children.Add(title); head.Children.Add(next);
            Children.Add(head);

            var grid = _grid = new UniformGrid { Columns = 7 };
            foreach (var n in new[] { "D", "S", "T", "Q", "Q", "S", "S" })
                grid.Children.Add(new TextBlock
                {
                    Text = n, FontSize = 11, Foreground = Ui.Res("Text2"), HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 4)
                });
            var start = Dates.StartOfWeek(_month);
            for (int i = 0; i < 42; i++)
            {
                var day = start.AddDays(i);
                var b = new Button
                {
                    Content = day.Day.ToString(), Style = Ui.St("GhostButton"), Padding = new Thickness(0),
                    Height = 32, FontWeight = FontWeights.Medium, Margin = new Thickness(1)
                };
                if (day.Month != _month.Month) b.Opacity = 0.4;
                if (day == DateTime.Today) b.BorderBrush = Ui.Res("Accent");
                if (Selected.HasValue && day == Selected.Value.Date)
                {
                    b.Background = Ui.Res("Primary");
                    b.Foreground = Brushes.White;
                }
                b.Click += (s, e) => Picked?.Invoke(day);
                grid.Children.Add(b);
            }
            Children.Add(grid);
        }

        private static Button IconButton(string icon, Action click)
        {
            var b = new Button { Style = Ui.St("IconButton"), Content = new Icon { Data = Ui.Geo(icon) } };
            b.Click += (s, e) => click();
            return b;
        }
    }

    /// <summary>Text box + calendar popup for picking a date. Accepts "dd/mm/aaaa", "dd/mm", "hoje", "amanhã".</summary>
    public class DateField : UserControl
    {
        public static readonly DependencyProperty SelectedDateProperty = DependencyProperty.Register(
            nameof(SelectedDate), typeof(DateTime?), typeof(DateField),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (o, e) => ((DateField)o).SyncText()));

        public DateTime? SelectedDate { get => (DateTime?)GetValue(SelectedDateProperty); set => SetValue(SelectedDateProperty, value); }

        /// <summary>Borderless look (border only on hover/focus).</summary>
        public bool Ghost
        {
            set
            {
                _box.Style = value ? Ui.St("GhostTextBox") : null;
                _box.Padding = new Thickness(8, 6, 34, 6);
            }
        }

        private readonly TextBox _box;
        private readonly Popup _popup;
        private readonly MiniCalendar _cal;

        public DateField()
        {
            var grid = new Grid();
            _box = new TextBox { Tag = T("dd/mm/aaaa"), Padding = new Thickness(10, 7, 34, 7) };
            _box.LostFocus += (s, e) => Commit();
            _box.KeyDown += (s, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
            var btn = new Button
            {
                Style = Ui.St("IconButton"), Content = new Icon { Data = Ui.Geo("I.Calendar"), Width = 15, Height = 15 },
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 4, 0), ToolTip = T("Escolher data")
            };
            grid.Children.Add(_box);
            grid.Children.Add(btn);

            _cal = new MiniCalendar();
            _cal.Picked += d => { SelectedDate = d; _popup.IsOpen = false; };

            var quick = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            quick.Children.Add(Quick(T("Hoje"), () => DateTime.Today));
            quick.Children.Add(Quick(T("Amanhã"), () => DateTime.Today.AddDays(1)));
            quick.Children.Add(Quick(T("+1 semana"), () => (SelectedDate ?? DateTime.Today).AddDays(7)));
            quick.Children.Add(Quick(T("Limpar"), () => null));

            var panel = new StackPanel();
            panel.Children.Add(_cal);
            panel.Children.Add(quick);

            _popup = new Popup
            {
                PlacementTarget = this, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true,
                PopupAnimation = PopupAnimation.None, VerticalOffset = 4,
                Child = new Border
                {
                    Background = Ui.Res("Elevated"), BorderBrush = Ui.Res("Line"), BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10), Padding = new Thickness(12), Child = panel, Margin = new Thickness(0, 0, 8, 8)
                }
            };
            Motion.SetPopIn(_popup, true); // opens from the calendar button
            btn.Click += (s, e) => { _cal.Show(SelectedDate); _popup.IsOpen = true; };
            grid.Children.Add(_popup);
            Content = grid;
        }

        private Button Quick(string text, Func<DateTime?> value)
        {
            var b = new Button { Content = text, Style = Ui.St("SecondaryButton"), Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(0, 0, 6, 0), FontSize = 12 };
            b.Click += (s, e) => { SelectedDate = value(); _popup.IsOpen = false; };
            return b;
        }

        private void Commit()
        {
            if (Dates.TryParseDate(_box.Text, out var d)) SelectedDate = d;
            SyncText();
        }

        private void SyncText()
        {
            _box.Text = SelectedDate.HasValue ? SelectedDate.Value.ToString(T("dd/MM/yyyy"), System.Globalization.CultureInfo.InvariantCulture) : "";
        }
    }
}
