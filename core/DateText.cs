using System;

namespace HelmetDesk.Core
{
    /// <summary>Date texts shared by the app and the view models in core (the app's Dates class uses these).</summary>
    public static class DateText
    {
        /// <summary>First letter in upper case, in the interface culture.</summary>
        public static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0], L.Culture) + s.Substring(1);

        /// <summary>"Novembro de 2026" / "November 2026".</summary>
        public static string MonthTitle(DateTime d) => Cap(d.ToString(T("MMMM 'de' yyyy"), L.Culture));
    }
}
