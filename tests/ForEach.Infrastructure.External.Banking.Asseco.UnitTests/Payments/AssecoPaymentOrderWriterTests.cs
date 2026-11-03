using ForEach.Infrastructure.External.Banking.Asseco.Payments;
using ForEach.Infrastructure.External.Banking.Payments;

namespace ForEach.Infrastructure.External.Banking.Asseco.UnitTests.Payments;

/// <summary>
/// Writing an Asseco payment request.
///
/// The one that matters most here is the account format: the same eighteen digits that ROL wants
/// bare are written with dashes for this bank. A writer that shared a formatter between the two
/// would produce a file one of them refuses — and the other might not.
/// </summary>
public class AssecoPaymentOrderWriterTests
{
    [Fact]
    public void The_account_is_written_in_the_banks_own_three_thirteen_two_shape()
    {
        var xml = AssecoPaymentOrderWriter.Write(Order()).Content!;

        Assert.Contains("<acctid>265-1100310000111-11</acctid>", xml, StringComparison.Ordinal);
        Assert.Contains("<acctid>330-0000027000111-11</acctid>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_bank_code_is_derived_from_the_account_rather_than_carried_beside_it()
    {
        // Two fields that can disagree are two fields that eventually do, and the file cannot say
        // which of them it meant.
        var xml = AssecoPaymentOrderWriter.Write(Order()).Content!;

        Assert.Contains("<bankid>265</bankid>", xml, StringComparison.Ordinal);
        Assert.Contains("<bankid>330</bankid>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_request_states_how_many_orders_are_under_it()
    {
        // The service checks the body against this count; one that disagrees is a rejection whose
        // reason nobody can see.
        var xml = AssecoPaymentOrderWriter.WriteBatch([Order(), Order(), Order()]).Content!;

        Assert.Contains("count=\"3\"", xml, StringComparison.Ordinal);
        Assert.Equal(3, Occurrences(xml, "<pmtorder>"));
    }

    [Fact]
    public void An_order_carries_the_dinar_label_this_family_still_uses()
    {
        var xml = AssecoPaymentOrderWriter.Write(Order()).Content!;

        Assert.Contains("<curdef>DIN</curdef>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_value_date_is_written_as_a_timestamp_at_midnight()
    {
        var xml = AssecoPaymentOrderWriter.Write(Order()).Content!;

        Assert.Contains("<dtdue>2026-04-16T00:00:00</dtdue>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void Urgency_says_which_settlement_the_payment_goes_through()
    {
        Assert.Contains(
            "<urgency>ACH</urgency>",
            AssecoPaymentOrderWriter.Write(Order()).Content!,
            StringComparison.Ordinal);

        Assert.Contains(
            "<urgency>RTGS</urgency>",
            AssecoPaymentOrderWriter.Write(Order() with { Priority = PaymentPriority.Urgent }).Content!,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_beneficiarys_reference_goes_in_the_payee_field_because_we_are_paying()
    {
        // On a payment out we are the ordering customer: refnumber is ours and payeerefnumber
        // names their invoice. Swapping the two sends the money quoting our own internal number,
        // which their ledger cannot match.
        var xml = AssecoPaymentOrderWriter.Write(Order()).Content!;

        Assert.Contains("<refnumber>NAL-5501</refnumber>", xml, StringComparison.Ordinal);
        Assert.Contains("<payeerefnumber>FT-9912</payeerefnumber>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_file_is_UTF8_without_a_byte_order_mark()
    {
        // The declaration already states the encoding; a mark before it is what makes a parser
        // refuse the first character of an otherwise correct document.
        var bytes = AssecoPaymentOrderWriter.Bytes(AssecoPaymentOrderWriter.Write(Order()));

        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal((byte)'<', bytes[0]);
    }

    [Fact]
    public void The_declaration_says_utf8_because_that_is_what_the_bytes_are()
    {
        // A writer handed a StringBuilder declares utf-16 — the encoding of the string — while
        // Bytes() produces UTF-8. A declaration that contradicts the bytes is a file a strict
        // parser refuses on its first line.
        var file = AssecoPaymentOrderWriter.Write(Order());

        Assert.StartsWith(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>", file.Content!, StringComparison.Ordinal);

        var bytes = AssecoPaymentOrderWriter.Bytes(file);

        Assert.Equal((byte)'<', bytes[0]);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.NotEqual(0xBB, bytes[1]);
    }

    [Fact]
    public void A_control_character_in_a_field_is_refused_by_name_rather_than_thrown()
    {
        // The XML writer throws on U+0001 rather than escaping it, and a line break inside a
        // purpose is one the bank's confirmation prints as two.
        var broken = AssecoPaymentOrderWriter.Write(Order() with { Purpose = "Plaćanje\r\nfakture" });

        Assert.False(broken.Written);
        Assert.Contains(broken.Refusals, r => r.Contains("Svrha plaćanja", StringComparison.Ordinal));

        var control = AssecoPaymentOrderWriter.Write(Order() with { BeneficiaryName = "ALFA\u0001DOO" });

        Assert.False(control.Written);
        Assert.Contains(control.Refusals, r => r.Contains("Naziv korisnika", StringComparison.Ordinal));
    }

    [Fact]
    public void An_account_that_is_not_eighteen_digits_is_refused()
    {
        var file = AssecoPaymentOrderWriter.Write(Order() with { BeneficiaryAccount = "330-11" });

        Assert.False(file.Written);
        Assert.Contains(file.Refusals, r => r.Contains("Račun korisnika", StringComparison.Ordinal));
    }

    [Fact]
    public void One_bad_order_refuses_the_whole_request()
    {
        var file = AssecoPaymentOrderWriter.WriteBatch(
            [Order(), Order() with { Purpose = "  " }]);

        Assert.False(file.Written);
        Assert.Null(file.Content);
        Assert.Contains(file.Refusals, r => r.StartsWith("Nalog 2:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_non_dinar_amount_is_refused_because_that_is_the_devizni_document()
    {
        var file = AssecoPaymentOrderWriter.Write(Order() with { Currency = "EUR" });

        Assert.False(file.Written);
    }

    [Fact]
    public void A_name_with_an_ampersand_is_escaped_rather_than_breaking_the_file()
    {
        var xml = AssecoPaymentOrderWriter
            .Write(Order() with { BeneficiaryName = "Petrović & sinovi" })
            .Content!;

        Assert.Contains("Petrović &amp; sinovi", xml, StringComparison.Ordinal);
    }

    private static int Occurrences(string text, string needle)
    {
        var count = 0;
        var at = text.IndexOf(needle, StringComparison.Ordinal);

        while (at >= 0)
        {
            count++;
            at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }

        return count;
    }

    private static PaymentOrder Order() =>
        new(
            OrderingAccount: "265110031000011111",
            OrderingName: "BETA DOO",
            OrderingPlace: "BEOGRAD",
            OrderingRegistrationNumber: "4117111111",
            BeneficiaryAccount: "330000002700011111",
            BeneficiaryName: "ALFA DOO",
            BeneficiaryPlace: "NOVI SAD",
            Amount: 10_000.10m,
            Currency: "RSD",
            ValueDate: new DateOnly(2026, 4, 16),
            PurposeCode: "221",
            Purpose: "Plaćanje po fakturi",
            DebitReference: "NAL-5501",
            CreditReferenceModel: "97",
            CreditReference: "FT-9912");
}
