using ForEach.Infrastructure.External.Banking.Statements;

namespace ForEach.Infrastructure.External.Banking.UnitTests.Statements;

/// <summary>
/// Reading a bank statement.
///
/// The failures worth writing down here are the quiet ones. A reader pinned to one camt version
/// answers "empty statement" for a valid file, which looks exactly like a quiet day at the bank. An
/// amount parsed under a Serbian culture reads 1234.56 as 123456 — a hundredfold error on money,
/// on whichever server happens to run the import. A direction indicator ignored books a bank charge
/// as a customer payment, in the direction that flatters the balance.
///
/// None of those throw, and none of them are visible on the screen that reports success.
/// </summary>
public class Camt053ReaderTests
{
    [Fact]
    public void A_statement_carries_its_account_number_and_balances()
    {
        var statement = Camt053Reader.Read(Sample());

        Assert.Equal("325-9500700194223-11", statement.AccountNumber);
        Assert.Equal("RSD", statement.Currency);
        Assert.Equal("143", statement.Number);
        Assert.Equal(new DateOnly(2026, 8, 24), statement.StatementDate);
        Assert.Equal(250_000m, statement.OpeningBalance);
        Assert.Equal(361_500m, statement.ClosingBalance);
    }

    [Fact]
    public void A_customer_payment_is_read_with_its_reference_and_payer()
    {
        var entry = Camt053Reader.Read(Sample()).Entries[0];

        Assert.Equal(120_000m, entry.Amount);
        Assert.Equal(StatementDirection.Credit, entry.Direction);
        Assert.Equal(new DateOnly(2026, 8, 24), entry.BookingDate);

        // The poziv na broj, which is what matches the payment to an invoice.
        Assert.Equal("2026-114", entry.Reference);
        Assert.Equal("Kafić Zvezda d.o.o.", entry.CounterpartyName);
        Assert.Equal("160-0000012345678-90", entry.CounterpartyAccount);
    }

    [Fact]
    public void A_charge_is_a_debit_and_its_amount_stays_positive()
    {
        // The sign lives in the direction. A host reading the amount alone would book this bank
        // charge as somebody paying us — silently, and in the direction that flatters the balance.
        var entry = Camt053Reader.Read(Sample()).Entries[2];

        Assert.Equal(500m, entry.Amount);
        Assert.Equal(StatementDirection.Debit, entry.Direction);
    }

    [Fact]
    public void Amounts_are_read_invariantly_whatever_the_machine_thinks_a_decimal_point_is()
    {
        // The one that would be a hundredfold error on money. A Serbian culture reads "1234.56" as
        // 123456, and nothing about the result looks wrong.
        var previous = Thread.CurrentThread.CurrentCulture;

        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("sr-Latn-RS");

            Assert.Equal(120_000m, Camt053Reader.Read(Sample()).Entries[0].Amount);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void The_same_file_reads_under_a_different_camt_version()
    {
        // .001.02 and .001.08 differ in the namespace and in fields this does not read. A reader
        // pinned to one answers "empty statement" for a perfectly valid file — and an empty
        // statement is indistinguishable from a quiet day at the bank.
        var eight = Sample().Replace(
            "camt.053.001.02", "camt.053.001.08", StringComparison.Ordinal);

        var statement = Camt053Reader.Read(eight);

        Assert.Equal(3, statement.Entries.Count);
        Assert.Equal("325-9500700194223-11", statement.AccountNumber);
    }

    [Fact]
    public void A_free_text_reference_is_used_only_when_there_is_no_structured_one()
    {
        // The structured field is the box the payer was asked to fill in; the free text is whatever
        // they typed. Preferring the second would match on a sentence.
        var entry = Camt053Reader.Read(Sample()).Entries[1];

        Assert.Equal("Uplata po predracunu PR-0007/2026", entry.Reference);
    }

    [Fact]
    public void The_entries_are_checked_against_the_balances()
    {
        // 250.000 + 120.000 + 12.000 − 500 = 381.500, and the file says 361.500. A shape of entry
        // this reader skipped would look exactly like this, and on a bank statement that is worth
        // saying out loud: a payment silently missing from an import is a customer chased for
        // money they have already paid.
        Assert.False(Camt053Reader.Read(Sample()).Reconciles);

        Assert.True(Camt053Reader.Read(Sample(closingBalance: "381500.00")).Reconciles);
    }

    [Fact]
    public void A_statement_with_no_balances_says_nothing_rather_than_reporting_a_fault()
    {
        // There is nothing to reconcile against. Answering false would report a problem nobody can
        // find.
        var withoutBalances = Camt053Reader.Read(Sample(includeBalances: false));

        Assert.Null(withoutBalances.Reconciles);
        Assert.Equal(3, withoutBalances.Entries.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("this is not xml at all")]
    [InlineData("<Document><Nonsense/></Document>")]
    public void Anything_that_is_not_a_statement_reads_as_an_empty_one(string? content)
    {
        // A renamed PDF, a truncated download, an export from something that is not a bank. The
        // caller's honest answer is "this is not a statement", not a stack trace on an import
        // screen.
        var statement = Camt053Reader.Read(content);

        Assert.Empty(statement.Entries);
        Assert.Null(statement.AccountNumber);
    }

    [Fact]
    public void NOTPROVIDED_is_not_a_reference()
    {
        // What the standard says to write when there is none. A host matching on it would match
        // every unreferenced payment in the file to the same nothing.
        var xml = Sample().Replace(
            "<Ustrd>Uplata po predracunu PR-0007/2026</Ustrd>", "", StringComparison.Ordinal);

        Assert.Null(Camt053Reader.Read(xml).Entries[1].Reference);
    }

    [Fact]
    public void A_file_holding_several_statements_gives_all_of_them()
    {
        var two = Sample().Replace("</BkToCstmrStmt>", StatementFor("144") + "</BkToCstmrStmt>",
            StringComparison.Ordinal);

        var statements = Camt053Reader.ReadAll(two);

        Assert.Equal(2, statements.Count);
        Assert.Equal("144", statements[1].Number);
    }

    [Fact]
    public void On_a_debit_the_counterparty_is_the_creditor_even_when_the_bank_names_us_as_debtor()
    {
        // Some banks name both parties on every entry. On a payment out we are the Dbtr, and a
        // reader taking "the debtor, else the creditor" hands back our own account as the
        // counterparty of every supplier payment — which matches no contact and looks like a
        // transfer to ourselves.
        var entry = Assert.Single(Camt053Reader.Read(DebitNamingBothParties).Entries);

        Assert.Equal(StatementDirection.Debit, entry.Direction);
        Assert.Equal("DOBAVLJAČ DOO", entry.CounterpartyName);
        Assert.Equal("RS35205000000000000099", entry.CounterpartyAccount);
    }

    [Fact]
    public void An_amount_that_is_not_a_number_refuses_the_whole_file()
    {
        // The alternative is a line booked at zero, in the credit direction, on a statement that
        // still looks complete — and TryReadAll is how a host tells that from a quiet day.
        var broken = Sample().Replace(
            "<Amt Ccy=\"RSD\">120000.00</Amt>", "<Amt Ccy=\"RSD\">abc</Amt>", StringComparison.Ordinal);

        Assert.Empty(Camt053Reader.ReadAll(broken));
        Assert.Empty(Camt053Reader.Read(broken).Entries);

        Assert.False(Camt053Reader.TryReadAll(broken, out var statements));
        Assert.Empty(statements);
    }

    [Fact]
    public void TryReadAll_tells_an_unreadable_file_from_a_quiet_day()
    {
        Assert.False(Camt053Reader.TryReadAll(Sample()[..(Sample().Length / 2)], out _));
        Assert.False(Camt053Reader.TryReadAll("not xml", out _));

        Assert.True(Camt053Reader.TryReadAll(Sample(), out var statements));
        Assert.Single(statements);
    }

    [Fact]
    public void A_document_type_declaration_is_refused()
    {
        // No bank writes one. An entity declaration in an uploaded file is a broken export at
        // best and an attempt to read the server's filesystem at worst, so it is refused outright
        // like every other XML reader here does.
        var withDoctype = Sample().Replace(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>",
            "<?xml version=\"1.0\"?><!DOCTYPE Document [<!ENTITY x SYSTEM \"file:///etc/passwd\">]>",
            StringComparison.Ordinal);

        Assert.Empty(Camt053Reader.ReadAll(withDoctype));
        Assert.False(Camt053Reader.TryReadAll(withDoctype, out _));
    }

    // ---- the file --------------------------------------------------------------------------------

    /// <summary>
    /// A payment out, on which the bank names both sides: us as the debtor and the supplier as
    /// the creditor.
    /// </summary>
    private const string DebitNamingBothParties = """
        <Document xmlns="urn:iso:std:iso:20022:tech:xsd:camt.053.001.02">
          <BkToCstmrStmt><Stmt>
            <Acct><Id><IBAN>RS35160000000000000001</IBAN></Id><Ccy>RSD</Ccy></Acct>
            <Ntry>
              <Amt Ccy="RSD">100.00</Amt>
              <CdtDbtInd>DBIT</CdtDbtInd>
              <BookgDt><Dt>2026-11-03</Dt></BookgDt>
              <NtryDtls><TxDtls><RltdPties>
                <Dbtr><Nm>NAŠA FIRMA DOO</Nm></Dbtr>
                <DbtrAcct><Id><IBAN>RS35160000000000000001</IBAN></Id></DbtrAcct>
                <Cdtr><Nm>DOBAVLJAČ DOO</Nm></Cdtr>
                <CdtrAcct><Id><IBAN>RS35205000000000000099</IBAN></Id></CdtrAcct>
              </RltdPties></TxDtls></NtryDtls>
            </Ntry>
          </Stmt></BkToCstmrStmt>
        </Document>
        """;

    /// <summary>
    /// A statement in the shape a Serbian bank sends: a domestic account number under Othr rather
    /// than an IBAN, a customer payment with a poziv na broj, one with only free text, and a bank
    /// charge.
    /// </summary>
    private static string Sample(string closingBalance = "361500.00", bool includeBalances = true)
    {
        var balances = includeBalances
            ? """
                  <Bal>
                    <Tp><CdOrPrtry><Cd>OPBD</Cd></CdOrPrtry></Tp>
                    <Amt Ccy="RSD">250000.00</Amt>
                    <CdtDbtInd>CRDT</CdtDbtInd>
                    <Dt><Dt>2026-08-24</Dt></Dt>
                  </Bal>
                  <Bal>
                    <Tp><CdOrPrtry><Cd>CLBD</Cd></CdOrPrtry></Tp>
                    <Amt Ccy="RSD">{CLOSING}</Amt>
                    <CdtDbtInd>CRDT</CdtDbtInd>
                    <Dt><Dt>2026-08-24</Dt></Dt>
                  </Bal>
              """.Replace("{CLOSING}", closingBalance, StringComparison.Ordinal)
            : string.Empty;

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <Document xmlns="urn:iso:std:iso:20022:tech:xsd:camt.053.001.02">
              <BkToCstmrStmt>
                <GrpHdr>
                  <MsgId>OTP-2026-143</MsgId>
                  <CreDtTm>2026-08-24T23:59:00</CreDtTm>
                </GrpHdr>
                <Stmt>
                  <Id>325-9500700194223-11-143</Id>
                  <LglSeqNb>143</LglSeqNb>
                  <CreDtTm>2026-08-24T23:59:00</CreDtTm>
                  <Acct>
                    <Id><Othr><Id>325-9500700194223-11</Id></Othr></Id>
                    <Ccy>RSD</Ccy>
                  </Acct>
            {balances}
                  <Ntry>
                    <Amt Ccy="RSD">120000.00</Amt>
                    <CdtDbtInd>CRDT</CdtDbtInd>
                    <Sts>BOOK</Sts>
                    <BookgDt><Dt>2026-08-24</Dt></BookgDt>
                    <ValDt><Dt>2026-08-24</Dt></ValDt>
                    <AcctSvcrRef>OTP0001</AcctSvcrRef>
                    <NtryDtls><TxDtls>
                      <Refs><EndToEndId>NOTPROVIDED</EndToEndId></Refs>
                      <RltdPties>
                        <Dbtr><Nm>Kafić Zvezda d.o.o.</Nm></Dbtr>
                        <DbtrAcct><Id><Othr><Id>160-0000012345678-90</Id></Othr></Id></DbtrAcct>
                      </RltdPties>
                      <RmtInf>
                        <Strd><CdtrRefInf><Ref>2026-114</Ref></CdtrRefInf></Strd>
                        <Ustrd>Placanje po fakturi</Ustrd>
                      </RmtInf>
                    </TxDtls></NtryDtls>
                  </Ntry>
                  <Ntry>
                    <Amt Ccy="RSD">12000.00</Amt>
                    <CdtDbtInd>CRDT</CdtDbtInd>
                    <Sts>BOOK</Sts>
                    <BookgDt><Dt>2026-08-24</Dt></BookgDt>
                    <AcctSvcrRef>OTP0002</AcctSvcrRef>
                    <NtryDtls><TxDtls>
                      <Refs><EndToEndId>NOTPROVIDED</EndToEndId></Refs>
                      <RltdPties><Dbtr><Nm>Pekara Sunce</Nm></Dbtr></RltdPties>
                      <RmtInf><Ustrd>Uplata po predracunu PR-0007/2026</Ustrd></RmtInf>
                    </TxDtls></NtryDtls>
                  </Ntry>
                  <Ntry>
                    <Amt Ccy="RSD">500.00</Amt>
                    <CdtDbtInd>DBIT</CdtDbtInd>
                    <Sts>BOOK</Sts>
                    <BookgDt><Dt>2026-08-24</Dt></BookgDt>
                    <AcctSvcrRef>OTP0003</AcctSvcrRef>
                    <AddtlNtryInf>Naknada za vođenje računa</AddtlNtryInf>
                  </Ntry>
                </Stmt>
              </BkToCstmrStmt>
            </Document>
            """;
    }

    /// <summary>A second, minimal statement, for the many-in-one-file case.</summary>
    private static string StatementFor(string number) => $"""
        <Stmt>
          <LglSeqNb>{number}</LglSeqNb>
          <Acct><Id><Othr><Id>325-9500700194223-11</Id></Othr></Id><Ccy>RSD</Ccy></Acct>
        </Stmt>
        """;
}
