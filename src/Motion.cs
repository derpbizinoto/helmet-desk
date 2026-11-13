using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace HelmetDesk
{
    /// <summary>
    /// Motion system: tokens (from Theme.xaml), reduced-motion support and the helpers every transition uses.
    /// Only Opacity and RenderTransform are animated (no layout work), except the group expand/collapse height.
    /// State changes are applied first; animations are purely visual and never block input.
    /// </summary>
    public static class Motion
    {
        // ---------- tokens ----------
        public static Duration Fast => Res("Motion.Fast", new Duration(TimeSpan.FromMilliseconds(150)));
        public static Duration Base => Res("Motion.Base", new Duration(TimeSpan.FromMilliseconds(220)));
        public static Duration Slow => Res("Motion.Slow", new Duration(TimeSpan.FromMilliseconds(300)));
        public static IEasingFunction EaseOut => Res<IEasingFunction>("Ease.Out", new CubicEase { EasingMode = EasingMode.EaseOut });
        public static IEasingFunction EaseIn => Res<IEasingFunction>("Ease.In", new CubicEase { EasingMode = EasingMode.EaseIn });
        public static IEasingFunction EaseStandard => Res<IEasingFunction>("Ease.Standard", new CubicEase { EasingMode = EasingMode.EaseInOut });
        public static double ShiftSmall => Res("Motion.ShiftSmall", 8.0);
        public static double Shift => Res("Motion.Shift", 16.0);
        public static double ShiftPanel => Res("Motion.ShiftPanel", 24.0);
        public static double PressScale => Res("Motion.PressScale", 0.97);
        public static double PopScale => Res("Motion.PopScale", 0.96);

        private static T Res<T>(string key, T fallback) =>
            Application.Current?.TryFindResource(key) is T v ? v : fallback;

        // ---------- switches ----------
        /// <summary>Test hook for reduced motion; null = follow Windows.</summary>
        public static bool? ReducedOverride { get; set; }
        /// <summary>True when Windows "Animation effects" is off (Settings › Accessibility › Visual effects):
        /// movement (slide/scale) is dropped and only short fades remain.</summary>
        public static bool Reduced => ReducedOverride ?? !SystemParameters.ClientAreaAnimation;
        /// <summary>Disables every transition (instant UI). Used by the snapshot/self-test tooling.</summary>
        public static bool Off { get; set; }

        private static Duration Dur(Duration d) => Reduced && d.HasTimeSpan && d.TimeSpan > Fast.TimeSpan ? Fast : d;

        // ---------- transform plumbing ----------
        private sealed class Fx { public ScaleTransform S = new(); public TranslateTransform T = new(); }
        private static readonly DependencyProperty FxProperty = DependencyProperty.RegisterAttached("Fx", typeof(Fx), typeof(Motion));

        private static Fx FxOf(UIElement el)
        {
            if (el.GetValue(FxProperty) is Fx fx && ReferenceEquals(el.RenderTransform is TransformGroup g && g.Children.Count > 1 ? g.Children[0] : null, fx.S)) return fx;
            fx = new Fx();
            var group = new TransformGroup();
            group.Children.Add(fx.S);
            group.Children.Add(fx.T);
            if (el.RenderTransform != null && !el.RenderTransform.Value.IsIdentity) group.Children.Add(el.RenderTransform);
            el.RenderTransform = group;
            el.SetValue(FxProperty, fx);
            return fx;
        }

        /// <summary>Stops any running transition on the element and leaves it at rest (opacity 1, no offset/scale).</summary>
        public static void Clear(UIElement el)
        {
            RestoreHit(el);
            Forget(el, UIElement.OpacityProperty);
            el.BeginAnimation(UIElement.OpacityProperty, null);
            if (el.GetValue(FxProperty) is Fx fx)
            {
                fx.T.BeginAnimation(TranslateTransform.XProperty, null);
                fx.T.BeginAnimation(TranslateTransform.YProperty, null);
                fx.S.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                fx.S.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                fx.T.X = fx.T.Y = 0;
                fx.S.ScaleX = fx.S.ScaleY = 1;
            }
        }

        /// <summary>True while the element still carries a non-rest offset/scale (used by the self-test).</summary>
        public static bool IsDisplaced(UIElement el) =>
            el.GetValue(FxProperty) is Fx fx && (Math.Abs(fx.T.X) > 0.01 || Math.Abs(fx.T.Y) > 0.01 || Math.Abs(fx.S.ScaleX - 1) > 0.001);

        public static Vector Offset(UIElement el) => el.GetValue(FxProperty) is Fx fx ? new Vector(fx.T.X, fx.T.Y) : new Vector();

        private static DoubleAnimation Anim(double? from, double? to, Duration d, IEasingFunction e) =>
            new() { From = from, To = to, Duration = d, EasingFunction = e };

        // latest animation started per (object, property): a finishing animation only cleans up if nothing replaced it
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Dictionary<DependencyProperty, object>> _active = new();

        /// <summary>Runs <paramref name="a"/> on (target, dp); when it ends the animation is removed so the base value shows again.</summary>
        private static void Run(IAnimatable target, DependencyProperty dp, DoubleAnimation a, Action done = null)
        {
            var token = new object();
            _active.GetOrCreateValue(target)[dp] = token;
            a.Completed += (s, e) =>
            {
                if (!_active.TryGetValue(target, out var map) || !map.TryGetValue(dp, out var cur) || cur != token) return;
                map.Remove(dp);
                target.BeginAnimation(dp, null);
                done?.Invoke();
            };
            target.BeginAnimation(dp, a, HandoffBehavior.SnapshotAndReplace);
        }

        private static void Forget(object target, DependencyProperty dp)
        {
            if (_active.TryGetValue(target, out var map)) map.Remove(dp);
        }

        // hit-testing switched off by Exit, restored by Enter/Clear if an exit gets interrupted
        private static readonly DependencyProperty SavedHitProperty = DependencyProperty.RegisterAttached("SavedHit", typeof(bool?), typeof(Motion));

        private static void RestoreHit(UIElement el)
        {
            if (el.GetValue(SavedHitProperty) is bool hit)
            {
                el.IsHitTestVisible = hit;
                el.ClearValue(SavedHitProperty);
            }
        }

        // ---------- high-level transitions ----------

        /// <summary>Fade in, optionally from an offset and/or scale. Ends at rest. Reduced motion: fade only, short.</summary>
        public static void Enter(UIElement el, double dx = 0, double dy = 0, double scale = 1, Duration? duration = null, Point? origin = null)
        {
            if (el == null) return;
            if (Off) { Clear(el); return; }
            var d = Dur(duration ?? Base);
            var fx = FxOf(el);
            RestoreHit(el);
            if (origin.HasValue) el.RenderTransformOrigin = origin.Value;
            Run(el, UIElement.OpacityProperty, Anim(0, null, d, EaseOut));
            if (Reduced) { ResetMove(fx); return; }
            fx.T.X = fx.T.Y = 0; fx.S.ScaleX = fx.S.ScaleY = 1;
            if (dx != 0) Run(fx.T, TranslateTransform.XProperty, Anim(dx, 0, d, EaseOut)); else fx.T.BeginAnimation(TranslateTransform.XProperty, null);
            if (dy != 0) Run(fx.T, TranslateTransform.YProperty, Anim(dy, 0, d, EaseOut)); else fx.T.BeginAnimation(TranslateTransform.YProperty, null);
            if (scale != 1)
            {
                Run(fx.S, ScaleTransform.ScaleXProperty, Anim(scale, 1, d, EaseOut));
                Run(fx.S, ScaleTransform.ScaleYProperty, Anim(scale, 1, d, EaseOut));
            }
        }

        private static void ResetMove(Fx fx)
        {
            fx.T.BeginAnimation(TranslateTransform.XProperty, null);
            fx.T.BeginAnimation(TranslateTransform.YProperty, null);
            fx.S.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            fx.S.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            fx.T.X = fx.T.Y = 0; fx.S.ScaleX = fx.S.ScaleY = 1;
        }

        /// <summary>Fade out (faster than enter, ease-in), optionally moving away. The element stops taking input at once.
        /// <paramref name="done"/> runs at the end (or immediately when motion is off); the element is then reset to rest.</summary>
        public static void Exit(UIElement el, double dx = 0, double dy = 0, double scale = 1, Action done = null)
        {
            if (el == null) return;
            if (Off) { done?.Invoke(); Clear(el); return; }
            var d = Fast;
            var fx = FxOf(el);
            if (el.GetValue(SavedHitProperty) == null) el.SetValue(SavedHitProperty, el.IsHitTestVisible);
            el.IsHitTestVisible = false;
            Run(el, UIElement.OpacityProperty, Anim(null, 0, d, EaseIn), () =>
            {
                done?.Invoke();
                Clear(el);
            });
            if (Reduced) return;
            if (dx != 0) fx.T.BeginAnimation(TranslateTransform.XProperty, Anim(null, dx, d, EaseIn));
            if (dy != 0) fx.T.BeginAnimation(TranslateTransform.YProperty, Anim(null, dy, d, EaseIn));
            if (scale != 1)
            {
                fx.S.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(null, scale, d, EaseIn));
                fx.S.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(null, scale, d, EaseIn));
            }
        }

        /// <summary>FLIP: the element was laid out at a new place; show it at the old one and glide over. Skipped under reduced motion.</summary>
        public static void Slide(UIElement el, Vector from, Duration? duration = null)
        {
            if (el == null || Off || Reduced) return;
            var fx = FxOf(el);
            var d = duration ?? Base;
            if (Math.Abs(from.X) > 0.5) Run(fx.T, TranslateTransform.XProperty, Anim(from.X, 0, d, EaseStandard));
            if (Math.Abs(from.Y) > 0.5) Run(fx.T, TranslateTransform.YProperty, Anim(from.Y, 0, d, EaseStandard));
        }

        /// <summary>Animates a single translate axis to an absolute value (used by the sidebar marker).</summary>
        public static void MoveTo(UIElement el, double y, bool animate = true)
        {
            var fx = FxOf(el);
            double from = fx.T.Y;
            fx.T.Y = y;
            if (!animate || Off || Reduced || Math.Abs(from - y) < 0.5) { fx.T.BeginAnimation(TranslateTransform.YProperty, null); return; }
            Run(fx.T, TranslateTransform.YProperty, Anim(from, y, Base, EaseStandard));
        }

        /// <summary>Turns an element (e.g. a chevron) between two angles; jumps under reduced motion.</summary>
        public static void Rotate(UIElement el, double from, double to)
        {
            var r = new RotateTransform(to);
            el.RenderTransform = r;
            if (Off || Reduced) return;
            r.BeginAnimation(RotateTransform.AngleProperty, Anim(from, to, Base, EaseStandard));
        }

        /// <summary>Freezes the element's current look into an image floating above <paramref name="host"/> and plays an exit on it.
        /// The real element can be hidden/removed right away, so layout and input never wait for the animation.</summary>
        public static void Ghost(FrameworkElement el, FrameworkElement host, double dx = 0, double dy = 0, double scale = 1)
        {
            if (Off || el == null || host == null || !el.IsVisible || el.ActualWidth < 1 || el.ActualHeight < 1) return;
            var img = Snap(el);
            if (img == null) return;
            Point at;
            try { at = el.TransformToAncestor(host).Transform(new Point()); } catch (InvalidOperationException) { return; }
            GhostAt(host, img, new Rect(at, el.RenderSize), dx, dy, scale);
        }

        internal static void GhostAt(FrameworkElement host, ImageSource img, Rect at, double dx = 0, double dy = 0, double scale = 1)
        {
            var layer = GhostLayer.For(host);
            if (layer == null) return;
            var image = new Image { Source = img, Width = at.Width, Height = at.Height, IsHitTestVisible = false, RenderTransformOrigin = new Point(0.5, 0.5) };
            Canvas.SetLeft(image, at.X);
            Canvas.SetTop(image, at.Y);
            layer.Canvas.Children.Add(image);
            Exit(image, dx, dy, scale, () => layer.Canvas.Children.Remove(image));
        }

        /// <summary>Number of ghosts currently fading above <paramref name="host"/> (self-test).</summary>
        public static int GhostCount(FrameworkElement host) => GhostLayer.Peek(host)?.Canvas.Children.Count ?? 0;

        internal static ImageSource Snap(FrameworkElement el)
        {
            try
            {
                var size = el.RenderSize;
                var dpi = VisualTreeHelper.GetDpi(el);
                int w = (int)Math.Ceiling(size.Width * dpi.DpiScaleX), h = (int)Math.Ceiling(size.Height * dpi.DpiScaleY);
                if (w <= 0 || h <= 0 || w * h > 8_000_000) return null;
                // the brush sees the element at its layout offset inside the parent, so the viewbox starts there
                var off = VisualTreeHelper.GetOffset(el);
                var dv = new DrawingVisual();
                using (var dc = dv.RenderOpen())
                    dc.DrawRectangle(new VisualBrush(el) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(new Point(off.X, off.Y), size) },
                        null, new Rect(size));
                var rtb = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                rtb.Render(dv);
                rtb.Freeze();
                return rtb;
            }
            catch (Exception ex) { Log.Error("Motion: snapshot failed", ex); return null; }
        }

        /// <summary>Expands/collapses a block by animating its height (the one layout animation, kept to the clicked group).</summary>
        public static void Collapse(FrameworkElement el, bool collapse)
        {
            el.BeginAnimation(FrameworkElement.HeightProperty, null);
            if (Off)
            {
                el.SetCurrentValue(UIElement.VisibilityProperty, collapse ? Visibility.Collapsed : Visibility.Visible);
                return;
            }
            if (Reduced)
            {
                el.SetCurrentValue(UIElement.VisibilityProperty, collapse ? Visibility.Collapsed : Visibility.Visible);
                if (!collapse) Run(el, UIElement.OpacityProperty, Anim(0, null, Fast, EaseOut));
                return;
            }
            bool clip = el.ClipToBounds;
            el.ClipToBounds = true;
            if (collapse)
            {
                double h = el.ActualHeight;
                Run(el, UIElement.OpacityProperty, Anim(null, 0, Fast, EaseIn));
                Run(el, FrameworkElement.HeightProperty, Anim(h, 0, Base, EaseStandard), () =>
                {
                    el.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Collapsed);
                    el.BeginAnimation(UIElement.OpacityProperty, null);
                    el.ClipToBounds = clip;
                });
            }
            else
            {
                el.SetCurrentValue(UIElement.VisibilityProperty, Visibility.Visible);
                double width = el.ActualWidth > 0 ? el.ActualWidth : (el.Parent as FrameworkElement)?.ActualWidth ?? double.PositiveInfinity;
                el.Measure(new Size(width, double.PositiveInfinity));
                double h = el.DesiredSize.Height;
                Run(el, UIElement.OpacityProperty, Anim(0, null, Base, EaseOut));
                Run(el, FrameworkElement.HeightProperty, Anim(0, h, Base, EaseStandard), () => el.ClipToBounds = clip);
            }
        }

        // ---------- brush transitions (hover/focus/selected colors) ----------
        // Templates and triggers set these attached "target" brushes; the real property follows with a short color fade.

        public static readonly DependencyProperty BackgroundProperty = BrushProp("Background");
        public static readonly DependencyProperty BorderBrushProperty = BrushProp("BorderBrush");
        public static readonly DependencyProperty ForegroundProperty = BrushProp("Foreground");
        public static readonly DependencyProperty FillProperty = BrushProp("Fill");

        public static Brush GetBackground(DependencyObject o) => (Brush)o.GetValue(BackgroundProperty);
        public static void SetBackground(DependencyObject o, Brush v) => o.SetValue(BackgroundProperty, v);
        public static Brush GetBorderBrush(DependencyObject o) => (Brush)o.GetValue(BorderBrushProperty);
        public static void SetBorderBrush(DependencyObject o, Brush v) => o.SetValue(BorderBrushProperty, v);
        public static Brush GetForeground(DependencyObject o) => (Brush)o.GetValue(ForegroundProperty);
        public static void SetForeground(DependencyObject o, Brush v) => o.SetValue(ForegroundProperty, v);
        public static Brush GetFill(DependencyObject o) => (Brush)o.GetValue(FillProperty);
        public static void SetFill(DependencyObject o, Brush v) => o.SetValue(FillProperty, v);

        private static DependencyProperty BrushProp(string name) =>
            DependencyProperty.RegisterAttached(name, typeof(Brush), typeof(Motion), new PropertyMetadata(null, OnBrushChanged));

        private static DependencyProperty RealProperty(DependencyObject d, DependencyProperty p)
        {
            if (p == BackgroundProperty) return d switch { Border => Border.BackgroundProperty, Panel => Panel.BackgroundProperty, Control => Control.BackgroundProperty, _ => null };
            if (p == BorderBrushProperty) return d switch { Border => Border.BorderBrushProperty, Control => Control.BorderBrushProperty, _ => null };
            if (p == FillProperty) return d is Shape ? Shape.FillProperty : null;
            if (p == ForegroundProperty) return TextElement.ForegroundProperty;
            return null;
        }

        private static void OnBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var real = RealProperty(d, e.Property);
            if (real == null) return;
            var to = e.NewValue as Brush;
            bool live = !Off && d is FrameworkElement fe && fe.IsLoaded && fe.IsVisible;
            if (to is SolidColorBrush target && live && d.GetValue(real) is SolidColorBrush cur && !cur.IsFrozen)
            {
                Color from = cur.Color, dest = target.Color;
                if (from.A == 0) from = Color.FromArgb(0, dest.R, dest.G, dest.B);
                if (dest.A == 0) dest = Color.FromArgb(0, from.R, from.G, from.B);
                var a = new ColorAnimation { From = from, To = dest, Duration = Fast, EasingFunction = EaseStandard, FillBehavior = FillBehavior.HoldEnd };
                cur.Color = dest;
                cur.BeginAnimation(SolidColorBrush.ColorProperty, a, HandoffBehavior.SnapshotAndReplace);
                return;
            }
            d.SetValue(real, to is SolidColorBrush s ? new SolidColorBrush(s.Color) { Opacity = s.Opacity } : to);
        }

        // ---------- press feedback ----------

        public static readonly DependencyProperty PressProperty = DependencyProperty.RegisterAttached(
            "Press", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnPressChanged));
        public static bool GetPress(DependencyObject o) => (bool)o.GetValue(PressProperty);
        public static void SetPress(DependencyObject o, bool v) => o.SetValue(PressProperty, v);

        private static void OnPressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not UIElement el) return;
            if ((bool)e.NewValue)
            {
                el.PreviewMouseLeftButtonDown += PressDown;
                el.PreviewMouseLeftButtonUp += PressUp;
                el.MouseLeave += PressUp;
                el.LostMouseCapture += PressUp;
                el.PreviewKeyDown += PressKey;
                el.PreviewKeyUp += PressKey;
            }
            else
            {
                el.PreviewMouseLeftButtonDown -= PressDown;
                el.PreviewMouseLeftButtonUp -= PressUp;
                el.MouseLeave -= PressUp;
                el.LostMouseCapture -= PressUp;
                el.PreviewKeyDown -= PressKey;
                el.PreviewKeyUp -= PressKey;
            }
        }

        private static void PressDown(object s, MouseButtonEventArgs e) => Press((UIElement)s, true);
        private static void PressUp(object s, EventArgs e) => Press((UIElement)s, false);
        private static void PressKey(object s, KeyEventArgs e)
        {
            if (e.Key == Key.Space && !e.IsRepeat) Press((UIElement)s, e.IsDown);
        }

        public static void Press(UIElement el, bool down)
        {
            if (Off || Reduced || !el.IsEnabled) return;
            var fx = FxOf(el);
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            double to = down ? PressScale : 1;
            if (!down && fx.S.ScaleX == 1 && !fx.S.HasAnimatedProperties) return;
            var d = down ? Fast : Base;
            fx.S.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(null, to, d, EaseOut), HandoffBehavior.SnapshotAndReplace);
            fx.S.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(null, to, d, EaseOut), HandoffBehavior.SnapshotAndReplace);
            fx.S.ScaleX = fx.S.ScaleY = to;
        }

        // ---------- popups open from their anchor ----------

        public static readonly DependencyProperty PopInProperty = DependencyProperty.RegisterAttached(
            "PopIn", typeof(bool), typeof(Motion), new PropertyMetadata(false, (d, e) =>
            {
                if (d is not Popup p) return;
                if ((bool)e.NewValue) p.Opened += PopupOpened; else p.Opened -= PopupOpened;
            }));
        public static bool GetPopIn(DependencyObject o) => (bool)o.GetValue(PopInProperty);
        public static void SetPopIn(DependencyObject o, bool v) => o.SetValue(PopInProperty, v);

        private static void PopupOpened(object sender, EventArgs e)
        {
            var p = (Popup)sender;
            if (p.Child is not FrameworkElement child) return;
            child.UpdateLayout();
            double ox = 0.5;
            if (p.PlacementTarget is FrameworkElement t && child.ActualWidth > 0)
            {
                try
                {
                    var a = t.PointToScreen(new Point(t.ActualWidth / 2, 0));
                    var c = child.PointToScreen(new Point());
                    var scale = VisualTreeHelper.GetDpi(child).DpiScaleX;
                    ox = Math.Clamp((a.X - c.X) / scale / child.ActualWidth, 0, 1);
                }
                catch (InvalidOperationException) { }
            }
            Enter(child, dy: -4, scale: PopScale, duration: Fast, origin: new Point(ox, 0));
        }

        // ---------- switch thumb ----------

        public static readonly DependencyProperty SwitchProperty = DependencyProperty.RegisterAttached(
            "Switch", typeof(bool), typeof(Motion), new PropertyMetadata(false, (d, e) =>
            {
                if (d is not ToggleButton t || !(bool)e.NewValue) return;
                t.Checked += (s, a) => SwitchMoved(t, true);
                t.Unchecked += (s, a) => SwitchMoved(t, false);
            }));
        public static bool GetSwitch(DependencyObject o) => (bool)o.GetValue(SwitchProperty);
        public static void SetSwitch(DependencyObject o, bool v) => o.SetValue(SwitchProperty, v);

        private static void SwitchMoved(ToggleButton t, bool on)
        {
            if (!t.IsLoaded || t.Template?.FindName("thumb", t) is not FrameworkElement thumb || t.Template.FindName("track", t) is not Border track) return;
            double travel = track.ActualWidth - track.Padding.Left - track.Padding.Right - thumb.ActualWidth;
            Slide(thumb, new Vector(on ? -travel : travel, 0), Fast);
        }

        // ---------- ghost layer (adorner that hosts exiting snapshots, clipped to its host) ----------

        private sealed class GhostLayer : Adorner
        {
            public readonly Canvas Canvas = new() { IsHitTestVisible = false };
            private static readonly DependencyProperty LayerProperty = DependencyProperty.RegisterAttached("GhostLayer", typeof(GhostLayer), typeof(GhostLayer));

            private GhostLayer(UIElement host) : base(host)
            {
                IsHitTestVisible = false;
                ClipToBounds = true;
                AddVisualChild(Canvas);
            }

            public static GhostLayer Peek(FrameworkElement host) => host.GetValue(LayerProperty) as GhostLayer;

            public static GhostLayer For(FrameworkElement host)
            {
                if (Peek(host) is GhostLayer g && VisualTreeHelper.GetParent(g) != null) return g;
                var layer = AdornerLayer.GetAdornerLayer(host);
                if (layer == null) return null;
                g = new GhostLayer(host);
                layer.Add(g);
                host.SetValue(LayerProperty, g);
                return g;
            }

            protected override int VisualChildrenCount => 1;
            protected override Visual GetVisualChild(int index) => Canvas;
            protected override Size MeasureOverride(Size constraint) { Canvas.Measure(AdornedElement.RenderSize); return AdornedElement.RenderSize; }
            protected override Size ArrangeOverride(Size finalSize) { Canvas.Arrange(new Rect(AdornedElement.RenderSize)); return finalSize; }
            protected override Geometry GetLayoutClip(Size layoutSlotSize) => new RectangleGeometry(new Rect(AdornedElement.RenderSize));
        }
    }

    /// <summary>
    /// FLIP for item lists: snapshot where every item container is, change the data, then let survivors glide from
    /// their old spot, new items fade in and removed items fade out as ghosts. The list itself is updated instantly.
    /// </summary>
    public static class Flip
    {
        private sealed class Entry
        {
            public FrameworkElement El;
            public Point Pos;
            public object Parent;
            public bool Visible;
            public ImageSource Image;
        }

        private static object KeyOf(object content) => content switch
        {
            ItemGroup g => "g:" + g.Title,
            Item or CheckItem => content,
            _ => null
        };

        /// <param name="viewport">The scroll viewer (or panel) that contains the lists; also clips the ghosts.</param>
        /// <param name="mutate">The data change (e.g. replacing ItemsSource).</param>
        public static void Run(FrameworkElement viewport, Action mutate, bool enterNew = true)
        {
            if (Motion.Off || viewport == null || !viewport.IsVisible || viewport.ActualHeight <= 0)
            {
                mutate();
                return;
            }
            var before = Collect(viewport);
            foreach (var e in before.Values) if (e.Visible) e.Image = Motion.Snap(e.El);

            mutate();
            viewport.UpdateLayout();

            foreach (var e in Collect(viewport, measure: false).Values) Motion.Clear(e.El);
            var after = Collect(viewport);
            if (before.Count + after.Count > 400) return; // huge change: just show the result

            var moved = new Dictionary<object, Vector>();
            foreach (var (key, a) in after)
            {
                if (a.Parent != null && !before.ContainsKey(a.Parent)) continue; // inside an entering parent
                if (before.TryGetValue(key, out var b))
                {
                    var delta = b.Pos - a.Pos;
                    if (a.Parent != null && moved.TryGetValue(a.Parent, out var pd)) delta -= pd;
                    moved[key] = (b.Pos - a.Pos);
                    if ((b.Visible || a.Visible) && delta.Length > 0.5) Motion.Slide(a.El, delta);
                }
                else if (enterNew && before.Count > 0 && a.Visible)
                {
                    Motion.Enter(a.El, dy: Motion.ShiftSmall);
                }
            }
            foreach (var (key, b) in before)
            {
                if (after.ContainsKey(key) || b.Image == null) continue;
                if (b.Parent != null && !after.ContainsKey(b.Parent)) continue; // the parent's ghost covers it
                Motion.GhostAt(viewport, b.Image, new Rect(b.Pos, b.El.RenderSize), scale: 0.98);
            }
        }

        private static Dictionary<object, Entry> Collect(FrameworkElement root, bool measure = true)
        {
            var map = new Dictionary<object, Entry>();
            var view = new Rect(root.RenderSize);
            void Walk(DependencyObject d, object parent)
            {
                int n = VisualTreeHelper.GetChildrenCount(d);
                for (int i = 0; i < n; i++)
                {
                    var c = VisualTreeHelper.GetChild(d, i);
                    object next = parent;
                    if (c is ContentPresenter cp && cp.IsVisible && KeyOf(cp.Content) is object key
                        && ItemsControl.ItemsControlFromItemContainer(cp) != null && !map.ContainsKey(key))
                    {
                        var e = new Entry { El = cp, Parent = parent };
                        if (measure)
                        {
                            try { e.Pos = cp.TransformToAncestor(root).Transform(new Point()); } catch (InvalidOperationException) { continue; }
                            e.Visible = new Rect(e.Pos, cp.RenderSize).IntersectsWith(view);
                        }
                        map[key] = e;
                        next = key;
                    }
                    else if (c is UIElement u && u.Visibility != Visibility.Visible) continue;
                    Walk(c, next);
                }
            }
            Walk(root, null);
            return map;
        }
    }
}
