using System.Globalization;
using System.Text;
using ForEach.Infrastructure.External.Banking.Payments;

namespace ForEach.Infrastructure.External.Banking.Halcom.Payments;

/// <summary>
/// Writes a Halcom / Multi E-bank **virman** file — the fixed-width payment-order import roughly
/// twenty Serbian banks accept through Hal E-Bank.
///
/// **Built from a specification and a real accepted file together**, which is the standard this
/// solution holds writers to and the reason this one waited: the Asseco OfficeBanking manual
/// documents the format under "Multi E-bank format — Platni nalozi", and a genuine file exported
/// for Banka Poštanska štedionica supplied what the manual does not say.
///
/// **The manual is out by 37 characters, and the file settles it.** Its detail record is 180 wide;
/// a real one is 217. Three differences account for every character of that:
///
/// - a **35-character address** sits between the beneficiary's name and their place, which the
///   manual does not mention at all;
/// - the **šifra plaćanja is three digits**, absorbing the position the manual calls "oblik
///   plaćanja" — exactly the same deviation the *statement* side has, where the manual likewise
///   describes a two-digit code from the pre-2003 codebook;
/// - two single-character fillers, one before the purpose and one before the value date.
///
/// The layout is then arithmetic rather than opinion: the fields sum to 217 exactly, and the
/// summary record's stated total matched the five orders beneath it to the para.
///
/// **Everything refuses rather than repairs**, for the reason written on the ROL writer: a rejected
/// file costs a morning, and an accepted wrong one moves real money to a real account that is not
/// the one intended.
/// </summary>
public static class HalcomPaymentOrderWriter
{
    /// <summary>Both header records and every order are this wide… except they are not.</summary>
    private const int HeaderLength = 180;

    /// <summary>…the orders are wider, and that is the manual's error rather than the file's.</summary>
    private const int OrderLength = 217;

    private const int AccountDigits = 18;
    private const int MaxName = 35;
    private const int MaxAddress = 35;
    private const int MaxPlace = 10;
    private const int MaxPurpose = 36;
    private const int MaxReference = 22;
    private const int AmountDigits = 13;

    /// <summary>Tip stavka: 0 addresses the file, 9 summarises it, 1 is an order.</summary>
    private const char AddressRecord = '0';
    private const char SummaryRecord = '9';
    private const char OrderRecord = '1';

    /// <summary>Tip dokumenta: an ordinary transfer. 4 would be a kompenzacija.</summary>
    private const char Transfer = '0';

    /// <summary>What the address record carries where a bank name might go.</summary>
    private const string Channel = "MULTI E-BANK";

    /// <summary>Two digits of year, as the format writes its dates: 150926 is 15.09.2026.</summary>
    private const string DateFormat = "ddMMyy";

    /// <summary>
    /// The whole file: an address record, a summary record, then one record per order.
    ///
    /// **The ordering party is taken from the first order**, which is what the manual says to do —
    /// "broj računa, naziv i mesto prepišu se iz prvog naloga" — so a batch paying from two
    /// different accounts is refused rather than silently filed under the first.
    /// </summary>
    public static PaymentFile Write(IReadOnlyList<PaymentOrder> orders)
    {
        if (orders.Count == 0) return PaymentFile.Refused(["Datoteka nema nijedan nalog."]);

        var refusals = orders
            .SelectMany((order, index) => Refusals(order, orders.Count == 1 ? null : index + 1))
            .ToList();

        // One file, one payer. The header states the account every order is drawn on, so a second
        // one in the list would be paid from the first one's account without anything saying so.
        var payers = orders
            .Select(order => Digits(order.OrderingAccount))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (payers.Count > 1)
        {
            refusals.Add(
                "Svi nalozi u jednoj datoteci moraju biti sa istog računa — uneto ih je "
                + $"{payers.Count}.");
        }

        if (refusals.Count > 0) return PaymentFile.Refused(refusals);

        var first = orders[0];

        var text = new StringBuilder();

        text.Append(AddressLine(first)).Append("\r\n");
        text.Append(SummaryLine(first, orders)).Append("\r\n");

        foreach (var order in orders) text.Append(OrderLine(order)).Append("\r\n");

        return PaymentFile.Of(text.ToString());
    }

    /// <summary>
    /// The file's bytes: Windows-1250, ending with the DOS end-of-file marker the format carries.
    ///
    /// Not UTF-8. A name with a Ć in it — which is most of them here — becomes two bytes and every
    /// fixed-width column after it moves, so the bank reads the amount out of the middle of
    /// somebody's street.
    /// </summary>
    public static byte[] Bytes(PaymentFile file) =>
        file.Content is null ? [] : Cp1250.WithTrailer(file.Content);

    // ---- the three record kinds ----------------------------------------------------------------

    private static string AddressLine(PaymentOrder order)
    {
        var record = Blank(HeaderLength);

        Put(record, 0, Digits(order.OrderingAccount));
        Put(record, 18, Fit(order.OrderingName, MaxName));
        Put(record, 53, Fit(order.OrderingPlace, MaxPlace));
        Put(record, 167, Channel);

        record[HeaderLength - 1] = AddressRecord;

        return new string(record);
    }

    private static string SummaryLine(PaymentOrder first, IReadOnlyList<PaymentOrder> orders)
    {
        var record = Blank(HeaderLength);

        Put(record, 0, Digits(first.OrderingAccount));
        Put(record, 18, Fit(first.OrderingName, MaxName));
        Put(record, 53, Fit(first.OrderingPlace, MaxPlace));

        // The total and the count, which is what makes the file checkable by the bank before it
        // moves anything — and what proved this layout when it was read back from a real file.
        Put(record, 63, Implied(orders.Sum(order => order.Amount), 15));
        Put(record, 78, orders.Count.ToString(CultureInfo.InvariantCulture).PadLeft(5, '0'));

        record[HeaderLength - 1] = SummaryRecord;

        return new string(record);
    }

    private static string OrderLine(PaymentOrder order)
    {
        var record = Blank(OrderLength);

        Put(record, 0, Digits(order.BeneficiaryAccount));
        Put(record, 18, Fit(order.BeneficiaryName, MaxName));
        Put(record, 53, Fit(order.BeneficiaryAddress ?? string.Empty, MaxAddress));
        Put(record, 88, Fit(order.BeneficiaryPlace, MaxPlace));

        record[98] = '0';

        Put(record, 99, Model(order.DebitReferenceModel));
        Put(record, 101, Fit(order.DebitReference ?? string.Empty, MaxReference));

        // [123] is a filler the manual does not mention.
        Put(record, 124, Fit(order.Purpose, MaxPurpose));
        Put(record, 160, "00000");

        // [165] blank, then the three-digit payment code across the position the manual splits
        // into "oblik plaćanja" and a two-digit code.
        Put(record, 166, order.PurposeCode.Trim());

        // [169..170] blank.
        Put(record, 171, Implied(order.Amount, AmountDigits));
        Put(record, 184, Model(order.CreditReferenceModel));
        Put(record, 186, Fit(order.CreditReference ?? string.Empty, MaxReference));

        // [208] the second filler.
        Put(record, 209, order.ValueDate.ToString(DateFormat, CultureInfo.InvariantCulture));

        record[215] = Transfer;
        record[216] = OrderRecord;

        return new string(record);
    }

    // ---- refusals ------------------------------------------------------------------------------

    private static List<string> Refusals(PaymentOrder order, int? position)
    {
        var refusals = new List<string>();
        var where = position is null ? string.Empty : $"Nalog {position}: ";

        Account(order.OrderingAccount, "Račun nalogodavca");
        Account(order.BeneficiaryAccount, "Račun korisnika");

        Required(order.OrderingName, MaxName, "Naziv nalogodavca");
        Required(order.OrderingPlace, MaxPlace, "Mesto nalogodavca");
        Required(order.BeneficiaryName, MaxName, "Naziv korisnika");
        Required(order.BeneficiaryPlace, MaxPlace, "Mesto korisnika");
        Required(order.Purpose, MaxPurpose, "Svrha plaćanja");

        Optional(order.BeneficiaryAddress, MaxAddress, "Adresa korisnika");
        Optional(order.DebitReference, MaxReference, "Poziv na broj zaduženja");
        Optional(order.CreditReference, MaxReference, "Poziv na broj odobrenja");

        Model(order.DebitReferenceModel, order.DebitReference, "zaduženja");
        Model(order.CreditReferenceModel, order.CreditReference, "odobrenja");

        if (order.PurposeCode.Trim().Length != 3 || !order.PurposeCode.Trim().All(char.IsAsciiDigit))
            refusals.Add($"{where}Šifra plaćanja mora biti trocifrena.");

        // This is the domestic transfer. A euro amount written into it is a dinar payment for
        // whatever the euro figure happened to be.
        if (!string.Equals(order.Currency.Trim(), "RSD", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(order.Currency.Trim(), "941", StringComparison.Ordinal))
        {
            refusals.Add(
                $"{where}Halcom virman se izdaje samo u dinarima — za {order.Currency} je potreban "
                + "devizni nalog.");
        }

        if (order.Amount <= 0) refusals.Add($"{where}Iznos mora biti veći od nule.");
        else if (decimal.Round(order.Amount, 2) != order.Amount)
            refusals.Add($"{where}Iznos ima više od dve decimale.");
        else if (order.Amount * 100m >= 10_000_000_000_000m)
            refusals.Add($"{where}Iznos je veći nego što format dozvoljava.");

        // Windows-1250 has no Cyrillic. A file written with a question mark where a beneficiary's
        // name should be is a payment somebody has to explain to a bank.
        Representable(order.BeneficiaryName, "Naziv korisnika");
        Representable(order.BeneficiaryPlace, "Mesto korisnika");
        Representable(order.BeneficiaryAddress, "Adresa korisnika");
        Representable(order.Purpose, "Svrha plaćanja");
        Representable(order.OrderingName, "Naziv nalogodavca");
        Representable(order.OrderingPlace, "Mesto nalogodavca");

        return refusals;

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

        void Optional(string? value, int max, string label)
        {
            if (value is not null && value.Trim().Length > max)
                refusals.Add($"{where}{label} je duži od {max} karaktera.");
        }

        void Model(string? model, string? reference, string label)
        {
            var trimmed = model?.Trim();

            if (string.IsNullOrEmpty(trimmed)) return;

            if (trimmed.Length > 2 || !trimmed.All(char.IsAsciiDigit))
                refusals.Add($"{where}Model {label} mora biti dvocifren.");

            // A model with nothing to qualify. The bank validates the control digits of a 97
            // reference and there is nothing to validate.
            if (string.IsNullOrWhiteSpace(reference))
                refusals.Add($"{where}Model {label} je unet bez poziva na broj.");
        }

        void Representable(string? value, string label)
        {
            if (value is not null && Cp1250.FirstUnrepresentable(value) is { } c)
            {
                refusals.Add(
                    $"{where}{label} sadrži znak „{c}” koji se ne može zapisati u Windows-1250 "
                    + "kodnoj strani koju banka očekuje.");
            }
        }
    }

    // ---- fixed-width helpers -------------------------------------------------------------------

    private static char[] Blank(int length)
    {
        var record = new char[length];

        Array.Fill(record, ' ');

        return record;
    }

    private static void Put(char[] record, int start, string value)
    {
        for (var i = 0; i < value.Length && start + i < record.Length; i++)
            record[start + i] = value[i];
    }

    /// <summary>
    /// A value already checked to fit. Trimmed rather than truncated: everything longer than its
    /// field was refused above, so anything reaching here is short enough and this only removes
    /// the whitespace a person typed.
    /// </summary>
    private static string Fit(string value, int length) =>
        value.Trim().Length <= length ? value.Trim() : value.Trim()[..length];

    /// <summary>An amount as digits with two implied decimals, right-aligned in zeroes.</summary>
    private static string Implied(decimal amount, int length) =>
        ((long)decimal.Round(amount * 100m, 0))
        .ToString(CultureInfo.InvariantCulture)
        .PadLeft(length, '0');

    /// <summary>
    /// A poziv-na-broj model as the two digits the file wants, blank where there is none.
    /// </summary>
    private static string Model(string? model) =>
        string.IsNullOrWhiteSpace(model) ? "  " : model.Trim().PadLeft(2, '0');

    private static string Digits(string? account) =>
        new(account?.Where(char.IsAsciiDigit).ToArray() ?? []);
}
