using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace HelmetDesk
{
    public class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            OnChanged(name);
            return true;
        }

        public void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        protected virtual void OnChanged(string name) { }
    }

    public enum ItemKind { Note, Task }

    public class CheckItem : Observable
    {
        private string _text = "";
        private bool _done;
        public string Text { get => _text; set => Set(ref _text, value ?? ""); }
        public bool Done { get => _done; set => Set(ref _done, value); }
    }

    public enum AttachmentKind { File = 0, Image = 1, Link = 2 }

    /// <summary>
    /// Something attached to a note. Files and images live in the data folder (attachments/&lt;item id&gt;/), so they
    /// stay local and have no size limit; links only keep the address (title and icon come from <see cref="LinkPreview"/>).
    /// </summary>
    public class Attachment
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public AttachmentKind Kind { get; set; }
        /// <summary>Shown name: the original file name, or the link address.</summary>
        public string Name { get; set; } = "";
        /// <summary>Path relative to the data folder (files and images).</summary>
        public string File { get; set; }
        public string Url { get; set; }
        public long Size { get; set; }
        public DateTime AddedAt { get; set; } = DateTime.Now;
    }

    public class LabelTag : Observable
    {
        private string _name = "";
        private string _color = "#8950D9";
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get => _name; set => Set(ref _name, value ?? ""); }
        public string Color { get => _color; set => Set(ref _color, value); }

        [JsonIgnore]
        public SolidColorBrush Brush => Palette.BrushFrom(Color);

        protected override void OnChanged(string name)
        {
            if (name == nameof(Color)) Raise(nameof(Brush));
            Store.NotifyLabelsChanged();
        }
    }

    public static class Palette
    {
        public static readonly string[] LabelColors =
        {
            "#8950D9", "#C8A8F5", "#78B7FF", "#64D6A2", "#F2C36B", "#FF6F91", "#F0955A", "#8A7F99"
        };

        private static readonly Dictionary<string, SolidColorBrush> Cache = new();

        public static SolidColorBrush BrushFrom(string hex)
        {
            if (string.IsNullOrEmpty(hex)) hex = "#8950D9";
            if (Cache.TryGetValue(hex, out var b)) return b;
            try { b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
            catch { b = new SolidColorBrush(Color.FromRgb(0x89, 0x50, 0xD9)); }
            b.Freeze();
            Cache[hex] = b;
            return b;
        }
    }

    public class Item : Observable
    {
        /// <summary>Raised when a property that affects lists, grouping or totals changes.</summary>
        public static event Action StructureChanged;

        private static readonly HashSet<string> StructuralProps = new()
        {
            nameof(Done), nameof(Date), nameof(Time), nameof(LabelId), nameof(EstimateMinutes), nameof(Priority), nameof(Kind)
        };

        private ItemKind _kind;
        private string _title = "";
        private string _body = "";
        private string _labelId;
        private DateTime? _date;
        private string _time = "";
        private int _estimate;
        private bool _done;
        private int _priority;
        private ObservableCollection<CheckItem> _checklist;
        private ObservableCollection<Attachment> _attachments;

        public Item() { Checklist = new ObservableCollection<CheckItem>(); Attachments = new ObservableCollection<Attachment>(); }

        public ObservableCollection<Attachment> Attachments
        {
            get => _attachments;
            set
            {
                if (_attachments != null) _attachments.CollectionChanged -= OnAttachmentsChanged;
                _attachments = value ?? new ObservableCollection<Attachment>();
                _attachments.CollectionChanged += OnAttachmentsChanged;
                Raise(nameof(Attachments));
                Raise(nameof(HasAttachments));
            }
        }

        private void OnAttachmentsChanged(object s, NotifyCollectionChangedEventArgs e)
        {
            Raise(nameof(HasAttachments));
            Touch();
        }

        [JsonIgnore] public bool HasAttachments => _attachments?.Count > 0;

        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public ItemKind Kind { get => _kind; set => Set(ref _kind, value); }
        public string Title { get => _title; set => Set(ref _title, value ?? ""); }
        public string Body { get => _body; set => Set(ref _body, value ?? ""); }
        public string LabelId { get => _labelId; set => Set(ref _labelId, value); }
        public DateTime? Date { get => _date; set => Set(ref _date, value?.Date); }
        public string Time { get => _time; set => Set(ref _time, value ?? ""); }
        public int EstimateMinutes { get => _estimate; set => Set(ref _estimate, Math.Max(0, value)); }
        public bool Done { get => _done; set => Set(ref _done, value); }
        public int Priority { get => _priority; set => Set(ref _priority, value); }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public DateTime? DoneAt { get; set; }
        public bool Reminded { get; set; }

        // Desktop widget state
        public bool WidgetOpen { get; set; }
        public bool WidgetTopmost { get; set; }
        public double WidgetX { get; set; } = double.NaN;
        public double WidgetY { get; set; } = double.NaN;
        public double WidgetW { get; set; } = 280;
        public double WidgetH { get; set; } = 300;

        public ObservableCollection<CheckItem> Checklist
        {
            get => _checklist;
            set
            {
                if (_checklist != null) _checklist.CollectionChanged -= OnChecklistChanged;
                _checklist = value ?? new ObservableCollection<CheckItem>();
                _checklist.CollectionChanged += OnChecklistChanged;
                foreach (var c in _checklist) c.PropertyChanged += OnCheckItemChanged;
                Raise(nameof(Checklist));
                RaiseChecklist();
            }
        }

        private void OnChecklistChanged(object s, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null) foreach (CheckItem c in e.NewItems) c.PropertyChanged += OnCheckItemChanged;
            if (e.OldItems != null) foreach (CheckItem c in e.OldItems) c.PropertyChanged -= OnCheckItemChanged;
            RaiseChecklist();
            Touch();
        }

        private void OnCheckItemChanged(object s, PropertyChangedEventArgs e)
        {
            RaiseChecklist();
            Touch();
        }

        private void RaiseChecklist()
        {
            Raise(nameof(ChecklistText));
            Raise(nameof(HasChecklist));
        }

        private void Touch()
        {
            UpdatedAt = DateTime.Now;
            Store.MarkDirty();
        }

        protected override void OnChanged(string name)
        {
            switch (name)
            {
                case nameof(Kind):
                    Raise(nameof(IsTask)); Raise(nameof(IsNote)); Raise(nameof(KindName)); Raise(nameof(HasEstimate));
                    break;
                case nameof(LabelId):
                    RaiseLabel();
                    break;
                case nameof(Date):
                case nameof(Time):
                    Reminded = false;
                    Raise(nameof(DateText)); Raise(nameof(HasDate)); Raise(nameof(IsOverdue)); Raise(nameof(TimeText));
                    break;
                case nameof(EstimateMinutes):
                    Raise(nameof(EstimateText)); Raise(nameof(EstimateDisplay)); Raise(nameof(HasEstimate));
                    break;
                case nameof(Done):
                    DoneAt = _done ? DateTime.Now : null;
                    Raise(nameof(IsOverdue));
                    break;
                case nameof(Priority):
                    Raise(nameof(PriorityText)); Raise(nameof(PriorityBrush)); Raise(nameof(HasPriority));
                    break;
                case nameof(Body):
                    Raise(nameof(Preview)); Raise(nameof(HasPreview));
                    break;
                case nameof(Title):
                    Raise(nameof(DisplayTitle));
                    break;
            }
            Touch();
            if (StructuralProps.Contains(name)) StructureChanged?.Invoke();
        }

        public void RaiseLabel()
        {
            Raise(nameof(LabelBrush)); Raise(nameof(LabelName));
        }

        public void RaiseDates()
        {
            Raise(nameof(DateText)); Raise(nameof(IsOverdue));
        }

        // ---------- computed (not saved) ----------

        [JsonIgnore] public bool IsTask => Kind == ItemKind.Task;
        [JsonIgnore] public bool IsNote => Kind == ItemKind.Note;
        [JsonIgnore] public string KindName => IsTask ? T("Tarefa") : T("Nota");
        [JsonIgnore] public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? (IsTask ? T("Tarefa sem título") : T("Nota sem título")) : Title;

        [JsonIgnore] public SolidColorBrush LabelBrush => Store.LabelById(LabelId)?.Brush ?? Palette.BrushFrom("#8950D9");
        [JsonIgnore] public string LabelName => Store.LabelById(LabelId)?.Name ?? "";

        [JsonIgnore]
        public string EstimateText
        {
            get => Hours.Format(EstimateMinutes);
            set
            {
                if (Hours.TryParse(value, out int m)) EstimateMinutes = m;
                Raise(nameof(EstimateText));
            }
        }
        [JsonIgnore] public string EstimateDisplay => Hours.Format(EstimateMinutes);
        /// <summary>Time estimates belong to tasks only; notes never show or count one.</summary>
        [JsonIgnore] public bool HasEstimate => IsTask && EstimateMinutes > 0;

        [JsonIgnore]
        public string TimeText
        {
            get => Time;
            set
            {
                var n = Dates.NormalizeTime(value);
                if (n != null) Time = n;
                Raise(nameof(TimeText));
            }
        }

        [JsonIgnore] public bool HasDate => Date.HasValue;
        [JsonIgnore] public string DateText => Dates.Friendly(Date, Time);

        [JsonIgnore]
        public DateTime? DueMoment
        {
            get
            {
                if (!Date.HasValue) return null;
                if (Dates.TryParseTime(Time, out var t)) return Date.Value + t;
                return Date.Value.AddDays(1).AddSeconds(-1);
            }
        }

        [JsonIgnore] public bool IsOverdue => IsTask && !Done && DueMoment.HasValue && DueMoment.Value < DateTime.Now;

        [JsonIgnore] public bool HasChecklist => Checklist.Count > 0;
        [JsonIgnore] public string ChecklistText => $"{Checklist.Count(c => c.Done)}/{Checklist.Count}";

        [JsonIgnore]
        public string Preview
        {
            get
            {
                var b = LinkText.Friendly((Body ?? "").Trim()); // links read as "🔗 Page name" in cards
                return b.Length > 260 ? b.Substring(0, 260) + "…" : b;
            }
        }
        [JsonIgnore] public bool HasPreview => !string.IsNullOrWhiteSpace(Body);

        [JsonIgnore] public bool HasPriority => Priority > 0;
        [JsonIgnore] public string PriorityText => Priority switch { 1 => T("Baixa"), 2 => T("Média"), 3 => T("Alta"), _ => "" };
        [JsonIgnore]
        public SolidColorBrush PriorityBrush => Priority switch
        {
            1 => Palette.BrushFrom("#78B7FF"),
            2 => Palette.BrushFrom("#F2C36B"),
            3 => Palette.BrushFrom("#FF6F91"),
            _ => Palette.BrushFrom("#B9A8CB")
        };

        /// <summary>Date used for week/month grouping.</summary>
        [JsonIgnore] public DateTime? GroupDate => IsNote ? (Date ?? CreatedAt.Date) : Date;

        public bool Matches(string q)
        {
            if (string.IsNullOrWhiteSpace(q)) return true;
            var c = CultureInfo.GetCultureInfo("pt-BR").CompareInfo;
            const CompareOptions o = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
            bool Has(string s) => !string.IsNullOrEmpty(s) && c.IndexOf(s, q.Trim(), o) >= 0;
            return Has(Title) || Has(Body) || Has(LabelName) || Checklist.Any(x => Has(x.Text));
        }
    }

    public class WindowBounds
    {
        public double X { get; set; } = double.NaN;
        public double Y { get; set; } = double.NaN;
        public double W { get; set; }
        public double H { get; set; }
        public bool Maximized { get; set; }
    }

    public class Settings : Observable
    {
        private bool _startWithWindows;
        private bool _startMinimized = true;
        private bool _closeToTray = true;
        private bool _minimizeToTray;
        private bool _reminders = true;
        private bool _tasksWidget;

        public bool StartWithWindows { get => _startWithWindows; set { if (Set(ref _startWithWindows, value)) Store.MarkDirty(); } }
        public bool StartMinimized { get => _startMinimized; set { if (Set(ref _startMinimized, value)) Store.MarkDirty(); } }
        public bool CloseToTray { get => _closeToTray; set { if (Set(ref _closeToTray, value)) Store.MarkDirty(); } }
        public bool MinimizeToTray { get => _minimizeToTray; set { if (Set(ref _minimizeToTray, value)) Store.MarkDirty(); } }
        public bool Reminders { get => _reminders; set { if (Set(ref _reminders, value)) Store.MarkDirty(); } }
        public bool TasksWidget { get => _tasksWidget; set { if (Set(ref _tasksWidget, value)) Store.MarkDirty(); } }
        private bool _soundEffects = true;
        private double _soundVolume = 60;
        /// <summary>Sounds when a task is completed and when the last pending one is (SoundService).</summary>
        public bool SoundEffects { get => _soundEffects; set { if (Set(ref _soundEffects, value)) Store.MarkDirty(); } }
        /// <summary>0–100 (%).</summary>
        public double SoundVolume
        {
            get => _soundVolume;
            set { if (Set(ref _soundVolume, Math.Round(Math.Clamp(value, 0, 100)))) { Raise(nameof(SoundVolumeText)); Store.MarkDirty(); } }
        }
        [JsonIgnore] public string SoundVolumeText => $"{_soundVolume:0}%";
        private bool _confirmDelete = true;
        /// <summary>Ask "tem certeza?" before deleting a note/task.</summary>
        public bool ConfirmDelete { get => _confirmDelete; set { if (Set(ref _confirmDelete, value)) Store.MarkDirty(); } }
        /// <summary>Visual theme: "classic" (the dark UI, default) or "aero" (Frutiger Aero). Applied at startup.</summary>
        public string Theme { get; set; } = "classic";
        /// <summary>Interface language: "en" or "pt-BR". Applied at startup (changing it restarts the app).
        /// Missing in data files from 1.0.0, which keep pt-BR; a new install starts in English (Store.Load).</summary>
        public string Language { get; set; }
        private string _currency;
        /// <summary>Currency shown in Finanças: "BRL", "USD" or "EUR" (symbol only, amounts are not converted).
        /// Missing in data files from 1.0.0, which keep BRL; a new install follows the language (Store.Load).</summary>
        public string Currency { get => _currency; set { if (Set(ref _currency, value)) Store.MarkDirty(); } }
        public bool SidebarCollapsed { get; set; }
        public double SidebarWidth { get; set; } = 228;

        public string GroupMode { get; set; } = "week";
        public List<string> CollapsedGroups { get; set; } = new();
        public bool TrayHintShown { get; set; }
        public WindowBounds Main { get; set; } = new();
        public WindowBounds TasksWidgetBounds { get; set; } = new() { W = 300, H = 380 };
        public bool TasksWidgetTopmost { get; set; }
    }

    public class AppData
    {
        public int Version { get; set; } = Store.CurrentSchema;
        public ObservableCollection<Item> Items { get; set; } = new();
        public ObservableCollection<LabelTag> Labels { get; set; } = new();
        public Settings Settings { get; set; } = new();
        /// <summary>Finanças panel (schema 2). Missing in older files: created empty, then seeded.</summary>
        public HelmetDesk.Core.Finance.FinanceData Finance { get; set; } = new();
    }

    public class ItemGroup
    {
        public string Title { get; set; }
        public string Meta { get; set; }
        public List<Item> Items { get; set; }
        /// <summary>Identifies the group across refreshes ("view:title"), used to remember collapsed groups.</summary>
        public string Key { get; set; }
        public bool Collapsed { get; set; }
    }

    public static class Hours
    {
        private static readonly Regex TrailingEstimate = new(@"\s+(\d{1,3}:\d{2}|\d{1,3}h\d{0,2}|\d{1,4}\s?min)$", RegexOptions.IgnoreCase);

        /// <summary>Quick add: "Ler relatório 1h30" → "Ler relatório" and 90 minutes. Without an estimate at the end, the text comes back as is and minutes is 0.</summary>
        public static string SplitEstimate(string text, out int minutes)
        {
            minutes = 0;
            var m = TrailingEstimate.Match(text);
            return m.Success && TryParse(m.Groups[1].Value, out minutes) ? text.Substring(0, m.Index).Trim() : text;
        }

        public static string Format(int minutes)
        {
            if (minutes <= 0) return "";
            int h = minutes / 60, m = minutes % 60;
            if (h == 0) return $"{m}min";
            if (m == 0) return $"{h}h";
            return $"{h}h{m:00}";
        }

        /// <summary>Accepts "1:30", "1h30", "1h", "90min", "45m", "1,5" or "2" (hours).</summary>
        public static bool TryParse(string s, out int minutes)
        {
            minutes = 0;
            s = (s ?? "").Trim().ToLowerInvariant().Replace(" ", "");
            if (s.Length == 0) return true;
            var inv = CultureInfo.InvariantCulture;
            Match m;
            if ((m = Regex.Match(s, @"^(\d{1,3}):(\d{1,2})$")).Success)
            {
                minutes = int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value);
                return true;
            }
            if ((m = Regex.Match(s, @"^(\d{1,3})h(\d{1,2})?(m|min)?$")).Success)
            {
                minutes = int.Parse(m.Groups[1].Value) * 60 + (m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0);
                return true;
            }
            if ((m = Regex.Match(s, @"^(\d{1,4})(m|min)$")).Success)
            {
                minutes = int.Parse(m.Groups[1].Value);
                return true;
            }
            if ((m = Regex.Match(s, @"^(\d{1,3}(?:[.,]\d+)?)h?$")).Success)
            {
                minutes = (int)Math.Round(double.Parse(m.Groups[1].Value.Replace(',', '.'), inv) * 60);
                return true;
            }
            return false;
        }
    }

    public static class Dates
    {
        /// <summary>Culture of the interface language (dates, numbers). See HelmetDesk.Core.L.</summary>
        public static CultureInfo Culture => L.Culture;

        public static bool TryParseTime(string s, out TimeSpan t)
        {
            t = default;
            s = (s ?? "").Trim().ToLowerInvariant().Replace("h", ":");
            if (s.EndsWith(":")) s += "00";
            if (Regex.IsMatch(s, @"^\d{1,2}$")) s += ":00";
            var m = Regex.Match(s, @"^(\d{1,2}):(\d{2})$");
            if (!m.Success) return false;
            int h = int.Parse(m.Groups[1].Value), mi = int.Parse(m.Groups[2].Value);
            if (h > 23 || mi > 59) return false;
            t = new TimeSpan(h, mi, 0);
            return true;
        }

        public static string NormalizeTime(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            return TryParseTime(s, out var t) ? $"{t.Hours:00}:{t.Minutes:00}" : null;
        }

        public static bool TryParseDate(string s, out DateTime? d)
        {
            d = null;
            s = (s ?? "").Trim().ToLowerInvariant();
            if (s.Length == 0) return true;
            var today = DateTime.Today;
            if (s is "hoje" or "today") { d = today; return true; }
            if (s is "amanhã" or "amanha" or "tomorrow") { d = today.AddDays(1); return true; }
            if (s is "ontem" or "yesterday") { d = today.AddDays(-1); return true; }
            // day first in pt-BR (25/12), month first in English (12/25); ISO always works
            string[] formats = L.IsEnglish
                ? new[] { "M/d/yyyy", "M/d/yy", "M-d-yyyy", "yyyy-M-d" }
                : new[] { "d/M/yyyy", "d/M/yy", "d-M-yyyy", "d.M.yyyy", "yyyy-M-d" };
            if (DateTime.TryParseExact(s, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var r)) { d = r.Date; return true; }
            if (DateTime.TryParseExact(s, L.IsEnglish ? new[] { "M/d", "M-d" } : new[] { "d/M", "d-M", "d.M" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out r))
            {
                d = new DateTime(today.Year, r.Month, r.Day);
                return true;
            }
            return false;
        }

        public static string Friendly(DateTime? date, string time)
        {
            if (!date.HasValue) return "";
            var d = date.Value.Date;
            var today = DateTime.Today;
            string s;
            if (d == today) s = T("Hoje");
            else if (d == today.AddDays(1)) s = T("Amanhã");
            else if (d == today.AddDays(-1)) s = T("Ontem");
            else if (d.Year == today.Year) s = d.ToString(T("ddd, d MMM"), Culture).Replace(".", "");
            else s = d.ToString(T("d MMM yyyy"), Culture).Replace(".", "");
            if (!string.IsNullOrWhiteSpace(time)) s += " · " + time;
            return s;
        }

        public static DateTime StartOfWeek(DateTime d)
        {
            int diff = (7 + (d.DayOfWeek - DayOfWeek.Sunday)) % 7;
            return d.Date.AddDays(-diff);
        }

        public static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0], Culture) + s.Substring(1);

        public static string MonthTitle(DateTime d) => Cap(d.ToString(T("MMMM 'de' yyyy"), Culture));

        public static string WeekTitle(DateTime start)
        {
            var end = start.AddDays(6);
            var today = DateTime.Today;
            var cur = StartOfWeek(today);
            string prefix = start == cur ? T("Esta semana") : start == cur.AddDays(7) ? T("Próxima semana") : start == cur.AddDays(-7) ? T("Semana passada") : T("Semana");
            int wk = ISOWeek.GetWeekOfYear(start.AddDays(1));
            string M(DateTime x) => x.ToString("MMM", Culture).Replace(".", "");
            string range = L.IsEnglish
                ? (start.Month == end.Month ? $"{M(end)} {start.Day}–{end.Day}" : $"{M(start)} {start.Day} – {M(end)} {end.Day}")
                : (start.Month == end.Month ? $"{start.Day}–{end.Day} {M(end)}" : $"{start.Day} {M(start)} – {end.Day} {M(end)}");
            if (end.Year != today.Year) range += L.IsEnglish ? $", {end.Year}" : $" {end.Year}";
            return $"{prefix} · {range} ({T("S")}{wk})";
        }
    }
}
