using System.Globalization;
using System.Text;
using System.Xml;
using ForEach.Infrastructure.External.Banking.Formats;
using ForEach.Infrastructure.External.Banking.Payments;

namespace ForEach.Infrastructure.External.Banking.Rol.Payments;

/// <summary>
/// Writes ROL dinar payment orders — a single <c>&lt;Nalog&gt;</c> or a
/// <c>&lt;PaketZaSlanje&gt;</c> batch — for upload to Raiffeisen OnLine.
///
/// **Everything here refuses rather than repairs**, and that is the whole design. A statement read
/// wrongly is found by the reconciliation check and imported again; a payment order written wrongly
/// is either rejected by the bank — a morning wasted after somebody prepared thirty of them — or
/// *accepted*, in which case real money goes to a real account that is not the one intended. So an
/// account number that is not eighteen digits, a purpose longer than the field, a poziv na broj
/// carrying a character the bank forbids: each is a named refusal and none is silently trimmed.
///
/// **The file must be UTF-16.** Raiffeisen's own examples are UTF-16 LE with a byte-order mark,
/// CRLF line endings and no XML declaration, and that is not cosmetic — it is what their importer
/// reads. <see cref="Bytes"/> is therefore the method a host should use; handing
/// <see cref="PaymentFile.Content"/> to a UTF-8 writer produces a file that looks right in an
/// editor and is refused at the bank.
/// </summary>
public static class RolPaymentOrderWriter
{
    /// <summary>Constant on every order in the format's own examples: the CORE product code.</summary>
    private const string ProductCode = "501";

    /// <summary>
    /// Constant 1, per the specification — "uvek 1". It is the ordinal *within one
    /// <c>&lt;Nalog&gt;</c>*, and a Nalog carries one Data, so there is nothing for it to count.
    /// </summary>
    private const string OrdinalWithinOrder = "1";

    /// <summary>ISO 4217 numeric for the dinar. The dinar order takes no other currency.</summary>
    private const string DinarCode = "941";

    private const int AccountDigits = 18;
    private const int MaxPurpose = 105;
    private const int MaxName = 105;
    private const int MaxPlace = 20;
    private const int MaxReference = 20;
    private const int MaxOwnReference = 32;
    private const int MaxRegistrationNumber = 13;

    /// <summary>
    /// The characters a poziv na broj may carry, per the specification: digits, the English
    /// alphabet, a dash and a slash. Serbian letters are *not* among them — a reference typed with
    /// one is refused here rather than at the bank.
    /// </summary>
    private static bool IsReferenceCharacter(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '-' or '/';

    /// <summary>One payment order, as its own <c>&lt;Nalog&gt;</c> document.</summary>
    public static PaymentFile Write(PaymentOrder order)
    {
        var refusals = Refusals(order, position: null);

        if (refusals.Count > 0) return PaymentFile.Refused(refusals);

        return PaymentFile.Of(RolDocument.Write(writer =>
        {
            writer.WriteStartElement("Nalog");
            WriteData(writer, order);
            writer.WriteEndElement();
        }));
    }

    /// <summary>
    /// A batch, as <c>&lt;Client&gt;&lt;PaketZaSlanje&gt;</c> with one
    /// <c>&lt;Nalog&gt;</c> per order.
    ///
    /// **Every order is checked before any is written.** A file holding the first twenty of thirty
    /// orders is worse than no file: somebody uploads it, the bank takes it, and the remaining ten
    /// suppliers are unpaid with nothing on any screen saying which.
    /// </summary>
    public static PaymentFile WriteBatch(IReadOnlyList<PaymentOrder> orders)
    {
        if (orders.Count == 0) return PaymentFile.Refused(["Paket nema nijedan nalog."]);

        var refusals = orders
            .SelectMany((order, index) => Refusals(order, position: index + 1))
            .ToList();

        if (refusals.Count > 0) return PaymentFile.Refused(refusals);

        return PaymentFile.Of(RolDocument.Batch(
        [
            .. orders.Select((order, index) => (

                // The examples name each order's notional file. It is the bank's own convention
                // for telling one apart in a rejection message, so it is written rather than left
                // out — a batch whose refusal names "Nalog_7.XML" is one somebody can act on.
                FileName: $"Nalog_{index + 1}.XML",
                Body: (Action<XmlWriter>)(writer => WriteData(writer, order))))
        ]));
    }

    /// <summary>
    /// The file's bytes: UTF-16 little-endian **with** a byte-order mark.
    ///
    /// See the class summary — this is part of the format rather than a preference, and it is the
    /// method to hand to whatever writes the file.
    /// </summary>
    public static byte[] Bytes(PaymentFile file) =>
        file.Content is null
            ? []
            : FileEncoding.GetPreamble().Concat(FileEncoding.GetBytes(file.Content)).ToArray();

    /// <summary>UTF-16 LE. Exposed so a host cannot get this wrong by accident.</summary>
    public static Encoding FileEncoding => RolDocument.FileEncoding;

    private static void WriteData(XmlWriter writer, PaymentOrder order)
    {
        writer.WriteStartElement("Data");

        Element("SifraProizvodaCORE", ProductCode);

        // Written empty on purpose: the bank fills these on receipt, and the examples carry them
        // as empty elements rather than omitting them.
        Empty("TransakcijaID");
        Empty("PlacanjeID");
        Empty("DatumPrijema");

        Element("RedniBrojNaloga", OrdinalWithinOrder);
        Element("MaticniBrojNalogodavca", order.OrderingRegistrationNumber.Trim());
        Element("VasBrojNaloga", order.OwnReference?.Trim() ?? string.Empty);
        Element("SifraPlacanja", order.PurposeCode.Trim());
        Element("SvrhaDoznake", order.Purpose.Trim());
        Element("SifraValute", DinarCode);

        // Two decimals always, with a dot, invariantly. "10000.1" and "10000,10" are both files the
        // bank reads as something other than what was meant.
        Element("Iznos", order.Amount.ToString("0.00", CultureInfo.InvariantCulture));

        Element("DatumValute", order.ValueDate.ToString(BankValues.SerbianDate, CultureInfo.InvariantCulture));

        // Mandatory in the specification, and the same day when nobody named a window: an order
        // that may be executed "until" a date earlier than its own value date is refused below.
        Element(
            "DatumValuteDo",
            (order.ValueDateUntil ?? order.ValueDate)
            .ToString(BankValues.SerbianDate, CultureInfo.InvariantCulture));

        Element("RacunNalogodavca", Digits(order.OrderingAccount));
        Element("RacunKorisnika", Digits(order.BeneficiaryAccount));
        Element("ModelNalogodavca", order.DebitReferenceModel?.Trim() ?? string.Empty);
        Element("PozivNaBrojNalogodavca", order.DebitReference?.Trim() ?? string.Empty);
        Element("ModelKorisnika", order.CreditReferenceModel?.Trim() ?? string.Empty);
        Element("PozivNaBrojKorisnika", order.CreditReference?.Trim() ?? string.Empty);
        Element("NazivNalogodavca", order.OrderingName.Trim());
        Element("MestoNalogodavca", order.OrderingPlace.Trim());
        Element("NazivKorisnika", order.BeneficiaryName.Trim());
        Element("MestoKorisnika", order.BeneficiaryPlace.Trim());

        Empty("BrojGrupnogNaloga");
        Empty("BrojZbirnogNaloga");
        Empty("Status");

        // 100 is žiro kliring and 80–99 is RTGS. 80 rather than 99 for urgent: the band is a
        // priority and the top of it is what "as soon as possible" means.
        Element("Prioritet", order.Priority is PaymentPriority.Urgent ? "80" : "100");

        writer.WriteEndElement();

        // Empty is written as a PAIRED tag — <Partija></Partija> — not as <Partija/>. The two are
        // identical to an XML parser and Raiffeisen's own examples distinguish them: fields the
        // caller may leave blank are paired, and only the ones the bank fills are self-closing.
        // Matching the example costs nothing and is the difference that matters if their importer
        // ever does string matching rather than parsing.
        void Element(string name, string value)
        {
            if (value.Length == 0)
            {
                writer.WriteStartElement(name);
                writer.WriteFullEndElement();
                return;
            }

            writer.WriteElementString(name, value);
        }

        void Empty(string name)
        {
            writer.WriteStartElement(name);
            writer.WriteEndElement();
        }
    }

    /// <summary>
    /// Every reason this order cannot be written, each a sentence naming the field.
    ///
    /// Serbian, because the person reading it is the one who typed the payment.
    /// </summary>
    private static List<string> Refusals(PaymentOrder order, int? position)
    {
        var refusals = new List<string>();
        var where = position is null ? string.Empty : $"Nalog {position}: ";

        Account(order.OrderingAccount, "Račun nalogodavca");
        Account(order.BeneficiaryAccount, "Račun korisnika");

        Required(order.OrderingRegistrationNumber, MaxRegistrationNumber, "Matični broj nalogodavca");
        Required(order.OrderingName, MaxName, "Naziv nalogodavca");
        Required(order.OrderingPlace, MaxPlace, "Mesto nalogodavca");
        Required(order.BeneficiaryName, MaxName, "Naziv korisnika");
        Required(order.BeneficiaryPlace, MaxPlace, "Mesto korisnika");
        Required(order.Purpose, MaxPurpose, "Svrha doznake");

        Reference(order.DebitReference, order.DebitReferenceModel, "zaduženja");
        Reference(order.CreditReference, order.CreditReferenceModel, "odobrenja");

        if (order.OwnReference is { Length: > MaxOwnReference })
            refusals.Add($"{where}Vaš broj naloga je duži od {MaxOwnReference} karaktera.");

        if (order.PurposeCode.Trim().Length != 3 || !order.PurposeCode.Trim().All(char.IsAsciiDigit))
            refusals.Add($"{where}Šifra plaćanja mora biti trocifrena.");

        // The dinar order carries SifraValute 941 and nothing else. A euro payment is the devizni
        // nalog, which is a different document with a different field set — writing one here would
        // produce a dinar order for a euro amount, and the bank would take it.
        if (!string.Equals(BankValues.Currency(order.Currency), "RSD", StringComparison.Ordinal))
        {
            refusals.Add(
                $"{where}Dinarski nalog se izdaje samo u RSD — za {order.Currency} je potreban "
                + "devizni nalog.");
        }

        if (order.Amount <= 0)
            refusals.Add($"{where}Iznos mora biti veći od nule.");

        // 13 integer digits plus 2 decimals, per the specification.
        else if (order.Amount >= 10_000_000_000_000m)
            refusals.Add($"{where}Iznos je veći nego što format dozvoljava.");

        else if (decimal.Round(order.Amount, 2) != order.Amount)
            refusals.Add($"{where}Iznos ima više od dve decimale.");

        if (order.ValueDateUntil is { } until && until < order.ValueDate)
            refusals.Add($"{where}Datum valute do je pre datuma valute.");

        // XML cannot carry most control characters at all — the writer throws on U+0001 rather
        // than escaping it — and a line break inside a purpose is one the bank's confirmation
        // prints as two. Refused by field, so the person fixing it knows which one.
        Clean(order.OrderingRegistrationNumber, "Matični broj nalogodavca");
        Clean(order.OrderingName, "Naziv nalogodavca");
        Clean(order.OrderingPlace, "Mesto nalogodavca");
        Clean(order.BeneficiaryName, "Naziv korisnika");
        Clean(order.BeneficiaryPlace, "Mesto korisnika");
        Clean(order.Purpose, "Svrha doznake");
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

        void Required(string value, int max, string label)
        {
            if (string.IsNullOrWhiteSpace(value)) refusals.Add($"{where}{label} je obavezno polje.");
            else if (value.Trim().Length > max)
                refusals.Add($"{where}{label} je duže od {max} karaktera.");
        }

        void Reference(string? value, string? model, string label)
        {
            var trimmed = value?.Trim();

            if (string.IsNullOrEmpty(trimmed))
            {
                // A model with no reference is the one combination that is worse than neither: the
                // bank validates the control digits of a 97 reference, and there is nothing to
                // validate.
                if (!string.IsNullOrWhiteSpace(model))
                    refusals.Add($"{where}Model {label} je unet bez poziva na broj.");

                return;
            }

            if (trimmed.Length > MaxReference)
                refusals.Add($"{where}Poziv na broj {label} je duži od {MaxReference} karaktera.");

            if (!trimmed.All(IsReferenceCharacter))
            {
                refusals.Add(
                    $"{where}Poziv na broj {label} sme da sadrži samo cifre, slova engleskog "
                    + "alfabeta, „-” i „/”.");
            }

            if (!string.IsNullOrWhiteSpace(model)
                && (model.Trim().Length > 2 || !model.Trim().All(char.IsAsciiDigit)))
            {
                refusals.Add($"{where}Model {label} mora biti dvocifren.");
            }
        }
    }

    /// <summary>
    /// An account number reduced to its digits.
    ///
    /// Accounts are written both ways in practice — <c>265-1100310000011-11</c> on a letterhead and
    /// <c>265110031000001111</c> in a file — and the format wants the second. Stripping is safe
    /// because the check that follows demands exactly eighteen digits: anything that was not an
    /// account number fails it rather than being quietly padded into one.
    /// </summary>
    private static string Digits(string? account) =>
        new(account?.Where(char.IsAsciiDigit).ToArray() ?? []);

}
