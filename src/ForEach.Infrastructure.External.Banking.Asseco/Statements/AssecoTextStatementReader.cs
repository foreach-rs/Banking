using ForEach.Infrastructure.External.Banking.Formats;
using ForEach.Infrastructure.External.Banking.Statements;

namespace ForEach.Infrastructure.External.Banking.Asseco.Statements;

/// <summary>
/// Reads the Asseco **fixed-width TXT** statement — the other export the OfficeBanking
/// specification documents beside the XML.
///
/// **Built from the specification alone; no sample was available.** That is weaker evidence than
/// every other reader here rests on, and it is why this one checks itself — see below. What makes
/// the layout credible is that both record shapes account for exactly 220 characters: the header's
/// sixteen fields and the detail's nineteen each sum to the record width, so an offset that drifts
/// leaves a gap rather than going unnoticed.
///
/// **It checks its own reading against the header's totals.** The header states the day's debit and
/// credit turnover, and the entries have to reproduce both. That converts an unverified layout into
/// a self-checking one: if these offsets are wrong the totals disagree and the file is refused,
/// rather than being imported as a statement whose amounts came from the wrong columns.
///
/// **The direction is stated, not derived** — a `D` or `P` at position 148 — so unlike the ROL and
/// Halcom fixed-width formats there is nothing to infer here.
///
/// **It never throws.**
/// </summary>
public static class AssecoTextStatementReader
{
    /// <summary>Both record kinds are exactly this wide.</summary>
    private const int LineLength = 220;

    /// <summary>The record kind, in the last character. 1 is the header, 2 a movement.</summary>
    private const char HeaderKind = '1';
    private const char DetailKind = '2';

    /// <summary>Money out, as the direction column writes it. `P` — potražuje — is money in.</summary>
    private const char DebitMark = 'D';

    private const string DateFormat = "ddMMyyyy";

    /// <summary>
    /// Reads the one statement a file carries, refusing it when the entries disagree with the
    /// header's totals — the self-check the class summary describes.
    /// </summary>
    public static AssecoStatementRead Read(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return NotRecognised;

        var lines = new List<string>();

        using (var reader = new StringReader(content))
        {
            while (reader.ReadLine() is { } line)
            {
                if (line.Trim().Length == 0) continue;

                // Trailing spaces are data in a fixed-width file and some transports eat them, so
                // a short record is padded back out. A long one is refused: it means the layout is
                // not this one, and reading it takes every field from the wrong place.
                if (line.Length > LineLength) return NotRecognised;

                var padded = line.PadRight(LineLength);

                if (padded[^1] is not (HeaderKind or DetailKind)) return NotRecognised;

                lines.Add(padded);
            }
        }

        var header = lines.FirstOrDefault(line => line[^1] is HeaderKind);

        // Without the header there are no balances, no currency and no izvod number — and nothing
        // to check the entries against, which is the whole safety net for a format nobody has seen
        // a sample of.
        if (header is null) return NotRecognised;

        var entries = lines
            .Where(line => line[^1] is DetailKind)
            .Select(ReadEntry)
            .ToList();

        var statedDebit = Signed(Field(header, 51, 18)) ?? 0m;
        var statedCredit = Signed(Field(header, 69, 18)) ?? 0m;

        var readDebit = entries
            .Where(entry => entry.Direction is StatementDirection.Debit)
            .Sum(entry => entry.Amount);

        var readCredit = entries
            .Where(entry => entry.Direction is StatementDirection.Credit)
            .Sum(entry => entry.Amount);

        // The self-check. The header says what the day moved in each direction, and the lines have
        // to agree with it — not merely add up to the same net figure, which a pair of swapped
        // columns would also do.
        if (readDebit != statedDebit || readCredit != statedCredit)
        {
            return new AssecoStatementRead(
                AssecoStatementKind.Statement, [],
                $"Stavke izvoda ne odgovaraju zbirovima iz zaglavlja "
                + $"(duguje {readDebit:0.00} prema {statedDebit:0.00}, "
                + $"potražuje {readCredit:0.00} prema {statedCredit:0.00}). Izvod se ne uvozi.");
        }

        var statement = new BankStatement(
            AccountNumber: Field(header, 12, 18),
            Currency: BankValues.Currency(Field(header, 30, 3)),
            Number: Field(header, 147, 3)?.TrimStart('0'),
            StatementDate: BankValues.Date(Field(header, 0, 8), DateFormat),
            OpeningBalance: Signed(Field(header, 33, 18)),
            ClosingBalance: Signed(Field(header, 87, 18)),
            Entries: entries);

        return new AssecoStatementRead(AssecoStatementKind.Statement, [statement]);
    }

    /// <summary>Whether the text has the shape of this export.</summary>
    public static bool Recognises(string? content) =>
        Read(content) is { Kind: not AssecoStatementKind.Unknown, Refusal: null };

    private static StatementEntry ReadEntry(string line)
    {
        var debitReference = Field(line, 76, 20);
        var creditReference = Field(line, 168, 20);

        return new StatementEntry(
            // Thirteen characters, eleven whole and two decimal, with no separator between them.
            // A reader that forgot to divide would be out by a factor of a hundred.
            Amount: BankValues.ImpliedDecimal(Field(line, 134, 13)) ?? 0m,

            // Stated rather than derived: D is duguje and P is potražuje.
            Direction: line[147] is DebitMark ? StatementDirection.Debit : StatementDirection.Credit,
            BookingDate: BankValues.Date(Field(line, 188, 8), DateFormat) ?? default,
            ValueDate: BankValues.Date(Field(line, 188, 8), DateFormat),

            // The beneficiary's reference first — the same rule every reader in this solution
            // follows. Here the second pair is the unqualified "poziv na broj", which on a
            // statement line is the odobrenja side.
            Reference: creditReference ?? debitReference,
            Description: Description(line, debitReference, creditReference),
            CounterpartyName: Field(line, 18, 35),
            CounterpartyAccount: Field(line, 148, 18),

            // "Reklamacija" — the number quoted to the bank when disputing a line, which is what
            // makes a second import of the same file detectable.
            BankReference: Field(line, 200, 14));
    }

    private static string? Description(string line, string? debit, string? credit)
    {
        var parts = new List<string>();

        Add(Field(line, 99, 35));

        if (Field(line, 96, 3) is { } code) Add($"Šifra plaćanja: {code}");

        if (credit is not null)
            Add($"Poziv na broj odobrenja: ({Field(line, 166, 2) ?? "00"}) {credit}");

        if (debit is not null)
            Add($"Poziv na broj zaduženja: ({Field(line, 74, 2) ?? "00"}) {debit}");

        if (Field(line, 53, 20) is { } place) Add($"Mesto: {place}");

        return parts.Count == 0 ? null : string.Join(" · ", parts);

        void Add(string? part)
        {
            if (!string.IsNullOrWhiteSpace(part)) parts.Add(part);
        }
    }

    /// <summary>
    /// A balance: fifteen whole digits, two decimal and a sign, in eighteen characters.
    ///
    /// The specification says the field carries a sign without saying which end it sits at, so
    /// both are accepted. An unsigned figure is positive, which is what an unused sign column
    /// means.
    /// </summary>
    private static decimal? Signed(string? field)
    {
        var text = field?.Trim();

        if (string.IsNullOrEmpty(text)) return 0m;

        var negative = text[0] is '-' || text[^1] is '-';

        var digits = text.Trim('+', '-');

        var value = BankValues.ImpliedDecimal(digits);

        return value is null ? null : negative ? -value : value;
    }

    private static string? Field(string line, int start, int length)
    {
        if (start >= line.Length) return null;

        var value = line.Substring(start, Math.Min(length, line.Length - start)).Trim();

        return value.Length == 0 ? null : value;
    }

    private static readonly AssecoStatementRead NotRecognised =
        new(AssecoStatementKind.Unknown, []);
}
