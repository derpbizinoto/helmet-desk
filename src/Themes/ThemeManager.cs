using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace HelmetDesk
{
    /// <summary>
    /// Picks the visual theme. Both themes (Themes/Legacy.xaml and Themes/VioletAero.xaml) define the same keys
    /// (styles equal in both live in Themes/Common.xaml), so the screens never know which one is loaded.
    /// The theme is applied once at startup, before any window exists; changing it in Configurações saves the choice
    /// and restarts the app (screens use StaticResource).
    /// </summary>
    public static class ThemeManager
    {
        public const string Aero = "aero";
        public const string Classic = "classic";

        /// <summary>The theme loaded in this run.</summary>
        public static string Current { get; private set; } = Classic;

        public static string DisplayName(string theme) => theme == Classic ? T("Clássico") : "Violet Aero";

        /// <summary>Loads the saved theme. HELMETDESK_THEME=aero|classic overrides it (snapshot/self-test tooling).</summary>
        public static void Apply(string saved)
        {
            var forced = Environment.GetEnvironmentVariable("HELMETDESK_THEME");
            Current = Normalize(string.IsNullOrWhiteSpace(forced) ? saved : forced);
            var file = Current == Classic ? "Legacy" : "VioletAero";
            var dicts = Application.Current.Resources.MergedDictionaries;
            foreach (var old in dicts.Where(d => d.Source != null && d.Source.OriginalString.Contains("/Themes/")).ToList()) dicts.Remove(old);
            dicts.Insert(0, new ResourceDictionary { Source = new Uri($"/HelmetDesk;component/Themes/{file}.xaml", UriKind.Relative) });
            // styles equal in both themes, then layout styles shared by the screens; they point at theme keys, so they load right after the theme
            dicts.Insert(1, new ResourceDictionary { Source = new Uri("/HelmetDesk;component/Themes/Common.xaml", UriKind.Relative) });
            dicts.Insert(2, new ResourceDictionary { Source = new Uri("/HelmetDesk;component/Themes/Screens.xaml", UriKind.Relative) });
            Log.Info($"Tema: {DisplayName(Current)}");
        }

        /// <summary>Classic is the default: anything other than "aero" means classic.</summary>
        public static string Normalize(string theme) => string.Equals(theme, Aero, StringComparison.OrdinalIgnoreCase) ? Aero : Classic;

        /// <summary>Saves the new choice and restarts so every screen is rebuilt with it.</summary>
        public static void Switch(string theme)
        {
            theme = Normalize(theme);
            if (Store.Data.Settings.Theme == theme) return;
            Store.Data.Settings.Theme = theme;
            Store.Save();
            if (theme != Current) App.Current.Restart();
        }

        // ---------- slow drift for decorative layers (aurora) ----------

        /// <summary>Seconds for one slow back-and-forth drift of a decorative element (0 = still).
        /// Skipped when Windows animations are off, or in the snapshot/self-test tooling.</summary>
        public static readonly DependencyProperty DriftProperty = DependencyProperty.RegisterAttached(
            "Drift", typeof(double), typeof(ThemeManager), new PropertyMetadata(0.0, OnDrift));
        public static void SetDrift(DependencyObject d, double v) => d.SetValue(DriftProperty, v);
        public static double GetDrift(DependencyObject d) => (double)d.GetValue(DriftProperty);

        /// <summary>How far (px) the drift moves the element, as "x,y".</summary>
        public static readonly DependencyProperty DriftByProperty = DependencyProperty.RegisterAttached(
            "DriftBy", typeof(Point), typeof(ThemeManager), new PropertyMetadata(new Point(60, 18)));
        public static void SetDriftBy(DependencyObject d, Point v) => d.SetValue(DriftByProperty, v);
        public static Point GetDriftBy(DependencyObject d) => (Point)d.GetValue(DriftByProperty);

        private static void OnDrift(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement fe) return;
            fe.Loaded -= StartDrift;
            if ((double)e.NewValue > 0) fe.Loaded += StartDrift;
        }

        private static void StartDrift(object sender, RoutedEventArgs e)
        {
            var fe = (FrameworkElement)sender;
            if (Motion.Off || Motion.Reduced) return;
            var secs = GetDrift(fe);
            var by = GetDriftBy(fe);
            var t = new TranslateTransform();
            fe.RenderTransform = t;
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            var dur = new Duration(TimeSpan.FromSeconds(secs));
            t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, by.X, dur) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
            t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, by.Y, dur) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease });
        }
    }
}
