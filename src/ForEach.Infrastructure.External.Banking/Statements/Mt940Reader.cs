using System.Text.RegularExpressions;
using ForEach.Infrastructure.External.Banking.Formats;

namespace ForEach.Infrastructure.External.Banking.Statements;

/// <summary>
/// Reads a SWIFT **MT940** account statement.
///
/// **Shared, like camt.053, because it is nobody's format and everybody's.** Raiffeisen publishes
/// it beside their XML, Asseco's "SWIFT SAP" export is it, Halcom emits it, and OTP's Business
/// E-bank lists it among four. A copy per bank would be five maintained implementations of one
/// SWIFT specification, and the fifth would drift.
///
/// **Two dialects of field 86, and the difference is not cosmetic.** The domestic statement writes
/// its subfields as <c>?20</c>, <c>?21</c>… each on its own line; the devizni one writes them
/// inline as <c>'20</c>, <c>'21</c>… on a single line. A reader that knows only one of them returns
/// a statement whose lines have no payer, no purpose and no reference — which reads as a bank that
/// sent no details rather than as a file half-read.
///
/// **Amounts use a comma**, as SWIFT prescribes: <c>11373667,73</c>. Parsed by rule rather than by
/// culture, because a machine set to English reads that as 1137366773 — out by a factor of a
/// hundred, on whichever server happens to run the import.
///
/// **It never throws.** Anything unreadable comes back as no statements.
/// </summary>
public static class Mt940Reader
{
    /// <summary>
    /// One entry: value date, an optional entry date, the debit/credit mark, an optional funds
    /// code, the amount, the four-character transaction type, then the references.
    ///
    /// <c>RC</c> and <c>RD</c> are matched before <c>C</c> and <c>D</c> deliberately: they are the
    /// **reversals**, and a reader taking the <c>C</c> of an <c>RC</c> books a storno as a second
    /// receipt in the same direction — which balances against nothing and looks like a payment.
    /// </summary>
    private static readonly Regex EntryPattern = new(
        @"^(?<value>\d{6})(?<entry>\d{4})?(?<mark>RC|RD|C|D)(?<funds>[A-Z](?=\d))?"
        + @"(?<amount>[\d,]+)(?<type>[NFS][A-Z0-9]{3})(?<references>.*)$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(1));

    /// <summary>
    /// A subfield of field 86, in either dialect: <c>?NN</c> on its own line, or <c>'NN</c> inline.
    /// </summary>
    private static readonly Regex SubfieldPattern = new(
        @"[?']([0-9]{2})",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    /// <summary>What the standard writes when there is no reference. Not a reference.</summary>
    private const string NoReference = "NONREF";

    /// <summary>The first statement in the file, or an empty one.</summary>
    public static BankStatement Read(string? content) =>
        ReadAll(content).FirstOrDefault() ?? BankStatement.Empty;

    /// <summary>
    /// Every statement in the file. One MT940 file routinely holds several — a bank sending a
    /// week at once, or one izvod per account — each opening with its own <c>:20:</c>.
    /// </summary>
    public static IReadOnlyList<BankStatement> ReadAll(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return [];

        var fields = Fields(content);

        if (fields.Count == 0) return [];

        var statements = new List<BankStatement>();
        var current = new List<(string Tag, string Value)>();

        foreach (var field in fields)
        {
            // :20: opens a statement. Anything before the first one is not part of any.
            if (field.Tag is "20" && current.Count > 0)
            {
                statements.Add(Statement(current));
                current = [];
            }

            current.Add(field);
        }

        if (current.Count > 0) statements.Add(Statement(current));

        // A file of fields that produced nothing recognisable is not an MT940 file. Reported as no
        // statements rather than as one empty one, so the host can say "this is not a statement"
        // instead of "the bank had a quiet day".
        return [.. statements.Where(statement => statement.AccountNumber is not null)];
    }

    /// <summary>
    /// Whether the text looks like MT940 at all.
    ///
    /// Both markers, because <c>:20:</c> alone appears in other SWIFT messages and this reader
    /// would otherwise claim an MT101 or an MT942 and read it wrongly.
    /// </summary>
    public static bool Recognises(string? content) =>
        content is not null
        && content.Contains(":20:", StringComparison.Ordinal)
        && content.Contains(":25:", StringComparison.Ordinal);

    private static BankStatement Statement(List<(string Tag, string Value)> fields)
    {
        string? account = null;
        var currency = string.Empty;
        string? number = null;
        decimal? opening = null;
        decimal? closing = null;
        DateOnly? statementDate = null;

        var entries = new List<StatementEntry>();

        for (var index = 0; index < fields.Count; index++)
        {
            var (tag, value) = fields[index];

            switch (tag)
            {
                case "25":
                    (account, var stated) = AccountAndCurrency(value);

                    if (stated.Length > 0) currency = stated;

                    break;

                // "00181/001" — the statement's number for the year, then the sequence within it.
                // The number is what people quote; the sequence is how one izvod is split across
                // several messages.
                case "28C" or "28":
                    number = value.Split('/')[0].Trim().TrimStart('0');

                    if (number.Length == 0) number = "0";

                    break;

                case "60F" or "60M":
                    var open = Balance(value);

                    opening ??= open.Amount;

                    if (open.Currency is { Length: > 0 } openCurrency && currency.Length == 0)
                        currency = openCurrency;

                    break;

                case "62F" or "62M":
                    var close = Balance(value);

                    // The last one wins: a statement split into several sequences closes with the
                    // final :62F:, and the intermediate ones are where each message stopped.
                    if (close.Amount is not null) closing = close.Amount;
                    if (close.Date is not null) statementDate = close.Date;

                    break;

                case "61":
                    // Field 86 describes the entry before it, and is optional — a line with no
                    // details is still a movement.
                    var details = index + 1 < fields.Count && fields[index + 1].Tag is "86"
                        ? fields[index + 1].Value
                        : null;

                    if (Entry(value, details) is { } entry) entries.Add(entry);

                    break;
            }
        }

        return new BankStatement(
            AccountNumber: account,
            Currency: BankValues.Currency(currency),
            Number: number,
            StatementDate: statementDate ?? entries.LastOrDefault()?.BookingDate,
            OpeningBalance: opening,
            ClosingBalance: closing,
            Entries: entries);
    }

    private static StatementEntry? Entry(string value, string? details)
    {
        var match = EntryPattern.Match(value.ReplaceLineEndings(" ").Trim());

        if (!match.Success) return null;

        var amount = BankValues.DisplayAmount(match.Groups["amount"].Value)
                     ?? BankValues.Amount(match.Groups["amount"].Value.Replace(',', '.'));

        if (amount is null) return null;

        var mark = match.Groups["mark"].Value;

        // **A reversal moves the account the opposite way to the letter it carries**, and this is
        // the one thing in the format worth proving rather than reading.
        //
        // The specification's wording is "RC - storno u korist, RD - storno na teret", which reads
        // at first glance as though RC credits. It does not: RC is the storno *of* a credit, so the
        // money goes back out.
        //
        // Raiffeisen's own example settles it to the dinar. Its first statement opens at
        // 11.373.667,73 and closes at 12.035.969,73 — a movement of 662.302,00 — and carries
        // C 2.400 · C 2.400 · RC 2.400 · D 5.608 · D 7.000 · D 80.000 · C 752.510. Treating RC as a
        // debit gives exactly 662.302,00; treating it as a credit gives 667.102,00, which is the
        // same file disagreeing with its own balances by twice the reversed amount.
        //
        // Read the wrong way this is a payment recorded twice — the original and its cancellation
        // both counted as money in — and the customer is credited for a receipt the bank took back.
        var isCredit = mark is "C" or "RD";

        var (customerReference, bankReference) = References(match.Groups["references"].Value);

        var described = Details(details);

        var date = BankValues.Date(match.Groups["value"].Value, "yyMMdd") ?? default;

        return new StatementEntry(
            Amount: Math.Abs(amount.Value),
            Direction: isCredit ? StatementDirection.Credit : StatementDirection.Debit,

            // The entry date is a day and a month with no year — it belongs to the statement's
            // year, and the value date already carries one. Preferring the value date avoids
            // guessing a year across a new-year boundary, where the guess is wrong by twelve
            // months rather than by a day.
            BookingDate: date,
            ValueDate: date,
            Reference: described.Reference,
            Description: described.Description,
            CounterpartyName: described.CounterpartyName,
            CounterpartyAccount: described.CounterpartyAccount,
            BankReference: bankReference ?? customerReference);
    }

    /// <summary>
    /// The customer's reference and, after <c>//</c>, the bank's own.
    ///
    /// <c>NONREF</c> is what the standard writes when there is none, and a host matching on it
    /// would match every unreferenced payment to the same nothing.
    /// </summary>
    private static (string? Customer, string? Bank) References(string text)
    {
        var trimmed = text.Trim();

        if (trimmed.Length == 0) return (null, null);

        var split = trimmed.IndexOf("//", StringComparison.Ordinal);

        var customer = split < 0 ? trimmed : trimmed[..split];
        var bank = split < 0 ? null : trimmed[(split + 2)..].Trim();

        customer = customer.Trim();

        return (
            Blank(customer) ? null : customer,
            Blank(bank) ? null : bank);

        static bool Blank(string? value) =>
            string.IsNullOrWhiteSpace(value)
            || string.Equals(value, NoReference, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record Described(
        string? Description, string? Reference, string? CounterpartyName, string? CounterpartyAccount);

    /// <summary>
    /// Field 86, in whichever dialect it arrived in.
    ///
    /// The text before the first subfield is the šifra plaćanja. The subfields that matter here:
    /// 20–29 describe the payment, 22 is the poziv na broj zaduženja, 23 the odobrenja, 30 the
    /// bank, 31 the counterparty's account and 32/33 their name and address.
    /// </summary>
    private static Described Details(string? details)
    {
        if (string.IsNullOrWhiteSpace(details)) return new Described(null, null, null, null);

        var text = details.ReplaceLineEndings("\n");

        var matches = SubfieldPattern.Matches(text);

        var subfields = new Dictionary<string, string>(StringComparer.Ordinal);

        var paymentCode = (matches.Count > 0 ? text[..matches[0].Index] : text)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();

        for (var index = 0; index < matches.Count; index++)
        {
            var start = matches[index].Index + matches[index].Length;

            var end = index + 1 < matches.Count ? matches[index + 1].Index : text.Length;

            var content = text[start..end].Replace("\n", " ", StringComparison.Ordinal).Trim();

            if (content.Length == 0) continue;

            var key = matches[index].Groups[1].Value;

            // Repeated tags continue the previous value rather than replacing it: a long
            // counterparty name is written across several ?32 lines.
            subfields[key] = subfields.TryGetValue(key, out var existing)
                ? $"{existing} {content}"
                : content;
        }

        var parts = new List<string>();

        if (paymentCode.Length > 0) parts.Add($"Šifra plaćanja: {paymentCode}");

        // 20 through 29 are the purpose, in order.
        foreach (var key in Enumerable.Range(20, 10).Select(number => number.ToString()))
        {
            if (key is "21" or "22" or "23") continue;

            if (subfields.TryGetValue(key, out var line)) parts.Add(line);
        }

        if (subfields.TryGetValue("22", out var debit)) parts.Add($"Poziv na broj zaduženja: {debit}");
        if (subfields.TryGetValue("23", out var credit)) parts.Add($"Poziv na broj odobrenja: {credit}");

        return new Described(
            Description: parts.Count == 0 ? null : string.Join(" · ", parts),

            // The beneficiary's reference first — the same rule every reader in this solution
            // follows, and for the same reason: poziv na broj odobrenja names the invoice.
            Reference: subfields.GetValueOrDefault("23") ?? subfields.GetValueOrDefault("22"),
            CounterpartyName: subfields.GetValueOrDefault("32"),
            CounterpartyAccount: CounterpartyAccount(
                subfields.GetValueOrDefault("30"), subfields.GetValueOrDefault("31")));
    }

    /// <summary>
    /// The counterparty's account, rebuilt from the bank code and the account when it is domestic.
    ///
    /// The specification says ?31 carries the number "bez koda banke i bez vodećih nula", so a
    /// domestic account is ?30 followed by ?31 padded back to fifteen digits — three for the bank,
    /// thirteen for the partija and two for the control number.
    ///
    /// **Only when ?30 is three digits.** On a devizni statement ?30 is a SWIFT code and ?31 an
    /// IBAN, and gluing them produces <c>INGBUAUK123456789…</c> — a string that matches no account
    /// anywhere and looks like one.
    /// </summary>
    private static string? CounterpartyAccount(string? bankCode, string? account)
    {
        if (string.IsNullOrWhiteSpace(account)) return null;

        var trimmed = account.Trim();

        if (bankCode is not { Length: 3 } code || !code.All(char.IsAsciiDigit)) return trimmed;

        return trimmed.All(char.IsAsciiDigit) ? code + trimmed.PadLeft(15, '0') : trimmed;
    }

    /// <summary>
    /// A balance: the debit/credit mark, the date, the currency and the amount.
    ///
    /// The sign is real here, unlike an entry's — an overdrawn account genuinely holds less than
    /// nothing, and the <c>D</c> is how the file says so.
    /// </summary>
    private static (decimal? Amount, string Currency, DateOnly? Date) Balance(string value)
    {
        var text = value.Trim();

        if (text.Length < 10) return (null, string.Empty, null);

        var isCredit = text[0] is 'C' or 'c';
        var date = BankValues.Date(text.Substring(1, 6), "yyMMdd");
        var currency = text.Substring(7, 3);

        var amount = BankValues.DisplayAmount(text[10..])
                     ?? BankValues.Amount(text[10..].Replace(',', '.'));

        return (amount is null ? null : isCredit ? amount : -amount, currency, date);
    }

    /// <summary>
    /// The account and its currency from field 25, in both shapes the banks write.
    ///
    /// A domestic statement writes them run together — eighteen digits then the numeric currency
    /// code — and a devizni one separates them with a slash and uses the letters.
    /// </summary>
    private static (string? Account, string Currency) AccountAndCurrency(string value)
    {
        var text = value.Trim();

        if (text.Length == 0) return (null, string.Empty);

        var slash = text.IndexOf('/', StringComparison.Ordinal);

        if (slash > 0) return (text[..slash].Trim(), text[(slash + 1)..].Trim());

        // 18 + 3: the account and the numeric currency code with nothing between them.
        if (text.Length == 21 && text.All(char.IsAsciiDigit))
            return (text[..18], text[18..]);

        return (text, string.Empty);
    }

    /// <summary>
    /// The file as a list of (tag, value), with a field's continuation lines folded into it.
    ///
    /// MT940 wraps: a long field 86 runs to the next line, and that line does not begin with a
    /// tag. Folding here means every reader below sees one string per field rather than having to
    /// know where the bank broke the line.
    /// </summary>
    private static List<(string Tag, string Value)> Fields(string content)
    {
        var fields = new List<(string Tag, string Value)>();

        string? tag = null;
        var value = new System.Text.StringBuilder();

        using var reader = new StringReader(content.ReplaceLineEndings("\n"));

        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.TrimEnd();

            // A tag line: ":", one or two digits, an optional letter, ":".
            if (trimmed.StartsWith(':') && trimmed.IndexOf(':', 1) is > 1 and var close
                && close <= 4)
            {
                if (tag is not null) fields.Add((tag, value.ToString()));

                tag = trimmed[1..close];
                value.Clear();
                value.Append(trimmed[(close + 1)..]);

                continue;
            }

            // A continuation, or the "-}" trailer some senders append. Kept with a newline so
            // field 86's line-per-subfield dialect survives.
            if (tag is null) continue;

            value.Append('\n').Append(trimmed);
        }

        if (tag is not null) fields.Add((tag, value.ToString()));

        return fields;
    }
}
