using System.Globalization;
using System.Xml;
using ForEach.Infrastructure.External.Banking.Formats;
using ForEach.Infrastructure.External.Banking.Payments;

namespace ForEach.Infrastructure.External.Banking.Rol.Payments;

/// <summary>
/// Writes ROL **devizni** payment orders — the foreign-currency nalog, and its batch.
///
/// Same envelope as the dinar order (<c>&lt;Nalog&gt;&lt;Data&gt;</c>, UTF-16 LE with a BOM, tabs,
/// CRLF, no XML declaration) and a different document inside it: a beneficiary named by IBAN and
/// country, a correspondent bank, a charge option, and a statistical declaration to the National
/// Bank.
///
/// **Three rules interlock around the cover basis**, and each of them is a file the bank refuses —
/// or, worse, takes:
///
/// - <c>$</c> and <c>D</c> may not be denominated in dinars, and <c>S</c> may only be dinars;
/// - <c>$</c> and <c>D</c> must name the payer's account, and <c>S</c> must not;
/// - the statistics must account for the whole amount.
///
/// The last is inferred rather than quoted: the specification does not state it in words, but the
/// statistical items are how the payment is declared to the National Bank and a set that does not
/// total the payment declares something other than what was paid. It is enforced here so that a
/// disagreement is found while somebody can still fix it — and named here so that, if a real file
/// ever disagrees, the reason is findable.
/// </summary>
public static class RolForeignPaymentOrderWriter
{
    /// <summary>The CORE product code for a devizni order — 210, where the dinar one is 501.</summary>
    private const string ProductCode = "210";

    /// <summary>The dinar, as a numeric ISO 4217 code. The cover-basis rules turn on it.</summary>
    private const string DinarCode = "941";

    private const int MaxBeneficiaryName = 70;
    private const int MaxBeneficiaryPlace = 70;
    private const int MaxBeneficiaryAccount = 34;
    private const int MaxBankName = 240;
    private const int MaxSwift = 11;
    private const int MaxDetails = 140;
    private const int MaxPlace = 50;
    private const int MaxContact = 70;
    private const int MaxOwnReference = 20;
    private const int MaxRegistrationNumber = 13;
    private const int MaxTransactionDescription = 70;
    private const int MaxStatistics = 999;

    /// <summary>
    /// The beneficiary's address, today.
    ///
    /// The specification says this field grows to 140 characters **on 16 November 2026** and is 70
    /// until then. The shorter limit is the one enforced, because it is the one in force: accepting
    /// 140 now produces a file the bank truncates or rejects, and the failure lands on whoever
    /// typed the address rather than on whoever read the note.
    /// </summary>
    private const int MaxBeneficiaryAddress = 70;

    /// <summary>One devizni order, as its own <c>&lt;Nalog&gt;</c> document.</summary>
    public static PaymentFile Write(ForeignPaymentOrder order)
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
    /// A batch, refused whole if any order is refused — see the dinar writer for why a partly
    /// written file is worse than none.
    /// </summary>
    public static PaymentFile WriteBatch(IReadOnlyList<ForeignPaymentOrder> orders)
    {
        if (orders.Count == 0) return PaymentFile.Refused(["Paket nema nijedan nalog."]);

        var refusals = orders
            .SelectMany((order, index) => Refusals(order, position: index + 1))
            .ToList();

        if (refusals.Count > 0) return PaymentFile.Refused(refusals);

        return PaymentFile.Of(RolDocument.Batch(
        [
            .. orders.Select((order, index) => (
                FileName: $"Nalog_{index + 1}.XML",
                Body: (Action<XmlWriter>)(writer => WriteData(writer, order))))
        ]));
    }

    /// <summary>UTF-16 LE with a BOM — see <see cref="RolPaymentOrderWriter.Bytes"/>.</summary>
    public static byte[] Bytes(PaymentFile file) => RolPaymentOrderWriter.Bytes(file);

    private static void WriteData(XmlWriter writer, ForeignPaymentOrder order)
    {
        writer.WriteStartElement("Data");

        Element("SifraProizvodaCORE", ProductCode);
        Empty("TransakcijaID");
        Element("BrojNaloga", order.OwnReference?.Trim() ?? string.Empty);
        Empty("PlacanjeID");
        Empty("DatumPrijema");
        Empty("StatusPlacanja");
        Element("MaticniBrojNalogodavca", order.OrderingRegistrationNumber.Trim());
        Element("Korisnik", order.BeneficiaryName.Trim());
        Element("KorisnikAdresa", order.BeneficiaryAddress?.Trim() ?? string.Empty);
        Element("MestoKorisnik", order.BeneficiaryPlace.Trim());
        Element("ZemljaKorisnika", order.BeneficiaryCountryCode.Trim());
        Element("RacunKorisnika", order.BeneficiaryAccount.Trim());
        Element("KontaktKorisnika", order.BeneficiaryContact?.Trim() ?? string.Empty);
        Element("SWIFTAdresaBankeKorisnika", order.BeneficiaryBankSwift?.Trim() ?? string.Empty);
        Element("BankaKorisnika", order.BeneficiaryBankName?.Trim() ?? string.Empty);

        // "prazan" in the specification — the bank derives it from the SWIFT address, and a value
        // here that disagreed with the one they derive is a contradiction the file cannot resolve.
        Empty("ZemljaBankeKorisnika");

        Element("KontaktBankeKorisnika", order.BeneficiaryBankContact?.Trim() ?? string.Empty);
        Element("SWIFTAdresaPosrednickeBanke", order.IntermediaryBankSwift?.Trim() ?? string.Empty);
        Element("PosrednickaBanka", order.IntermediaryBankName?.Trim() ?? string.Empty);

        Element(
            "RacunBankeKorisnikaKodPosrednickeBanke",
            order.IntermediaryBankAccount?.Trim() ?? string.Empty);

        Element("DetaljiPlacanja", order.Details?.Trim() ?? string.Empty);
        Element("SifraValute", order.CurrencyCode.Trim());
        Element("Iznos", Money(order.Amount));
        Element(
            "TrazeniDatumValute",
            order.ValueDate.ToString(BankValues.SerbianDate, CultureInfo.InvariantCulture));
        Element("IndikatorPokrica", Indicator(order.CoverBasis));
        Empty("OrgJedinica");
        Empty("Konto");

        // Empty for the dinar cover basis, and that is the rule rather than an omission: the bank
        // takes a dinar payment from the account the order arrives on, and naming one here is what
        // makes the file wrong.
        Element("Partija", order.OrderingAccount?.Trim() ?? string.Empty);

        Element("BrojKreditnePrijave", Number(order.CreditApplicationNumber));
        Element("GodinaKreditnePrijave", Number(order.CreditApplicationYear));
        Element("TudjiTroskovi", Charges(order.Charges));

        // "prazan" — the bank counts the statistics items itself. Writing our own count would give
        // the file two answers to one question.
        Empty("BrojStavkiSpecifikacije");

        Element("Mesto", order.OrderingPlace.Trim());
        Element("PosebnaOznaka", Number(order.SpecialMark));
        Element("InstrumentPlacanja", Number(order.PaymentInstrument));

        writer.WriteStartElement("Statistika");

        for (var index = 0; index < order.Statistics.Count; index++)
        {
            var item = order.Statistics[index];

            writer.WriteStartElement("StavkaStatistike");

            // The ordinal is the position rather than something the caller supplies: it exists to
            // order the items and a caller-chosen one can repeat or skip, which the bank reads as
            // a missing declaration.
            Element("RedniBrojStavke", (index + 1).ToString(CultureInfo.InvariantCulture));

            Element("SifraOsnovaPlacanja", item.ReasonCode.Trim());
            Element("BrojUgovora", item.ContractNumber?.Trim() ?? string.Empty);
            Element("GodinaUgovora", Number(item.ContractYear));
            Element("OpisTransakcije", item.Description.Trim());

            // Signed, as the bank's own examples write it: "+1010.10".
            Element("IznosStatistika", "+" + Money(item.Amount));

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
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

    private static string Money(decimal amount) =>
        amount.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Number(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;

    private static string Indicator(ForeignCoverBasis basis) => basis switch
    {
        ForeignCoverBasis.ForeignCurrencyAccount => "$",
        ForeignCoverBasis.PurchaseCurrency => "D",
        _ => "S"
    };

    private static string Charges(ForeignChargeOption option) => option switch
    {
        ForeignChargeOption.Ours => "OUR",
        ForeignChargeOption.Beneficiary => "BEN",
        _ => "SHA"
    };

    private static List<string> Refusals(ForeignPaymentOrder order, int? position)
    {
        var refusals = new List<string>();
        var where = position is null ? string.Empty : $"Nalog {position}: ";

        Required(order.OrderingRegistrationNumber, MaxRegistrationNumber, "Matični broj nalogodavca");
        Required(order.BeneficiaryName, MaxBeneficiaryName, "Naziv korisnika");
        Required(order.BeneficiaryPlace, MaxBeneficiaryPlace, "Mesto korisnika");
        Required(order.BeneficiaryAccount, MaxBeneficiaryAccount, "Račun korisnika");
        Required(order.OrderingPlace, MaxPlace, "Mesto");

        Optional(order.BeneficiaryAddress, MaxBeneficiaryAddress, "Adresa korisnika");
        Optional(order.BeneficiaryContact, MaxContact, "Kontakt korisnika");
        Optional(order.BeneficiaryBankContact, MaxContact, "Kontakt banke korisnika");
        Optional(order.BeneficiaryBankSwift, MaxSwift, "SWIFT adresa banke korisnika");
        Optional(order.BeneficiaryBankName, MaxBankName, "Banka korisnika");
        Optional(order.IntermediaryBankSwift, MaxSwift, "SWIFT adresa posredničke banke");
        Optional(order.IntermediaryBankName, MaxBankName, "Posrednička banka");
        Optional(order.Details, MaxDetails, "Detalji plaćanja");
        Optional(order.OwnReference, MaxOwnReference, "Broj naloga");

        // One of the two must name the bank. Neither is a payment with nowhere to go, and the bank
        // cannot ask.
        if (string.IsNullOrWhiteSpace(order.BeneficiaryBankSwift)
            && string.IsNullOrWhiteSpace(order.BeneficiaryBankName))
        {
            refusals.Add(
                $"{where}Mora biti popunjena SWIFT adresa banke korisnika ili naziv banke "
                + "korisnika.");
        }

        if (!IsCode(order.BeneficiaryCountryCode, 3))
            refusals.Add($"{where}Šifra zemlje korisnika mora biti trocifrena.");

        if (!IsCode(order.CurrencyCode, 3))
            refusals.Add($"{where}Šifra valute mora biti trocifrena.");
        else
        {
            var isDinar = order.CurrencyCode.Trim() == DinarCode;

            // The cover basis and the currency are one decision stated twice, and the bank checks
            // that they agree. A dinar amount on a foreign-currency cover is a payment the bank
            // cannot fund from the account named.
            if (order.CoverBasis is ForeignCoverBasis.Dinar && !isDinar)
            {
                refusals.Add(
                    $"{where}Za plaćanje u dinarima (indikator „S”) valuta mora biti RSD ({DinarCode}).");
            }
            else if (order.CoverBasis is not ForeignCoverBasis.Dinar && isDinar)
            {
                refusals.Add(
                    $"{where}Za devizno pokriće (indikator „$” ili „D”) valuta ne sme biti RSD.");
            }
        }

        // Named for a foreign-currency cover, absent for a dinar one. Both directions are refused,
        // because an account written where none belongs is as wrong as one missing.
        var account = order.OrderingAccount?.Trim();

        if (order.CoverBasis is ForeignCoverBasis.Dinar)
        {
            if (!string.IsNullOrEmpty(account))
                refusals.Add($"{where}Za indikator „S” partija se ne popunjava.");
        }
        else if (string.IsNullOrEmpty(account))
        {
            refusals.Add($"{where}Za indikator „$” ili „D” partija je obavezna.");
        }
        else if (account.Length > 18)
        {
            refusals.Add($"{where}Partija je duža od 18 karaktera.");
        }

        if (order.Amount <= 0) refusals.Add($"{where}Iznos mora biti veći od nule.");
        else if (order.Amount >= 10_000_000_000_000m)
            refusals.Add($"{where}Iznos je veći nego što format dozvoljava.");
        else if (decimal.Round(order.Amount, 2) != order.Amount)
            refusals.Add($"{where}Iznos ima više od dve decimale.");

        if (order.SpecialMark is { } mark && mark is < 1 or > 7)
            refusals.Add($"{where}Posebna oznaka mora biti između 1 i 7.");

        if (order.PaymentInstrument is { } instrument && instrument is not (1 or 2))
            refusals.Add($"{where}Instrument plaćanja može biti 1 ili 2.");

        Statistics();

        return refusals;

        void Statistics()
        {
            if (order.Statistics.Count == 0)
            {
                refusals.Add($"{where}Nalog mora imati bar jednu stavku statistike.");
                return;
            }

            if (order.Statistics.Count > MaxStatistics)
                refusals.Add($"{where}Nalog ne može imati više od {MaxStatistics} stavki statistike.");

            for (var index = 0; index < order.Statistics.Count; index++)
            {
                var item = order.Statistics[index];
                var at = $"{where}Stavka {index + 1}: ";

                if (!IsCode(item.ReasonCode, 3))
                    refusals.Add($"{at}Šifra osnova plaćanja mora biti trocifrena.");

                if (string.IsNullOrWhiteSpace(item.Description))
                    refusals.Add($"{at}Opis transakcije je obavezan.");
                else if (item.Description.Trim().Length > MaxTransactionDescription)
                {
                    refusals.Add(
                        $"{at}Opis transakcije je duži od {MaxTransactionDescription} karaktera.");
                }

                // 112 is payment for goods already imported, and the invoice it settles is what
                // makes the declaration checkable against the customs record.
                if (item.ReasonCode.Trim() == "112" && string.IsNullOrWhiteSpace(item.ContractNumber))
                    refusals.Add($"{at}Za šifru osnova 112 broj fakture je obavezan.");

                if (!string.IsNullOrWhiteSpace(item.ContractNumber) && item.ContractYear is null)
                    refusals.Add($"{at}Uz broj fakture mora se uneti i godina.");

                if (item.Amount <= 0) refusals.Add($"{at}Iznos stavke mora biti veći od nule.");
                else if (decimal.Round(item.Amount, 2) != item.Amount)
                    refusals.Add($"{at}Iznos stavke ima više od dve decimale.");
            }

            // Inferred rather than quoted — see the class summary. The statistics are how the
            // payment is declared to the National Bank, and a set that does not total the payment
            // declares something other than what was paid.
            var declared = order.Statistics.Sum(item => item.Amount);

            if (order.Amount > 0 && declared != order.Amount)
            {
                refusals.Add(
                    $"{where}Zbir stavki statistike ({Money(declared)}) se ne slaže sa iznosom "
                    + $"naloga ({Money(order.Amount)}).");
            }
        }

        void Required(string value, int max, string label)
        {
            if (string.IsNullOrWhiteSpace(value)) refusals.Add($"{where}{label} je obavezno polje.");
            else if (value.Trim().Length > max)
                refusals.Add($"{where}{label} je duže od {max} karaktera.");
        }

        void Optional(string? value, int max, string label)
        {
            if (value is not null && value.Trim().Length > max)
                refusals.Add($"{where}{label} je duže od {max} karaktera.");
        }
    }

    private static bool IsCode(string? value, int digits)
    {
        var trimmed = value?.Trim();

        return trimmed is not null && trimmed.Length == digits && trimmed.All(char.IsAsciiDigit);
    }
}
