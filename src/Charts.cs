using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace HelmetDesk
{
    /// <summary>
    /// Ring ("donut") chart drawn with plain WPF paths, so it follows both themes. One path per slice; hover grows the
    /// slice a little, a click selects it (it stays out). Enters with a clockwise sweep (a fade only with reduced motion).
    /// </summary>
    public sealed class DonutChart : Canvas
    {
        public sealed record Slice(double Value, Brush Fill, string Tip);

        public double Diameter { get; set; } = 210;
        public double RingThickness { get; set; } = 34;
        private const double Grow = 1.06;

        private readonly List<Path> _paths = new();
        private List<Slice> _slices = new();
        private int _selected = -1;

        /// <summary>Index of the clicked slice (-1 = clicked the selected one again, i.e. cleared).</summary>
        public event Action<int> SliceClicked;

        public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
            nameof(Progress), typeof(double), typeof(DonutChart), new PropertyMetadata(1.0, (o, e) => ((DonutChart)o).UpdateGeometry()));

        /// <summary>0..1: how much of the ring is drawn (entry sweep).</summary>
        public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

        public DonutChart()
        {
            Width = Diameter; Height = Diameter;
            ClipToBounds = false;
        }

        public int Selected
        {
            get => _selected;
            set
            {
                _selected = value;
                for (int i = 0; i < _paths.Count; i++) ScaleTo(_paths[i], i == _selected ? Grow : 1, animate: true);
            }
        }

        public void SetSlices(IEnumerable<Slice> slices, bool animate)
        {
            Width = Diameter; Height = Diameter;
            Children.Clear();
            _paths.Clear();
            _slices = slices.Where(s => s.Value > 0).ToList();
            _selected = -1;

            // empty track under the slices (also what shows when there is nothing to draw)
            var track = new Path { Data = Ring(0, 359.999), Opacity = 0.55, IsHitTestVisible = false };
            track.SetResourceReference(Shape.FillProperty, "Line");
            Children.Add(track);

            for (int i = 0; i < _slices.Count; i++)
            {
                int index = i;
                var p = new Path
                {
                    Fill = _slices[i].Fill,
                    ToolTip = _slices[i].Tip,
                    Cursor = Cursors.Hand,
                    RenderTransform = new ScaleTransform(1, 1, Diameter / 2, Diameter / 2),
                };
                ToolTipService.SetInitialShowDelay(p, 150);
                p.MouseEnter += (s, e) => ScaleTo(p, Grow, true);
                p.MouseLeave += (s, e) => { if (index != _selected) ScaleTo(p, 1, true); };
                p.MouseLeftButtonUp += (s, e) =>
                {
                    Selected = index == _selected ? -1 : index;
                    SliceClicked?.Invoke(_selected);
                    e.Handled = true;
                };
                _paths.Add(p);
                Children.Add(p);
            }

            BeginAnimation(ProgressProperty, null);
            if (animate && !Motion.Off && !Motion.Reduced && _slices.Count > 0)
            {
                Progress = 0;
                BeginAnimation(ProgressProperty, new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(650)))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            }
            else
            {
                Progress = 1;
                UpdateGeometry();
                if (animate) Motion.Enter(this);
            }
        }

        private void UpdateGeometry()
        {
            if (_paths.Count == 0) return;
            double total = _slices.Sum(s => s.Value);
            double gap = _slices.Count > 1 ? 1.4 : 0;
            double start = 0, p = Progress;
            for (int i = 0; i < _paths.Count; i++)
            {
                double sweep = _slices[i].Value / total * 360;
                // the sweep reveals the ring clockwise: slices past the current angle are not drawn yet
                double visibleEnd = Math.Min(start + sweep, 360 * p);
                double a0 = start + gap / 2, a1 = visibleEnd - gap / 2;
                _paths[i].Data = a1 - a0 > 0.05 ? Ring(a0, Math.Min(a1 - a0, 359.999)) : null;
                start += sweep;
            }
        }

        /// <summary>Ring segment from <paramref name="startDeg"/> (0 = 12 o'clock, clockwise) spanning <paramref name="sweepDeg"/>.</summary>
        private Geometry Ring(double startDeg, double sweepDeg)
        {
            double c = Diameter / 2, ro = c - 2, ri = ro - RingThickness;
            Point P(double r, double deg)
            {
                double rad = (deg - 90) * Math.PI / 180;
                return new Point(c + r * Math.Cos(rad), c + r * Math.Sin(rad));
            }
            double end = startDeg + sweepDeg;
            bool large = sweepDeg > 180;
            var fig = new PathFigure { StartPoint = P(ro, startDeg), IsClosed = true, IsFilled = true };
            fig.Segments.Add(new ArcSegment(P(ro, end), new Size(ro, ro), 0, large, SweepDirection.Clockwise, true));
            fig.Segments.Add(new LineSegment(P(ri, end), true));
            fig.Segments.Add(new ArcSegment(P(ri, startDeg), new Size(ri, ri), 0, large, SweepDirection.Counterclockwise, true));
            var g = new PathGeometry();
            g.Figures.Add(fig);
            g.Freeze();
            return g;
        }

        private static void ScaleTo(Path p, double s, bool animate)
        {
            if (p.RenderTransform is not ScaleTransform st) return;
            if (!animate || Motion.Off || Motion.Reduced)
            {
                st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                st.ScaleX = st.ScaleY = s;
                return;
            }
            var a = new DoubleAnimation(s, Motion.Fast) { EasingFunction = Motion.EaseOut };
            st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }
    }

    /// <summary>
    /// Month-by-month chart: an income bar and an expense bar per month, and the month's balance as a line on top.
    /// Redrawn on resize; bars grow from the zero line when the data is set.
    /// </summary>
    public sealed class MonthBarChart : Canvas
    {
        public sealed record Month(string Label, decimal Income, decimal Expense, string Tip);

        private List<Month> _data = new();
        private bool _animateNext;

        public MonthBarChart()
        {
            ClipToBounds = false;
            SizeChanged += (s, e) => { if (e.WidthChanged) Draw(false); };
        }

        public void SetData(IEnumerable<Month> months, bool animate)
        {
            _data = months.ToList();
            _animateNext = animate;
            if (ActualWidth > 0) Draw(animate);
        }

        private static TextBlock Label(string text, double size = 11)
        {
            var t = new TextBlock { Text = text, FontSize = size, IsHitTestVisible = false };
            t.SetResourceReference(TextBlock.ForegroundProperty, "Text2");
            return t;
        }

        private void Draw(bool animate)
        {
            if (_animateNext) { animate = true; _animateNext = false; }
            Children.Clear();
            double w = ActualWidth, h = ActualHeight;
            if (w <= 0 || h <= 0 || _data.Count == 0) return;

            const double axisW = 62, bottom = 22, top = 8;
            double plotW = w - axisW, plotH = h - bottom - top;
            double max = (double)_data.Max(m => Math.Max(Math.Max(m.Income, m.Expense), m.Income - m.Expense));
            double min = (double)Math.Min(0, _data.Min(m => m.Income - m.Expense));
            if (max <= 0 && min >= 0) max = 100;
            double step = NiceStep((max - min) / 4);
            max = Math.Ceiling(max / step) * step;
            min = Math.Floor(min / step) * step;
            double Y(double v) => top + (max - v) / (max - min) * plotH;

            // grid + axis labels
            for (double v = min; v <= max + step / 2; v += step)
            {
                var line = new Rectangle { Width = plotW, Height = 1, Opacity = Math.Abs(v) < step / 2 ? 0.9 : 0.4, IsHitTestVisible = false };
                line.SetResourceReference(Shape.FillProperty, "Line");
                SetLeft(line, axisW); SetTop(line, Math.Round(Y(v)));
                Children.Add(line);
                var t = Label(Compact((decimal)v), 10.5);
                t.Width = axisW - 10; t.TextAlignment = TextAlignment.Right;
                SetLeft(t, 0); SetTop(t, Y(v) - 7);
                Children.Add(t);
            }

            int n = _data.Count;
            double gw = plotW / n;
            double barW = Math.Max(4, Math.Min(18, gw * 0.28));
            double zero = Y(0);
            var linePts = new PointCollection();
            for (int i = 0; i < n; i++)
            {
                var m = _data[i];
                double cx = axisW + gw * (i + 0.5);

                // hover band (also carries the tooltip for the whole month)
                var band = new Rectangle { Width = gw - 4, Height = plotH, Fill = Brushes.Transparent, RadiusX = 6, RadiusY = 6, ToolTip = m.Tip };
                ToolTipService.SetInitialShowDelay(band, 150);
                band.MouseEnter += (s, e) => band.SetResourceReference(Shape.FillProperty, "AccentSoft");
                band.MouseLeave += (s, e) => band.Fill = Brushes.Transparent;
                SetLeft(band, cx - (gw - 4) / 2); SetTop(band, top);
                Children.Add(band);

                AddBar(cx - barW - 1.5, barW, zero, Y((double)m.Income), "Success", animate, i);
                AddBar(cx + 1.5, barW, zero, Y((double)m.Expense), "Danger", animate, i);

                linePts.Add(new Point(cx, Y((double)(m.Income - m.Expense))));

                var lab = Label(m.Label);
                lab.Width = gw; lab.TextAlignment = TextAlignment.Center;
                SetLeft(lab, cx - gw / 2); SetTop(lab, h - bottom + 5);
                Children.Add(lab);
            }

            // balance line
            var poly = new Polyline { Points = linePts, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
            poly.SetResourceReference(Shape.StrokeProperty, "Accent");
            Children.Add(poly);
            var dots = new List<UIElement> { poly };
            foreach (var p in linePts)
            {
                var dot = new Ellipse { Width = 7, Height = 7, StrokeThickness = 1.5, IsHitTestVisible = false };
                dot.SetResourceReference(Shape.FillProperty, "Accent");
                dot.SetResourceReference(Shape.StrokeProperty, "Surface");
                SetLeft(dot, p.X - 3.5); SetTop(dot, p.Y - 3.5);
                Children.Add(dot);
                dots.Add(dot);
            }
            if (animate && !Motion.Off)
                foreach (var d in dots)
                {
                    d.Opacity = 0;
                    d.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Motion.Slow) { BeginTime = TimeSpan.FromMilliseconds(Motion.Reduced ? 0 : 280) });
                }
        }

        private void AddBar(double x, double width, double zero, double y, string brush, bool animate, int index)
        {
            double height = Math.Max(0, zero - y);
            if (height < 0.5) return;
            var r = new Rectangle { Width = width, Height = height, RadiusX = Math.Min(3, width / 2), RadiusY = 3, IsHitTestVisible = false };
            r.SetResourceReference(Shape.FillProperty, brush);
            SetLeft(r, x); SetTop(r, y);
            Children.Add(r);
            if (!animate || Motion.Off || Motion.Reduced) return;
            var st = new ScaleTransform(1, 0, 0, height);
            r.RenderTransform = st;
            st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0, 1, Motion.Slow)
            {
                EasingFunction = Motion.EaseOut,
                BeginTime = TimeSpan.FromMilliseconds(Math.Min(index * 35, 300)),
            });
        }

        private static double NiceStep(double raw)
        {
            if (raw <= 0) return 1;
            double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double f = raw / mag;
            return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10) * mag;
        }

        /// <summary>Short money for axes, in the chosen currency: "R$ 1,2 mil" / "$1.2K" (see Money.Compact).</summary>
        public static string Compact(decimal v) => HelmetDesk.Core.Finance.Money.Compact(v);
    }
}
