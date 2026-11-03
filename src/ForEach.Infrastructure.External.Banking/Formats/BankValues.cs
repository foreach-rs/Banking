using System.Globalization;

namespace ForEach.Infrastructure.External.Banking.Formats;

/// <summary>
/// Reading the values a Serbian bank file is made of.
///
/// Four shapes recur across every format in this solution, and each has one way of being read
/// wrong that no screen would show:
///
/// - **Amounts with a dot** (`10000.10`) — parsed invariantly. Under a Serbian culture
///   `decimal.Parse("1234.56")` returns 123456: a hundredfold error on money, silent, and
///   dependent on the regional settings of whichever server runs the import.
/// - **Amounts in Serbian display form** (`3.000.000,00`) — the same digits with the separators
///   swapped. Read by the wrong rule this is three, not three million.
/// - **Amounts with implied decimals** (`000000006980880` → 69 808,80) — fixed-width files carry
///   no separator at all, so a reader that forgets to divide is out by a factor of a hundred.
/// - **Dates as `dd.MM.yyyy`** — never ISO, whatever the schema claims. ROL declares
///   `DatumIzvoda` as `xs:date` and writes `26.06.2006` in it. A parser trusting the declared type
///   reads the day as the month for the first twelve of each, and fails outright after that.
///
/// Everything here is total: a value that cannot be read comes back null rather than throwing, so
/// a reader can report an unrecognised file instead of a stack trace.
/// </summary>
public static class BankValues
{
    /// <summary>
    /// The dinar's legacy codes.
    ///
    /// `DIN` is what the Asseco and ROL specs still write for dinars, and `CSD` is the code that
    /// preceded `RSD`. Both mean the same money and both turn up in files being exported today, so
    /// they are normalised on the way in — otherwise a statement in dinars fails to match a bank
    /// account recorded as RSD, and the import reports an unknown currency for the only currency
    /// this company has.
    /// </summary>
    private static readonly Dictionary<string, string> LegacyCurrencies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DIN"] = "RSD",
        ["CSD"] = "RSD",

        // The numeric ISO 4217 code, which the ROL payment order writes instead of the letters.
        ["941"] = "RSD"
    };

    /// <summary>
    /// A currency code in the form the rest of the application uses.
    ///
    /// Unknown codes pass through unchanged rather than being rejected: a euro statement is a
    /// perfectly good statement and this is not the place that decides what the host supports.
    /// </summary>
    public static string Currency(string? code)
    {
        var trimmed = code?.Trim();

        if (string.IsNullOrEmpty(trimmed)) return string.Empty;

        return LegacyCurrencies.TryGetValue(trimmed, out var normalised)
            ? normalised
            : trimmed.ToUpperInvariant();
    }

    /// <summary>
    /// An amount written with a dot as the decimal separator — the XML formats.
    ///
    /// Invariant and not negotiable; see the class summary. Thousands separators are not accepted
    /// here on purpose: in these files a dot is the decimal point, so treating a second dot as a
    /// grouping mark would read `1.234.56` as something rather than refusing it.
    /// </summary>
    public static decimal? Amount(string? text)
    {
        var trimmed = text?.Trim();

        if (string.IsNullOrEmpty(trimmed)) return null;

        return decimal.TryParse(
            trimmed, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>
    /// An amount in Serbian display form: <c>3.000.000,00</c> → 3000000.00.
    ///
    /// Used by the Halcom extended export, which writes amounts the way they appear on a screen
    /// rather than the way a machine reads them. The dots are stripped before the comma becomes a
    /// point, so the two separators cannot be confused for each other.
    /// </summary>
    public static decimal? DisplayAmount(string? text)
    {
        var trimmed = text?.Trim();

        if (string.IsNullOrEmpty(trimmed)) return null;

        return Amount(trimmed.Replace(".", string.Empty, StringComparison.Ordinal).Replace(',', '.'));
    }

    /// <summary>
    /// A fixed-width amount with two implied decimal places: <c>000000006980880</c> → 69 808,80.
    ///
    /// A leading sign is tolerated because the recap lines carry one; blank reads as zero, which is
    /// what an unused fixed-width field means.
    /// </summary>
    public static decimal? ImpliedDecimal(string? digits)
    {
        var trimmed = digits?.Trim();

        if (string.IsNullOrEmpty(trimmed)) return 0m;

        var negative = trimmed[0] is '-';

        if (negative || trimmed[0] is '+') trimmed = trimmed[1..];

        if (trimmed.Length == 0 || !trimmed.All(char.IsAsciiDigit)) return null;

        var value = decimal.Parse(trimmed, CultureInfo.InvariantCulture) / 100m;

        return negative ? -value : value;
    }

    /// <summary>
    /// A date in one of the formats these files use, tried in the order given.
    ///
    /// Exact formats rather than <see cref="DateOnly.TryParse(string?, out DateOnly)"/>: a loose
    /// parse under an invariant culture reads <c>10.02.2015</c> as the 2nd of October, which is a
    /// valid date, on a payment order, eight months from where it belongs.
    /// </summary>
    public static DateOnly? Date(string? text, params string[] formats)
    {
        var trimmed = text?.Trim();

        if (string.IsNullOrEmpty(trimmed)) return null;

        foreach (var format in formats)
        {
            if (DateOnly.TryParseExact(
                    trimmed, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                return date;
            }
        }

        // Some Asseco elements carry a full timestamp where the sibling carries a date.
        return DateTimeOffset.TryParse(
            trimmed, CultureInfo.InvariantCulture, DateTimeStyles.None, out var moment)
            ? DateOnly.FromDateTime(moment.Date)
            : null;
    }

    /// <summary>The Serbian date format every one of these formats writes.</summary>
    public const string SerbianDate = "dd.MM.yyyy";
}
