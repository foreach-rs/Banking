using System.Xml.Linq;
using ForEach.Infrastructure.External.Banking.Formats;
using ForEach.Infrastructure.External.Banking.Statements;

namespace ForEach.Infrastructure.External.Banking.Rol.Statements;

/// <summary>Which ROL statement a file turned out to be.</summary>
public enum RolStatementKind
{
    /// <summary>Nothing recognised it.</summary>
    Unknown,

    /// <summary>Dinar current account — <c>&lt;TransakcioniRacunPrivredaIzvod&gt;</c>.</summary>
    Dinar,

    /// <summary>Foreign-currency account — <c>&lt;RacunPrivredaIzvod&gt;</c>.</summary>
    Foreign
}

/// <summary>What a ROL file — XML or fixed-width text — was read as, and what it held.</summary>
/// <param name="Kind">
/// Which shape answered. Carried out because "this file is not a ROL statement" and "this statement
/// had no entries" are different sentences to somebody at an import screen, and both arrive as an
/// empty statement otherwise.
/// </param>
/// <param name="Statement">
/// What was read, or <see cref="BankStatement.Empty"/> when nothing recognised the file — or when
/// it was recognised and refused. ROL writes one statement per file, so there is no list here.
/// </param>
/// <param name="Refusal">
/// Why a recognised file was not read, in Serbian: an amount that is not a number, a date that
/// does not parse. Null when the file was read, or when nothing recognised it.
/// </param>
public sealed record RolStatementRead(
    RolStatementKind Kind,
    BankStatement Statement,
    string? Refusal = null);

/// <summary>
/// Reads a ROL (Raiffeisen OnLine) XML account statement — the format Raiffeisen and OTP both
/// export.
///
/// **Attribute-based, not element-based.** Every value is an XML attribute on one of two elements:
/// <c>&lt;Zaglavlje&gt;</c> carries the whole header and each <c>&lt;Stavke&gt;</c> is one
/// transaction. That is unusual enough to be worth saying, because a reader written against the
/// habits of camt.053 finds no child elements and reports an empty statement for a perfectly good
/// file.
///
/// **Two roots, one reader.** The dinar statement is
/// <c>&lt;TransakcioniRacunPrivredaIzvod&gt;</c> and the devizni one is
/// <c>&lt;RacunPrivredaIzvod&gt;</c>. They differ in three attributes and not in shape, so one
/// reader handles both rather than two that would drift.
///
/// **It never throws.** A renamed PDF, a truncated download, an export from something that is not
/// a bank: each comes back as <see cref="RolStatementKind.Unknown"/>, because the caller's honest
/// answer is "this is not a statement" and not a stack trace on an import screen.
/// </summary>
public static class RolStatementReader
{
    private const string DinarRoot = "TransakcioniRacunPrivredaIzvod";
    private const string ForeignRoot = "RacunPrivredaIzvod";

    /// <summary>
    /// Reads the statement in the document.
    ///
    /// ROL writes one statement per file — there is no list wrapper in the format — so unlike
    /// camt.053 there is no <c>ReadAll</c> here. If a wrapper ever appears, it arrives as a new
    /// root and this method is where it is recognised.
    /// </summary>
    public static RolStatementRead Read(string? xml)
    {
        var root = SafeXml.Load(xml)?.Root;

        if (root is null) return NotRecognised;

        var kind = KindOf(root);

        if (kind is RolStatementKind.Unknown) return NotRecognised;

        var header = Child(root, "Zaglavlje");

        // A ROL root with no header is a file that opened and said nothing. Treated as
        // unrecognised rather than as an empty statement, because "no entries" would invite the
        // host to import a day that was never read.
        if (header is null) return NotRecognised;

        var statementDate = BankValues.Date(Attribute(header, "DatumIzvoda"), BankValues.SerbianDate);

        var lines = root.Elements()
            .Where(element => element.Name.LocalName == "Stavke")
            .ToList();

        // Every amount is read before any line is. "100,00" in a column that writes "100.00" is
        // not zero, and a line booked at zero — in the credit direction — on a statement that
        // otherwise looks complete is the quiet failure this reader exists to avoid.
        foreach (var stavka in lines)
        {
            foreach (var column in new[] { "Duguje", "Potrazuje" })
            {
                if (Attribute(stavka, column) is { } text && BankValues.Amount(text) is null)
                {
                    return new RolStatementRead(
                        kind, BankStatement.Empty,
                        $"Iznos „{text}” u koloni {column} nije čitljiv. Izvod se ne uvozi.");
                }
            }
        }

        var statement = new BankStatement(
            // The Partija is the 18-digit domestic account. On a devizni statement it is a
            // substring of the IBAN, so an account recorded either way still matches.
            AccountNumber: Attribute(header, "Partija"),

            // The dinar root states no currency at all — it is the dinar account statement, and
            // that is what the root name means.
            Currency: kind is RolStatementKind.Dinar
                ? "RSD"
                : BankValues.Currency(Attribute(header, "OznakaValute")),

            // The number people quote — "izvod 181" — not the bank's internal IzvodID.
            Number: Attribute(header, "BrojIzvoda"),
            StatementDate: statementDate,
            OpeningBalance: BankValues.Amount(Attribute(header, "PrethodnoStanje")),
            ClosingBalance: BankValues.Amount(Attribute(header, "NovoStanje")),
            Entries: [.. lines.Select(stavka => ReadEntry(stavka, statementDate, kind))]);

        return new RolStatementRead(kind, statement);
    }

    private static StatementEntry ReadEntry(
        XElement stavka, DateOnly? statementDate, RolStatementKind kind)
    {
        // Two columns rather than an indicator, which is the format's own shape: a line states what
        // it owes in one and what it credits in the other, and the unused one is "0". Unreadable
        // was refused above; absent is the unused side.
        var debit = BankValues.Amount(Attribute(stavka, "Duguje")) ?? 0m;
        var credit = BankValues.Amount(Attribute(stavka, "Potrazuje")) ?? 0m;

        // Netted rather than "whichever is non-zero", and that is deliberate: real files carry a
        // *negative* Potrazuje for a storno pair. Reading the non-zero column would book that
        // correction as money arriving, in the direction that flatters the balance, and the
        // statement would still add up because the header's own turnover split counts it the same
        // way. Netting makes the sign the fact.
        var net = credit - debit;

        return new StatementEntry(
            Amount: Math.Abs(net),
            Direction: net < 0 ? StatementDirection.Debit : StatementDirection.Credit,

            // A dinar Stavka carries only DatumValute; the day it was booked is the statement's
            // own. The devizni one carries DatumObrade and that is preferred where present.
            BookingDate: BankValues.Date(Attribute(stavka, "DatumObrade"), BankValues.SerbianDate)
                         ?? statementDate
                         ?? default,
            ValueDate: BankValues.Date(Attribute(stavka, "DatumValute"), BankValues.SerbianDate),
            Reference: ReferenceOf(stavka),
            Description: DescriptionOf(stavka, kind),
            CounterpartyName: Attribute(stavka, "NalogKorisnik"),
            CounterpartyAccount: Attribute(stavka, "BrojRacunaPrimaocaPosiljaoca")
                                 ?? Attribute(stavka, "RacunNalogodavacKorisnik"),

            // "Broj za reklamaciju" — the number somebody quotes to the bank when disputing a
            // line, which is exactly what makes it unique and what makes a second import of the
            // same file detectable.
            BankReference: Attribute(stavka, "BrojZaReklamaciju") ?? Attribute(stavka, "Referenca"));
    }

    /// <summary>
    /// The poziv na broj, taking the **beneficiary's** reference first.
    ///
    /// This is the one decision in the reader that is silent when wrong, so it is written down.
    /// A ROL line carries two references: <c>PozivNaBrojZaduzenjaOdobrenja</c> is the *ordering
    /// customer's* (poziv na broj zaduženja) and <c>PozivNaBrojKorisnika</c> is the *beneficiary's*
    /// (poziv na broj odobrenja).
    ///
    /// The beneficiary's is the one that names the invoice being paid, and it is right in **both**
    /// directions:
    ///
    /// - money in — a customer pays us, we are the beneficiary, and what they quoted is our own
    ///   invoice number;
    /// - money out — we pay a supplier, they are the beneficiary, and what we quoted is their
    ///   invoice number.
    ///
    /// Which is exactly the asymmetry the host already relies on: a receipt is matched against our
    /// own invoice number and a payment out against the supplier's. Taking the debit reference
    /// instead would match inbound payments against whatever the *payer* uses for their own
    /// bookkeeping, which is a number this company has never seen.
    /// </summary>
    private static string? ReferenceOf(XElement stavka) =>
        Attribute(stavka, "PozivNaBrojKorisnika")
        ?? Attribute(stavka, "PozivNaBrojZaduzenjaOdobrenja");

    /// <summary>
    /// What the line was for, with the details a person needs when the matcher cannot decide.
    ///
    /// The model is kept beside its reference rather than folded into it: a model 97 reference
    /// carries a control prefix, and gluing the two together would produce a string that matches no
    /// invoice number while looking like one.
    /// </summary>
    private static string? DescriptionOf(XElement stavka, RolStatementKind kind)
    {
        var parts = new List<string>();

        Add(Attribute(stavka, "Opis"));

        if (Attribute(stavka, "SifraPlacanja") is { } code)
        {
            var label = Attribute(stavka, "SifraPlacanjaOpis");

            Add(label is null ? $"Šifra plaćanja: {code}" : $"Šifra plaćanja: {code} ({label})");
        }

        if (Attribute(stavka, "PozivNaBrojKorisnika") is { } credit)
            Add($"Poziv na broj odobrenja: ({Attribute(stavka, "ModelKorisnika") ?? "00"}) {credit}");

        if (Attribute(stavka, "PozivNaBrojZaduzenjaOdobrenja") is { } debit)
        {
            Add($"Poziv na broj zaduženja: "
                + $"({Attribute(stavka, "ModelZaduzenjaOdobrenja") ?? "00"}) {debit}");
        }

        if (Attribute(stavka, "VasBrojNaloga") is { } ours) Add($"Vaš broj naloga: {ours}");

        if (kind is RolStatementKind.Foreign)
        {
            Add(Attribute(stavka, "TekstOsnova") is { } basis ? $"Osnov plaćanja: {basis}" : null);
            Add(Attribute(stavka, "Napomena") is { } note ? $"Napomena: {note}" : null);

            Add(Attribute(stavka, "DinarskaProtivvrednost") is { } counter
                ? $"Dinarska protivvrednost: {counter}"
                : null);
        }

        return parts.Count == 0 ? null : string.Join(" · ", parts);

        void Add(string? part)
        {
            if (!string.IsNullOrWhiteSpace(part)) parts.Add(part);
        }
    }

    private static RolStatementKind KindOf(XElement root) => root.Name.LocalName switch
    {
        DinarRoot => RolStatementKind.Dinar,
        ForeignRoot => RolStatementKind.Foreign,
        _ => RolStatementKind.Unknown
    };

    private static XElement? Child(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(element => element.Name.LocalName == localName);

    /// <summary>
    /// An attribute's value, with whitespace-only read as absent.
    ///
    /// ROL writes empty attributes rather than omitting them — <c>Mesto=""</c> is on nearly every
    /// line — so a caller checking for null would otherwise have to check for "" as well, and the
    /// one place that forgot would put an empty string where a name belongs.
    /// </summary>
    private static string? Attribute(XElement element, string name)
    {
        var value = element.Attribute(name)?.Value.Trim();

        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static readonly RolStatementRead NotRecognised =
        new(RolStatementKind.Unknown, BankStatement.Empty);
}
