using System;
using System.Collections.Generic;
using System.Globalization;

namespace HelmetDesk.Core
{
    /// <summary>
    /// Interface language. The source code is written in Brazilian Portuguese and every user-facing text goes through
    /// <see cref="T"/> / <see cref="TF"/>: in pt-BR the text is returned as is, in English it is looked up in
    /// <see cref="En"/> (core/Localization/En.cs), keyed by the Portuguese text. A missing entry falls back to the
    /// Portuguese text and is logged once. The language is chosen once at startup; changing it restarts the app.
    /// Tests (LocalizationTests) check that every T("…") in the sources has an English entry.
    /// </summary>
    public static class L
    {
        public const string English = "en";
        public const string Portuguese = "pt-BR";

        private static readonly CultureInfo EnCulture = MakeEnglish();
        private static readonly CultureInfo PtCulture = CultureInfo.GetCultureInfo("pt-BR");

        /// <summary>Language of this run: <see cref="English"/> (default) or <see cref="Portuguese"/>.</summary>
        public static string Lang { get; private set; } = English;
        public static bool IsEnglish => Lang == English;
        /// <summary>Culture for dates and numbers in the current language.</summary>
        public static CultureInfo Culture => IsEnglish ? EnCulture : PtCulture;

        /// <summary>Called with each text that has no English entry (the app logs it).</summary>
        public static Action<string> Missing;
        private static readonly HashSet<string> _reported = new();

        public static string Normalize(string lang) =>
            lang != null && lang.StartsWith("pt", StringComparison.OrdinalIgnoreCase) ? Portuguese : English;

        public static void Set(string lang) => Lang = Normalize(lang);

        public static string DisplayName(string lang) => Normalize(lang) == Portuguese ? "Português (Brasil)" : "English";

        /// <summary>The text in the current language (<paramref name="pt"/> is the Portuguese original).</summary>
        public static string T(string pt)
        {
            if (!IsEnglish || string.IsNullOrEmpty(pt)) return pt;
            if (En.Map.TryGetValue(pt, out var en)) return en;
            lock (_reported) if (_reported.Add(pt)) Missing?.Invoke(pt);
            return pt;
        }

        /// <summary><see cref="T"/> then string.Format with the current culture: TF("{0} tarefas", n).</summary>
        public static string TF(string pt, params object[] args) => string.Format(Culture, T(pt), args);

        /// <summary>Singular or plural by <paramref name="n"/>: TN(n, "{0} tarefa", "{0} tarefas").</summary>
        public static string TN(long n, string one, string many) => TF(n == 1 ? one : many, n);

        private static CultureInfo MakeEnglish()
        {
            var c = (CultureInfo)CultureInfo.GetCultureInfo("en-US").Clone();
            // the app shows times as 24h everywhere ("14:30"), as it always did
            c.DateTimeFormat.ShortTimePattern = "HH:mm";
            c.DateTimeFormat.LongTimePattern = "HH:mm:ss";
            return CultureInfo.ReadOnly(c);
        }
    }
}
