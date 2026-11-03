using System.Globalization;
using System.Xml.Linq;

namespace ForEach.Infrastructure.External.Banking.Statements;

/// <summary>
/// Reads an ISO 20022 <c>camt.053</c> account statement.
///
/// **Why this format and not a bank's own.** Every bank in the region is moving to camt.053 — the
/// old fixed-width and delimited exports were announced for withdrawal — and it is a published
/// standard rather than a layout that changes when somebody redesigns an export screen. A reader
/// written against one bank's shape is a reader that breaks at the next bank and cannot be checked
/// against anything.
///
/// **Version-agnostic by construction.** camt.053.001.02 and .001.08 differ in their namespace and
/// in fields this does not read; both put the account, the balances and the entries under the same
/// element names. So every lookup is by local name and the namespace is ignored — a reader pinned
/// to one namespace answers "empty statement" for a file that is entirely valid, which is the
/// worst possible failure here because it looks like a quiet day at the bank.
///
/// **It never throws.** A file somebody chose by mistake, a truncated download, an export from a
/// system that is not a bank: each produces <see cref="BankStatement.Empty"/> rather than an
/// exception, because the caller's honest answer is "this is not a statement" and not a stack
/// trace on an import screen.
///
/// **What it deliberately does not do.** It does not decide what a payment means: no contact
/// matching, no invoice allocation, no currency conversion. Those are the host's decisions and
/// depend on the host's own data — a library that guessed them would be a library that has to know
/// about customers.
/// </summary>
public static class Camt053Reader
{
    /// <summary>Money in — the payer's side is credited to us.</summary>
    private const string CreditIndicator = "CRDT";

    /// <summary>Opening booked balance, as the code appears in the file.</summary>
    private const string OpeningBalanceCode = "OPBD";

    /// <summary>Closing booked balance.</summary>
    private const string ClosingBalanceCode = "CLBD";

    /// <summary>
    /// The first statement in the document.
    ///
    /// One file usually holds one; where it holds several — a bank sending a week at once — this
    /// reads the first, and <see cref="ReadAll"/> reads them all. The single-statement method
    /// exists because that is the ordinary case and asking every caller to index into a list would
    /// be ceremony.
    /// </summary>
    public static BankStatement Read(string? xml) =>
        ReadAll(xml).FirstOrDefault() ?? BankStatement.Empty;

    /// <summary>Every statement in the document, in the order the file lists them.</summary>
    public static IReadOnlyList<BankStatement> ReadAll(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return [];

        XDocument document;

        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            // Not XML at all — a PDF somebody renamed, a truncated download. "Not a statement" is
            // the answer, and it is the caller's to report.
            return [];
        }

        if (document.Root is null) return [];

        return
        [
            .. Elements(document.Root, "Stmt").Select(ReadStatement)
        ];
    }

    private static BankStatement ReadStatement(XElement statement)
    {
        var account = Element(statement, "Acct");

        return new BankStatement(
            AccountNumber: AccountNumberOf(account),

            // From the account first, and from a balance's amount as a fallback: some banks state
            // the currency only on the figures. Empty rather than a guess where neither says.
            Currency: Value(account, "Ccy")
                      ?? Elements(statement, "Bal")
                          .Select(balance => Attribute(Element(balance, "Amt"), "Ccy"))
                          .FirstOrDefault(currency => currency is not null)
                      ?? string.Empty,

            // The sequence number people quote — "izvod 143" — rather than the bank's internal id,
            // which is usually a long opaque string nobody reads out.
            Number: Value(statement, "LglSeqNb") ?? Value(statement, "ElctrncSeqNb"),
            StatementDate: DateOf(Element(statement, "CreDtTm"))
                           ?? DateIn(Element(statement, "FrToDt"), "ToDtTm"),
            OpeningBalance: BalanceOf(statement, OpeningBalanceCode),
            ClosingBalance: BalanceOf(statement, ClosingBalanceCode),
            Entries: [.. Elements(statement, "Ntry").Select(ReadEntry)]);
    }

    private static StatementEntry ReadEntry(XElement entry)
    {
        // The transaction detail, where the payer and the reference actually live. Absent on a bank
        // charge and on some aggregated entries, which is why every read below tolerates it being
        // null rather than assuming the shape.
        var details = Element(Element(entry, "NtryDtls"), "TxDtls");

        var counterparty = Element(Element(details, "RltdPties"), "Dbtr")
                           ?? Element(Element(details, "RltdPties"), "Cdtr");

        var counterpartyAccount = Element(Element(details, "RltdPties"), "DbtrAcct")
                                  ?? Element(Element(details, "RltdPties"), "CdtrAcct");

        return new StatementEntry(
            // Absolute, because the direction carries the sign. A file that ever wrote a negative
            // amount beside a DBIT indicator would otherwise be counted twice over.
            Amount: Math.Abs(Decimal(Element(entry, "Amt")) ?? 0m),
            Direction: string.Equals(Value(entry, "CdtDbtInd"), CreditIndicator, StringComparison.OrdinalIgnoreCase)
                ? StatementDirection.Credit
                : StatementDirection.Debit,

            // The booking date is the statement's own date for the entry. Falling back to the
            // value date rather than to today: an entry with no booking date belongs to the day
            // the money moved, not to the day somebody imported the file.
            BookingDate: DateIn(Element(entry, "BookgDt"), "Dt")
                         ?? DateIn(Element(entry, "ValDt"), "Dt")
                         ?? default,
            ValueDate: DateIn(Element(entry, "ValDt"), "Dt"),
            Reference: ReferenceOf(entry, details),
            Description: Value(entry, "AddtlNtryInf") ?? RemittanceText(details),
            CounterpartyName: Value(counterparty, "Nm"),
            CounterpartyAccount: AccountNumberOf(counterpartyAccount),
            BankReference: Value(entry, "AcctSvcrRef")
                           ?? Value(Element(details, "Refs"), "AcctSvcrRef")
                           ?? Value(Element(details, "Refs"), "EndToEndId"));
    }

    /// <summary>
    /// The payment reference, structured first.
    ///
    /// A Serbian transfer's poziv na broj is what matches a payment to an invoice, and banks put it
    /// in either the structured creditor reference or the free-text remittance line. The structured
    /// one wins because it is the box the payer was asked to fill in; the free text is whatever
    /// they typed, and it is read only when there is nothing better.
    /// </summary>
    private static string? ReferenceOf(XElement entry, XElement? details)
    {
        var remittance = Element(details, "RmtInf") ?? Element(entry, "RmtInf");

        var structured = Value(
            Element(Element(Element(remittance, "Strd"), "CdtrRefInf"), "Ref"), null);

        if (!string.IsNullOrWhiteSpace(structured)) return structured;

        var endToEnd = Value(Element(details, "Refs"), "EndToEndId");

        // "NOTPROVIDED" is what the standard says to put when there is no reference, and a host
        // that matched on it would match every unreferenced payment to the same nothing.
        if (!string.IsNullOrWhiteSpace(endToEnd)
            && !string.Equals(endToEnd, "NOTPROVIDED", StringComparison.OrdinalIgnoreCase))
        {
            return endToEnd;
        }

        return RemittanceText(details);
    }

    private static string? RemittanceText(XElement? details)
    {
        var unstructured = Elements(Element(details, "RmtInf"), "Ustrd")
            .Select(element => element.Value.Trim())
            .Where(text => text.Length > 0)
            .ToList();

        return unstructured.Count == 0 ? null : string.Join(" ", unstructured);
    }

    /// <summary>
    /// An account number: IBAN where the bank states one, otherwise the domestic number under
    /// <c>Othr/Id</c> — which is what a Serbian statement carries.
    /// </summary>
    private static string? AccountNumberOf(XElement? account)
    {
        var id = Element(account, "Id");

        return Value(id, "IBAN") ?? Value(Element(id, "Othr"), "Id");
    }

    /// <summary>
    /// A balance of the given type, signed.
    ///
    /// The type code is nested two elements deep and can be either a code or a proprietary string;
    /// both are checked, because a bank using the proprietary form would otherwise report no
    /// balances at all and the reconciliation check would go quiet rather than failing.
    /// </summary>
    private static decimal? BalanceOf(XElement statement, string code)
    {
        foreach (var balance in Elements(statement, "Bal"))
        {
            var type = Element(Element(balance, "Tp"), "CdOrPrtry");

            var stated = Value(type, "Cd") ?? Value(type, "Prtry");

            if (!string.Equals(stated, code, StringComparison.OrdinalIgnoreCase)) continue;

            if (Decimal(Element(balance, "Amt")) is not { } amount) continue;

            // Here the sign is real: an overdrawn account has a negative balance, and the debit
            // indicator is how the file says so.
            return string.Equals(
                Value(balance, "CdtDbtInd"), CreditIndicator, StringComparison.OrdinalIgnoreCase)
                ? amount
                : -amount;
        }

        return null;
    }

    // ---- element access, all by local name -----------------------------------------------------

    /// <summary>
    /// Every descendant with this local name, whatever namespace it is in.
    ///
    /// The namespace is the version — camt.053.001.02 against .001.08 — and pinning to one makes a
    /// perfectly valid file read as an empty statement, which looks exactly like a quiet day at the
    /// bank. Descendants rather than children so a bank that nests an element one level deeper than
    /// the example still reads.
    /// </summary>
    private static IEnumerable<XElement> Elements(XElement? parent, string localName) =>
        parent is null
            ? []
            : parent.Descendants().Where(e => e.Name.LocalName == localName);

    private static XElement? Element(XElement? parent, string localName) =>
        Elements(parent, localName).FirstOrDefault();

    /// <summary>
    /// The text of a named child, or of the element itself when <paramref name="localName"/> is
    /// null. Whitespace-only comes back as null: a present-but-empty element says nothing, and a
    /// caller checking for null should not have to check for "" as well.
    /// </summary>
    private static string? Value(XElement? parent, string? localName)
    {
        var element = localName is null ? parent : Element(parent, localName);

        var text = element?.Value.Trim();

        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string? Attribute(XElement? element, string name)
    {
        var value = element?.Attribute(name)?.Value.Trim();

        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// A decimal, parsed invariantly.
    ///
    /// Invariant and not negotiable: the file writes 1234.56 whatever the reader's machine thinks a
    /// decimal separator is, and a Serbian culture would read that as 123456 — a hundredfold error
    /// on money, silently, on whichever server happens to run the import.
    /// </summary>
    private static decimal? Decimal(XElement? element) =>
        element is not null
        && decimal.TryParse(
            element.Value.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static DateOnly? DateIn(XElement? parent, string localName) =>
        DateOf(Element(parent, localName));

    /// <summary>
    /// A date, from either an ISO date or an ISO timestamp — camt writes both, and which one
    /// depends on the element rather than on the bank.
    /// </summary>
    private static DateOnly? DateOf(XElement? element)
    {
        var text = element?.Value.Trim();

        if (string.IsNullOrEmpty(text)) return null;

        if (DateOnly.TryParse(
                text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }

        return DateTimeOffset.TryParse(
            text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var moment)
            ? DateOnly.FromDateTime(moment.Date)
            : null;
    }
}
