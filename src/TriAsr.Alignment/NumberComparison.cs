using System.Globalization;

namespace TriAsr.Alignment;

internal static class NumberComparison
{
    private static readonly string[] EnglishUnits = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];
    private static readonly string[] GermanUnits = ["null", "eins", "zwei", "drei", "vier", "fünf", "sechs", "sieben", "acht", "neun", "zehn", "elf", "zwölf", "dreizehn", "vierzehn", "fünfzehn", "sechzehn", "siebzehn", "achtzehn", "neunzehn"];
    private static readonly string[] HungarianUnits = ["nulla", "egy", "kettő", "három", "négy", "öt", "hat", "hét", "nyolc", "kilenc", "tíz"];
    private static readonly string[] EnglishTens = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];
    private static readonly string[] GermanTens = ["", "", "zwanzig", "dreißig", "vierzig", "fünfzig", "sechzig", "siebzig", "achtzig", "neunzig"];
    private static readonly string[] HungarianTens = ["", "", "húsz", "harminc", "negyven", "ötven", "hatvan", "hetven", "nyolcvan", "kilencven"];
    private static string Digits(int number) => number.ToString(CultureInfo.InvariantCulture);
    public static bool TryEnglishPair(string first, string second, out string number)
    {
        var tens = Array.IndexOf(EnglishTens, first.ToLowerInvariant());
        var unit = Array.IndexOf(EnglishUnits, second.ToLowerInvariant());
        number = tens >= 2 && unit is > 0 and < 10 ? Digits(tens * 10 + unit) : "";
        return number.Length > 0;
    }
    /// <param name="language">A language code, or two joined with <c>+</c> for a recording in two languages: a number word of either counts.</param>
    public static string Normalize(string word, string language)
    {
        if (language.Contains('+'))
        {
            foreach (var code in language.Split('+'))
                if (Normalize(word, code) is var number && number != word) return number;
            return word;
        }
        var units = language switch { "en" => EnglishUnits, "de" => GermanUnits, "hu" => HungarianUnits, _ => [] };
        var tens = language switch { "en" => EnglishTens, "de" => GermanTens, "hu" => HungarianTens, _ => [] };
        var direct = Array.IndexOf(units, word);
        if (direct >= 0) return Digits(direct);
        for (var ten = 2; ten < tens.Length; ten++)
        {
            if (word == tens[ten]) return Digits(ten * 10);
            for (var unit = 1; unit < 10; unit++)
            {
                var compound = language switch
                {
                    "en" => tens[ten] + units[unit],
                    "de" => (unit == 1 ? "ein" : units[unit]) + "und" + tens[ten],
                    "hu" => (ten == 2 ? "huszon" : tens[ten]) + (unit == 2 ? "kettő" : units[unit]),
                    _ => ""
                };
                if (word == compound || language == "hu" && unit == 2 && word == (ten == 2 ? "huszon" : tens[ten]) + "két") return Digits(ten * 10 + unit);
            }
        }
        if (language == "hu")
            for (var unit = 1; unit < 10; unit++)
                if (word == "tizen" + units[unit] || unit == 2 && word == "tizenkét") return Digits(10 + unit);
        return word;
    }
}
