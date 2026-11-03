using ForEach.Infrastructure.External.Banking.Halcom.Payments;
using ForEach.Infrastructure.External.Banking.Payments;

namespace ForEach.Infrastructure.External.Banking.Halcom.UnitTests.Payments;

/// <summary>
/// Writing a Halcom / Multi E-bank virman.
///
/// **The expected file below is a real one with its identities replaced.** The writer was checked
/// against the original — a virman genuinely exported for Banka Poštanska štedionica — and
/// reproduced it byte for byte, including the two header records, every filler character, the
/// summary total, the count, the Windows-1250 encoding and the trailing 0x1A. What is committed
/// here is that file with the account numbers, names, addresses, references and one amount
/// substituted at the same lengths, so the structure it pins is the bank's rather than this
/// writer's opinion of it.
///
/// That matters because the published specification is wrong by 37 characters per order: it
/// describes a 180-wide record and a real one is 217. The address field, the three-digit šifra
/// plaćanja and the two fillers are all findings from the file, and this test is what stops them
/// being tidied away by somebody reading the manual.
/// </summary>
public class HalcomPaymentOrderWriterTests
{
    [Fact]
    public void The_file_reproduces_the_shape_of_a_real_accepted_virman()
    {
        var file = HalcomPaymentOrderWriter.Write(Orders);

        Assert.True(file.Written, string.Join(" | ", file.Refusals));

        var written = file.Content!.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(Expected.Length, written.Length);

        for (var i = 0; i < Expected.Length; i++)
        {
            Assert.Equal(Expected[i], written[i]);
        }
    }

    [Fact]
    public void The_records_are_the_widths_the_bank_reads_and_not_the_ones_the_manual_states()
    {
        // 180 for the two headers and 217 for each order. The manual says 180 throughout, and a
        // writer that believed it would put every field after the beneficiary's name into the
        // wrong column.
        var written = HalcomPaymentOrderWriter.Write(Orders).Content!
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(180, written[0].Length);
        Assert.Equal(180, written[1].Length);
        Assert.All(written.Skip(2), record => Assert.Equal(217, record.Length));
    }

    [Fact]
    public void The_summary_record_states_the_total_and_the_count()
    {
        // The bank checks the file against these before it moves anything, and they are what
        // proved the amount offset when the real file was read back: the stated total matched the
        // orders beneath it to the para.
        var summary = HalcomPaymentOrderWriter.Write(Orders).Content!
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)[1];

        Assert.Equal("000000019498084", summary[63..78]);
        Assert.Equal("00005", summary[78..83]);
        Assert.Equal('9', summary[179]);

        Assert.Equal(194_980.84m, Orders.Sum(order => order.Amount));
    }

    [Fact]
    public void The_address_record_names_the_channel()
    {
        var address = HalcomPaymentOrderWriter.Write(Orders).Content!
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)[0];

        Assert.Equal("MULTI E-BANK", address[167..179]);
        Assert.Equal('0', address[179]);
    }

    [Fact]
    public void An_order_carries_the_three_digit_payment_code_where_the_manual_expects_two()
    {
        // The same deviation the statement side has: the manual describes a leading "oblik
        // plaćanja" digit and a two-digit code from the pre-2003 codebook, and every real file
        // carries the modern three-digit NBS code across those positions.
        var order = HalcomPaymentOrderWriter.Write(Orders).Content!
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)[2];

        Assert.Equal("221", order[166..169]);
    }

    [Fact]
    public void The_value_date_is_two_digits_of_year()
    {
        var order = HalcomPaymentOrderWriter.Write(Orders).Content!
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)[2];

        // 150926 is the fifteenth of September 2026.
        Assert.Equal("150926", order[209..215]);
        Assert.Equal('0', order[215]);
        Assert.Equal('1', order[216]);
    }

    [Fact]
    public void The_bytes_are_windows_1250_and_end_with_the_DOS_marker()
    {
        // Not UTF-8. A name with a Ć in it becomes two bytes and every fixed-width column after it
        // moves, so the bank reads the amount out of the middle of somebody's street.
        var bytes = HalcomPaymentOrderWriter.Bytes(HalcomPaymentOrderWriter.Write(Orders));

        Assert.Equal(0x1A, bytes[^1]);

        // "Obračun" — the č is one byte, 0xE8, in this code page.
        Assert.Contains((byte)0xE8, bytes);

        // And nothing became a UTF-8 lead byte.
        Assert.DoesNotContain((byte)0xC4, bytes);
    }

    [Fact]
    public void A_name_the_code_page_cannot_write_is_refused_rather_than_replaced()
    {
        // Windows-1250 has no Cyrillic. A file with a question mark where a beneficiary's name
        // should be is a payment somebody has to explain to a bank.
        var file = HalcomPaymentOrderWriter.Write(
            [Orders[0] with { BeneficiaryName = "Књиговодство доо" }]);

        Assert.False(file.Written);
        Assert.Contains(file.Refusals, r => r.Contains("Windows-1250", StringComparison.Ordinal));
    }

    [Fact]
    public void Orders_drawn_on_two_different_accounts_are_refused()
    {
        // The header states the account every order is drawn on, so a second one would be paid
        // from the first one's account without anything saying so.
        var file = HalcomPaymentOrderWriter.Write(
            [Orders[0], Orders[1] with { OrderingAccount = "160000000099999999" }]);

        Assert.False(file.Written);
        Assert.Contains(file.Refusals, r => r.Contains("istog računa", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("")]
    public void An_account_that_is_not_eighteen_digits_is_refused(string account)
    {
        var file = HalcomPaymentOrderWriter.Write([Orders[0] with { BeneficiaryAccount = account }]);

        Assert.False(file.Written);
    }

    [Fact]
    public void A_purpose_longer_than_the_field_is_refused_rather_than_truncated()
    {
        // Truncation here is how a payment arrives describing something other than what it is for.
        var file = HalcomPaymentOrderWriter.Write(
            [Orders[0] with { Purpose = new string('A', 37) }]);

        Assert.False(file.Written);
        Assert.Contains(file.Refusals, r => r.Contains("Svrha plaćanja", StringComparison.Ordinal));
    }

    [Fact]
    public void A_non_dinar_amount_is_refused_because_this_is_the_domestic_transfer()
    {
        var file = HalcomPaymentOrderWriter.Write([Orders[0] with { Currency = "EUR" }]);

        Assert.False(file.Written);
        Assert.Contains(file.Refusals, r => r.Contains("devizni nalog", StringComparison.Ordinal));
    }

    [Fact]
    public void A_model_with_no_reference_is_refused()
    {
        var file = HalcomPaymentOrderWriter.Write(
            [Orders[0] with { CreditReferenceModel = "97", CreditReference = null }]);

        Assert.False(file.Written);
        Assert.Contains(file.Refusals, r => r.Contains("bez poziva na broj", StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_file_is_refused()
    {
        Assert.False(HalcomPaymentOrderWriter.Write([]).Written);
    }

    // ---- fixtures ------------------------------------------------------------------------------

    private static readonly PaymentOrder[] Orders =
    [
        Order("325000000000000001", "KNJIGOVODSTVO DOO", "GLAVNA 1", "11300 Smed",
            15_000.00m, "221", "knjigovodstvene usluge 08/2026", new DateOnly(2026, 9, 15)),

        Order("840000000000000001", "Poreska uprava (Centrala)", "Cara Dusana 145", "Beograd Ze",
            27_407.82m, "253", "Akontacija poreza na dobit 08.2026", new DateOnly(2026, 9, 15),
            creditModel: "97", creditReference: "9999999999999999999"),

        Order("840000000000000002", "Poreska uprava (Centrala)", "Cara Dusana 145", "Beograd Ze",
            56_145.02m, "254", "Objedinjena naplata poreza i doprino", new DateOnly(2026, 9, 30),
            creditModel: "97", creditReference: "9999999999999999999",
            debitReference: "8888888888"),

        Order("115000000000000001", "Petar Petrovic", "Ulica 1", "Smederevo",
            34_000.00m, "240", "Obračun za mesec avgust 2026", new DateOnly(2026, 9, 30),
            creditModel: "97", creditReference: "9999999999999999999",
            debitReference: "8888888888"),

        Order("115000000000000002", "Ana Anic", "Ulica 2", "Smederevo",
            62_428.00m, "240", "Obračun za mesec avgust 2026", new DateOnly(2026, 9, 30),
            creditModel: "97", creditReference: "9999999999999999999",
            debitReference: "8888888888")
    ];

    private static PaymentOrder Order(
        string account,
        string name,
        string address,
        string place,
        decimal amount,
        string purposeCode,
        string purpose,
        DateOnly valueDate,
        string? creditModel = null,
        string? creditReference = null,
        string? debitReference = null) =>
        new(
            OrderingAccount: "160000000012345678",
            OrderingName: "FIRMA DOO",
            OrderingPlace: "Smederevo",
            OrderingRegistrationNumber: "0000000",
            BeneficiaryAccount: account,
            BeneficiaryName: name,
            BeneficiaryPlace: place,
            Amount: amount,
            Currency: "RSD",
            ValueDate: valueDate,
            PurposeCode: purposeCode,
            Purpose: purpose,
            DebitReference: debitReference,
            CreditReferenceModel: creditModel,
            CreditReference: creditReference,
            BeneficiaryAddress: address);

    /// <summary>The real file's records, with its identities replaced at the same lengths.</summary>
    private static readonly string[] Expected =
    [
        "160000000012345678FIRMA DOO                          Smederevo                                                                                                         MULTI E-BANK0",
        "160000000012345678FIRMA DOO                          Smederevo 00000001949808400005                                                                                                9",
        "325000000000000001KNJIGOVODSTVO DOO                  GLAVNA 1                           11300 Smed0                         knjigovodstvene usluge 08/2026      00000 221  0000001500000                         15092601",
        "840000000000000001Poreska uprava (Centrala)          Cara Dusana 145                    Beograd Ze0                         Akontacija poreza na dobit 08.2026  00000 253  0000002740782979999999999999999999    15092601",
        "840000000000000002Poreska uprava (Centrala)          Cara Dusana 145                    Beograd Ze0  8888888888             Objedinjena naplata poreza i doprino00000 254  0000005614502979999999999999999999    30092601",
        "115000000000000001Petar Petrovic                     Ulica 1                            Smederevo 0  8888888888             Obračun za mesec avgust 2026        00000 240  0000003400000979999999999999999999    30092601",
        "115000000000000002Ana Anic                           Ulica 2                            Smederevo 0  8888888888             Obračun za mesec avgust 2026        00000 240  0000006242800979999999999999999999    30092601"
    ];
}
