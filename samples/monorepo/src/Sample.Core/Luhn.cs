namespace Sample.Core;

/// <summary>The Luhn mod-10 check digit (card numbers, IMEI and similar identifiers).</summary>
public static class Luhn
{
    public static bool IsValid(string digits) => digits.Length > 1 && digits.All(char.IsAsciiDigit) && Sum(digits, doubleFirst: false) % 10 == 0;

    public static int CheckDigit(string payload) =>
        payload.Length > 0 && payload.All(char.IsAsciiDigit) ? (10 - (Sum(payload, doubleFirst: true) % 10)) % 10 : throw new FormatException("Digits only.");

    private static int Sum(string digits, bool doubleFirst)
    {
        var sum = 0;
        var dbl = doubleFirst;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (dbl)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
            dbl = !dbl;
        }

        return sum;
    }
}
