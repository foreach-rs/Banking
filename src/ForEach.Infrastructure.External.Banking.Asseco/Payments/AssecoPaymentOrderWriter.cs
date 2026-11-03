using System.Globalization;
using System.Text;
using System.Xml;
using ForEach.Infrastructure.External.Banking.Formats;
using ForEach.Infrastructure.External.Banking.Payments;

namespace ForEach.Infrastructure.External.Banking.Asseco.Payments;

/// <summary>
/// Writes an Asseco <c>&lt;pmtorderrq&gt;</c> — one or more domestic payment orders for upload to
/// an iBank / OfficeBanking e-banking service.
///
/// **The account is written with its dashes**, <c>b(3)-p(13)-k(2)</c>, which is the opposite of
/// what the ROL format wants for the same number. That is the reason there is no shared "format an
/// account" helper in this solution: the two banks disagree, the disagreement is the format, and a
/// helper taking a flag would put the decision one call away from the file it belongs to.
///
/// **Refusals over repairs**, for the reason stated on the ROL writer: a rejected file costs a
/// morning and an accepted wrong one costs the money.
///
/// **One request per file, and the request states its own count.** <c>count</c> on the root is what
/// the service checks the body against, so it is written from the list rather than passed in —
/// a count that disagrees with the orders beneath it is a file rejected for a reason nobody can
/// see.
/// </summary>
public static class AssecoPaymentOrderWriter
{
    /// <summary>The dinar, as this family still labels it.</summary>
    private const string DinarCode = "DIN";

    /// <summary>
    /// "Mesto izvršenja transakcije" for anything sent through e-banking, per the specification.
    /// </summary>
    private const string OnlinePlace = "online";

    /// <summary>Net settlement — žiro kliring.</summary>
    private const string Clearing = "ACH";

    /// <summary>Gross settlement in real time.</summary>
    private const string RealTime = "RTGS";

    private const int AccountDigits = 18;
    private const int BankCodeDigits = 3;
    private const int BranchDigits = 13;

    /// <summary>One payment order, as a request of one.</summary>
    public static PaymentFile Write(PaymentOrder order) => WriteBatch([order]);

    /// <summary>
    /// Every order in one request. Refused whole if any single order is refused — see the ROL
    /// writer for why a partly-written batch is worse than none.
    /// </summary>
    public static PaymentFile WriteBatch(IReadOnlyList<PaymentOrder> orders)
    {
        if (orders.Count == 0) return PaymentFile.Refused(["Zahtev nema nijedan nalog."]);

        var refusals = orders
            .SelectMany((order, index) => Refusals(order, orders.Count == 1 ? null : index + 1))
            .ToList();

        if (refusals.Count > 0) return PaymentFile.Refused(refusals);

        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "     ",
            NewLineChars = "\r\n",

            // The declaration is written here, unlike the ROL file: the specification's own
            // examples begin with <?xml version="1.0" encoding="utf-8"?>.
            Encoding = FileEncoding
        };

        // Written through a stream rather than a StringBuilder, because the writer names the
        // encoding of whatever it writes *to*: handed a StringBuilder it declares utf-16, and the
        // declaration then contradicts the UTF-8 bytes Bytes() produces — a file a strict parser
        // refuses on its first line. The stream makes the declaration say what the bytes are.
        using var stream = new MemoryStream();

        using (var writer = XmlWriter.Create(stream, settings))
        {
            writer.WriteStartElement("pmtorderrq");
            writer.WriteAttributeString("count", orders.Count.ToString(CultureInfo.InvariantCulture));

            foreach (var order in orders) WriteOrder(writer, order);

            writer.WriteEndElement();
            writer.Flush();
        }

        return PaymentFile.Of(FileEncoding.GetString(stream.ToArray()));
    }

    /// <summary>
    /// The file's bytes: UTF-8 without a byte-order mark.
    ///
    /// No BOM because the declaration already states the encoding, and a mark before it is what
    /// makes an XML parser refuse the first character of a document that is otherwise correct.
    /// </summary>
    public static byte[] Bytes(PaymentFile file) =>
        file.Content is null ? [] : FileEncoding.GetBytes(file.Content);

    /// <summary>UTF-8, no BOM. Exposed so a host cannot get this wrong by accident.</summary>
    public static readonly Encoding FileEncoding =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static void WriteOrder(XmlWriter writer, PaymentOrder order)
    {
        var ordering = Digits(order.OrderingAccount);
        var beneficiary = Digits(order.BeneficiaryAccount);

        writer.WriteStartElement("pmtorder");

        Party("companyinfo", order.OrderingName, order.OrderingPlace);
        Account("accountinfo", ordering);
        Party("payeecompanyinfo", order.BeneficiaryName, order.BeneficiaryPlace);
        Account("payeeaccountinfo", beneficiary);

        // "Jedinstveni identifikator transakcije u evidenciji nalogodavca" — ours, not the bank's.
        // Written from the host's own reference where it has one; empty otherwise, because
        // inventing an identifier here would produce a different one every time the same batch was
        // regenerated, and the bank uses it to recognise a duplicate submission.
        Element("trnuid", order.OwnReference?.Trim() ?? string.Empty);

        // Midnight on the value date. The specification's format string is written "DDDD-MM-YYYY"
        // in the document, which is a typo for the year — its own example is 2002-11-26T00:00:00.
        // The time is appended as a literal rather than formatted: DateOnly refuses a format
        // string carrying time components, and there is no time here to carry — the value date is
        // a day and the bank wants it written as a timestamp at midnight.
        Element(
            "dtdue",
            order.ValueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T00:00:00");

        Element("trnamt", order.Amount.ToString("0.00", CultureInfo.InvariantCulture));
        Element("trnplace", OnlinePlace);
        Element("purpose", order.Purpose.Trim());
        Element("purposecode", order.PurposeCode.Trim());
        Element("curdef", DinarCode);

        // Ours on the debit side, the beneficiary's on the credit side — the same naming the
        // reader documents, used here in the direction a payment out always has: we are the
        // ordering customer, so refnumber is ours and payeerefnumber names their invoice.
        Element("refmodel", order.DebitReferenceModel?.Trim() ?? string.Empty);
        Element("refnumber", order.DebitReference?.Trim() ?? string.Empty);
        Element("payeerefmodel", order.CreditReferenceModel?.Trim() ?? string.Empty);
        Element("payeerefnumber", order.CreditReference?.Trim() ?? string.Empty);

        Element("urgency", order.Priority is PaymentPriority.Urgent ? RealTime : Clearing);

        writer.WriteEndElement();

        void Element(string name, string value) => writer.WriteElementString(name, value);

        void Party(string name, string partyName, string city)
        {
            writer.WriteStartElement(name);
            Element("name", partyName.Trim());
            Element("city", city.Trim());
            writer.WriteEndElement();
        }

        void Account(string name, string digits)
        {
            writer.WriteStartElement(name);
            Element("acctid", Formatted(digits));

            // "Vodeće tri cifre računa" — derived rather than carried, because a bank code that
            // disagreed with the account it sits beside is a contradiction the file cannot resolve.
            Element("bankid", digits[..BankCodeDigits]);

            // The bank's *name* is not on the neutral order and is not invented here: it is a
            // lookup this library cannot do, and a blank is honest where a guess would be quoted
            // back on a confirmation.
            Element("bankname", string.Empty);
            writer.WriteEndElement();
        }
    }

    private static List<string> Refusals(PaymentOrder order, int? position)
    {
        var refusals = new List<string>();
        var where = position is null ? string.Empty : $"Nalog {position}: ";

        Account(order.OrderingAccount, "Račun nalogodavca");
        Account(order.BeneficiaryAccount, "Račun korisnika");

        Required(order.OrderingName, "Naziv nalogodavca");
        Required(order.OrderingPlace, "Mesto nalogodavca");
        Required(order.BeneficiaryName, "Naziv korisnika");
        Required(order.BeneficiaryPlace, "Mesto korisnika");
        Required(order.Purpose, "Svrha plaćanja");

        if (order.PurposeCode.Trim().Length != 3 || !order.PurposeCode.Trim().All(char.IsAsciiDigit))
            refusals.Add($"{where}Šifra plaćanja mora biti trocifrena.");

        if (!string.Equals(BankValues.Currency(order.Currency), "RSD", StringComparison.Ordinal))
        {
            refusals.Add(
                $"{where}Nalog za prenos u zemlji se izdaje samo u RSD — za {order.Currency} je "
                + "potreban devizni nalog.");
        }

        if (order.Amount <= 0) refusals.Add($"{where}Iznos mora biti veći od nule.");
        else if (decimal.Round(order.Amount, 2) != order.Amount)
            refusals.Add($"{where}Iznos ima više od dve decimale.");

        // XML cannot carry most control characters at all — the writer throws on U+0001 rather
        // than escaping it — and a line break inside a purpose is one the bank's confirmation
        // prints as two. Refused by field, so the person fixing it knows which one.
        Clean(order.OrderingName, "Naziv nalogodavca");
        Clean(order.OrderingPlace, "Mesto nalogodavca");
        Clean(order.BeneficiaryName, "Naziv korisnika");
        Clean(order.BeneficiaryPlace, "Mesto korisnika");
        Clean(order.Purpose, "Svrha plaćanja");
        Clean(order.PurposeCode, "Šifra plaćanja");
        Clean(order.OwnReference, "Vaš broj naloga");
        Clean(order.DebitReferenceModel, "Model zaduženja");
        Clean(order.DebitReference, "Poziv na broj zaduženja");
        Clean(order.CreditReferenceModel, "Model odobrenja");
        Clean(order.CreditReference, "Poziv na broj odobrenja");

        return refusals;

        void Clean(string? value, string label)
        {
            if (BankValues.FirstControlCharacter(value) is { } c)
                refusals.Add($"{where}{label} sadrži nedozvoljen kontrolni znak (U+{(int)c:X4}).");
        }

        void Account(string value, string label)
        {
            var digits = Digits(value);

            if (digits.Length != AccountDigits)
            {
                refusals.Add(
                    $"{where}{label} mora imati tačno {AccountDigits} cifara "
                    + $"(uneto: {(digits.Length == 0 ? "prazno" : digits.Length + " cifara")}).");
            }
        }

        void Required(string value, string label)
        {
            if (string.IsNullOrWhiteSpace(value)) refusals.Add($"{where}{label} je obavezno polje.");
        }
    }

    /// <summary>
    /// The account in the shape this format states: <c>b(3)-p(13)-k(2)</c>.
    ///
    /// The eighteen digits are the same number the ROL file writes bare, so the host keeps one
    /// account on record and each writer presents it the way its own bank reads it.
    /// </summary>
    private static string Formatted(string digits) =>
        $"{digits[..BankCodeDigits]}-{digits.Substring(BankCodeDigits, BranchDigits)}-"
        + digits[(BankCodeDigits + BranchDigits)..];

    private static string Digits(string? account) =>
        new(account?.Where(char.IsAsciiDigit).ToArray() ?? []);
}
