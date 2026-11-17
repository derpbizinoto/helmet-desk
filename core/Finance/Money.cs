using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace HelmetDesk.Core.Finance
{
    /// <summary>
    /// A currency the Finanças tab can show. Changing it only changes the symbol: amounts are never converted.
    /// To add one, add a line to <see cref="Money.Currencies"/> (and its name to core/Localization/En.cs).
    /// </summary>
    public sealed class CurrencyInfo
    {
        public string Code { get; }
        /// <summary>Symbol in English ("$") and in pt-BR ("US$": the usual Brazilian way to tell the dollar from the real).</summary>
        public string SymbolEn { get; }
        public string SymbolPt { get; }
        /// <summary>Portuguese name (translated with T()).</summary>
        public string NamePt { get; }

        public CurrencyInfo(string code, string symbolEn, string symbolPt, string namePt)
        {
            Code = code; SymbolEn = symbolEn; SymbolPt = symbolPt; NamePt = namePt;
        }

        public string Symbol(bool english) => english ? SymbolEn : SymbolPt;
    }

    /// <summary>
    /// Money in the chosen currency and the interface language. Separators follow the language, not the currency:
    /// English "$1,234.56" / "R$1,234.56"; pt-BR "R$ 1.234,56" / "US$ 1.234,56".
    /// </summary>
    public static class Money
    {
        public static readonly CurrencyInfo Brl = new("BRL", "R$", "R$", "Real");
        public static readonly CurrencyInfo Usd = new("USD", "$", "US$", "Dólar");
        public static readonly CurrencyInfo Eur = new("EUR", "€", "€", "Euro");

        /// <summary>Every currency offered in Configurações, in display order.</summary>
        public static readonly IReadOnlyList<CurrencyInfo> Currencies = new[] { Brl, Usd, Eur };

        /// <summary>Currency of this run (Configurações → Finanças → Moeda).</summary>
        public static CurrencyInfo Current { get; private set; } = Brl;

        public static CurrencyInfo Find(string code) =>
            Currencies.FirstOrDefault(c => string.Equals(c.Code, code, StringComparison.OrdinalIgnoreCase));

        /// <summary>Sets the currency by code ("BRL", "USD", "EUR"); unknown codes fall back to the real.</summary>
        public static void SetCurrency(string code) => Current = Find(code) ?? Brl;

        /// <summary>Default for a new install: dollar in English, real in pt-BR.</summary>
        public static string DefaultFor(string lang) => L.Normalize(lang) == L.English ? Usd.Code : Brl.Code;

        /// <summary>The symbol of the current currency in the current language ("$", "US$", "R$", "€").</summary>
        public static string Symbol => Current.Symbol(L.IsEnglish);

        public static string Format(decimal value) => Format(value, Current, L.IsEnglish);

        public static string Format(decimal value, CurrencyInfo currency, bool english)
        {
            var number = Math.Abs(value).ToString("N2", Culture(english));
            var s = Attach(currency.Symbol(english), number, english);
            return value < 0 ? "-" + s : s;
        }

        /// <summary>Signed, for income/expense rows: "+R$ 10,00" / "-$10.00".</summary>
        public static string Signed(decimal value) => value > 0 ? "+" + Format(value) : Format(value);

        /// <summary>Number without the currency symbol ("1.234,56" / "1,234.56"), for text boxes.</summary>
        public static string Plain(decimal value) => value.ToString("N2", Culture(L.IsEnglish));

        /// <summary>Short money for chart axes: "R$ 850", "R$ 1,2 mil", "R$ 3,5 mi" / "$850", "$1.2K", "$3.5M".</summary>
        public static string Compact(decimal value) => Compact(value, Current, L.IsEnglish);

        public static string Compact(decimal value, CurrencyInfo currency, bool english)
        {
            string sign = value < 0 ? "-" : "";
            var v = Math.Abs(value);
            var c = Culture(english);
            string number =
                v >= 1_000_000 ? (v / 1_000_000).ToString("0.#", c) + (english ? "M" : " mi") :
                v >= 1_000 ? (v / 1_000).ToString("0.#", c) + (english ? "K" : " mil") :
                v.ToString("0", c);
            return sign + Attach(currency.Symbol(english), number, english);
        }

        public static bool TryParse(string text, out decimal value) => TryParse(text, L.IsEnglish, out value);

        /// <summary>
        /// Reads a typed amount, with or without a currency symbol. pt-BR: "1234,56", "1.234,56", "R$ 50", "12.5";
        /// English: "1234.56", "1,234.56", "$50", "12,5".
        /// </summary>
        public static bool TryParse(string text, bool english, out decimal value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var s = text.Trim();
            foreach (var sym in Currencies.SelectMany(c => new[] { c.SymbolPt, c.SymbolEn }).Distinct().OrderByDescending(x => x.Length))
                s = s.Replace(sym, "", StringComparison.OrdinalIgnoreCase);
            s = s.Replace(" ", "").Replace(" ", "");
            bool negative = false;
            if (s.StartsWith("-")) { negative = true; s = s.Substring(1); }
            else if (s.StartsWith("+")) s = s.Substring(1);
            if (s.Length == 0) return false;

            if (english)
            {
                if (s.Contains('.'))
                {
                    // English: commas group thousands, the dot is the decimal point
                    if (s.IndexOf('.') != s.LastIndexOf('.')) return false;
                    s = s.Replace(",", "");
                }
                else if (s.Contains(','))
                {
                    if (ThousandsComma.IsMatch(s)) s = s.Replace(",", "");   // "1,234" = one thousand two hundred thirty-four
                    else if (s.IndexOf(',') == s.LastIndexOf(',')) s = s.Replace(',', '.'); // "12,5": a decimal comma
                    else return false;
                }
            }
            else if (s.Contains(','))
            {
                // pt-BR: dots group thousands, the comma is the decimal separator
                if (s.IndexOf(',') != s.LastIndexOf(',')) return false;
                s = s.Replace(".", "").Replace(',', '.');
            }
            else if (s.Contains('.') && ThousandsDot.IsMatch(s))
                s = s.Replace(".", ""); // "1.234" = one thousand two hundred thirty-four
            // else a single dot is a decimal point ("12.5")

            foreach (var ch in s) if (!(char.IsDigit(ch) || ch == '.')) return false;
            if (!decimal.TryParse(s, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d)) return false;
            value = Math.Round(negative ? -d : d, 2, MidpointRounding.AwayFromZero);
            return true;
        }

        private static readonly Regex ThousandsDot = new(@"^\d{1,3}(\.\d{3})+$", RegexOptions.CultureInvariant);
        private static readonly Regex ThousandsComma = new(@"^\d{1,3}(,\d{3})+$", RegexOptions.CultureInvariant);
        private static readonly CultureInfo EnNumbers = CultureInfo.GetCultureInfo("en-US");
        private static readonly CultureInfo PtNumbers = CultureInfo.GetCultureInfo("pt-BR");

        private static CultureInfo Culture(bool english) => english ? EnNumbers : PtNumbers;

        /// <summary>English writes the symbol right before the number ("$10.00"); pt-BR with a space ("R$ 10,00").</summary>
        private static string Attach(string symbol, string number, bool english) => english ? symbol + number : symbol + " " + number;
    }
}
