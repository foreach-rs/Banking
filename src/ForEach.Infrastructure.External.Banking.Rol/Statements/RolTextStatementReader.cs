using ForEach.Infrastructure.External.Banking.Formats;
using ForEach.Infrastructure.External.Banking.Statements;

namespace ForEach.Infrastructure.External.Banking.Rol.Statements;

/// <summary>
/// Reads the ROL **fixed-width TXT** statement — the other export Raiffeisen publishes beside the
/// XML and the MT940.
///
/// **Every offset below was checked against the bank's own example**, and the specification turned
/// out to be exactly right: the detail record's nineteen field lengths sum to 489, which is the
/// line width to the character. That is a stronger signature than any of the other formats here
/// gave, and it is why this reader trusts the layout rather than sniffing for markers inside it.
///
/// **The file is UTF-16.** Like Raiffeisen's payment orders — and unlike anything else read here.
/// A host decoding it as UTF-8 or Windows-1250 gets a string with a NUL between every character,
/// which doubles every offset and reads as an unrecognised file rather than a mis-decoded one.
///
/// **Two amount columns rather than a direction**, the same shape as the ROL XML: `iznos_duguje`
/// and `iznos_potrazuje`, netted rather than picked, so a correction written as a negative credit
/// moves the money the way it actually went.
///
/// **It never throws.**
/// </summary>
public static class RolTextStatementReader
{
    /// <summary>
    /// The record width. Both record kinds are padded to it, and the detail layout accounts for
    /// every character of it.
    /// </summary>
    private const int LineLength = 489;

    private const char HeaderLevel = '1';
    private const char DetailLevel = '2';

    /// <summary>Two-digit years, as the format writes its dates.</summary>
    private const string DateFormat = "dd/MM/yy";

    /// <summary>
    /// Reads the one statement a file carries. Unknown unless every line has the shape of this
    /// layout — the punctuation check below, which is what keeps a Halcom file out.
    /// </summary>
    public static RolStatementRead Read(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return NotRecognised;

        var lines = new List<string>();

        using (var reader = new StringReader(content))
        {
            while (reader.ReadLine() is { } line)
            {
                if (line.Trim().Length == 0) continue;

                // Padded back out rather than refused: trailing spaces are data in a fixed-width
                // file and some transports eat them. Longer than the record is refused — it means
                // the layout is not this one, and reading it would take every field from the wrong
                // place.
                if (line.Length > LineLength) return NotRecognised;

                var padded = line.PadRight(LineLength);

                // **The level digit alone is not enough to recognise this format**, and finding
                // that out is what this check exists for: a Halcom fixed-width line begins with
                // the counterparty's account number, which routinely starts with a 1 or a 2. Read
                // as a ROL record it produces a statement whose account is a date and whose
                // balances are somebody's reference — a file confidently misread rather than
                // refused.
                //
                // So the shape is checked where the format fixes punctuation: the slashes inside
                // the dates, and the decimal points inside the two amount columns. Nothing else in
                // this solution puts those characters at those offsets.
                if (!IsHeader(padded) && !IsDetail(padded)) return NotRecognised;

                lines.Add(padded);
            }
        }

        // A file of detail records with no header states no balances and no izvod number, which is
        // not a statement this reader can vouch for.
        var header = lines.FirstOrDefault(IsHeader);

        if (header is null || lines.Count == 0) return NotRecognised;

        var statementDate = BankValues.Date(Field(header, 1, 8), DateFormat);

        // Every movement's amounts and date are read before any line is. A column that is not an
        // amount, or a line with no date of its own on a header with none either, refuses the
        // file: the alternative is a line booked at zero, or on the first of January 0001, on a
        // statement that otherwise looks complete.
        var entries = new List<StatementEntry>();

        foreach (var line in lines.Where(IsDetail))
        {
            var debitText = Field(line, 66, 24);
            var creditText = Field(line, 90, 24);

            var debit = debitText is null ? 0m : Signed(debitText);
            var credit = creditText is null ? 0m : Signed(creditText);

            if (debit is null || credit is null)
            {
                return new RolStatementRead(
                    RolStatementKind.Dinar, BankStatement.Empty,
                    $"Iznos „{(debit is null ? debitText : creditText)}” nije čitljiv. "
                    + "Izvod se ne uvozi.");
            }

            var date = BankValues.Date(Field(line, 25, 8), DateFormat) ?? statementDate;

            if (date is null)
            {
                return new RolStatementRead(
                    RolStatementKind.Dinar, BankStatement.Empty,
                    $"Datum „{Field(line, 25, 8)}” nije čitljiv. Izvod se ne uvozi.");
            }

            entries.Add(ReadEntry(line, debit.Value, credit.Value, date.Value));
        }

        var statement = new BankStatement(
            AccountNumber: Field(header, 9, 20),
            Currency: "RSD",
            Number: Field(header, 55, 4)?.TrimStart('0'),
            StatementDate: statementDate,
            OpeningBalance: Signed(Field(header, 72, 24)),
            ClosingBalance: Signed(Field(header, 144, 24)),
            Entries: entries);

        return new RolStatementRead(RolStatementKind.Dinar, statement);
    }

    /// <summary>A header record: the level digit, then <c>dd/MM/yy</c>.</summary>
    private static bool IsHeader(string line) =>
        line[0] is HeaderLevel && line[3] is '/' && line[6] is '/';

    /// <summary>
    /// A detail record: the level digit, the statement date's slashes, and the decimal points of
    /// the two amount columns — each a sign, twenty digits, a point and two more.
    /// </summary>
    private static bool IsDetail(string line) =>
        line[0] is DetailLevel
        && line[27] is '/'
        && line[30] is '/'
        && line[87] is '.'
        && line[111] is '.';

    /// <summary>Whether the text has the shape of this export.</summary>
    public static bool Recognises(string? content) =>
        Read(content).Kind is not RolStatementKind.Unknown;

    /// <summary>
    /// One movement. The two amount columns and the date arrive already read — a blank column is
    /// the unused side and reads as zero — because a line either fails on refuses the file before
    /// this is reached.
    /// </summary>
    private static StatementEntry ReadEntry(string line, decimal debit, decimal credit, DateOnly date)
    {
        // Netted, for the reason the XML reader states: a storno is written as a negative in one
        // of the two columns, and taking whichever is non-zero books a correction as money
        // arriving.
        var net = credit - debit;

        var creditReference = Field(line, 411, 20);
        var debitReference = Field(line, 389, 20);

        return new StatementEntry(
            Amount: Math.Abs(net),
            Direction: net < 0 ? StatementDirection.Debit : StatementDirection.Credit,

            // A detail record carries the statement's own date rather than a value date of its own.
            BookingDate: date,
            ValueDate: null,

            // The beneficiary's reference first — poziv_korisnika — which is the same rule and the
            // same field the XML reader takes.
            Reference: creditReference ?? debitReference,
            Description: Description(line, creditReference, debitReference),
            CounterpartyName: Field(line, 114, 125),
            CounterpartyAccount: Field(line, 259, 20),
            BankReference: Field(line, 431, 42) ?? Field(line, 473, 16));
    }

    private static string? Description(string line, string? credit, string? debit)
    {
        var parts = new List<string>();

        Add(Field(line, 282, 105));

        if (Field(line, 279, 3) is { } code) Add($"Šifra plaćanja: {code}");

        if (credit is not null)
            Add($"Poziv na broj odobrenja: ({Field(line, 409, 2) ?? "00"}) {credit}");

        if (debit is not null)
            Add($"Poziv na broj zaduženja: ({Field(line, 387, 2) ?? "00"}) {debit}");

        if (Field(line, 239, 20) is { } place) Add($"Mesto: {place}");

        return parts.Count == 0 ? null : string.Join(" · ", parts);

        void Add(string? part)
        {
            if (!string.IsNullOrWhiteSpace(part)) parts.Add(part);
        }
    }

    /// <summary>
    /// An amount in the format's <c>Z20N.2N</c> shape: a sign, twenty digits, a point and two more.
    ///
    /// The sign is a leading space for a positive figure and a minus for a negative one, so the
    /// field is parsed whole rather than by trimming a character nobody can see.
    /// </summary>
    private static decimal? Signed(string? field) => BankValues.Amount(field);

    /// <summary>A fixed-width field, trimmed, with empty read as absent.</summary>
    private static string? Field(string line, int start, int length)
    {
        if (start >= line.Length) return null;

        var value = line.Substring(start, Math.Min(length, line.Length - start)).Trim();

        return value.Length == 0 ? null : value;
    }

    private static readonly RolStatementRead NotRecognised =
        new(RolStatementKind.Unknown, BankStatement.Empty);
}
