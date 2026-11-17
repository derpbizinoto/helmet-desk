using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HelmetDesk
{
    /// <summary>
    /// Finds natural-language dates inside free text (Todoist-style), in the interface language.
    /// Portuguese: "hoje", "amanhã", "depois de amanhã", "semana que vem", "daqui 3 dias", "na sexta",
    /// "15/11", "15/11/26", "15 de novembro", "dia 20", "fim do mês"…
    /// English: "today", "tomorrow", "day after tomorrow", "next week", "in 3 days", "on friday",
    /// "11/15", "11/15/26", "Nov 15", "15th of November", "the 20th", "end of the month"…
    /// Dates without a year always use the current year (read at parse time, so it follows New Year).
    /// </summary>
    public static class SmartDate
    {
        public sealed class Hit
        {
            public int Index;
            public int Length;
            public DateTime Date;
            public TimeSpan Time;
            public string Text;   // the original words that were recognised
        }

        // ---------- times of day ----------
        // "13:00", "13h", "13h30", "13h30m", "13h30min", "13 horas", "às 9", "pelas 8", "3 da tarde", "meio-dia", "meia-noite"
        private static readonly Regex TimeRx = new(B +
            @"(?<p>(?:as|a partir das|ate as|pelas|por volta das|ao)\s+)?" +
            @"(?:(?<noon>meio[- ]dia)|(?<midnight>meia[- ]noite)|" +
            @"(?<h>\d{1,2})(?:(?<colon>:)(?<m>\d{2})|(?<hh>h)(?:(?<m>\d{2})(?:min|m)?)?|\s*(?<hh>horas?))?" +
            @"(?:\s+da\s+(?<per>manha|tarde|noite|madrugada))?)" + E,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // English: "3pm", "3:30 pm", "15:00", "at 9", "around 8", "9 in the morning", "8 at night", "noon", "midnight", "5 o'clock"
        private static readonly Regex TimeRxEn = new(B +
            @"(?<p>(?:at|around|by|from|until|till)\s+)?" +
            @"(?:(?<noon>noon|midday)|(?<midnight>midnight)|" +
            @"(?<h>\d{1,2})(?:(?<colon>:)(?<m>\d{2}))?(?:\s*(?<ampm>am|pm|a\.m\.|p\.m\.))?(?:\s*(?<oc>o'?clock))?" +
            @"(?:\s+in\s+the\s+(?<per>morning|afternoon|evening)|\s+at\s+(?<per>night)|\s+(?<per>tonight))?)" +
            @"(?![\p{L}\d])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Last complete time of day in the text (same "finished" rule as <see cref="Find"/>).</summary>
        /// <remarks>
        /// Short hour forms without "às" ("1h30", "2h") stay text: in the quick-add box they are the task's estimate.
        /// So a bare "Nh" / "N:MM" is a time when the hour has two digits or is 6 or later.
        /// </remarks>
        public static Hit FindTime(string text, bool finished, ICollection<string> ignore = null)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string norm = Normalize(text);
            Hit best = null;
            bool en = L.IsEnglish;
            for (var m = (en ? TimeRxEn : TimeRx).Match(norm); m.Success; m = m.NextMatch())
            {
                int end = m.Index + m.Length;
                if (!finished && (end >= norm.Length || !IsBreak(norm[end]))) continue;
                var t = en ? EvalTimeEn(m, text) : EvalTime(m, text);
                if (t == null) continue;
                string original = text.Substring(m.Index, m.Length);
                if (ignore != null && ignore.Contains(Normalize(original).Trim())) continue;
                if (best == null || end > best.Index + best.Length)
                    best = new Hit { Index = m.Index, Length = m.Length, Time = t.Value, Text = original };
            }
            return best;
        }

        private static TimeSpan? EvalTime(Match m, string original)
        {
            var p = m.Groups["p"];
            string prefix = p.Success ? original.Substring(p.Index, p.Length).Trim().ToLowerInvariant() : "";
            if (m.Groups["noon"].Success) return new TimeSpan(12, 0, 0);
            if (m.Groups["midnight"].Success) return TimeSpan.Zero;
            if (prefix == "ao") return null;                      // "ao" only goes with meio-dia
            if (!m.Groups["h"].Success) return null;
            string hs = m.Groups["h"].Value;
            int h = int.Parse(hs), mi = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value) : 0;
            bool colon = m.Groups["colon"].Success, hourWord = m.Groups["hh"].Success, per = m.Groups["per"].Success;
            // plain "as" may just be the article ("comprar as 3 camisas"); "às", "pelas", "a partir das"… are clearly times
            bool strong = p.Success && prefix != "as";
            if (!colon && !hourWord && !per && !strong) return null;          // a bare number needs "às" or "da tarde"
            if (!p.Success && !per && (colon || hourWord) && hs.Length < 2 && h < 6) return null; // "1h30" = estimate
            if (per)
            {
                string w = m.Groups["per"].Value;
                if ((w == "tarde" || w == "noite") && h < 12) h += 12;
                if (w == "manha" && h == 12) h = 0;
            }
            if (h > 23 || mi > 59) return null;
            return new TimeSpan(h, mi, 0);
        }

        private static TimeSpan? EvalTimeEn(Match m, string original)
        {
            if (m.Groups["noon"].Success) return new TimeSpan(12, 0, 0);
            if (m.Groups["midnight"].Success) return TimeSpan.Zero;
            if (!m.Groups["h"].Success) return null;
            string hs = m.Groups["h"].Value;
            int h = int.Parse(hs), mi = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value) : 0;
            bool p = m.Groups["p"].Success, colon = m.Groups["colon"].Success, ampm = m.Groups["ampm"].Success,
                 oclock = m.Groups["oc"].Success, per = m.Groups["per"].Success;
            if (!p && !colon && !ampm && !oclock && !per) return null;            // a bare number is not a time
            if (!p && !ampm && !per && colon && hs.Length < 2 && h < 6) return null; // "1:30" = estimate
            if (ampm)
            {
                if (h < 1 || h > 12) return null;
                bool pm = m.Groups["ampm"].Value.StartsWith("p");
                if (pm && h < 12) h += 12;
                if (!pm && h == 12) h = 0;
            }
            else if (per)
            {
                string w = m.Groups["per"].Value;
                if (w != "morning" && h < 12) h += 12;
                if (w == "morning" && h == 12) h = 0;
            }
            if (h > 23 || mi > 59) return null;
            return new TimeSpan(h, mi, 0);
        }

        private const string B = @"(?<![\p{L}\d])";        // start of a word
        private const string E = @"(?![\p{L}\d])";         // end of a word
        private const string Num = @"(\d{1,3}|um|uma|dois|duas|tres|quatro|cinco|seis|sete|oito|nove|dez|onze|doze|quinze|vinte|trinta)";
        private const string Months = "janeiro|fevereiro|marco|abril|maio|junho|julho|agosto|setembro|outubro|novembro|dezembro";
        private const string Weekdays = @"(segunda|terca|quarta|quinta|sexta)(?:[- ]feira)?|sabado|domingo";
        // little words that belong with the date and go away with it ("até sexta", "para amanhã", "na semana que vem")
        private const string Lead = @"(?:(?:para|pra|ate|no|na|nesta|neste|nessa|nesse|esta|este|essa|esse|em|dia|a partir de)\s+)?";

        // order matters: longer phrases first so "depois de amanhã" wins over "amanhã"
        private static readonly (Regex rx, Func<Match, DateTime, DateTime?> eval)[] Rules =
        {
            (R(@"(\d{1,2})/(\d{1,2})(?:/(\d{4}|\d{2}))?(?![/\d])"), (m, t) => Explicit(m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value, t)),
            (R(@"(\d{1,2})\s+de\s+(" + Months + @")(?:\s+de\s+(\d{4}))?"), (m, t) => Explicit(m.Groups[1].Value, (Array.IndexOf(Months.Split('|'), m.Groups[2].Value) + 1).ToString(), m.Groups[3].Value, t)),
            (R(@"dia\s+(\d{1,2})(?![/\d])"), (m, t) => DayOfMonth(int.Parse(m.Groups[1].Value), t)),
            (R(@"depois\s+de\s+amanha"), (m, t) => t.AddDays(2)),
            (R(@"amanha|proximo\s+dia|dia\s+seguinte"), (m, t) => t.AddDays(1)),
            (R(@"hoje|hj"), (m, t) => t),
            (R(@"ontem"), (m, t) => t.AddDays(-1)),
            (R(@"(?:daqui\s+a|daqui|dentro\s+de|em)\s+" + Num + @"\s+(dias?|semanas?|mes|meses|anos?)"), (m, t) => Offset(m.Groups[1].Value, m.Groups[2].Value, t)),
            (R(@"(?:(?:a\s+)?semana\s+que\s+vem|(?:a\s+)?proxima\s+semana|semana\s+seguinte)"), (m, t) => NextWeekday(t, DayOfWeek.Monday)),
            (R(@"(?:o\s+)?(?:mes\s+que\s+vem|proximo\s+mes)"), (m, t) => t.AddMonths(1)),
            (R(@"(?:o\s+)?(?:ano\s+que\s+vem|proximo\s+ano)"), (m, t) => t.AddYears(1)),
            (R(@"(?:o\s+)?(?:fim|final)\s+de\s+semana|fds"), (m, t) => t.DayOfWeek == DayOfWeek.Saturday ? t : NextWeekday(t, DayOfWeek.Saturday)),
            (R(@"(?:o\s+)?(?:fim|final)\s+do\s+mes"), (m, t) => new DateTime(t.Year, t.Month, DateTime.DaysInMonth(t.Year, t.Month))),
            (new Regex(B + @"((?:para|pra|ate|no|na|nesta|neste|nessa|nesse|esta|este|essa|esse|proxima|proximo)\s+)?(?:(?:proxima|proximo)\s+)?(" + Weekdays + ")" + E, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                (m, t) =>
                {
                    // a bare "segunda"/"quinta"… is too often an ordinary word: needs "-feira" or a little word before it
                    var word = m.Groups[2].Value;
                    bool workday = !word.StartsWith("sabado") && !word.StartsWith("domingo");
                    if (workday && !m.Groups[1].Success && !word.Contains("feira")) return null;
                    return NextWeekday(t, Day(word));
                }),
        };

        private static Regex R(string core) =>
            new Regex(B + Lead + "(?:" + core + ")" + E, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        // ---------- English ----------
        private const string NumEn = @"(\d{1,3}|a|an|one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve|fifteen|twenty|thirty)";
        private const string MonthsEn = "january|february|march|april|may|june|july|august|september|october|november|december|jan|feb|mar|apr|jun|jul|aug|sept|sep|oct|nov|dec";
        private const string WeekdaysEn = "monday|tuesday|wednesday|thursday|friday|saturday|sunday|mon|tue|tues|wed|thu|thur|thurs|fri|sat|sun";
        private const string Ord = "(?:st|nd|rd|th)?";
        // little words that belong with the date and go away with it ("by friday", "due tomorrow", "on the 20th")
        private const string LeadEn = @"(?:(?:on|by|until|till|due|for|from)\s+)?";

        private static readonly (Regex rx, Func<Match, DateTime, DateTime?> eval)[] RulesEn =
        {
            // month first, as written in English: 11/15, 11/15/26
            (REn(@"(\d{1,2})/(\d{1,2})(?:/(\d{4}|\d{2}))?(?![/\d])"), (m, t) => Explicit(m.Groups[2].Value, m.Groups[1].Value, m.Groups[3].Value, t)),
            (REn(@"(?:the\s+)?(" + MonthsEn + @")\.?\s+(\d{1,2})" + Ord + @"(?:,?\s+(\d{4}))?"), (m, t) => Explicit(m.Groups[2].Value, MonthEn(m.Groups[1].Value), m.Groups[3].Value, t)),
            (REn(@"(?:the\s+)?(\d{1,2})" + Ord + @"\s+(?:of\s+)?(" + MonthsEn + @")\.?(?:,?\s+(\d{4}))?"), (m, t) => Explicit(m.Groups[1].Value, MonthEn(m.Groups[2].Value), m.Groups[3].Value, t)),
            (REn(@"the\s+(\d{1,2})(?:st|nd|rd|th)(?![/\d])"), (m, t) => DayOfMonth(int.Parse(m.Groups[1].Value), t)),
            (REn(@"(?:the\s+)?day\s+after\s+tomorrow"), (m, t) => t.AddDays(2)),
            (REn(@"tomorrow|tmrw|tmr|the\s+next\s+day"), (m, t) => t.AddDays(1)),
            (REn(@"today|tonight|tdy"), (m, t) => t),
            (REn(@"yesterday"), (m, t) => t.AddDays(-1)),
            (REn(@"in\s+" + NumEn + @"\s+(days?|weeks?|months?|years?)"), (m, t) => OffsetEn(m.Groups[1].Value, m.Groups[2].Value, t)),
            (REn(@"next\s+week|the\s+following\s+week"), (m, t) => NextWeekday(t, DayOfWeek.Monday)),
            (REn(@"next\s+month"), (m, t) => t.AddMonths(1)),
            (REn(@"next\s+year"), (m, t) => t.AddYears(1)),
            (REn(@"(?:this\s+|the\s+|next\s+)?weekend"), (m, t) => t.DayOfWeek == DayOfWeek.Saturday ? t : NextWeekday(t, DayOfWeek.Saturday)),
            (REn(@"(?:the\s+)?end\s+of\s+(?:the\s+)?month"), (m, t) => new DateTime(t.Year, t.Month, DateTime.DaysInMonth(t.Year, t.Month))),
            (new Regex(B + @"((?:on|by|until|till|due|for|this|next|from)\s+)?(?:next\s+)?(" + WeekdaysEn + ")" + E, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                (m, t) =>
                {
                    // "sat", "sun", "wed"… alone are too often other words: short names need a little word before them
                    var word = m.Groups[2].Value;
                    if (word.Length <= 5 && !word.EndsWith("day") && !m.Groups[1].Success) return null;
                    return NextWeekday(t, DayEn(word));
                }),
        };

        private static Regex REn(string core) =>
            new Regex(B + LeadEn + "(?:" + core + ")" + E, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static string MonthEn(string name) => (Array.IndexOf(
            new[] { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" }, name.Substring(0, 3)) + 1).ToString();

        private static DayOfWeek DayEn(string w) => w.Substring(0, 3) switch
        {
            "mon" => DayOfWeek.Monday, "tue" => DayOfWeek.Tuesday, "wed" => DayOfWeek.Wednesday,
            "thu" => DayOfWeek.Thursday, "fri" => DayOfWeek.Friday, "sat" => DayOfWeek.Saturday, _ => DayOfWeek.Sunday
        };

        private static DateTime? OffsetEn(string n, string unit, DateTime today)
        {
            int k = n switch
            {
                "a" or "an" or "one" => 1, "two" => 2, "three" => 3, "four" => 4, "five" => 5, "six" => 6, "seven" => 7,
                "eight" => 8, "nine" => 9, "ten" => 10, "eleven" => 11, "twelve" => 12, "fifteen" => 15, "twenty" => 20,
                "thirty" => 30, _ => int.Parse(n)
            };
            if (k > 400) return null;
            return unit switch
            {
                var u when u.StartsWith("day") => today.AddDays(k),
                var u when u.StartsWith("week") => today.AddDays(7 * k),
                var u when u.StartsWith("month") => today.AddMonths(k),
                _ => today.AddYears(k)
            };
        }

        /// <summary>
        /// Last complete date phrase in <paramref name="text"/>. With <paramref name="finished"/> = false
        /// a phrase only counts once something (a space, punctuation) follows it, so the user has stopped writing it.
        /// </summary>
        public static Hit Find(string text, DateTime today, bool finished, ICollection<string> ignore = null)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string norm = Normalize(text);
            Hit best = null;
            foreach (var (rx, eval) in L.IsEnglish ? RulesEn : Rules)
            {
                for (var m = rx.Match(norm); m.Success; m = m.NextMatch())
                {
                    int end = m.Index + m.Length;
                    if (!finished && (end >= norm.Length || !IsBreak(norm[end]))) continue;
                    var d = eval(m, today.Date);
                    if (d == null) continue;
                    string original = text.Substring(m.Index, m.Length);
                    if (ignore != null && ignore.Contains(Normalize(original).Trim())) continue;
                    // the phrase that ends last wins; ending at the same spot, the longest ("depois de amanhã" over "amanhã")
                    int bestEnd = best == null ? -1 : best.Index + best.Length;
                    if (end > bestEnd || (end == bestEnd && m.Length > best.Length))
                        best = new Hit { Index = m.Index, Length = m.Length, Date = d.Value.Date, Text = original };
                }
            }
            return best;
        }

        public static string Normalize(string s)
        {
            // one char in, one char out, so match positions line up with the original text
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                var d = c.ToString().Normalize(NormalizationForm.FormD);
                sb.Append(char.ToLowerInvariant(d[0]));
            }
            return sb.ToString();
        }

        /// <summary>Removes a recognised phrase and tidies the spaces it leaves behind.</summary>
        public static string Remove(string text, int index, int length, out int caret)
        {
            string before = text.Substring(0, index).TrimEnd(' ');
            string after = text.Substring(index + length).TrimStart(' ');
            string glue = before.Length > 0 && after.Length > 0 && !char.IsPunctuation(after[0]) ? " " : "";
            if (before.Length > 0 && after.Length == 0) glue = " "; // keep typing after the chip
            caret = before.Length + glue.Length;
            return before + glue + after;
        }

        /// <summary>Short chip text: "Hoje", "Amanhã", "sex, 14 nov", "3 jan 2027".</summary>
        public static string Label(DateTime d) => Dates.Friendly(d, null);

        private static bool IsBreak(char c) => char.IsWhiteSpace(c) || c is ',' or '.' or ';' or '!' or '?' or ')' or ':';

        private static DateTime? Explicit(string day, string month, string year, DateTime today)
        {
            int dd = int.Parse(day), mm = int.Parse(month);
            int yy = today.Year; // no year written: the current one
            if (!string.IsNullOrEmpty(year)) { yy = int.Parse(year); if (yy < 100) yy += 2000; }
            if (mm < 1 || mm > 12 || yy < 1900 || yy > 2200 || dd < 1 || dd > DateTime.DaysInMonth(yy, mm)) return null;
            return new DateTime(yy, mm, dd);
        }

        private static DateTime? DayOfMonth(int day, DateTime today)
        {
            if (day < 1 || day > 31) return null;
            var m = new DateTime(today.Year, today.Month, 1);
            for (int i = 0; i < 13; i++, m = m.AddMonths(1))
            {
                if (day > DateTime.DaysInMonth(m.Year, m.Month)) continue;
                var d = new DateTime(m.Year, m.Month, day);
                if (d >= today) return d;
            }
            return null;
        }

        private static DateTime? Offset(string n, string unit, DateTime today)
        {
            int k = n switch
            {
                "um" or "uma" => 1, "dois" or "duas" => 2, "tres" => 3, "quatro" => 4, "cinco" => 5, "seis" => 6,
                "sete" => 7, "oito" => 8, "nove" => 9, "dez" => 10, "onze" => 11, "doze" => 12, "quinze" => 15,
                "vinte" => 20, "trinta" => 30, _ => int.Parse(n)
            };
            if (k > 400) return null;
            return unit switch
            {
                var u when u.StartsWith("dia") => today.AddDays(k),
                var u when u.StartsWith("semana") => today.AddDays(7 * k),
                var u when u.StartsWith("mes") => today.AddMonths(k),
                _ => today.AddYears(k)
            };
        }

        private static DateTime NextWeekday(DateTime today, DayOfWeek dow)
        {
            int diff = ((int)dow - (int)today.DayOfWeek + 7) % 7;
            return today.AddDays(diff == 0 ? 7 : diff);
        }

        private static DayOfWeek Day(string w) => w.Substring(0, 3) switch
        {
            "seg" => DayOfWeek.Monday, "ter" => DayOfWeek.Tuesday, "qua" => DayOfWeek.Wednesday,
            "qui" => DayOfWeek.Thursday, "sex" => DayOfWeek.Friday, "sab" => DayOfWeek.Saturday, _ => DayOfWeek.Sunday
        };
    }
}
