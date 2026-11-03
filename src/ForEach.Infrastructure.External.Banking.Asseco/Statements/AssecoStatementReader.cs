using System.Xml.Linq;
using ForEach.Infrastructure.External.Banking.Formats;
using ForEach.Infrastructure.External.Banking.Statements;

namespace ForEach.Infrastructure.External.Banking.Asseco.Statements;

/// <summary>What an Asseco file turned out to be.</summary>
public enum AssecoStatementKind
{
    /// <summary>Nothing recognised it.</summary>
    Unknown,

    /// <summary>A domestic statement — <c>&lt;stmtrs&gt;</c>, answering a request.</summary>
    Statement,

    /// <summary>An instant notification — <c>&lt;pmtnotification&gt;</c>. Same shape.</summary>
    Notification,

    /// <summary>
    /// A devizni (FX Client) statement. Same root, different child set — see the reader.
    /// </summary>
    ForeignStatement,

    /// <summary>The bank answered with an error rather than a statement.</summary>
    BankError
}

/// <summary>What an Asseco file — XML or fixed-width text — was read as, and what it held.</summary>
/// <param name="Kind">
/// Which document answered, or <see cref="AssecoStatementKind.Unknown"/> when none did. Carried
/// out because "not a statement" and "a statement with no entries" are different things to tell
/// somebody at an import screen, and both arrive as an empty statement otherwise.
/// </param>
/// <param name="Statements">
/// Every statement in the file — several under a <c>&lt;stmtrslist&gt;</c>, one otherwise, none
/// when nothing was read.
/// </param>
/// <param name="Refusal">
/// Why a recognised file was not read, in Serbian, when
/// <see cref="AssecoStatementRead.Kind"/> is <see cref="AssecoStatementKind.BankError"/>. Null
/// otherwise.
/// </param>
public sealed record AssecoStatementRead(
    AssecoStatementKind Kind,
    IReadOnlyList<BankStatement> Statements,
    string? Refusal = null)
{
    /// <summary>The first statement, or an empty one — the ordinary single-statement case.</summary>
    public BankStatement Statement => Statements.Count > 0 ? Statements[0] : BankStatement.Empty;
}

/// <summary>
/// Reads an Asseco XML account statement — the iBank / OfficeBanking / FX Client family, exported
/// by Banca Intesa and the other banks running Asseco's e-banking.
///
/// **Three roots, and two different documents inside them.** <c>&lt;stmtrs&gt;</c> is a statement
/// answering a request, <c>&lt;pmtnotification&gt;</c> is the same document pushed as a
/// notification, and <c>&lt;stmtrslist&gt;</c> wraps several of either.
///
/// **The devizni statement shares the <c>&lt;stmtrs&gt;</c> root and agrees with the domestic one
/// about almost nothing else**, which is the trap this reader exists to avoid. It writes
/// <c>stmbal</c>/<c>closing</c> where the domestic one writes <c>ledgerbal</c>/<c>availbal</c>,
/// <c>stmtno</c> for <c>stmtnumber</c>, <c>dateasto</c> for the statement's date — and it carries
/// no counterparty on the transaction at all. Read with the domestic element names it *succeeds*,
/// yielding a statement with no balances, no number and nameless lines, which looks like a quiet
/// day rather than an unread file. So the two are told apart by <c>rstype</c> and read separately.
///
/// **`ledgerbal` and `stmbal` are the OPENING balance**, which reads backwards until you see the
/// specification's own words: it is the balance *"sa kojim je zaključen prethodni izvod"*. A reader
/// that took it for the current balance would have the pair the wrong way round and every correct
/// file would fail the reconciliation check.
///
/// **Balances are read as declared and not enforced.** The vendor's own demo file is internally
/// inconsistent — its closing balance and the sum of its transactions differ by ninety lipa — so
/// refusing on a mismatch here would refuse the file the bank ships as an example. Whether the
/// entries account for the movement is <see cref="BankStatement.Reconciles"/>, which is the host's
/// to act on.
///
/// **It never throws.** Anything unreadable comes back <see cref="AssecoStatementKind.Unknown"/>.
/// </summary>
public static class AssecoStatementReader
{
    private const string ListRoot = "stmtrslist";
    private const string StatementRoot = "stmtrs";
    private const string NotificationRoot = "pmtnotification";

    /// <summary>Money in, as the <c>benefit</c> element writes it.</summary>
    private const string CreditIndicator = "credit";

    /// <summary>
    /// The document-type prefix that marks a devizni statement — <c>ibank.fps.</c>, the foreign
    /// payment system. It is the only thing that tells the two documents apart before reading them.
    /// </summary>
    private const string ForeignTypePrefix = "ibank.fps.";

    /// <summary>
    /// Reads whatever was uploaded and says what it was: every statement under a list root, the
    /// one statement otherwise, or the bank's own error when the document is one.
    /// </summary>
    public static AssecoStatementRead Read(string? xml)
    {
        var root = SafeXml.Load(xml)?.Root;

        if (root is null) return NotRecognised;

        var rootName = root.Name.LocalName.ToLowerInvariant();

        if (rootName is ListRoot)
        {
            var documents = root.Elements()
                .Where(child => child.Name.LocalName.ToLowerInvariant()
                    is StatementRoot or NotificationRoot)
                .ToList();

            if (documents.Count == 0) return NotRecognised;

            // A refusal on any one of them refuses the file: a list is a single upload, and
            // importing three of five statements while saying nothing about the other two is how a
            // day goes missing.
            foreach (var document in documents)
            {
                if (RefusalFor(document) is { } refusal) return refusal;
            }

            return new AssecoStatementRead(
                documents.Any(IsForeign)
                    ? AssecoStatementKind.ForeignStatement
                    : AssecoStatementKind.Statement,
                [.. documents.Select(ReadStatement)]);
        }

        if (rootName is not (StatementRoot or NotificationRoot)) return NotRecognised;

        if (RefusalFor(root) is { } single) return single;

        var kind = IsForeign(root)
            ? AssecoStatementKind.ForeignStatement
            : rootName is NotificationRoot
                ? AssecoStatementKind.Notification
                : AssecoStatementKind.Statement;

        return new AssecoStatementRead(kind, [ReadStatement(root)]);
    }

    private static bool IsForeign(XElement document) =>
        DocumentTypeOf(document).StartsWith(ForeignTypePrefix, StringComparison.OrdinalIgnoreCase);

    private static string DocumentTypeOf(XElement document) =>
        Value(document, "rstype") ?? Value(document, "notiftype") ?? string.Empty;

    /// <summary>The one reason a recognised document is not read: the bank saying no.</summary>
    private static AssecoStatementRead? RefusalFor(XElement document)
    {
        var status = Value(Child(document, "status"), "code");

        // Absent is fine — some exports carry no status block at all. Present and non-zero is the
        // bank telling us this is an error document that happens to have a statement's root.
        if (status is null || status == "0") return null;

        var detail = Value(Child(document, "status"), "detail");

        return new AssecoStatementRead(
            AssecoStatementKind.BankError, [],
            $"Banka je vratila grešku {status}"
            + (string.IsNullOrWhiteSpace(detail) ? "." : $": {detail}"));
    }

    private static BankStatement ReadStatement(XElement document) =>
        IsForeign(document) ? ReadForeign(document) : ReadDomestic(document);

    // ---- the domestic statement ----------------------------------------------------------------

    private static BankStatement ReadDomestic(XElement document) =>
        new(
            AccountNumber: Value(document, "acctid"),
            Currency: BankValues.Currency(Value(document, "curdef")),

            // Populated on a real izvod and empty on the intraday "dnevni nalozi" message, where
            // the specification says so outright: "stmtnumber — uvek prazno".
            Number: Value(document, "stmtnumber"),

            // The day this statement closed. Its own dtasof rather than the ledger balance's,
            // which belongs to the statement before it.
            StatementDate: BankValues.Date(Value(Child(document, "availbal"), "dtasof")),
            OpeningBalance: BankValues.Amount(Value(Child(document, "ledgerbal"), "balamt")),
            ClosingBalance: BankValues.Amount(Value(Child(document, "availbal"), "balamt")),
            Entries: [.. Transactions(document).Select(ReadDomesticEntry)]);

    private static StatementEntry ReadDomesticEntry(XElement transaction)
    {
        var isCredit = IsCredit(transaction);

        var payee = Child(transaction, "payeeinfo");
        var payeeAccount = Child(transaction, "payeeaccountinfo");

        return new StatementEntry(
            // Absolute in the file — "iznos se daje u apsolutnom iznosu" — with the direction
            // carried by benefit. Abs anyway, so a file that ever wrote a negative beside a debit
            // cannot be counted twice over.
            Amount: Math.Abs(BankValues.Amount(Value(transaction, "trnamt")) ?? 0m),
            Direction: isCredit ? StatementDirection.Credit : StatementDirection.Debit,
            BookingDate: BankValues.Date(Value(transaction, "dtposted"))
                         ?? BankValues.Date(Value(transaction, "dtavail"))
                         ?? default,
            ValueDate: BankValues.Date(Value(transaction, "dtavail")),
            Reference: DomesticReferenceOf(transaction, isCredit),
            Description: DomesticDescriptionOf(transaction),
            CounterpartyName: Value(payee, "name"),
            CounterpartyAccount: Value(payeeAccount, "acctid"),

            // "Broj za reklamaciju… vrednost je jedinstvena na nivou jedne banke" — which is
            // exactly the guarantee a second import of the same file needs.
            BankReference: Value(transaction, "fitid") ?? Value(transaction, "trnuid"));
    }

    /// <summary>
    /// The poziv na broj that names the invoice — which is a **different element depending on the
    /// direction**, and this is the one place in the reader where being wrong is invisible.
    ///
    /// Asseco names its two references from the point of view of the statement's owner, not of the
    /// payment: <c>refnumber</c> is "poziv na broj vlasnika računa za koji se daje izvod" — ours —
    /// and <c>payeerefnumber</c> is the other side's. So the reference belonging to whoever is
    /// being *paid* swaps over:
    ///
    /// - money in — we are the beneficiary, and the number the payer quoted against our invoice is
    ///   <c>refnumber</c>;
    /// - money out — the supplier is the beneficiary, and the number we quoted against *their*
    ///   invoice is <c>payeerefnumber</c>.
    ///
    /// Note this is the opposite convention to ROL, where the field named "korisnika" is the
    /// beneficiary's in both directions. Two formats, two namings, one question — which is why
    /// each reader answers it in its own file rather than in a shared helper that would have to
    /// take a flag.
    /// </summary>
    private static string? DomesticReferenceOf(XElement transaction, bool isCredit)
    {
        var ours = Value(transaction, "refnumber");
        var theirs = Value(transaction, "payeerefnumber");

        return isCredit ? ours ?? theirs : theirs ?? ours;
    }

    private static string? DomesticDescriptionOf(XElement transaction)
    {
        var parts = new List<string>();

        Add(parts, Value(transaction, "purpose"));

        if (Value(transaction, "purposecode") is { } code) Add(parts, $"Šifra plaćanja: {code}");

        if (Value(transaction, "refnumber") is { } ours)
            Add(parts, $"Poziv na broj (naš): ({Value(transaction, "refmodel") ?? "00"}) {ours}");

        if (Value(transaction, "payeerefnumber") is { } theirs)
        {
            Add(parts, $"Poziv na broj (druga strana): "
                       + $"({Value(transaction, "payeerefmodel") ?? "00"}) {theirs}");
        }

        return Joined(parts);
    }

    // ---- the devizni (FX Client) statement -----------------------------------------------------

    /// <summary>
    /// The devizni statement, whose every structural element is named differently.
    ///
    /// <c>stmbal</c> is the opening balance and <c>closing</c> the closing one; the statement's
    /// number is <c>stmtno</c> and its date <c>dateasto</c>. The currency is a real one rather than
    /// the domestic file's constant dinar.
    /// </summary>
    private static BankStatement ReadForeign(XElement document)
    {
        var beneficiaries = Beneficiaries(document);

        return new BankStatement(
            AccountNumber: Value(document, "acctid"),
            Currency: BankValues.Currency(Value(document, "curdef")),
            Number: Value(document, "stmtno"),
            StatementDate: BankValues.Date(Value(document, "dateasto"))
                           ?? BankValues.Date(Value(Child(document, "closing"), "dtasof")),
            OpeningBalance: BankValues.Amount(Value(Child(document, "stmbal"), "balamt")),
            ClosingBalance: BankValues.Amount(Value(Child(document, "closing"), "balamt")),
            Entries:
            [
                .. Transactions(document)
                    .Select(transaction => ReadForeignEntry(transaction, beneficiaries))
            ]);
    }

    private static StatementEntry ReadForeignEntry(
        XElement transaction, IReadOnlyDictionary<string, Beneficiary> beneficiaries)
    {
        var documentId = Value(transaction, "documentid");

        // The counterparty is not on the transaction — it is on the document the transaction was
        // generated from, in a list beside them. Absent where the document is not in the list,
        // which is honest: a name invented here would be attributed to a payment nobody checked.
        var beneficiary = documentId is not null && beneficiaries.TryGetValue(documentId, out var found)
            ? found
            : null;

        var parts = new List<string>();

        Add(parts, Value(transaction, "trndsc") ?? Value(transaction, "description"));

        if (documentId is not null) Add(parts, $"Broj dokumenta: {documentId}");

        // The dinar counter-value, which the devizni statement states per line. Kept in the text
        // rather than in the amount: the amount is what moved, and it moved in this currency.
        if (Value(transaction, "trnamtlocal") is { } local)
            Add(parts, $"Dinarska protivvrednost: {local}");

        return new StatementEntry(
            Amount: Math.Abs(BankValues.Amount(Value(transaction, "trnamt")) ?? 0m),
            Direction: IsCredit(transaction) ? StatementDirection.Credit : StatementDirection.Debit,
            BookingDate: BankValues.Date(Value(transaction, "dtposted"))
                         ?? BankValues.Date(Value(transaction, "dtavail"))
                         ?? default,
            ValueDate: BankValues.Date(Value(transaction, "dtavail")),

            // A devizni line has one reference and it is not split by direction, so there is no
            // beneficiary-versus-payer question to get wrong here.
            Reference: Value(transaction, "reference"),
            Description: Joined(parts),
            CounterpartyName: beneficiary?.Name,
            CounterpartyAccount: beneficiary?.Account,
            BankReference: Value(transaction, "fitid") ?? documentId);
    }

    private sealed record Beneficiary(string? Name, string? Account);

    /// <summary>
    /// The documents a devizni statement's transactions were generated from, by their id.
    ///
    /// The account element is spelled <c>accountifo</c> in the specification — a typo the files
    /// follow, since they are generated from it — so both spellings are accepted. Reading only the
    /// corrected one would leave every counterparty account blank against a file that is exactly as
    /// documented.
    /// </summary>
    private static IReadOnlyDictionary<string, Beneficiary> Beneficiaries(XElement document)
    {
        var found = new Dictionary<string, Beneficiary>(StringComparer.Ordinal);

        foreach (var entry in Descendants(document, "document"))
        {
            if (Value(entry, "documentid") is not { } id || found.ContainsKey(id)) continue;

            var beneficiary = Child(entry, "beneficiary");

            if (beneficiary is null) continue;

            var account = Child(beneficiary, "accountifo") ?? Child(beneficiary, "accountinfo");

            found[id] = new Beneficiary(Value(beneficiary, "name"), Value(account, "acctid"));
        }

        return found;
    }

    // ---- shared --------------------------------------------------------------------------------

    private static bool IsCredit(XElement transaction) =>
        string.Equals(
            Value(transaction, "benefit"), CreditIndicator, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The statement's transactions.
    ///
    /// Taken from under <c>trnlist</c> where there is one, so a devizni file's
    /// <c>documentlist</c> cannot contribute a line: both hold elements this reader would
    /// otherwise treat as transactions, and a statement carrying each payment twice balances
    /// against nothing.
    /// </summary>
    private static IEnumerable<XElement> Transactions(XElement document)
    {
        var list = Child(document, "trnlist");

        return Descendants(list ?? document, "stmttrn");
    }

    private static IEnumerable<XElement> Descendants(XElement? parent, string localName) =>
        parent is null
            ? []
            : parent.Descendants().Where(element => element.Name.LocalName == localName);

    private static XElement? Child(XElement? parent, string localName) =>
        parent?.Elements().FirstOrDefault(element => element.Name.LocalName == localName);

    /// <summary>
    /// The text of a named descendant, with whitespace-only read as absent.
    ///
    /// Descendants rather than children because the transaction's counterparty sits one level down
    /// and the statement's own fields sit at the top; a present-but-empty element — which this
    /// format writes freely, <c>&lt;trnuid/&gt;</c> on nearly every line — says nothing.
    /// </summary>
    private static string? Value(XElement? parent, string localName)
    {
        var element = Descendants(parent, localName).FirstOrDefault();

        var text = element?.Value.Trim();

        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static void Add(List<string> parts, string? part)
    {
        if (!string.IsNullOrWhiteSpace(part)) parts.Add(part);
    }

    private static string? Joined(List<string> parts) =>
        parts.Count == 0 ? null : string.Join(" · ", parts);

    private static readonly AssecoStatementRead NotRecognised =
        new(AssecoStatementKind.Unknown, []);
}
