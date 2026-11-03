using ForEach.Infrastructure.External.Banking.Otp.Statements;

namespace ForEach.Infrastructure.External.Banking.Otp.UnitTests.Statements;

/// <summary>
/// Recognising what came out of OTP's Business E-bank.
///
/// The distinction being held here is between "we could not read this file" and "this file had no
/// transactions". Both arrive as an empty statement, and they are different sentences to somebody
/// who has just downloaded it — one means try a different export, the other means it was a quiet
/// day at the bank.
/// </summary>
public class OtpStatementReaderTests
{
    [Fact]
    public void A_camt_export_is_recognised_and_read()
    {
        var read = OtpStatementReader.Read(Camt());

        Assert.Equal(OtpStatementFormat.Camt053, read.Format);
        Assert.Equal("325-9500700194223-11", read.Statement.AccountNumber);
        Assert.Single(read.Statement.Entries);
    }

    [Fact]
    public void A_camt_day_with_no_movement_is_still_a_camt_file()
    {
        // A real thing — a quiet day — and it must not report as unreadable, or somebody goes
        // looking for a different export that does not exist.
        var read = OtpStatementReader.Read(Camt(withEntry: false));

        Assert.Equal(OtpStatementFormat.Camt053, read.Format);
        Assert.Empty(read.Statement.Entries);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("%PDF-1.7 something binary")]
    [InlineData("<Document xmlns=\"urn:something:else\"><Nonsense/></Document>")]
    public void Anything_else_is_reported_as_unrecognised_rather_than_thrown(string? content)
    {
        var read = OtpStatementReader.Read(content);

        Assert.Equal(OtpStatementFormat.Unknown, read.Format);
        Assert.Empty(read.Statement.Entries);
    }

    [Fact]
    public void A_statement_whose_entries_do_not_reach_its_closing_balance_says_so()
    {
        // The check that matters on the first real file. If the entries do not account for the
        // movement, something in the export was not understood — and a payment silently missing
        // from an import is a customer chased for money they have already paid.
        var read = OtpStatementReader.Read(Camt(closing: "999999.00"));

        Assert.Equal(OtpStatementFormat.Camt053, read.Format);
        Assert.False(read.Statement.Reconciles);
    }

    [Fact]
    public void A_truncated_camt_download_is_unreadable_and_not_a_quiet_day()
    {
        // Half a file carries the namespace and parses as nothing. Reported as Camt053 with no
        // entries it reads as "no transactions today", and somebody believes it.
        var half = Camt()[..(Camt().Length / 2)];

        var read = OtpStatementReader.Read(half);

        Assert.Equal(OtpStatementFormat.Unknown, read.Format);
        Assert.Empty(read.Statement.Entries);
        Assert.Empty(read.Statements);
    }

    [Fact]
    public void A_file_holding_several_statements_exposes_all_of_them()
    {
        var two = Camt().Replace(
            "</BkToCstmrStmt>", Second + "</BkToCstmrStmt>", StringComparison.Ordinal);

        var read = OtpStatementReader.Read(two);

        Assert.Equal(OtpStatementFormat.Camt053, read.Format);
        Assert.Equal(2, read.Statements.Count);
        Assert.Equal(["143", "144"], read.Statements.Select(statement => statement.Number));

        // The single-statement property is the first of the list, not a different read.
        Assert.Same(read.Statements[0], read.Statement);
    }

    private const string Second = """
        <Stmt>
          <LglSeqNb>144</LglSeqNb>
          <Acct><Id><Othr><Id>325-9500700194223-11</Id></Othr></Id><Ccy>RSD</Ccy></Acct>
        </Stmt>
        """;

    private static string Camt(bool withEntry = true, string closing = "370000.00") =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <Document xmlns="urn:iso:std:iso:20022:tech:xsd:camt.053.001.02">
          <BkToCstmrStmt>
            <Stmt>
              <LglSeqNb>143</LglSeqNb>
              <Acct><Id><Othr><Id>325-9500700194223-11</Id></Othr></Id><Ccy>RSD</Ccy></Acct>
              <Bal>
                <Tp><CdOrPrtry><Cd>OPBD</Cd></CdOrPrtry></Tp>
                <Amt Ccy="RSD">250000.00</Amt><CdtDbtInd>CRDT</CdtDbtInd>
              </Bal>
              <Bal>
                <Tp><CdOrPrtry><Cd>CLBD</Cd></CdOrPrtry></Tp>
                <Amt Ccy="RSD">{closing}</Amt><CdtDbtInd>CRDT</CdtDbtInd>
              </Bal>
        {(withEntry ? Entry : string.Empty)}
            </Stmt>
          </BkToCstmrStmt>
        </Document>
        """;

    private const string Entry = """
              <Ntry>
                <Amt Ccy="RSD">120000.00</Amt>
                <CdtDbtInd>CRDT</CdtDbtInd>
                <BookgDt><Dt>2026-08-24</Dt></BookgDt>
                <AcctSvcrRef>OTP0001</AcctSvcrRef>
                <NtryDtls><TxDtls>
                  <RltdPties><Dbtr><Nm>Kafić Zvezda d.o.o.</Nm></Dbtr></RltdPties>
                  <RmtInf><Strd><CdtrRefInf><Ref>2026-114</Ref></CdtrRefInf></Strd></RmtInf>
                </TxDtls></NtryDtls>
              </Ntry>
        """;
}
