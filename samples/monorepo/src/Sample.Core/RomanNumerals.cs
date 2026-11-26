using System.Text;

namespace Sample.Core;

/// <summary>Roman numerals from 1 to 3999.</summary>
public static class RomanNumerals
{
    private static readonly (int Value, string Text)[] Table =
        [(1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")];

    public static string To(int number)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, 3999);
        var sb = new StringBuilder();
        foreach (var (value, text) in Table)
        {
            while (number >= value)
            {
                sb.Append(text);
                number -= value;
            }
        }

        return sb.ToString();
    }

    public static int From(string roman)
    {
        var total = 0;
        var i = 0;
        foreach (var (value, text) in Table)
        {
            while (string.CompareOrdinal(roman, i, text, 0, text.Length) == 0)
            {
                total += value;
                i += text.Length;
            }
        }

        return i == roman.Length && total is > 0 and < 4000 && To(total) == roman ? total : throw new FormatException($"'{roman}' is not a Roman numeral.");
    }
}
