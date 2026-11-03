using ForEach.Infrastructure.External.Banking.Rol.Statements;
using ForEach.Infrastructure.External.Banking.Statements;

namespace ForEach.Infrastructure.External.Banking.Rol.UnitTests.Statements;

/// <summary>
/// Reading a ROL statement.
///
/// The sample below follows the shape of Raiffeisen's own published example (`Formati 2026`), which
/// is where every attribute name and every format here comes from.
///
/// The failures worth writing down are the ones no screen would show. A date read as ISO turns the
/// 26th of June into a parse failure and the 6th of February into the 2nd of June. A storno read
/// from "whichever column is non-zero" books a correction as money arriving. And the wrong one of
/// the two poziv-na-broj attributes matches inbound payments against a number this company has
/// never seen.
/// </summary>
public class RolStatementReaderTests
{
    [Fact]
    public void A_dinar_statement_carries_its_account_number_and_balances()
    {
        var read = RolStatementReader.Read(DinarSample);

        Assert.Equal(RolStatementKind.Dinar, read.Kind);

        var statement = read.Statement;

        Assert.Equal("265110031000008888", statement.AccountNumber);
        Assert.Equal("RSD", statement.Currency);
        Assert.Equal("181", statement.Number);
        Assert.Equal(new DateOnly(2006, 6, 26), statement.StatementDate);
        Assert.Equal(11_373_667.73m, statement.OpeningBalance);
        Assert.Equal(13_976_765.67m, statement.ClosingBalance);
    }

    [Fact]
    public void A_customer_payment_is_read_with_the_reference_that_names_our_invoice()
    {
        var entry = RolStatementReader.Read(DinarSample).Statement.Entries[0];

        Assert.Equal(363.44m, entry.Amount);
        Assert.Equal(StatementDirection.Credit, entry.Direction);

        // PozivNaBrojKorisnika — the beneficiary's, which on money in is ours. Taking
        // PozivNaBrojZaduzenjaOdobrenja instead would match against the payer's own bookkeeping
        // reference, which names nothing in this database.
        Assert.Equal("9009206", entry.Reference);

        Assert.Equal("LIDER TRADE COMPANY", entry.CounterpartyName);
        Assert.Equal("220000000005682934", entry.CounterpartyAccount);

        // Broj za reklamaciju — what makes a second import of the same file detectable.
        Assert.Equal("1131062230000090000006", entry.BankReference);
    }

    [Fact]
    public void A_dinar_line_is_booked_on_the_statements_own_date()
    {
        // A dinar Stavka carries only DatumValute. Its booking date is the header's, and a reader
        // that defaulted to today would file August's payments into whichever day somebody
        // imported them.
        var entry = RolStatementReader.Read(DinarSample).Statement.Entries[0];

        Assert.Equal(new DateOnly(2006, 6, 26), entry.BookingDate);
        Assert.Equal(new DateOnly(2006, 6, 26), entry.ValueDate);
    }

    [Fact]
    public void Dates_are_read_as_day_first_not_as_ISO()
    {
        // 06.02.2015 is the sixth of February. Read as ISO it is either a failure or, worse, the
        // second of June — a valid date four months away, on a line nobody would question.
        var statement = RolStatementReader.Read(
            DinarSample.Replace("26.06.2006", "06.02.2015", StringComparison.Ordinal)).Statement;

        Assert.Equal(new DateOnly(2015, 2, 6), statement.StatementDate);
    }

    [Fact]
    public void A_negative_credit_is_a_storno_and_moves_the_money_back_out()
    {
        // Real files carry a correction as a negative Potrazuje rather than as a Duguje. A reader
        // taking "whichever column is non-zero" would book this as money arriving, in the
        // direction that flatters the balance, and the statement would still add up.
        var entry = RolStatementReader.Read(DinarSample).Statement.Entries[2];

        Assert.Equal(2_400m, entry.Amount);
        Assert.Equal(StatementDirection.Debit, entry.Direction);
    }

    [Fact]
    public void A_payment_out_is_a_debit_with_a_positive_amount()
    {
        var entry = RolStatementReader.Read(DinarSample).Statement.Entries[1];

        Assert.Equal(54_538.13m, entry.Amount);
        Assert.Equal(StatementDirection.Debit, entry.Direction);
    }

    [Fact]
    public void The_entries_account_for_the_movement_between_the_balances()
    {
        // The check the host refuses an import on. It is only true if every line was read and read
        // in the right direction, which is why it is worth asserting on a whole sample rather than
        // per entry.
        var statement = RolStatementReader.Read(DinarSample).Statement;

        Assert.True(statement.Reconciles);
    }

    [Fact]
    public void A_foreign_statement_takes_its_currency_from_the_header()
    {
        var read = RolStatementReader.Read(ForeignSample);

        Assert.Equal(RolStatementKind.Foreign, read.Kind);
        Assert.Equal("EUR", read.Statement.Currency);

        // The devizni line carries its own booking date, unlike the dinar one.
        Assert.Equal(new DateOnly(2026, 3, 2), read.Statement.Entries[0].BookingDate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not xml at all")]
    [InlineData("<html><body>a renamed download</body></html>")]
    [InlineData("<TransakcioniRacunPrivredaIzvod />")]
    public void Anything_that_is_not_a_statement_is_answered_rather_than_thrown_at(string? content)
    {
        var read = RolStatementReader.Read(content);

        Assert.Equal(RolStatementKind.Unknown, read.Kind);
        Assert.Empty(read.Statement.Entries);
    }

    [Fact]
    public void A_document_type_declaration_is_refused()
    {
        // No Serbian bank export uses a DTD. One in an uploaded file is a broken export at best and
        // an attempt to read the server's filesystem at worst.
        var read = RolStatementReader.Read(
            """
            <!DOCTYPE izvod [<!ENTITY x SYSTEM "file:///etc/passwd">]>
            <TransakcioniRacunPrivredaIzvod><Zaglavlje Partija="265110031000008888" /></TransakcioniRacunPrivredaIzvod>
            """);

        Assert.Equal(RolStatementKind.Unknown, read.Kind);
    }

    /// <summary>
    /// Raiffeisen's published example, trimmed to four lines: a receipt, a payment out, a storno
    /// and a second receipt. The header's balances are set so the four account for the movement.
    /// </summary>
    private const string DinarSample =
        """
        <TransakcioniRacunPrivredaIzvod>
          <Zaglavlje VrstaIzvoda="R" IzvodID="2437981278" BrojIzvoda="181" DatumIzvoda="26.06.2006"
                     MaticniBroj="4117377777" KomitentNaziv="FIRMA DOO" KomitentAdresa="Adresa bb,"
                     KomitentMesto="BEOGRAD" Partija="265110031000008888"
                     TipRacuna="Tek. rac.dr. pred.u din" PrethodnoStanje="11373667.73"
                     DugovniPromet="54538.13" PotrazniPromet="2657636.07" NovoStanje="13976765.67"
                     StanjeObracunateProvizije="0" TekstPoruke="" />
          <Stavke NalogKorisnik="LIDER TRADE COMPANY" Mesto="" VasBrojNaloga="549020390020011"
                  BrojRacunaPrimaocaPosiljaoca="220000000005682934"
                  Opis="PROMET ROBE I USLUGA FINALNA POTROSNJA" SifraPlacanja="221"
                  SifraPlacanjaOpis="Promet robe i usluga-finalna potrošn" Duguje="0"
                  Potrazuje="363.44" ModelZaduzenjaOdobrenja="99" PozivNaBrojZaduzenjaOdobrenja=""
                  ModelKorisnika="0" PozivNaBrojKorisnika="9009206"
                  BrojZaReklamaciju="1131062230000090000006" Referenca="5131062230000083"
                  Objasnjenje="500251" DatumValute="26.06.2006" />
          <Stavke NalogKorisnik="IDEA PLUS COMMUNICATIONS D.O.O." Mesto="" VasBrojNaloga=""
                  BrojRacunaPrimaocaPosiljaoca="355000000100501634"
                  Opis="PROMET ROBA I USLUGA" SifraPlacanja="221"
                  SifraPlacanjaOpis="Promet robe i usluga-finalna potrošn" Duguje="54538.13"
                  Potrazuje="0" ModelZaduzenjaOdobrenja="0" PozivNaBrojZaduzenjaOdobrenja="774411"
                  ModelKorisnika="97" PozivNaBrojKorisnika="90089125"
                  BrojZaReklamaciju="1131062230093616000028" Referenca="5131062230093863"
                  Objasnjenje="500241" DatumValute="26.06.2006" />
          <Stavke NalogKorisnik="FUTURA PLUS DOO" Mesto="" VasBrojNaloga=""
                  BrojRacunaPrimaocaPosiljaoca="275221843408110528"
                  Opis="STORNO - za pomen" SifraPlacanja="221"
                  SifraPlacanjaOpis="Promet robe i usluga-finalna potrošn" Duguje="0"
                  Potrazuje="-2400" ModelZaduzenjaOdobrenja="99" PozivNaBrojZaduzenjaOdobrenja=""
                  ModelKorisnika="11" PozivNaBrojKorisnika=".08."
                  BrojZaReklamaciju="1131062230040253000006" Referenca="5131062230040247"
                  Objasnjenje="500241" DatumValute="26.06.2006" />
          <Stavke NalogKorisnik="SRNA D.O.O." Mesto="" VasBrojNaloga="654000152660011"
                  BrojRacunaPrimaocaPosiljaoca="325000000003928329"
                  Opis="PROMET ROBE I USLUGA FINALNA POTROŠNJA" SifraPlacanja="221"
                  SifraPlacanjaOpis="Promet robe i usluga-finalna potrošn" Duguje="0"
                  Potrazuje="2659672.63" ModelZaduzenjaOdobrenja="99"
                  PozivNaBrojZaduzenjaOdobrenja="" ModelKorisnika="0" PozivNaBrojKorisnika="5208"
                  BrojZaReklamaciju="1131062230143680000006" Referenca="5131062230143666"
                  Objasnjenje="500241" DatumValute="26.06.2006" />
        </TransakcioniRacunPrivredaIzvod>
        """;

    private const string ForeignSample =
        """
        <RacunPrivredaIzvod>
          <Zaglavlje BrojIzvoda="12" DatumIzvoda="02.03.2026" Partija="265110031000009999"
                     IBANBroj="RS35265110031000009999" OznakaValute="EUR"
                     PrethodnoStanje="1000.00" NovoStanje="2500.00" />
          <Stavke NalogKorisnik="ACME GMBH" RacunNalogodavacKorisnik="DE89370400440532013000"
                  Opis="Advance payment" Duguje="0" Potrazuje="1500.00"
                  DatumObrade="02.03.2026" DatumValute="02.03.2026"
                  ModelKorisnika="97" PozivNaBrojKorisnika="2026-114"
                  BrojZaReklamaciju="9931062230143680000006" TekstOsnova="112"
                  DinarskaProtivvrednost="175800.00" />
        </RacunPrivredaIzvod>
        """;
}
