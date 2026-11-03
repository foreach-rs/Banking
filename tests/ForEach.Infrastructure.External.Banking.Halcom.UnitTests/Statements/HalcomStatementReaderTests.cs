using ForEach.Infrastructure.External.Banking.Halcom.Statements;
using ForEach.Infrastructure.External.Banking.Statements;

namespace ForEach.Infrastructure.External.Banking.Halcom.UnitTests.Statements;

/// <summary>
/// Reading a Halcom export.
///
/// The offsets used by <see cref="Line"/> below were checked against a real Hal E-Bank export
/// before this was written, and two of them differ from what the published specification says:
/// the payment code is a three-digit NBS code across 106–111 rather than a two-digit one behind an
/// "oblik plaćanja" digit, and the recap's dates carry no dots.
///
/// Three failures here are silent. An amount read without its two implied decimals is a
/// hundredfold error. Posting codes 30 and 40 taken as ordinary debits and credits book a reversal
/// as a second payment the same way round. And a bank reference reused across lines of one order —
/// which real files do — makes a host that dedupes on it drop genuine payments.
/// </summary>
public class HalcomStatementReaderTests
{
    [Fact]
    public void A_credit_line_is_read_with_its_reference_and_partner()
    {
        var read = HalcomStatementReader.Read(Line(
            partnerAccount: "160000000026565175",
            posting: "20",
            bookingDate: "27.12.11",
            valueDate: "271211",
            ownAccount: "205000000010804045",
            amount: "000000006980880",
            paymentCode: " 221  ",
            creditReference: "P1194-11",
            purpose: "PROMET ROBE I USLUGA",
            city: "SIMANOVCI",
            partnerName: "KLEEMANN LIFTOVI DOO",
            bankReference: "087000793707972"));

        Assert.Equal(HalcomStatementKind.FixedWidth, read.Kind);

        var entry = Assert.Single(read.Statement.Entries);

        // Two implied decimals: 000000006980880 is 69 808,80 and not 6 980 880.
        Assert.Equal(69_808.80m, entry.Amount);
        Assert.Equal(StatementDirection.Credit, entry.Direction);
        Assert.Equal(new DateOnly(2011, 12, 27), entry.BookingDate);
        Assert.Equal(new DateOnly(2011, 12, 27), entry.ValueDate);

        // The beneficiary's poziv na broj — on money in, our own invoice number.
        Assert.Equal("P1194-11", entry.Reference);

        Assert.Equal("KLEEMANN LIFTOVI DOO", entry.CounterpartyName);
        Assert.Equal("160000000026565175", entry.CounterpartyAccount);
        Assert.Equal("087000793707972", entry.BankReference);
        Assert.Equal("205000000010804045", read.Statement.AccountNumber);
    }

    [Fact]
    public void The_payment_code_is_read_as_the_modern_three_digit_one()
    {
        // The spec describes a two-digit code behind a leading digit; every file written since 2003
        // carries the three-digit NBS code across the same zone with a blank in front.
        var read = HalcomStatementReader.Read(Line(paymentCode: " 221  "));

        Assert.Contains(
            "Šifra plaćanja: 221",
            read.Statement.Entries[0].Description!,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("10", StatementDirection.Debit)]
    [InlineData("20", StatementDirection.Credit)]
    [InlineData("30", StatementDirection.Credit)]
    [InlineData("40", StatementDirection.Debit)]
    public void A_posting_code_decides_which_way_the_money_went(string posting, StatementDirection expected)
    {
        // 30 and 40 are the stornos of 10 and 20, so the money moves the other way. Treated as
        // ordinary postings they double a correction instead of reversing it.
        var read = HalcomStatementReader.Read(Line(posting: posting));

        Assert.Equal(expected, read.Statement.Entries[0].Direction);
    }

    [Fact]
    public void A_storno_says_so_in_its_description()
    {
        var read = HalcomStatementReader.Read(Line(posting: "30"));

        Assert.StartsWith("STORNO", read.Statement.Entries[0].Description!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_repeated_bank_reference_is_disambiguated_rather_than_left_to_collide()
    {
        // Real files reuse the broj za reklamaciju across several lines of one order. A host
        // deduplicating on it alone would keep the first and drop the rest — genuine payments,
        // silently missing, discovered when a customer is chased for money they have paid.
        var read = HalcomStatementReader.Read(
            Line(bankReference: "087000793707972", amount: "000000000010000") + "\r\n"
            + Line(bankReference: "087000793707972", amount: "000000000020000"));

        var references = read.Statement.Entries.Select(entry => entry.BankReference).ToList();

        Assert.Equal(["087000793707972", "087000793707972#2"], references);
    }

    [Fact]
    public void A_line_with_no_bank_reference_still_gets_a_stable_identifier()
    {
        var first = HalcomStatementReader.Read(Line(bankReference: "")).Statement.Entries[0];
        var again = HalcomStatementReader.Read(Line(bankReference: "")).Statement.Entries[0];

        Assert.NotNull(first.BankReference);

        // Stable across reads, or a second import of the same file would look like new money.
        Assert.Equal(first.BankReference, again.BankReference);
    }

    [Fact]
    public void The_recap_supplies_the_statement_number_and_both_balances()
    {
        var read = HalcomStatementReader.Read(
            Line(ownAccount: "205000000010804045", posting: "20", amount: "000000006980880"),
            Recap(
                account: "205000000010804045",
                date: "27122011",
                previousDate: "26122011",
                openingBalance: "000000000306440645",
                debitTotal: "000000000003216625",
                creditTotal: "000000000006980880",
                closingBalance: "000000000310204900",
                number: "247"));

        var statement = read.Statement;

        Assert.Equal("247", statement.Number);
        Assert.Equal(new DateOnly(2011, 12, 27), statement.StatementDate);
        Assert.Equal(3_064_406.45m, statement.OpeningBalance);
        Assert.Equal(3_102_049.00m, statement.ClosingBalance);
    }

    [Fact]
    public void Without_the_recap_there_is_nothing_to_reconcile_against_and_it_says_so()
    {
        // The reduction in safety worth knowing about: the host's "the entries do not account for
        // the movement" refusal cannot fire on a statement that states no balances. Null rather
        // than false — there is nothing to disagree with.
        var statement = HalcomStatementReader.Read(Line()).Statement;

        Assert.Null(statement.OpeningBalance);
        Assert.Null(statement.ClosingBalance);
        Assert.Null(statement.Reconciles);
        Assert.Null(statement.Number);
    }

    [Fact]
    public void A_recap_line_inside_the_transaction_file_is_used_rather_than_refused()
    {
        // Somebody concatenating the two exports, or pasting both into one upload. Before this it
        // was refused outright as unrecognised — the worst of the three outcomes, because the
        // person did have both files and was told the format was not understood.
        var read = HalcomStatementReader.Read(
            Recap(
                account: "205000000010804045",
                date: "27122011",
                previousDate: "26122011",
                openingBalance: "000000000306440645",
                debitTotal: "000000000003216625",
                creditTotal: "000000000006980880",
                closingBalance: "000000000310204900",
                number: "247")
            + "\r\n"
            + Line(ownAccount: "205000000010804045", posting: "20", amount: "000000006980880"));

        Assert.Equal(HalcomStatementKind.FixedWidth, read.Kind);

        // The recap line is not counted as a transaction.
        Assert.Single(read.Statement.Entries);

        Assert.Equal("247", read.Statement.Number);
        Assert.Equal(3_064_406.45m, read.Statement.OpeningBalance);
        Assert.Equal(3_102_049.00m, read.Statement.ClosingBalance);
    }

    [Fact]
    public void An_explicitly_passed_recap_still_wins_over_one_found_in_the_file()
    {
        // The caller knows which file is which; the sniffing is a fallback, not a preference.
        var read = HalcomStatementReader.Read(
            Line() + "\r\n" + Recap(
                account: "205000000010804045", date: "27122011", previousDate: "26122011",
                openingBalance: "000000000000000000", debitTotal: "000000000000000000",
                creditTotal: "000000000000000000", closingBalance: "000000000000000000",
                number: "111"),
            Recap(
                account: "205000000010804045", date: "27122011", previousDate: "26122011",
                openingBalance: "000000000306440645", debitTotal: "000000000003216625",
                creditTotal: "000000000006980880", closingBalance: "000000000310204900",
                number: "247"));

        Assert.Equal("247", read.Statement.Number);
        Assert.Equal(3_064_406.45m, read.Statement.OpeningBalance);
    }

    [Fact]
    public void A_file_mixing_two_accounts_is_refused_rather_than_merged()
    {
        var read = HalcomStatementReader.Read(
            Line(ownAccount: "205000000010804045") + "\r\n"
            + Line(ownAccount: "160000000026565175"));

        Assert.Empty(read.Statements);
        Assert.Contains("više računa", read.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_DOS_end_of_file_marker_is_tolerated_whether_present_or_not()
    {
        // The spec mandates it; no real file carries it.
        var withMarker = HalcomStatementReader.Read(Line() + "");
        var without = HalcomStatementReader.Read(Line());

        Assert.Equal(HalcomStatementKind.FixedWidth, withMarker.Kind);
        Assert.Single(withMarker.Statement.Entries);
        Assert.Equal(without.Statement.Entries[0].Amount, withMarker.Statement.Entries[0].Amount);
    }

    // ---- the devizni export --------------------------------------------------------------------

    [Fact]
    public void A_devizni_movement_is_read_from_its_paired_balance_record()
    {
        var read = HalcomStatementReader.Read(ForeignSample);

        Assert.Equal(HalcomStatementKind.ForeignStatement, read.Kind);
        Assert.Null(read.Refusal);

        var statement = read.Statement;

        Assert.Equal("265-100000008731506", statement.AccountNumber);
        Assert.Equal("EUR", statement.Currency);
        Assert.Equal(1_839.06m, statement.OpeningBalance);
        Assert.Equal(1_891.57m, statement.ClosingBalance);

        var entry = Assert.Single(statement.Entries);

        Assert.Equal(52.51m, entry.Amount);
        Assert.Equal(new DateOnly(2012, 1, 4), entry.BookingDate);
        Assert.Equal(new DateOnly(2012, 1, 3), entry.ValueDate);
        Assert.Equal("DVA BORA 2000 DOO RIBNIK ZABLEDJE R", entry.CounterpartyName);
        Assert.Equal("22012010446110501472/2#110116020326", entry.BankReference);
    }

    [Fact]
    public void The_direction_is_derived_from_the_balances_rather_than_from_a_flag()
    {
        // The format carries several codes that might be a debit/credit indicator and the only
        // real sample holds one transaction, which is a credit — so there is nothing to learn the
        // debit encoding from. Guessing produces a reader that books every outgoing payment as
        // money in. The KONTO record states the balance before and after, so the direction is
        // arithmetic.
        var credit = Assert.Single(HalcomStatementReader.Read(ForeignSample).Statement.Entries);

        Assert.Equal(StatementDirection.Credit, credit.Direction);

        // The same file with the balance going the other way is the same amount leaving.
        var debited = ForeignSample
            .Replace("<AMM>1839,06</AMM>", "<AMM>1891,57</AMM>", StringComparison.Ordinal)
            .Replace("<PST>1891,57</PST>", "<PST>1839,06</PST>", StringComparison.Ordinal);

        var debit = Assert.Single(HalcomStatementReader.Read(debited).Statement.Entries);

        Assert.Equal(StatementDirection.Debit, debit.Direction);
        Assert.Equal(52.51m, debit.Amount);
    }

    [Fact]
    public void A_movement_with_no_balance_record_refuses_the_whole_file()
    {
        // Not the line — the file. Importing what could be resolved and reporting success is how a
        // payment goes missing, and this format has been verified against one sample.
        var orphaned = ForeignSample.Replace(
            "<K_GI>1YYYYQ62F0JFJ2RJ</K_GI>", "<K_GI>SOMETHINGELSE</K_GI>", StringComparison.Ordinal);

        var read = HalcomStatementReader.Read(orphaned);

        Assert.Empty(read.Statements);
        Assert.Contains("smer knjiženja", read.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_devizni_statement_carries_no_reference_and_no_number()
    {
        // OZK holds free text that happens to contain a SWIFT-style /INV/ reference in the one
        // sample. Pulling an invoice number out of that would hand the matcher a value to match
        // invoices against on the strength of a single example; a null falls through to matching
        // by payer and amount, which is the safe direction to be wrong in.
        var read = HalcomStatementReader.Read(ForeignSample);

        var entry = Assert.Single(read.Statement.Entries);

        Assert.Null(entry.Reference);
        Assert.Contains("/INV/0004/11", entry.Description!, StringComparison.Ordinal);

        // The format states no izvod number.
        Assert.Null(read.Statement.Number);
    }

    [Fact]
    public void A_devizni_file_with_no_movements_says_so()
    {
        var read = HalcomStatementReader.Read("<CLIENTPROFILE><C_AI>265-1</C_AI></CLIENTPROFILE>");

        Assert.Empty(read.Statements);
        Assert.NotNull(read.Refusal);
    }

    /// <summary>
    /// A real Halcom devizni export, trimmed to the three records that matter: the client profile,
    /// the running balance and the movement it belongs to, joined by <c>K_GI</c> / <c>P_GI</c>.
    /// </summary>
    private const string ForeignSample =
        "<CLIENTPROFILE><ID>1000022A8Y2KA806</ID><CLIENTID>200034079</CLIENTID>"
        + "<CNM>MELANY TEAM DOO</CNM><C_AI>265-100000008731506</C_AI><OVPR>RSD</OVPR>"
        + "</CLIENTPROFILE>"
        + "<KONTO><ID>1YYYYYYSF0JFJ2QN</ID><STK>265-100000008731506</STK><MTK>20167653</MTK>"
        + "<DO>04.01.2012</DO><OV>EUR</OV><AMM>1839,06</AMM><VTO>52,51</VTO><PST>1891,57</PST>"
        + "<K_GI>1YYYYQ62F0JFJ2RJ</K_GI><K_AI>265-100000008731506</K_AI></KONTO>"
        + "<PPTPROMET><ID>1YYYYYYSF0H408DQ</ID><SK>265-100000008731506</SK><OV>EUR</OV>"
        + "<VBD>52,51</VBD><DOV>03.01.2012</DOV><DOB>04.01.2012</DOB>"
        + "<INS>22012010446110501472/2#110116020326</INS>"
        + "<OZK>/INV/0004/11  Iznos upucen od stran/INV/0004/11</OZK>"
        + "<NPO>DVA BORA 2000 DOO RIBNIK ZABLEDJE R</NPO>"
        + "<P_GI>1YYYYQ62F0JFJ2RJ</P_GI><P_AI>265-100000008731506</P_AI></PPTPROMET>";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a short line")]
    [InlineData("<?xml version=\"1.0\"?><stmtrs />")]
    public void Anything_that_is_not_a_Halcom_export_is_answered_rather_than_thrown_at(string? content)
    {
        Assert.Equal(HalcomStatementKind.Unknown, HalcomStatementReader.Read(content).Kind);
    }

    // ---- the extended variant ------------------------------------------------------------------

    [Fact]
    public void The_extended_variant_reads_amounts_in_Serbian_display_form()
    {
        // "3.000.000,00" is three million. A parser treating those dots as decimal points makes it
        // three, and the row still looks like a number.
        var read = HalcomStatementReader.Read(
            "ST_RACUNA#ST_IZPISKA#DATUM_KNJ#ZNESEK_V_BREME#ZNESEK_V_DOBRO#NAZIV_NAL_PREJ#NAMEN#ID_NALOGA#SKLIC_ODOBR\r\n"
            + "205000000010804045#12#16.04.2026#0,00#3.000.000,00#KUPAC DOO#Uplata#ID-1#2026-114");

        Assert.Equal(HalcomStatementKind.Extended, read.Kind);

        var entry = Assert.Single(read.Statement.Entries);

        Assert.Equal(3_000_000m, entry.Amount);
        Assert.Equal(StatementDirection.Credit, entry.Direction);
        Assert.Equal("2026-114", entry.Reference);
        Assert.Equal("12", read.Statement.Number);
    }

    [Fact]
    public void The_extended_variant_splits_one_file_into_a_statement_per_account_and_number()
    {
        var read = HalcomStatementReader.Read(
            "ST_RACUNA#ST_IZPISKA#DATUM_KNJ#ZNESEK_V_BREME#ZNESEK_V_DOBRO\r\n"
            + "205000000010804045#12#16.04.2026#0,00#1.000,00\r\n"
            + "205000000010804045#13#17.04.2026#0,00#2.000,00");

        Assert.Equal(2, read.Statements.Count);
        Assert.Equal(["12", "13"], read.Statements.Select(statement => statement.Number));
    }

    [Fact]
    public void A_missing_required_column_is_named_rather_than_read_as_zeros()
    {
        // Without these every row is a silent zero on a nameless account, and the import reports
        // success over nothing.
        var read = HalcomStatementReader.Read(
            "ST_RACUNA#DATUM_KNJ#NAZIV_NAL_PREJ\r\n205000000010804045#16.04.2026#KUPAC DOO");

        Assert.Empty(read.Statements);
        Assert.Contains("ZNESEK_V_BREME", read.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_repeated_column_name_is_refused()
    {
        var read = HalcomStatementReader.Read(
            "ST_RACUNA#ZNESEK_V_BREME#ZNESEK_V_DOBRO#DATUM_KNJ#DATUM_KNJ\r\n"
            + "205000000010804045#0,00#1.000,00#16.04.2026#16.04.2026");

        Assert.Empty(read.Statements);
        Assert.Contains("DATUM_KNJ", read.Refusal!, StringComparison.Ordinal);
    }

    // ---- fixtures ------------------------------------------------------------------------------

    /// <summary>
    /// One 280-character transaction line, built by placing each field at the offset a real export
    /// puts it. Written as a builder rather than a literal so the offsets are legible — and so a
    /// change to one of them fails here rather than somewhere downstream.
    /// </summary>
    private static string Line(
        string partnerAccount = "160000000026565175",
        string posting = "20",
        string bookingDate = "27.12.11",
        string storno = "",
        string valueDate = "271211",
        string ownAccount = "205000000010804045",
        string amount = "000000006980880",
        string paymentCode = " 221  ",
        string debitModel = "00",
        string debitReference = "",
        string creditModel = "00",
        string creditReference = "P1194-11",
        string purpose = "PROMET ROBE I USLUGA",
        string city = "SIMANOVCI",
        string partnerName = "KLEEMANN LIFTOVI DOO",
        string bankReference = "087000793707972",
        string alternateAccount = "")
    {
        var line = new char[280];

        Array.Fill(line, ' ');

        Put(0, partnerAccount);
        Put(18, posting);
        Put(20, bookingDate);
        Put(28, storno);
        Put(66, valueDate);
        Put(72, ownAccount);
        Put(90, amount.PadLeft(15, '0'));
        Put(105, paymentCode);
        Put(111, debitModel);
        Put(113, debitReference);
        Put(135, creditModel);
        Put(137, creditReference);
        Put(159, purpose);
        Put(195, city);
        Put(205, partnerName);
        Put(240, bankReference);
        Put(262, alternateAccount);

        return new string(line);

        void Put(int start, string value)
        {
            for (var i = 0; i < value.Length && start + i < line.Length; i++)
                line[start + i] = value[i];
        }
    }

    /// <summary>One 147-character recap line — the companion <c>*_cov.txt</c>.</summary>
    private static string Recap(
        string account,
        string date,
        string previousDate,
        string openingBalance,
        string debitTotal,
        string creditTotal,
        string closingBalance,
        string number)
    {
        var line = new char[147];

        Array.Fill(line, '0');

        Put(0, "01");
        Put(2, account);
        Put(20, date);
        Put(28, previousDate);
        Put(36, openingBalance);
        Put(54, "000005");
        Put(60, debitTotal);
        Put(78, "000001");
        Put(84, creditTotal);
        Put(102, closingBalance);
        Put(144, number.PadLeft(3, '0'));

        return new string(line);

        void Put(int start, string value)
        {
            for (var i = 0; i < value.Length && start + i < line.Length; i++)
                line[start + i] = value[i];
        }
    }
}
