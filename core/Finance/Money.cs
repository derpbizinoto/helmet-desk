using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace HelmetDesk.Core.Finance
{
    /// <summary>pt-BR money: "R$ 1.234,56" out; "1234,56", "1.234,56", "R$ 50", "12.5" in.</summary>
    public static class Money
    {
        public static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
        private static readonly Regex Thousands = new(@"^\d{1,3}(\.\d{3})+$", RegexOptions.CultureInvariant);

        public static string Format(decimal value)
        {
            var s = "R$ " + Math.Abs(value).ToString("N2", Pt);
            return value < 0 ? "-" + s : s;
        }

        /// <summary>Signed, for income/expense rows: "+R$ 10,00" / "-R$ 10,00".</summary>
        public static string Signed(decimal value) => value > 0 ? "+" + Format(value) : Format(value);

        /// <summary>Number without the currency symbol ("1.234,56"), for text boxes.</summary>
        public static string Plain(decimal value) => value.ToString("N2", Pt);

        public static bool TryParse(string text, out decimal value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var s = text.Trim().Replace("R$", "", StringComparison.OrdinalIgnoreCase).Replace(" ", "").Replace(" ", "");
            bool negative = false;
            if (s.StartsWith("-")) { negative = true; s = s.Substring(1); }
            else if (s.StartsWith("+")) s = s.Substring(1);
            if (s.Length == 0) return false;

            if (s.Contains(','))
            {
                // pt-BR: dots group thousands, the comma is the decimal separator
                if (s.IndexOf(',') != s.LastIndexOf(',')) return false;
                s = s.Replace(".", "").Replace(',', '.');
            }
            else if (s.Contains('.') && Thousands.IsMatch(s))
                s = s.Replace(".", ""); // "1.234" = one thousand two hundred thirty-four
            // else a single dot is a decimal point ("12.5")

            foreach (var ch in s) if (!(char.IsDigit(ch) || ch == '.')) return false;
            if (!decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d)) return false;
            value = Math.Round(negative ? -d : d, 2, MidpointRounding.AwayFromZero);
            return true;
        }
    }
}
