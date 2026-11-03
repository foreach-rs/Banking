using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ForEach.Infrastructure.External.Banking.Formats;
using ForEach.Infrastructure.External.Banking.Statements;

namespace ForEach.Infrastructure.External.Banking.Halcom.Statements;

/// <summary>Which Halcom export a file turned out to be.</summary>
public enum HalcomStatementKind
{
    /// <summary>Nothing recognised it.</summary>
    Unknown,

    /// <summary>The fixed-width 280-character export ("Promet i izvodi").</summary>
    FixedWidth,

    /// <summary>The extended <c>#</c>-delimited export with a header row ("prošireni").</summary>
    Extended,

    /// <summary>
    /// A devizni statement — the tag-based export opening <c>&lt;CLIENTPROFILE&gt;</c>.
    /// </summary>
    ForeignStatement
}

/// <summary>What a Halcom file was read as, and what it held.</summary>
/// <param name="Kind">
/// Which export answered, or <see cref="HalcomStatementKind.Unknown"/> when none did. Carried out
/// because "not a statement" and "a statement with no entries" are different things to tell
/// somebody at an import screen, and both arrive as an empty statement otherwise.
/// </param>
/// <param name="Statements">
/// Every statement in the file. The extended export can carry several accounts and izvod numbers
/// at once, and each becomes its own statement; the other two carry one. None when nothing was
/// read.
/// </param>
/// <param name="Refusal">Why a recognised file was not read, in Serbian. Null when it was.</param>
public sealed record HalcomStatementRead(
    HalcomStatementKind Kind,
    IReadOnlyList<BankStatement> Statements,
    string? Refusal = null)
{
    /// <summary>The first statement, or an empty one — the ordinary single-statement case.</summary>
    public BankStatement Statement => Statements.Count > 0 ? Statements[0] : BankStatement.Empty;
}

/// <summary>
/// Reads a Halcom Hal E-Bank account statement export.
///
/// Halcom is not a bank — it is the e-banking client roughly twenty Serbian banks run, so this one
/// reader covers Raiffeisen, Intesa, Erste, UniCredit, AIK, ProCredit, Komercijalna, Addiko, OTP
/// and the rest of that list. That reach is the reason it is worth the fixed-width work.
///
/// **The balances live in a second file.** The transaction export carries no opening or closing
/// balance and no statement number; those are in the companion recap (<c>*_cov.txt</c>,
/// "Rekapitulacija izvoda"). Without it a statement is read with its lines and nothing to check
/// them against — <see cref="BankStatement.Reconciles"/> is null, and the host's refusal for
/// "the entries do not account for the movement" cannot fire. That is a real limitation and is
/// stated rather than papered over: ask for both files.
///
/// **The bank's own reference is not unique.** Real files repeat the broj za reklamaciju across
/// several lines of one order, so a host deduplicating on it alone would drop genuine payments.
/// Repeats are given a deterministic suffix — see <see cref="Disambiguate"/> — which keeps the
/// reklamacija number readable while making a second import of the same file detectable.
///
/// **It never throws.**
/// </summary>
public static class HalcomStatementReader
{
    private const int TransactionLineLength = 280;
    private const int RecapLineLength = 147;

    /// <summary>
    /// Posting codes and the direction each gives the amount.
    ///
    /// 10 and 20 are an ordinary debit and credit; 30 and 40 are their stornos, so the money moves
    /// the other way. Getting 30 and 40 backwards would book a reversal as a second payment in the
    /// same direction, and the file would still add up against nothing.
    /// </summary>
    private static readonly Dictionary<string, int> PostingSign = new()
    {
        ["10"] = -1,
        ["20"] = +1,
        ["30"] = +1,
        ["40"] = -1
    };

    private const string ExtendedHeaderMarker = "ST_RACUNA#";
    private const string ForeignMarker = HalcomForeignStatement.Marker;

    /// <summary>
    /// Reads a statement.
    /// </summary>
    /// <param name="content">The transaction export, or the extended one.</param>
    /// <param name="recap">
    /// The companion <c>*_cov.txt</c>, when the host has it. It is where the statement number and
    /// both balances come from; without it they are absent and the statement is named by its date.
    /// </param>
    public static HalcomStatementRead Read(string? content, string? recap = null)
    {
        if (string.IsNullOrWhiteSpace(content)) return NotRecognised;

        if (content.TrimStart().StartsWith(ForeignMarker, StringComparison.OrdinalIgnoreCase))
            return HalcomForeignStatement.Read(content);

        if (content.TrimStart().StartsWith(ExtendedHeaderMarker, StringComparison.OrdinalIgnoreCase))
            return ReadExtended(content);

        return ReadFixedWidth(content, recap);
    }

    // ---- the fixed-width export ----------------------------------------------------------------

    private static HalcomStatementRead ReadFixedWidth(string content, string? recap)
    {
        var lines = ContentLines(content);

        if (lines.Count == 0) return NotRecognised;

        // A recap line inside the transaction file, which happens whenever somebody concatenates
        // the two exports or pastes both into one upload. It is unambiguous — 147 characters
        // beginning "01", against transactions of 280 — and taking it costs nothing.
        //
        // Worth having because of what it buys: without a recap there are no balances, so
        // BankStatement.Reconciles is null and the host's "these lines do not account for the
        // movement" refusal cannot fire. Before this, a file carrying both was refused outright as
        // unrecognised, which is the worst of the three outcomes.
        //
        // It is not a substitute for the port taking two files. It is the case that can be handled
        // without one.
        var embedded = lines.Where(IsRecapLine).ToList();

        if (embedded.Count > 0)
        {
            // Always taken out of the transaction set — a recap line is not a movement, and left
            // in it fails the 280-character check and refuses the whole file. Only *used* when the
            // caller supplied no recap of their own: they know which file is which, and the
            // sniffing is a fallback rather than a preference.
            lines = [.. lines.Where(line => !IsRecapLine(line))];

            recap ??= embedded[0];

            if (lines.Count == 0) return NotRecognised;
        }

        var padded = new List<string>(lines.Count);

        foreach (var line in lines)
        {
            // Trailing spaces are data in a fixed-width file and some transports eat them, so a
            // short line is padded back out rather than refused. A *long* one is refused: it means
            // the layout is not the one this reader knows, and reading it would take every field
            // from the wrong place.
            if (line.Length > TransactionLineLength) return NotRecognised;

            var whole = line.PadRight(TransactionLineLength);

            if (!IsTransactionLine(whole)) return NotRecognised;

            padded.Add(whole);
        }

        // Every amount is read before any line is: a figure that is not fifteen digits refuses the
        // file, because the alternative is a line booked at zero — in the credit direction — on a
        // statement that otherwise looks complete.
        var amounts = new List<decimal>(padded.Count);

        for (var i = 0; i < padded.Count; i++)
        {
            if (BankValues.ImpliedDecimal(padded[i].Substring(90, 15)) is not { } amount)
            {
                return new HalcomStatementRead(
                    HalcomStatementKind.FixedWidth, [],
                    $"Iznos „{padded[i].Substring(90, 15).Trim()}” u {i + 1}. redu nije čitljiv. "
                    + "Izvod se ne uvozi.");
            }

            amounts.Add(amount);
        }

        var entries = padded.Select((line, i) => ReadFixedWidthEntry(line, amounts[i])).ToList();

        var ownAccounts = padded.Select(line => Field(line, 72, 18)).Distinct().ToList();

        // One file, one account. Two would make the balances meaningless and silently merge two
        // izvodi into one, so it is refused rather than guessed at.
        if (ownAccounts.Count > 1)
        {
            return new HalcomStatementRead(
                HalcomStatementKind.FixedWidth, [],
                "Datoteka sadrži promete za više računa: " + string.Join(", ", ownAccounts));
        }

        Disambiguate(entries);

        var recapInfo = recap is null ? null : ReadRecap(recap);

        var statement = new BankStatement(
            AccountNumber: ownAccounts.Count == 1 ? ownAccounts[0] : null,

            // The fixed-width export is the dinar payment system's; a devizni statement is the
            // pseudo-XML shape refused above.
            Currency: "RSD",
            Number: recapInfo?.Number,
            StatementDate: recapInfo?.Date
                           ?? (entries.Count > 0 ? entries.Max(entry => entry.BookingDate) : null),
            OpeningBalance: recapInfo?.OpeningBalance,
            ClosingBalance: recapInfo?.ClosingBalance,
            Entries: entries);

        return new HalcomStatementRead(HalcomStatementKind.FixedWidth, [statement]);
    }

    /// <summary>
    /// Whether a line has the shape of a transaction record.
    ///
    /// Three markers rather than a length alone: the posting code, and the two dots inside the
    /// booking date. A file of the right length whose fields sit elsewhere fails on the dots, which
    /// is what stops another bank's fixed-width export from being read as this one.
    /// </summary>
    /// <summary>
    /// Whether a line is the recap record — 147 characters opening with the level marker "01",
    /// followed by the eighteen digits of the account.
    /// </summary>
    private static bool IsRecapLine(string line) =>
        line.Length == RecapLineLength
        && line.StartsWith("01", StringComparison.Ordinal)
        && line.AsSpan(2, 18).ToString().All(char.IsAsciiDigit);

    private static bool IsTransactionLine(string line) =>
        line.Length == TransactionLineLength
        && PostingSign.ContainsKey(line.Substring(18, 2))
        && line[22] is '.'
        && line[25] is '.';

    private static StatementEntry ReadFixedWidthEntry(string line, decimal amount)
    {
        var posting = line.Substring(18, 2);
        var signed = amount * PostingSign[posting];

        var debitReference = Field(line, 113, 22);
        var creditReference = Field(line, 137, 22);
        var purpose = Field(line, 159, 36);
        var partnerName = Field(line, 205, 35);
        var bankReference = Field(line, 240, 22);

        // The payment code zone spans 106–111 rather than sitting where the pre-2003 spec puts it:
        // that edition described a two-digit code behind an "oblik plaćanja" digit, and every file
        // written since carries the modern three-digit NBS code across the same zone. Reading the
        // whole zone and trimming is what survives both.
        var paymentCode = Field(line, 105, 6);

        var isStorno = posting is "30" or "40" || Field(line, 28, 2) is not null;

        return new StatementEntry(
            Amount: Math.Abs(signed),
            Direction: signed < 0 ? StatementDirection.Debit : StatementDirection.Credit,
            BookingDate: BankValues.Date(line.Substring(20, 8), "dd.MM.yy") ?? default,
            ValueDate: BankValues.Date(line.Substring(66, 6), "ddMMyy"),

            // The beneficiary's reference first — the same rule the ROL reader documents, and for
            // the same reason: poziv na broj odobrenja is what names the invoice being paid, in
            // both directions.
            Reference: creditReference ?? debitReference,
            Description: Description(purpose, paymentCode, debitReference, creditReference, isStorno),
            CounterpartyName: partnerName,

            // The counterparty account is at the front of the line, and at the back on the lines
            // where the front is blank — a bank-side transfer writes one and not the other.
            CounterpartyAccount: Field(line, 0, 18) ?? Field(line, 262, 18),
            BankReference: bankReference ?? Hash(line));
    }

    private static string? Description(
        string? purpose, string? paymentCode, string? debit, string? credit, bool isStorno)
    {
        var parts = new List<string>();

        if (isStorno) parts.Add("STORNO");
        if (purpose is not null) parts.Add(purpose);
        if (paymentCode is not null) parts.Add($"Šifra plaćanja: {paymentCode}");
        if (credit is not null) parts.Add($"Poziv na broj odobrenja: {credit}");
        if (debit is not null) parts.Add($"Poziv na broj zaduženja: {debit}");

        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    // ---- the recap file ------------------------------------------------------------------------

    private sealed record Recap(
        string Account, string Number, DateOnly? Date, decimal? OpeningBalance, decimal? ClosingBalance);

    /// <summary>
    /// The recap, which is where the statement number and the balances are.
    ///
    /// Its dates carry no dots — <c>27122011</c> — although the specification writes
    /// <c>DD.MM.GGGG</c> for them, which cannot fit the eight characters the field has. The files
    /// are right and the document is not.
    /// </summary>
    private static Recap? ReadRecap(string content)
    {
        var line = ContentLines(content).FirstOrDefault();

        if (line is null || line.Length < RecapLineLength) return null;

        if (!line.StartsWith("01", StringComparison.Ordinal)) return null;

        return new Recap(
            Account: Field(line, 2, 18) ?? string.Empty,
            Number: (Field(line, 144, 3) ?? string.Empty).TrimStart('0'),
            Date: BankValues.Date(line.Substring(20, 8), "ddMMyyyy"),
            OpeningBalance: BankValues.ImpliedDecimal(line.Substring(36, 18)),
            ClosingBalance: BankValues.ImpliedDecimal(line.Substring(102, 18)));
    }

    // ---- the extended export -------------------------------------------------------------------

    /// <summary>
    /// The <c>#</c>-delimited export: a header row of column names, then one row per line.
    ///
    /// The columns are Slovenian — Halcom's own — and the amounts are in Serbian display form
    /// (<c>3.000.000,00</c>), which is the combination most likely to be read wrongly: a parser
    /// treating those dots as decimal points turns three million into three.
    ///
    /// One file can hold several statements; they are grouped by account, number and currency.
    /// </summary>
    private static HalcomStatementRead ReadExtended(string content)
    {
        var lines = ContentLines(content);

        if (lines.Count < 2) return NotRecognised;

        var header = lines[0].Split('#').Select(name => name.Trim().ToUpperInvariant()).ToList();

        var index = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < header.Count; i++)
        {
            // A repeated column name means two different things would be read from one place.
            if (header[i].Length > 0 && !index.TryAdd(header[i], i))
            {
                return new HalcomStatementRead(
                    HalcomStatementKind.Extended, [],
                    $"Kolona „{header[i]}” se u zaglavlju pojavljuje više puta.");
            }
        }

        // Demanded up front: without these every row would read as a silent zero on a nameless
        // account, and the import would report success over nothing.
        var missing = new[] { "ST_RACUNA", "ZNESEK_V_BREME", "ZNESEK_V_DOBRO" }
            .Where(name => !index.ContainsKey(name))
            .ToList();

        if (!index.ContainsKey("DATUM_KNJ") && !index.ContainsKey("DATUM_OBD"))
            missing.Add("DATUM_KNJ/DATUM_OBD");

        if (missing.Count > 0)
        {
            return new HalcomStatementRead(
                HalcomStatementKind.Extended, [],
                "Zaglavlju nedostaju kolone: " + string.Join(", ", missing));
        }

        var grouped = new Dictionary<(string, string, string), List<StatementEntry>>();
        var order = new List<(string Account, string Number, string Currency)>();

        foreach (var line in lines.Skip(1))
        {
            var row = line.Split('#');

            var account = Column(row, "ST_RACUNA") ?? string.Empty;
            var number = Column(row, "ST_IZPISKA") ?? string.Empty;
            var currency = BankValues.Currency(Column(row, "VALUTA") ?? "RSD");

            var key = (account, number, currency);

            if (!grouped.TryGetValue(key, out var entries))
            {
                grouped[key] = entries = [];
                order.Add(key);
            }

            // A blank column is the unused side of the line and reads as zero. A filled one that
            // is not an amount in display form — "1234.50", "1 234,50" — refuses the file rather
            // than reading as zero or, worse, as a hundred times itself.
            var debitText = Column(row, "ZNESEK_V_BREME");
            var creditText = Column(row, "ZNESEK_V_DOBRO");

            var debit = debitText is null ? 0m : BankValues.DisplayAmount(debitText);
            var credit = creditText is null ? 0m : BankValues.DisplayAmount(creditText);

            if (debit is null || credit is null)
            {
                return new HalcomStatementRead(
                    HalcomStatementKind.Extended, [],
                    $"Iznos „{(debit is null ? debitText : creditText)}” nije čitljiv — očekuje se "
                    + "oblik 3.000.000,00. Izvod se ne uvozi.");
            }

            var net = credit.Value - debit.Value;

            var paymentCode = Column(row, "OZN_VRSTE_POSLA");
            var orderId = Column(row, "ID_NALOGA");
            var purpose = Column(row, "NAMEN") ?? Column(row, "OPIS");

            var parts = new List<string>();
            if (purpose is not null) parts.Add(purpose);
            if (paymentCode is not null) parts.Add($"Šifra plaćanja: {paymentCode}");
            if (orderId is not null) parts.Add($"ID naloga: {orderId}");

            entries.Add(new StatementEntry(
                Amount: Math.Abs(net),
                Direction: net < 0 ? StatementDirection.Debit : StatementDirection.Credit,
                BookingDate: BankValues.Date(
                                 Column(row, "DATUM_KNJ") ?? Column(row, "DATUM_OBD"),
                                 BankValues.SerbianDate)
                             ?? default,
                ValueDate: BankValues.Date(
                    Column(row, "DATUM_VAL") ?? Column(row, "DATUM_OBD"), BankValues.SerbianDate),

                // SKLIC_ODOBR is the beneficiary's, SKLIC_OBREM the payer's — the same preference
                // as everywhere else here.
                Reference: Column(row, "SKLIC_ODOBR") ?? Column(row, "SKLIC_OBREM"),
                Description: parts.Count == 0 ? null : string.Join(" · ", parts),
                CounterpartyName: Column(row, "NAZIV_NAL_PREJ")?.TrimEnd(';', ' '),
                CounterpartyAccount: Column(row, "RAC_NAL_PREJ"),
                BankReference: orderId ?? Hash(line)));

            continue;

            string? Column(string[] cells, string name) =>
                index.TryGetValue(name, out var at) && at < cells.Length
                && cells[at].Trim() is { Length: > 0 } value
                    ? value
                    : null;
        }

        var statements = new List<BankStatement>();

        foreach (var key in order)
        {
            var entries = grouped[key];

            Disambiguate(entries);

            statements.Add(new BankStatement(
                AccountNumber: key.Account.Length == 0 ? null : key.Account,
                Currency: key.Currency,
                Number: key.Number.Length == 0 ? null : key.Number,
                StatementDate: entries.Count > 0 ? entries.Max(entry => entry.BookingDate) : null,

                // This variant carries no balances at all, which is why they are null rather than
                // zero: zero would claim the account was empty.
                OpeningBalance: null,
                ClosingBalance: null,
                Entries: entries));
        }

        return new HalcomStatementRead(HalcomStatementKind.Extended, statements);
    }

    // ---- shared --------------------------------------------------------------------------------

    /// <summary>
    /// Gives repeated bank references a deterministic suffix.
    ///
    /// One order can produce several lines carrying the same broj za reklamaciju — that is the
    /// bank's doing, not a fault — and a host deduplicating on the reference would keep the first
    /// and silently drop real payments. Suffixing by position makes them distinct while leaving the
    /// first occurrence untouched, and it is deterministic, so importing the same file twice still
    /// recognises every line as seen before.
    /// </summary>
    private static void Disambiguate(List<StatementEntry> entries)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].BankReference is not { Length: > 0 } reference) continue;

            var count = seen.GetValueOrDefault(reference);

            seen[reference] = count + 1;

            if (count > 0) entries[i] = entries[i] with { BankReference = $"{reference}#{count + 1}" };
        }
    }

    /// <summary>
    /// A stable identifier for a line the bank gave no reference for.
    ///
    /// Content-derived rather than positional, so a file re-exported with an extra line at the top
    /// still recognises the lines it shares with the first export.
    /// </summary>
    private static string Hash(string line) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(line)))[..16];

    /// <summary>
    /// A fixed-width field, trimmed, with empty read as absent.
    ///
    /// Tolerant of a line that ends before the field does — the extended variant shares this
    /// helper and its rows are not padded.
    /// </summary>
    private static string? Field(string line, int start, int length)
    {
        if (start >= line.Length) return null;

        var value = line.Substring(start, Math.Min(length, line.Length - start)).Trim();

        return value.Length == 0 ? null : value;
    }

    /// <summary>
    /// The lines that carry content, with the DOS end-of-file marker removed.
    ///
    /// The specification mandates a trailing <c>0x1A</c> and none of the real files carry one, so
    /// both are tolerated.
    /// </summary>
    private static List<string> ContentLines(string content) =>
    [
        .. content
            .Replace("\u001A", string.Empty, StringComparison.Ordinal)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Trim().Length > 0)
    ];

    private static readonly HalcomStatementRead NotRecognised =
        new(HalcomStatementKind.Unknown, []);
}
