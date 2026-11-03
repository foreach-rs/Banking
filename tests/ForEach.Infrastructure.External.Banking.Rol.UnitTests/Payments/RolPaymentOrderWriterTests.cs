using System.Text;
using ForEach.Infrastructure.External.Banking.Payments;
using ForEach.Infrastructure.External.Banking.Rol.Payments;

namespace ForEach.Infrastructure.External.Banking.Rol.UnitTests.Payments;

/// <summary>
/// Writing a ROL payment order.
///
/// This is the side where being wrong costs the money rather than a morning: a bank that accepts an
/// order with a truncated account number moves real dinars to a real account. So the tests here are
/// mostly about **refusals** — the things the writer must not quietly repair — plus the two
/// mechanical facts a file is rejected for outright: the encoding and the decimal format.
/// </summary>
public class RolPaymentOrderWriterTests
{
    [Fact]
    public void An_order_writes_the_fields_the_bank_expects()
    {
        var file = RolPaymentOrderWriter.Write(Order());

        Assert.True(file.Written);
        Assert.Empty(file.Refusals);

        var xml = file.Content!;

        Assert.Contains("<SifraProizvodaCORE>501</SifraProizvodaCORE>", xml, StringComparison.Ordinal);
        Assert.Contains("<SifraValute>941</SifraValute>", xml, StringComparison.Ordinal);

        // Bare eighteen digits — the opposite of what the Asseco format wants for the same number.
        Assert.Contains(
            "<RacunNalogodavca>265110031000011111</RacunNalogodavca>", xml, StringComparison.Ordinal);

        Assert.Contains(
            "<RacunKorisnika>330000002700011111</RacunKorisnika>", xml, StringComparison.Ordinal);

        Assert.Contains("<DatumValute>10.02.2015</DatumValute>", xml, StringComparison.Ordinal);
        Assert.Contains("<ModelKorisnika>97</ModelKorisnika>", xml, StringComparison.Ordinal);
        Assert.Contains("<Prioritet>100</Prioritet>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_amount_is_written_with_two_decimals_and_a_dot()
    {
        // "10000,1" and "10000.1" are both files the bank reads as something other than the amount
        // intended, and a machine set to a Serbian culture produces the first without being asked.
        var file = RolPaymentOrderWriter.Write(Order() with { Amount = 10_000.1m });

        Assert.Contains("<Iznos>10000.10</Iznos>", file.Content!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_account_written_with_dashes_is_accepted_and_normalised()
    {
        // Staff read account numbers off letterheads, where they carry dashes. Accepting both and
        // demanding eighteen digits afterwards is safe; padding a short one would not be.
        var file = RolPaymentOrderWriter.Write(
            Order() with { BeneficiaryAccount = "330-0000027000111-11" });

        Assert.True(file.Written);

        Assert.Contains(
            "<RacunKorisnika>330000002700011111</RacunKorisnika>",
            file.Content!,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_file_is_UTF16_with_a_byte_order_mark()
    {
        // Not cosmetic — it is what the bank's importer reads. A UTF-8 file looks perfect in an
        // editor and is refused on upload.
        var bytes = RolPaymentOrderWriter.Bytes(RolPaymentOrderWriter.Write(Order()));

        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xFE, bytes[1]);

        // And it round-trips: the second byte of the first character is the high byte of '<'.
        Assert.Equal((byte)'<', bytes[2]);
        Assert.Equal(0x00, bytes[3]);
    }

    [Fact]
    public void The_document_carries_no_XML_declaration()
    {
        // Raiffeisen's own examples have none, and an importer reading the first bytes expecting
        // "<Nalog" is one a declaration would break.
        var xml = RolPaymentOrderWriter.Write(Order()).Content!;

        Assert.StartsWith("<Nalog>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void A_field_the_caller_left_blank_is_written_as_a_paired_tag_like_the_banks_example()
    {
        // <ModelNalogodavca></ModelNalogodavca>, not <ModelNalogodavca/>. Identical to an XML
        // parser, and Raiffeisen's own examples distinguish them — caller-blank fields are paired
        // and only the bank-filled ones self-close. Free insurance if their importer ever does
        // string matching rather than parsing.
        var xml = RolPaymentOrderWriter.Write(Order() with { DebitReference = null }).Content!;

        Assert.Contains(
            "<PozivNaBrojNalogodavca></PozivNaBrojNalogodavca>", xml, StringComparison.Ordinal);

        // And the bank-filled ones stay self-closing, as the example has them.
        Assert.Contains("<TransakcijaID />", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void A_batch_names_each_order_the_way_the_bank_does()
    {
        var file = RolPaymentOrderWriter.WriteBatch([Order(), Order() with { Amount = 250m }]);

        Assert.True(file.Written);

        var xml = file.Content!;

        Assert.Contains("<PaketZaSlanje>", xml, StringComparison.Ordinal);
        Assert.Contains("ImeFajla=\"Nalog_1.XML\"", xml, StringComparison.Ordinal);
        Assert.Contains("ImeFajla=\"Nalog_2.XML\"", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void One_bad_order_refuses_the_whole_batch()
    {
        // A file holding the first order and not the second is worse than no file: the bank takes
        // it, and the supplier who is missing from it is unpaid with nothing on any screen saying
        // which one.
        var file = RolPaymentOrderWriter.WriteBatch(
            [Order(), Order() with { BeneficiaryAccount = "12345" }]);

        Assert.False(file.Written);
        Assert.Null(file.Content);

        Assert.Contains(file.Refusals, refusal => refusal.StartsWith("Nalog 2:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("12345", "Račun korisnika")]
    [InlineData("", "Račun korisnika")]
    [InlineData("3300000027000111119", "Račun korisnika")]
    public void An_account_that_is_not_eighteen_digits_is_refused_by_name(string account, string named)
    {
        var file = RolPaymentOrderWriter.Write(Order() with { BeneficiaryAccount = account });

        Assert.False(file.Written);
        Assert.Contains(file.Refusals, refusal => refusal.Contains(named, StringComparison.Ordinal));
    }

    [Fact]
    public void A_poziv_na_broj_with_a_forbidden_character_is_refused_rather_than_stripped()
    {
        // The specification allows digits, the English alphabet, "-" and "/". A Serbian letter
        // typed into a reference is refused here rather than at the bank — and stripping it would
        // send the money quoting a reference that names nothing.
        var file = RolPaymentOrderWriter.Write(Order() with { CreditReference = "RAČUN-114" });

        Assert.False(file.Written);
        Assert.Contains(file.Refusals, refusal => refusal.Contains("Poziv na broj", StringComparison.Ordinal));
    }

    [Fact]
    public void A_control_character_in_a_field_is_refused_by_name_rather_than_thrown()
    {
        // The XML writer throws on U+0001 rather than escaping it, and a line break inside a
        // purpose is one the bank's confirmation prints as two.
        var broken = RolPaymentOrderWriter.Write(Order() with { Purpose = "Plaćanje\r\nfakture" });

        Assert.False(broken.Written);
        Assert.Contains(broken.Refusals, refusal => refusal.Contains("Svrha doznake", StringComparison.Ordinal));

        var control = RolPaymentOrderWriter.Write(Order() with { BeneficiaryName = "ALFA\u0001DOO" });

        Assert.False(control.Written);
        Assert.Contains(control.Refusals, refusal => refusal.Contains("Naziv korisnika", StringComparison.Ordinal));
    }

    [Fact]
    public void A_model_with_no_reference_is_refused()
    {
        // The bank validates the control digits of a 97 reference, and there is nothing to
        // validate. Worse than neither.
        var file = RolPaymentOrderWriter.Write(
            Order() with { CreditReferenceModel = "97", CreditReference = null });

        Assert.False(file.Written);
        Assert.Contains(file.Refusals, refusal => refusal.Contains("bez poziva na broj", StringComparison.Ordinal));
    }

    [Fact]
    public void A_non_dinar_amount_is_refused_because_that_is_a_different_document()
    {
        // Writing a euro amount into a dinar order produces a file the bank takes, for the number
        // of dinars the euro figure happened to be.
        var file = RolPaymentOrderWriter.Write(Order() with { Currency = "EUR" });

        Assert.False(file.Written);
        Assert.Contains(file.Refusals, refusal => refusal.Contains("devizni nalog", StringComparison.Ordinal));
    }

    [Fact]
    public void An_amount_with_more_than_two_decimals_is_refused_rather_than_rounded()
    {
        // Rounding here decides, silently, that somebody's figure was wrong.
        var file = RolPaymentOrderWriter.Write(Order() with { Amount = 100.005m });

        Assert.False(file.Written);
        Assert.Contains(file.Refusals, refusal => refusal.Contains("dve decimale", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_zero_or_negative_amount_is_refused(decimal amount)
    {
        var file = RolPaymentOrderWriter.Write(Order() with { Amount = amount });

        Assert.False(file.Written);
    }

    [Fact]
    public void A_purpose_longer_than_the_field_is_refused_rather_than_truncated()
    {
        // Truncation here is how a payment arrives describing something other than what it is for.
        var file = RolPaymentOrderWriter.Write(Order() with { Purpose = new string('A', 106) });

        Assert.False(file.Written);
        Assert.Contains(file.Refusals, refusal => refusal.Contains("Svrha doznake", StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_batch_is_refused()
    {
        var file = RolPaymentOrderWriter.WriteBatch([]);

        Assert.False(file.Written);
    }

    [Fact]
    public void Serbian_letters_in_a_name_survive_the_encoding()
    {
        var file = RolPaymentOrderWriter.Write(Order() with { BeneficiaryName = "Šećer Đačić d.o.o." });

        var bytes = RolPaymentOrderWriter.Bytes(file);

        var round = new UnicodeEncoding(bigEndian: false, byteOrderMark: true)
            .GetString(bytes, 2, bytes.Length - 2);

        Assert.Contains("Šećer Đačić d.o.o.", round, StringComparison.Ordinal);
    }

    private static PaymentOrder Order() =>
        new(
            OrderingAccount: "265110031000011111",
            OrderingName: "BETA DOO",
            OrderingPlace: "BEOGRAD",
            OrderingRegistrationNumber: "4117111111",
            BeneficiaryAccount: "330000002700011111",
            BeneficiaryName: "ALFA DOO",
            BeneficiaryPlace: "BEOGRAD",
            Amount: 10_000.10m,
            Currency: "RSD",
            ValueDate: new DateOnly(2015, 2, 10),
            PurposeCode: "223",
            Purpose: "Investicije u objekte i opremu",
            CreditReferenceModel: "97",
            CreditReference: "123456");
}
