using ForEach.Infrastructure.External.Banking.Payments;
using ForEach.Infrastructure.External.Banking.Rol.Payments;

namespace ForEach.Infrastructure.External.Banking.Rol.UnitTests.Payments;

/// <summary>
/// Writing a ROL devizni payment order.
///
/// Three rules interlock around the cover basis, and each of them is a file the bank refuses — or
/// takes and funds from somewhere nobody chose:
///
/// - `$` and `D` may not be denominated in dinars, and `S` may only be dinars;
/// - `$` and `D` must name the payer's account, and `S` must not;
/// - the statistics must account for the whole amount.
///
/// The last is inferred rather than quoted from the specification, and the writer says so. It is
/// enforced because the statistics are how the payment is declared to the National Bank, and a set
/// that does not total the payment declares something other than what was paid.
/// </summary>
public class RolForeignPaymentOrderWriterTests
{
    [Fact]
    public void An_order_writes_the_fields_the_bank_expects()
    {
        var file = RolForeignPaymentOrderWriter.Write(Order());

        Assert.True(file.Written);
        Assert.Empty(file.Refusals);

        var xml = file.Content!;

        // 210 for a devizni order, where the dinar one is 501.
        Assert.Contains("<SifraProizvodaCORE>210</SifraProizvodaCORE>", xml, StringComparison.Ordinal);

        Assert.Contains("<Korisnik>ADRIA DD</Korisnik>", xml, StringComparison.Ordinal);
        Assert.Contains("<RacunKorisnika>DE89370400440532013000</RacunKorisnika>", xml, StringComparison.Ordinal);
        Assert.Contains("<SifraValute>978</SifraValute>", xml, StringComparison.Ordinal);
        Assert.Contains("<Iznos>1010.10</Iznos>", xml, StringComparison.Ordinal);
        Assert.Contains("<TrazeniDatumValute>06.02.2015</TrazeniDatumValute>", xml, StringComparison.Ordinal);
        Assert.Contains("<IndikatorPokrica>$</IndikatorPokrica>", xml, StringComparison.Ordinal);
        Assert.Contains("<TudjiTroskovi>BEN</TudjiTroskovi>", xml, StringComparison.Ordinal);
        Assert.Contains("<Partija>265000000000000001</Partija>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void A_statistics_item_is_written_signed_the_way_the_banks_examples_write_it()
    {
        var xml = RolForeignPaymentOrderWriter.Write(Order()).Content!;

        Assert.Contains("<IznosStatistika>+1010.10</IznosStatistika>", xml, StringComparison.Ordinal);
        Assert.Contains("<SifraOsnovaPlacanja>112</SifraOsnovaPlacanja>", xml, StringComparison.Ordinal);
        Assert.Contains("<BrojUgovora>123ABC-RTY</BrojUgovora>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_statistics_ordinal_is_the_position_rather_than_something_a_caller_can_repeat()
    {
        var order = Order() with
        {
            Amount = 300m,
            Statistics =
            [
                new ForeignPaymentStatistic("110", "Prva", 100m),
                new ForeignPaymentStatistic("110", "Druga", 200m)
            ]
        };

        var xml = RolForeignPaymentOrderWriter.Write(order).Content!;

        Assert.Contains("<RedniBrojStavke>1</RedniBrojStavke>", xml, StringComparison.Ordinal);
        Assert.Contains("<RedniBrojStavke>2</RedniBrojStavke>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_file_is_UTF16_with_a_byte_order_mark_like_the_dinar_one()
    {
        var bytes = RolForeignPaymentOrderWriter.Bytes(RolForeignPaymentOrderWriter.Write(Order()));

        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xFE, bytes[1]);
    }

    // ---- the cover basis, and the three rules that hang off it ---------------------------------

    [Fact]
    public void A_dinar_cover_may_not_be_denominated_in_a_foreign_currency()
    {
        var file = RolForeignPaymentOrderWriter.Write(
            Order() with { CoverBasis = ForeignCoverBasis.Dinar, OrderingAccount = null });

        Assert.False(file.Written);
        Assert.Contains(file.Refusals, r => r.Contains("valuta mora biti RSD", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ForeignCoverBasis.ForeignCurrencyAccount)]
    [InlineData(ForeignCoverBasis.PurchaseCurrency)]
    public void A_foreign_cover_may_not_be_denominated_in_dinars(ForeignCoverBasis basis)
    {
        var file = RolForeignPaymentOrderWriter.Write(
            Order() with { CoverBasis = basis, CurrencyCode = "941" });

        Assert.False(file.Written);
        Assert.Contains(file.Refusals, r => r.Contains("ne sme biti RSD", StringComparison.Ordinal));
    }

    [Fact]
    public void A_dinar_cover_writes_no_account_and_is_refused_if_one_is_given()
    {
        // The bank takes a dinar payment from the account the order arrives on. Naming one is what
        // makes the file wrong, so it is refused rather than dropped.
        var file = RolForeignPaymentOrderWriter.Write(
            Order() with
            {
                CoverBasis = ForeignCoverBasis.Dinar,
                CurrencyCode = "941",
                OrderingAccount = "265000000000000001"
            });

        Assert.False(file.Written);
        Assert.Contains(file.Refusals, r => r.Contains("partija se ne popunjava", StringComparison.Ordinal));
    }

    [Fact]
    public void A_dinar_cover_order_is_written_when_it_names_no_account()
    {
        var file = RolForeignPaymentOrderWriter.Write(
            Order() with
            {
                CoverBasis = ForeignCoverBasis.Dinar,
                CurrencyCode = "941",
                OrderingAccount = null
            });

        Assert.True(file.Written);
        Assert.Contains("<IndikatorPokrica>S</IndikatorPokrica>", file.Content!, StringComparison.Ordinal);
        Assert.Contains("<Partija></Partija>", file.Content!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_purchase_cover_matches_the_banks_own_D_example()
    {
        // Checked against Raiffeisen's `DevizniNalogPrimer_D`, which is the one cover basis the
        // other examples do not show: indicator D, currency 978, and a partija named — so "D"
        // behaves like "$" on both rules rather than like the dinar case it sounds closer to.
        var file = RolForeignPaymentOrderWriter.Write(
            Order() with
            {
                CoverBasis = ForeignCoverBasis.PurchaseCurrency,
                CurrencyCode = "978",
                OrderingAccount = "265000000000000001",
                Amount = 3_030.03m,
                SpecialMark = 1,
                PaymentInstrument = 2,
                Statistics =
                [
                    new ForeignPaymentStatistic(
                        "112", "Placanja za uvoz robe po izvrsenom uvozu", 3_030.03m, "123ABC-RTY", 2022)
                ]
            });

        Assert.True(file.Written);

        var xml = file.Content!;

        Assert.Contains("<IndikatorPokrica>D</IndikatorPokrica>", xml, StringComparison.Ordinal);
        Assert.Contains("<SifraValute>978</SifraValute>", xml, StringComparison.Ordinal);
        Assert.Contains("<Partija>265000000000000001</Partija>", xml, StringComparison.Ordinal);
        Assert.Contains("<IznosStatistika>+3030.03</IznosStatistika>", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void A_batch_is_wrapped_the_way_the_banks_own_bundle_example_is()
    {
        // The devizni bundle shares the dinar one's envelope — <Client><PaketZaSlanje> with one
        // <Nalog ImeFajla="…"> each, every order carrying its own <Statistika>. That was an
        // assumption when the writer was built and is checked here against their example.
        var file = RolForeignPaymentOrderWriter.WriteBatch([Order(), Order(), Order(), Order()]);

        Assert.True(file.Written);

        var xml = file.Content!;

        Assert.StartsWith("<Client>", xml, StringComparison.Ordinal);
        Assert.Contains("<PaketZaSlanje>", xml, StringComparison.Ordinal);
        Assert.Contains("ImeFajla=\"Nalog_4.XML\"", xml, StringComparison.Ordinal);
        Assert.EndsWith("</PaketZaSlanje></Client>", xml.TrimEnd(), StringComparison.Ordinal);

        // One statistics block per order, not one for the batch.
        Assert.Equal(4, xml.Split("<Statistika>", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void A_foreign_cover_without_an_account_is_refused()
    {
        var file = RolForeignPaymentOrderWriter.Write(Order() with { OrderingAccount = null });

        Assert.False(file.Written);
        Assert.Contains(file.Refusals, r => r.Contains("partija je obavezna", StringComparison.Ordinal));
    }

    [Fact]
    public void Statistics_that_do_not_total_the_amount_are_refused()
    {
        // The declaration to the National Bank would otherwise say something other than what was
        // paid, and the payment itself would still be correct — so nothing downstream would notice.
        var file = RolForeignPaymentOrderWriter.Write(
            Order() with { Statistics = [new ForeignPaymentStatistic("110", "Delimično", 500m)] });

        Assert.False(file.Written);
        Assert.Contains(file.Refusals, r => r.Contains("Zbir stavki statistike", StringComparison.Ordinal));
    }

    [Fact]
    public void An_order_with_no_statistics_is_refused()
    {
        var file = RolForeignPaymentOrderWriter.Write(Order() with { Statistics = [] });

        Assert.False(file.Written);
        Assert.Contains(file.Refusals, r => r.Contains("stavku statistike", StringComparison.Ordinal));
    }

    [Fact]
    public void Reason_code_112_demands_the_invoice_it_settles()
    {
        // It is payment for goods already imported, and the invoice is what makes the declaration
        // checkable against the customs record.
        var file = RolForeignPaymentOrderWriter.Write(
            Order() with
            {
                Statistics = [new ForeignPaymentStatistic("112", "Uvoz robe", 1_010.10m)]
            });

        Assert.False(file.Written);
        Assert.Contains(file.Refusals, r => r.Contains("broj fakture je obavezan", StringComparison.Ordinal));
    }

    [Fact]
    public void An_invoice_number_without_its_year_is_refused()
    {
        var file = RolForeignPaymentOrderWriter.Write(
            Order() with
            {
                Statistics =
                [
                    new ForeignPaymentStatistic("110", "Usluge", 1_010.10m, ContractNumber: "F-1")
                ]
            });

        Assert.False(file.Written);
        Assert.Contains(file.Refusals, r => r.Contains("godina", StringComparison.Ordinal));
    }

    // ---- the beneficiary's bank ----------------------------------------------------------------

    [Fact]
    public void An_order_naming_the_bank_by_neither_swift_nor_name_is_refused()
    {
        // A payment with nowhere to go, and the bank cannot ask.
        var file = RolForeignPaymentOrderWriter.Write(
            Order() with { BeneficiaryBankSwift = null, BeneficiaryBankName = null });

        Assert.False(file.Written);
        Assert.Contains(file.Refusals, r => r.Contains("SWIFT", StringComparison.Ordinal));
    }

    [Fact]
    public void Either_the_swift_or_the_bank_name_alone_is_enough()
    {
        Assert.True(RolForeignPaymentOrderWriter
            .Write(Order() with { BeneficiaryBankName = null }).Written);

        Assert.True(RolForeignPaymentOrderWriter
            .Write(Order() with { BeneficiaryBankSwift = null }).Written);
    }

    [Fact]
    public void The_beneficiary_address_is_held_to_the_limit_in_force_today()
    {
        // The field grows to 140 characters on 16 November 2026. Until then 70 is what the bank
        // takes, and accepting more produces a file it truncates or rejects.
        var file = RolForeignPaymentOrderWriter.Write(
            Order() with { BeneficiaryAddress = new string('A', 71) });

        Assert.False(file.Written);
        Assert.Contains(file.Refusals, r => r.Contains("Adresa korisnika", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("12")]
    [InlineData("1234")]
    [InlineData("EUR")]
    public void A_currency_that_is_not_a_three_digit_code_is_refused(string code)
    {
        // The file wants the numeric ISO code. "EUR" here would be written straight through and the
        // bank would refuse a file that looked entirely reasonable.
        var file = RolForeignPaymentOrderWriter.Write(Order() with { CurrencyCode = code });

        Assert.False(file.Written);
        Assert.Contains(file.Refusals, r => r.Contains("Šifra valute", StringComparison.Ordinal));
    }

    [Fact]
    public void One_bad_order_refuses_the_whole_batch()
    {
        var file = RolForeignPaymentOrderWriter.WriteBatch(
            [Order(), Order() with { BeneficiaryName = "  " }]);

        Assert.False(file.Written);
        Assert.Null(file.Content);
        Assert.Contains(file.Refusals, r => r.StartsWith("Nalog 2:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_batch_names_each_order_the_way_the_bank_does()
    {
        var file = RolForeignPaymentOrderWriter.WriteBatch([Order(), Order()]);

        Assert.True(file.Written);
        Assert.Contains("ImeFajla=\"Nalog_2.XML\"", file.Content!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_special_mark_outside_one_to_seven_is_refused()
    {
        Assert.False(RolForeignPaymentOrderWriter.Write(Order() with { SpecialMark = 8 }).Written);
        Assert.True(RolForeignPaymentOrderWriter.Write(Order() with { SpecialMark = 7 }).Written);
    }

    private static ForeignPaymentOrder Order() =>
        new(
            OrderingRegistrationNumber: "4117111111",
            OrderingPlace: "Beograd",
            BeneficiaryName: "ADRIA DD",
            BeneficiaryPlace: "Sofija",
            BeneficiaryCountryCode: "191",
            BeneficiaryAccount: "DE89370400440532013000",
            Amount: 1_010.10m,
            CurrencyCode: "978",
            ValueDate: new DateOnly(2015, 2, 6),
            CoverBasis: ForeignCoverBasis.ForeignCurrencyAccount,
            Charges: ForeignChargeOption.Beneficiary,
            Statistics:
            [
                new ForeignPaymentStatistic(
                    ReasonCode: "112",
                    Description: "Placanja za uvoz robe po izvrsenom uvozu",
                    Amount: 1_010.10m,
                    ContractNumber: "123ABC-RTY",
                    ContractYear: 2022)
            ],
            OrderingAccount: "265000000000000001",
            BeneficiaryAddress: "Adresa bb",
            BeneficiaryBankSwift: "DEUTDEFF",
            BeneficiaryBankName: "DEUTSCHE BANK, Frankfurt",
            Details: "primer placanja sa deviznog racuna",
            SpecialMark: 3,
            PaymentInstrument: 1);
}
