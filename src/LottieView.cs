using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SkiaSharp;
using SkiaSharp.Skottie;

namespace HelmetDesk
{
    /// <summary>
    /// Plays a Lottie animation (.json or .lottie) with Skottie. The only place in the app that talks to the player.
    /// Source is a file name inside Assets/Animations (extension optional) or an absolute path.
    /// Loads lazily on first use, honours Windows "Animation effects" (reduced motion) and never throws:
    /// a missing or broken file just renders nothing and is written to log.txt.
    /// </summary>
    public class LottieView : FrameworkElement
    {
        public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
            nameof(Source), typeof(string), typeof(LottieView), new PropertyMetadata(null, (o, e) => ((LottieView)o).Reset()));
        public static readonly DependencyProperty LoopProperty = DependencyProperty.Register(
            nameof(Loop), typeof(bool), typeof(LottieView), new PropertyMetadata(false));
        public static readonly DependencyProperty AutoPlayProperty = DependencyProperty.Register(
            nameof(AutoPlay), typeof(bool), typeof(LottieView), new PropertyMetadata(true));
        public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
            nameof(Size), typeof(double), typeof(LottieView), new PropertyMetadata(double.NaN, (o, e) =>
            {
                var v = (LottieView)o;
                v.Width = v.Height = (double)e.NewValue;
            }));
        /// <summary>Hides the animation after a one-shot play ends (feedback overlays).</summary>
        public static readonly DependencyProperty ClearOnCompleteProperty = DependencyProperty.Register(
            nameof(ClearOnComplete), typeof(bool), typeof(LottieView), new PropertyMetadata(false));
        /// <summary>Plays once each time this goes from false to true while on screen (e.g. bound to Done).</summary>
        public static readonly DependencyProperty PlayWhenProperty = DependencyProperty.Register(
            nameof(PlayWhen), typeof(bool), typeof(LottieView), new PropertyMetadata(false, (o, e) =>
            {
                var v = (LottieView)o;
                if ((bool)e.NewValue && !(bool)e.OldValue && v.IsLoaded) v.Play();
            }));

        /// <summary>True while a frame is on screen (playing, or the reduced-motion still). Lets templates hide what the animation replaces.</summary>
        private static readonly DependencyPropertyKey IsAnimatingKey = DependencyProperty.RegisterReadOnly(
            nameof(IsAnimating), typeof(bool), typeof(LottieView), new PropertyMetadata(false));
        public static readonly DependencyProperty IsAnimatingProperty = IsAnimatingKey.DependencyProperty;
        public bool IsAnimating => (bool)GetValue(IsAnimatingProperty);

        public string Source { get => (string)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
        public bool Loop { get => (bool)GetValue(LoopProperty); set => SetValue(LoopProperty, value); }
        public bool AutoPlay { get => (bool)GetValue(AutoPlayProperty); set => SetValue(AutoPlayProperty, value); }
        public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
        public bool ClearOnComplete { get => (bool)GetValue(ClearOnCompleteProperty); set => SetValue(ClearOnCompleteProperty, value); }
        public bool PlayWhen { get => (bool)GetValue(PlayWhenProperty); set => SetValue(PlayWhenProperty, value); }

        /// <summary>Optional segment to play, in the file's frame numbers (like dotLottie's "segment"). NaN = whole animation.</summary>
        public static readonly DependencyProperty StartFrameProperty = DependencyProperty.Register(
            nameof(StartFrame), typeof(double), typeof(LottieView), new PropertyMetadata(double.NaN));
        public static readonly DependencyProperty EndFrameProperty = DependencyProperty.Register(
            nameof(EndFrame), typeof(double), typeof(LottieView), new PropertyMetadata(double.NaN));
        public double StartFrame { get => (double)GetValue(StartFrameProperty); set => SetValue(StartFrameProperty, value); }
        public double EndFrame { get => (double)GetValue(EndFrameProperty); set => SetValue(EndFrameProperty, value); }

        /// <summary>Segment bounds as 0..1 positions of the whole animation.</summary>
        private (double a, double b) Segment
        {
            get
            {
                if (_anim == null) return (0, 1);
                double total = Math.Max(1, _anim.Duration.TotalSeconds * _anim.Fps);
                double a = double.IsNaN(StartFrame) ? 0 : Math.Clamp(StartFrame / total, 0, 1);
                double b = double.IsNaN(EndFrame) ? 1 : Math.Clamp(EndFrame / total, 0, 1);
                return b > a ? (a, b) : (0, 1);
            }
        }

        /// <summary>Raised when a non-looping play reaches the end (also when the file failed, so callers never hang).</summary>
        public event EventHandler Completed;

        /// <summary>Test hook; null = follow Windows settings.</summary>
        public static bool? ReducedMotionOverride { get => Motion.ReducedOverride; set => Motion.ReducedOverride = value; }
        /// <summary>True when Windows "Animation effects" is off (Settings › Accessibility › Visual effects).</summary>
        public static bool ReducedMotion => Motion.Reduced;

        public bool HasAnimation => _anim != null;
        public bool LoadFailed { get; private set; }
        public bool IsPlaying => _playing;
        /// <summary>Current position, 0 = first frame, 1 = last frame.</summary>
        public double Progress => _pos;

        private static readonly HashSet<string> LoggedFailures = new(StringComparer.OrdinalIgnoreCase);
        private Animation _anim;
        private WriteableBitmap _bmp;
        private readonly Stopwatch _clock = new();
        private DispatcherTimer _staticTimer;
        private bool _playing;
        private bool _showFrame;
        private bool ShowingFrame
        {
            get => _showFrame;
            set { _showFrame = value; SetValue(IsAnimatingKey, value); }
        }
        private double _pos;

        public LottieView()
        {
            IsHitTestVisible = false;
            Loaded += (s, e) => { if (AutoPlay) Play(); };
            Unloaded += (s, e) => StopTicking();
        }

        private void Reset()
        {
            StopTicking();
            _anim?.Dispose();
            _anim = null;
            LoadFailed = false;
            ShowingFrame = false;
            InvalidateVisual();
            if (IsLoaded && AutoPlay) Play();
        }

        private void EnsureLoaded()
        {
            if (_anim != null || LoadFailed || string.IsNullOrWhiteSpace(Source)) return;
            try
            {
                var json = LottieCache.GetJson(Source);
                if (!Animation.TryParse(json, out _anim) || _anim == null)
                    throw new InvalidDataException("conteúdo Lottie inválido ou não suportado");
            }
            catch (Exception ex)
            {
                _anim = null;
                LoadFailed = true;
                if (LoggedFailures.Add(Source)) Log.Error($"Animação \"{Source}\" não carregou", ex);
            }
        }

        public void Play()
        {
            EnsureLoaded();
            if (_anim == null)
            {
                if (!Loop) Completed?.Invoke(this, EventArgs.Empty);
                return;
            }
            ShowingFrame = true;
            if (ReducedMotion)
            {
                // Static last frame instead of motion; one-shots still "finish" so callers can move on.
                StopTicking();
                _pos = Segment.b;
                RenderFrame();
                if (!Loop)
                {
                    _staticTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
                    _staticTimer.Tick -= OnStaticDone;
                    _staticTimer.Tick += OnStaticDone;
                    _staticTimer.Start();
                }
                return;
            }
            _pos = Segment.a;
            _clock.Restart();
            if (!_playing)
            {
                _playing = true;
                CompositionTarget.Rendering += OnFrame;
            }
            RenderFrame();
        }

        /// <summary>Shows one still frame (0 = first, 1 = last) without playing. Used by tests and previews.</summary>
        public void ShowFrame(double progress)
        {
            EnsureLoaded();
            StopTicking();
            if (_anim == null) return;
            _pos = Math.Clamp(progress, 0, 1);
            ShowingFrame = true;
            RenderFrame();
        }

        public void Stop()
        {
            StopTicking();
            ShowingFrame = false;
            InvalidateVisual();
        }

        private void OnStaticDone(object sender, EventArgs e)
        {
            _staticTimer.Stop();
            Finish();
        }

        private void StopTicking()
        {
            _staticTimer?.Stop();
            if (_playing)
            {
                CompositionTarget.Rendering -= OnFrame;
                _playing = false;
            }
            _clock.Stop();
        }

        private void OnFrame(object sender, EventArgs e)
        {
            if (_anim == null) { StopTicking(); return; }
            var (a, b) = Segment;
            double dur = Math.Max(0.01, _anim.Duration.TotalSeconds * (b - a));
            double t = _clock.Elapsed.TotalSeconds / dur;
            if (t >= 1)
            {
                if (Loop)
                {
                    _clock.Restart();
                    t %= 1;
                }
                else
                {
                    _pos = b;
                    RenderFrame();
                    Finish();
                    return;
                }
            }
            _pos = a + t * (b - a);
            RenderFrame();
        }

        private void Finish()
        {
            StopTicking();
            if (ClearOnComplete)
            {
                ShowingFrame = false;
                InvalidateVisual();
            }
            Completed?.Invoke(this, EventArgs.Empty);
        }

        private void RenderFrame()
        {
            if (_anim == null || ActualWidth <= 0 || ActualHeight <= 0) return;
            try
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                int w = Math.Max(1, (int)Math.Round(ActualWidth * dpi.DpiScaleX));
                int h = Math.Max(1, (int)Math.Round(ActualHeight * dpi.DpiScaleY));
                if (_bmp == null || _bmp.PixelWidth != w || _bmp.PixelHeight != h)
                    _bmp = new WriteableBitmap(w, h, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32, null);

                _bmp.Lock();
                try
                {
                    var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
                    using var surface = SKSurface.Create(info, _bmp.BackBuffer, _bmp.BackBufferStride);
                    var canvas = surface.Canvas;
                    canvas.Clear(SKColors.Transparent);
                    _anim.Seek(_pos);
                    _anim.Render(canvas, Fit(_anim.Size, w, h));
                    canvas.Flush();
                    _bmp.AddDirtyRect(new Int32Rect(0, 0, w, h));
                }
                finally { _bmp.Unlock(); }
                InvalidateVisual();
            }
            catch (Exception ex)
            {
                StopTicking();
                _anim = null;
                LoadFailed = true;
                Log.Error($"Animação \"{Source}\" falhou ao desenhar", ex);
            }
        }

        private static SKRect Fit(SKSize src, int w, int h)
        {
            if (src.Width <= 0 || src.Height <= 0) return new SKRect(0, 0, w, h);
            float s = Math.Min(w / src.Width, h / src.Height);
            float rw = src.Width * s, rh = src.Height * s;
            float x = (w - rw) / 2, y = (h - rh) / 2;
            return new SKRect(x, y, x + rw, y + rh);
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo info)
        {
            base.OnRenderSizeChanged(info);
            if (_showFrame) RenderFrame();
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (_showFrame && _bmp != null) dc.DrawImage(_bmp, new Rect(0, 0, ActualWidth, ActualHeight));
        }
    }

    /// <summary>
    /// Plays the "fx" LottieView inside a TaskCheck checkbox when the user checks it (mouse or keyboard), and the
    /// completion sound at the same moment (<see cref="SoundService"/>). Uses Click, which only fires for user input,
    /// so opening an already-done task, "Desfazer" or code changing Done never animates nor plays a sound.
    /// </summary>
    public static class TaskCheckFx
    {
        public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
            "Enabled", typeof(bool), typeof(TaskCheckFx), new PropertyMetadata(false, (d, e) =>
            {
                if (d is not System.Windows.Controls.CheckBox cb) return;
                cb.Click -= OnClick;
                if ((bool)e.NewValue) cb.Click += OnClick;
            }));

        public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);
        public static void SetEnabled(DependencyObject o, bool v) => o.SetValue(EnabledProperty, v);

        private static void OnClick(object sender, RoutedEventArgs e)
        {
            var cb = (System.Windows.Controls.CheckBox)sender;
            if (cb.IsChecked != true) return; // unchecking: no animation, no sound
            if (cb.Template?.FindName("fx", cb) is LottieView fx) fx.Play();
            if (cb.DataContext is Item it) SoundService.OnTaskChecked(it); // Done is already true here (binding runs before Click)
        }
    }

    /// <summary>Reads animation files on first request and keeps the JSON in memory.</summary>
    public static class LottieCache
    {
        private static readonly Dictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

        public static string GetJson(string source)
        {
            if (Cache.TryGetValue(source, out var json)) return json;
            var bytes = ReadBytes(source);
            json = IsZip(bytes) ? FromDotLottie(bytes) : Encoding.UTF8.GetString(bytes).TrimStart('﻿');
            Cache[source] = json;
            return json;
        }

        private static byte[] ReadBytes(string source)
        {
            if (Path.IsPathRooted(source)) return File.ReadAllBytes(source);
            var names = Path.HasExtension(source) ? new[] { source } : new[] { source + ".lottie", source + ".json" };
            foreach (var name in names)
            {
                try
                {
                    var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/Animations/" + name));
                    if (res == null) continue;
                    using var s = res.Stream;
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
                catch (IOException) { /* not embedded under this name; try the next one */ }
            }
            throw new FileNotFoundException($"arquivo não encontrado em Assets/Animations ({string.Join(" ou ", names)})");
        }

        private static bool IsZip(byte[] b) => b.Length > 3 && b[0] == 'P' && b[1] == 'K';

        /// <summary>.lottie is a zip: manifest.json + animations/&lt;id&gt;.json (v1) or a/&lt;id&gt;.json (v2). Plays the first animation.</summary>
        private static string FromDotLottie(byte[] bytes)
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            ZipArchiveEntry entry = null;
            var manifest = zip.GetEntry("manifest.json");
            if (manifest != null)
            {
                using var ms = manifest.Open();
                using var doc = JsonDocument.Parse(ms);
                if (doc.RootElement.TryGetProperty("animations", out var list) && list.GetArrayLength() > 0 &&
                    list[0].TryGetProperty("id", out var id))
                {
                    entry = zip.GetEntry($"animations/{id.GetString()}.json") ?? zip.GetEntry($"a/{id.GetString()}.json");
                }
            }
            entry ??= zip.Entries.FirstOrDefault(e =>
                e.FullName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                (e.FullName.StartsWith("animations/") || e.FullName.StartsWith("a/")));
            if (entry == null) throw new InvalidDataException("arquivo .lottie sem animação JSON");
            using var r = new StreamReader(entry.Open(), Encoding.UTF8);
            return r.ReadToEnd();
        }
    }
}
