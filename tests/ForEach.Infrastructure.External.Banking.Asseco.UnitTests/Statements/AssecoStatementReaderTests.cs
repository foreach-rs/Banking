using ForEach.Infrastructure.External.Banking.Asseco.Statements;
using ForEach.Infrastructure.External.Banking.Statements;

namespace ForEach.Infrastructure.External.Banking.Asseco.UnitTests.Statements;

/// <summary>
/// Reading an Asseco statement.
///
/// Two of these tests are the whole reason the file is written the way it is.
///
/// **`ledgerbal` is the opening balance**, not the closing one — the specification says it is the
/// balance the *previous* statement closed with. Taken the obvious way round, every correct file
/// fails the reconciliation check and the import refuses days that are perfectly good.
///
/// **The reference that names an invoice swaps with the direction**, because Asseco names its two
/// references from the account holder's point of view rather than the payment's. Read the wrong one
/// and inbound payments match against the payer's own bookkeeping number.
/// </summary>
public class AssecoStatementReaderTests
{
    [Fact]
    public void The_ledger_balance_is_the_opening_one_and_availbal_is_the_closing_one()
    {
        var read = AssecoStatementReader.Read(Sample);

        Assert.Equal(AssecoStatementKind.Statement, read.Kind);

        var statement = read.Statement;

        Assert.Equal("111-0000000000011-11", statement.AccountNumber);
        Assert.Equal("247", statement.Number);
        Assert.Equal(100_000m, statement.OpeningBalance);
        Assert.Equal(215_500m, statement.ClosingBalance);
        Assert.Equal(new DateOnly(2026, 4, 16), statement.StatementDate);
    }

    [Fact]
    public void The_entries_account_for_the_movement_between_the_balances()
    {
        Assert.True(AssecoStatementReader.Read(Sample).Statement.Reconciles);
    }

    [Fact]
    public void The_legacy_dinar_code_becomes_RSD()
    {
        // curdef is "DIN" in this family. Left alone it matches no bank account on file, and the
        // import reports an unknown currency for the only currency this company has.
        Assert.Equal("RSD", AssecoStatementReader.Read(Sample).Statement.Currency);
    }

    [Fact]
    public void On_money_in_the_reference_is_ours_because_we_are_the_beneficiary()
    {
        var entry = AssecoStatementReader.Read(Sample).Statement.Entries[0];

        Assert.Equal(StatementDirection.Credit, entry.Direction);
        Assert.Equal(120_000m, entry.Amount);

        // refnumber — "poziv na broj vlasnika računa za koji se daje izvod". The customer quoted
        // our invoice, and this is where it landed.
        Assert.Equal("2026-114", entry.Reference);

        Assert.Equal("Kafić Zvezda d.o.o.", entry.CounterpartyName);
        Assert.Equal("160-0000012345678-90", entry.CounterpartyAccount);

        // fitid — unique per bank, which is what makes a repeated import detectable.
        Assert.Equal("87000000052481", entry.BankReference);
    }

    [Fact]
    public void On_money_out_the_reference_is_the_beneficiarys_because_they_are_being_paid()
    {
        var entry = AssecoStatementReader.Read(Sample).Statement.Entries[1];

        Assert.Equal(StatementDirection.Debit, entry.Direction);
        Assert.Equal(4_500m, entry.Amount);

        // payeerefnumber — the supplier's own invoice reference, which is what we quoted. Reading
        // refnumber here would return OUR internal number for the payment, which matches nothing
        // on their statement and nothing on their invoice.
        Assert.Equal("FT-9912", entry.Reference);
    }

    [Fact]
    public void An_amount_stays_positive_and_the_direction_carries_the_sign()
    {
        // The file states amounts absolutely — "u apsolutnom iznosu" — with benefit giving the
        // direction. A host reading the amount alone would book the outgoing payment as a receipt.
        Assert.All(
            AssecoStatementReader.Read(Sample).Statement.Entries,
            entry => Assert.True(entry.Amount > 0));
    }

    [Fact]
    public void A_notification_is_read_like_a_statement()
    {
        var read = AssecoStatementReader.Read(
            Sample
                .Replace("<stmtrs>", "<pmtnotification>", StringComparison.Ordinal)
                .Replace("</stmtrs>", "</pmtnotification>", StringComparison.Ordinal)
                .Replace(
                    "<rstype>ibank.payment.stmtrs.ledger</rstype>",
                    "<notiftype>ibank.payment.notification.ledger</notiftype>",
                    StringComparison.Ordinal));

        Assert.Equal(AssecoStatementKind.Notification, read.Kind);
        Assert.Equal(2, read.Statement.Entries.Count);
    }

    [Fact]
    public void A_list_wrapper_yields_every_statement_in_it()
    {
        var read = AssecoStatementReader.Read($"<stmtrslist>{Body}{Body}</stmtrslist>");

        Assert.Equal(AssecoStatementKind.Statement, read.Kind);
        Assert.Equal(2, read.Statements.Count);
        Assert.All(read.Statements, statement => Assert.Equal(2, statement.Entries.Count));
    }

    [Fact]
    public void A_devizni_statement_is_read_from_its_own_element_names()
    {
        // It shares the <stmtrs> root and agrees about almost nothing else: stmbal/closing for the
        // balances, stmtno for the number, dateasto for the date. Read with the domestic names it
        // *succeeds* and yields a statement with no balances and nameless lines — which looks like
        // a quiet day rather than an unread file. That is why the two are told apart by rstype.
        var read = AssecoStatementReader.Read(ForeignSample);

        Assert.Equal(AssecoStatementKind.ForeignStatement, read.Kind);
        Assert.Null(read.Refusal);

        var statement = read.Statement;

        Assert.Equal("00-500-1234567.8", statement.AccountNumber);
        Assert.Equal("EUR", statement.Currency);
        Assert.Equal("12", statement.Number);
        Assert.Equal(new DateOnly(2026, 3, 2), statement.StatementDate);
        Assert.Equal(1_000m, statement.OpeningBalance);
        Assert.Equal(2_500m, statement.ClosingBalance);
        Assert.True(statement.Reconciles);
    }

    [Fact]
    public void A_devizni_line_takes_its_counterparty_from_the_document_it_came_from()
    {
        // The devizni transaction carries no payeeinfo at all — the counterparty is on the
        // document beside it, joined by documentid. The account element is spelled "accountifo"
        // in the specification, and the files follow the typo because they are generated from it.
        var entry = Assert.Single(AssecoStatementReader.Read(ForeignSample).Statement.Entries);

        Assert.Equal(1_500m, entry.Amount);
        Assert.Equal(StatementDirection.Credit, entry.Direction);
        Assert.Equal("ACME GMBH", entry.CounterpartyName);
        Assert.Equal("DE89370400440532013000", entry.CounterpartyAccount);
        Assert.Equal("FX-88231", entry.BankReference);
        Assert.Equal("INV-2026-114", entry.Reference);
    }

    [Fact]
    public void A_devizni_statement_counts_each_payment_once()
    {
        // Both <trnlist> and <documentlist> sit under the statement, and a reader taking every
        // transaction-shaped descendant would find the payment twice. A statement carrying each
        // line twice balances against nothing.
        Assert.Single(AssecoStatementReader.Read(ForeignSample).Statement.Entries);
    }

    [Fact]
    public void A_devizni_line_keeps_the_dinar_counter_value_as_text_not_as_the_amount()
    {
        // The amount is what moved, and it moved in euros. The dinar figure is context.
        var entry = Assert.Single(AssecoStatementReader.Read(ForeignSample).Statement.Entries);

        Assert.Equal(1_500m, entry.Amount);
        Assert.Contains("175800.00", entry.Description!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bank_error_document_is_reported_rather_than_read_as_an_empty_day()
    {
        var read = AssecoStatementReader.Read(
            Sample.Replace("<code>0</code>", "<code>17</code>", StringComparison.Ordinal));

        Assert.Equal(AssecoStatementKind.BankError, read.Kind);
        Assert.Contains("17", read.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_intraday_message_has_no_statement_number_and_that_is_not_an_error()
    {
        // The specification says stmtnumber is "uvek prazno" on the daily message. Null rather
        // than "", so a host checking for a number does not have to check for both.
        var read = AssecoStatementReader.Read(
            Sample.Replace("<stmtnumber>247</stmtnumber>", "<stmtnumber/>", StringComparison.Ordinal));

        Assert.Equal(AssecoStatementKind.Statement, read.Kind);
        Assert.Null(read.Statement.Number);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<html><body>renamed</body></html>")]
    [InlineData("<stmtrslist></stmtrslist>")]
    public void Anything_that_is_not_a_statement_is_answered_rather_than_thrown_at(string? content)
    {
        Assert.Equal(AssecoStatementKind.Unknown, AssecoStatementReader.Read(content).Kind);
    }

    [Fact]
    public void A_document_type_declaration_is_refused()
    {
        var read = AssecoStatementReader.Read(
            """
            <!DOCTYPE stmtrs [<!ENTITY x SYSTEM "file:///etc/passwd">]>
            <stmtrs><acctid>111-0000000000011-11</acctid></stmtrs>
            """);

        Assert.Equal(AssecoStatementKind.Unknown, read.Kind);
    }

    private const string Body =
        """
        <stmtrs>
          <rstype>ibank.payment.stmtrs.ledger</rstype>
          <status><code>0</code><severity>INFO</severity><detail/></status>
          <curdef>DIN</curdef>
          <acctid>111-0000000000011-11</acctid>
          <stmtnumber>247</stmtnumber>
          <ledgerbal><balamt>100000.00</balamt><dtasof>2026-04-15T00:00:00</dtasof></ledgerbal>
          <availbal><balamt>215500.00</balamt><dtasof>2026-04-16T00:00:00</dtasof></availbal>
          <reservedfunds>0.00</reservedfunds>
          <trnlist count="2">
            <stmttrn>
              <trntype>ibank.payment.pp3</trntype>
              <trnuid/>
              <fitid>87000000052481</fitid>
              <benefit>credit</benefit>
              <payeeinfo><name>Kafić Zvezda d.o.o.</name><city>Beograd</city></payeeinfo>
              <payeeaccountinfo>
                <acctid>160-0000012345678-90</acctid><bankid>160</bankid><bankname>Banka</bankname>
              </payeeaccountinfo>
              <trnamt>120000.00</trnamt>
              <curdef>DIN</curdef>
              <purpose>Uplata po fakturi</purpose>
              <purposecode>221</purposecode>
              <trnplace>online</trnplace>
              <dtposted>2026-04-16T00:00:00</dtposted>
              <dtavail>2026-04-16T00:00:00</dtavail>
              <refmodel>97</refmodel>
              <refnumber>2026-114</refnumber>
              <payeerefmodel/>
              <payeerefnumber/>
              <urgency>ACH</urgency>
            </stmttrn>
            <stmttrn>
              <trntype>ibank.payment.pp3</trntype>
              <trnuid/>
              <fitid>87000000052482</fitid>
              <benefit>debit</benefit>
              <payeeinfo><name>Papir Promet d.o.o.</name><city>Novi Sad</city></payeeinfo>
              <payeeaccountinfo>
                <acctid>355-0000001005016-34</acctid><bankid>355</bankid><bankname>Banka</bankname>
              </payeeaccountinfo>
              <trnamt>4500.00</trnamt>
              <curdef>DIN</curdef>
              <purpose>Plaćanje po fakturi</purpose>
              <purposecode>221</purposecode>
              <trnplace>online</trnplace>
              <dtposted>2026-04-16T00:00:00</dtposted>
              <dtavail>2026-04-16T00:00:00</dtavail>
              <refmodel>00</refmodel>
              <refnumber>NAL-5501</refnumber>
              <payeerefmodel>97</payeerefmodel>
              <payeerefnumber>FT-9912</payeerefnumber>
              <urgency>ACH</urgency>
            </stmttrn>
          </trnlist>
        </stmtrs>
        """;

    /// <summary>
    /// A devizni (FX Client) statement, in the element names that specification uses.
    /// <c>accountifo</c> is spelled as the document spells it.
    /// </summary>
    private const string ForeignSample =
        """
        <stmtrs>
          <rstype>ibank.fps.stmtrs.statement</rstype>
          <status><code>0</code><severity>INFO</severity></status>
          <curdef>EUR</curdef>
          <acctid>00-500-1234567.8</acctid>
          <stmtno>12</stmtno>
          <stmtyear>2026</stmtyear>
          <dateasto>2026-03-02T00:00:00</dateasto>
          <stmbal>
            <balamt>1000.00</balamt><balamtlcl>117200.00</balamtlcl>
            <dtasof>2026-03-01T00:00:00</dtasof>
          </stmbal>
          <closing><balamt>2500.00</balamt><balamtlcl>293000.00</balamtlcl></closing>
          <trnlist>
            <stmttrn>
              <trntype>ibank.fps.document.remittance</trntype>
              <trndsc>Advance payment</trndsc>
              <fitid>FX-88231</fitid>
              <documentid>DOC-551</documentid>
              <trnamt>1500.00</trnamt>
              <trnamtlocal>175800.00</trnamtlocal>
              <benefit>credit</benefit>
              <dtposted>2026-03-02T00:00:00</dtposted>
              <dtavail>2026-03-02T00:00:00</dtavail>
              <reference>INV-2026-114</reference>
            </stmttrn>
          </trnlist>
          <documentlist>
            <document>
              <doctype>ibank.fps.document.remittance</doctype>
              <documentid>DOC-551</documentid>
              <beneficiary>
                <legalid>HRB12345</legalid>
                <name>ACME GMBH</name>
                <address><street>Hauptstrasse 1</street><city>Berlin</city><zip>10115</zip></address>
                <accountifo>
                  <acctid>DE89370400440532013000</acctid>
                  <bankinfo><bic>COBADEFF</bic><bankname>Commerzbank</bankname></bankinfo>
                </accountifo>
              </beneficiary>
            </document>
          </documentlist>
        </stmtrs>
        """;

    private const string Sample = Body;
}
