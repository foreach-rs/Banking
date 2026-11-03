using ForEach.Infrastructure.External.Banking.Asseco.Statements;
using ForEach.Infrastructure.External.Banking.Statements;

namespace ForEach.Infrastructure.External.Banking.Asseco.UnitTests.Statements;

/// <summary>
/// Reading the Asseco fixed-width TXT statement.
///
/// **This is the one reader in the solution built from a specification with no sample to check it
/// against**, so the records below are constructed from the documented field lengths rather than
/// copied from a bank. Two things make that acceptable, and both are asserted here.
///
/// The layout is self-consistent: each record's fields account for exactly 220 characters, so an
/// offset that drifts leaves a gap.
///
/// And the reader checks itself. The header states the day's debit and credit turnover, and the
/// entries must reproduce **both** — not merely net to the same figure, which two swapped columns
/// would also do. If these offsets are wrong against a real file, the totals disagree and the
/// import is refused rather than being handed a statement whose amounts came from elsewhere.
/// </summary>
public class AssecoTextStatementReaderTests
{
    [Fact]
    public void A_statement_carries_its_account_number_currency_and_balances()
    {
        var read = AssecoTextStatementReader.Read(Sample);

        Assert.Equal(AssecoStatementKind.Statement, read.Kind);
        Assert.Null(read.Refusal);

        var statement = read.Statement;

        Assert.Equal("265110031000008888", statement.AccountNumber);
        Assert.Equal("RSD", statement.Currency);
        Assert.Equal("181", statement.Number);
        Assert.Equal(new DateOnly(2026, 6, 26), statement.StatementDate);
        Assert.Equal(1_000m, statement.OpeningBalance);
        Assert.Equal(1_300m, statement.ClosingBalance);
    }

    [Fact]
    public void The_direction_is_stated_rather_than_derived()
    {
        // D is duguje and P is potražuje. Unlike the ROL and Halcom fixed-width formats there is
        // nothing to infer here, which is the one thing this format makes easier.
        var entries = AssecoTextStatementReader.Read(Sample).Statement.Entries;

        Assert.Equal(StatementDirection.Credit, entries[0].Direction);
        Assert.Equal(500m, entries[0].Amount);

        Assert.Equal(StatementDirection.Debit, entries[1].Direction);
        Assert.Equal(200m, entries[1].Amount);
    }

    [Fact]
    public void The_amount_carries_two_implied_decimals()
    {
        // Thirteen characters, eleven whole and two decimal, with nothing between them. A reader
        // that forgot to divide is out by a factor of a hundred.
        Assert.Equal(500m, AssecoTextStatementReader.Read(Sample).Statement.Entries[0].Amount);
    }

    [Fact]
    public void An_entry_carries_its_payer_reference_and_reklamacija()
    {
        var entry = AssecoTextStatementReader.Read(Sample).Statement.Entries[0];

        Assert.Equal("KUPAC DOO", entry.CounterpartyName);
        Assert.Equal("160000000012345678", entry.CounterpartyAccount);
        Assert.Equal("2026-114", entry.Reference);
        Assert.Equal("11310622300001", entry.BankReference);
        Assert.Equal(new DateOnly(2026, 6, 26), entry.BookingDate);
        Assert.Contains("UPLATA PO FAKTURI", entry.Description!, StringComparison.Ordinal);
    }

    [Fact]
    public void The_entries_are_checked_against_the_headers_own_totals()
    {
        // The safety net that makes a sample-less reader acceptable. Reading the amount from the
        // wrong offset changes the totals, and the file is refused rather than imported.
        // Built rather than string-edited: the header's own total contains the same digits, so a
        // textual substitution would move both and prove nothing.
        var wrong = string.Join(
            Environment.NewLine,
            Header(),
            Detail(
                counterpartyAccount: "160000000012345678",
                name: "KUPAC DOO",
                purpose: "UPLATA PO FAKTURI",
                paymentCode: "221",
                amount: "0000000050900",
                direction: 'P',
                reference: "2026-114",
                reklamacija: "11310622300001"),
            Detail(
                counterpartyAccount: "160000000087654321",
                name: "DOBAVLJAC DOO",
                purpose: "PLACANJE PO FAKTURI",
                paymentCode: "221",
                amount: "0000000020000",
                direction: 'D',
                reference: "FT-9912",
                reklamacija: "11310622300002"));

        var read = AssecoTextStatementReader.Read(wrong);

        Assert.Empty(read.Statements);
        Assert.Contains("ne odgovaraju zbirovima", read.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_swapped_directions_are_caught_because_both_totals_are_checked()
    {
        // Netting would not catch this: a debit and a credit exchanged leave the movement between
        // the balances unchanged. Checking each direction against its own stated total does.
        var swapped = Sample
            .Replace("P160000000012345678", "X160000000012345678", StringComparison.Ordinal)
            .Replace("D160000000087654321", "P160000000087654321", StringComparison.Ordinal)
            .Replace("X160000000012345678", "D160000000012345678", StringComparison.Ordinal);

        var read = AssecoTextStatementReader.Read(swapped);

        Assert.Empty(read.Statements);
        Assert.NotNull(read.Refusal);
    }

    [Fact]
    public void An_amount_that_is_not_digits_refuses_the_file_rather_than_booking_zero()
    {
        var broken = string.Join(
            Environment.NewLine,
            Header(),
            Detail(
                counterpartyAccount: "160000000012345678",
                name: "KUPAC DOO",
                purpose: "UPLATA PO FAKTURI",
                paymentCode: "221",
                amount: "0000000000abc",
                direction: 'P',
                reference: "2026-114",
                reklamacija: "11310622300001"));

        var read = AssecoTextStatementReader.Read(broken);

        Assert.Empty(read.Statements);
        Assert.Contains("abc", read.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_whose_date_does_not_parse_refuses_the_file_rather_than_defaulting()
    {
        var lines = Sample.Split("\r\n");

        lines[1] = lines[1].Remove(188, 8).Insert(188, "99999999");

        var read = AssecoTextStatementReader.Read(string.Join("\r\n", lines));

        Assert.Empty(read.Statements);
        Assert.Contains("99999999", read.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_blank_balance_is_absent_rather_than_zero()
    {
        // Zero would claim the account was empty, and Reconciles would then report a fault
        // nobody can find. Null says nothing, which is what a blank field says.
        var lines = Sample.Split("\r\n");

        lines[0] = lines[0].Remove(33, 18).Insert(33, new string(' ', 18));

        var statement = AssecoTextStatementReader.Read(string.Join("\r\n", lines)).Statement;

        Assert.Null(statement.OpeningBalance);
        Assert.Null(statement.Reconciles);
        Assert.Equal(2, statement.Entries.Count);
    }

    [Fact]
    public void Trailing_spaces_cannot_be_lost_because_the_kind_marker_is_the_last_character()
    {
        var trimmed = string.Join(
            "\r\n",
            Sample.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd()));

        // Worth pinning rather than assuming. Every other fixed-width format here can have its
        // trailing spaces eaten in transit and needs padding back; this one ends with the record
        // kind, so a whitespace-stripping transport cannot shorten it.
        Assert.Equal(Sample, trimmed);
        Assert.Equal(AssecoStatementKind.Statement, AssecoTextStatementReader.Read(trimmed).Kind);
    }

    [Fact]
    public void A_file_with_no_header_is_not_a_statement()
    {
        var detailOnly = Sample.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)[1];

        Assert.Equal(AssecoStatementKind.Unknown, AssecoTextStatementReader.Read(detailOnly).Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a statement")]
    [InlineData("<stmtrs />")]
    public void Anything_that_is_not_this_export_is_answered_rather_than_thrown_at(string? content)
    {
        Assert.Equal(AssecoStatementKind.Unknown, AssecoTextStatementReader.Read(content).Kind);
        Assert.False(AssecoTextStatementReader.Recognises(content));
    }

    // ---- fixtures ------------------------------------------------------------------------------

    /// <summary>
    /// A header and two movements, built from the documented field lengths. Each record accounts
    /// for exactly 220 characters, which the builder asserts by construction.
    /// </summary>
    private static readonly string Sample = string.Join(
        "\r\n",
        Header(),
        Detail(
            counterpartyAccount: "160000000012345678",
            name: "KUPAC DOO",
            purpose: "UPLATA PO FAKTURI",
            paymentCode: "221",
            amount: "0000000050000",
            direction: 'P',
            reference: "2026-114",
            reklamacija: "11310622300001"),
        Detail(
            counterpartyAccount: "160000000087654321",
            name: "DOBAVLJAC DOO",
            purpose: "PLACANJE PO FAKTURI",
            paymentCode: "221",
            amount: "0000000020000",
            direction: 'D',
            reference: "FT-9912",
            reklamacija: "11310622300002"));

    private static string Header()
    {
        var record = new char[220];

        Array.Fill(record, ' ');

        Put(record, 0, "26062026");                  // datum izrade
        Put(record, 8, "1432");                      // vreme izrade
        Put(record, 12, "265110031000008888");       // račun
        Put(record, 30, "941");                      // oznaka valute
        Put(record, 33, "000000000100000".PadLeft(18, '0'));  // stari saldo — 1.000,00
        Put(record, 51, "000000000020000".PadLeft(18, '0'));  // dnevni promet duguje — 200,00
        Put(record, 69, "000000000050000".PadLeft(18, '0'));  // dnevni promet potražuje — 500,00
        Put(record, 87, "000000000130000".PadLeft(18, '0'));  // novi saldo — 1.300,00
        Put(record, 141, "000001");                  // OJ banke
        Put(record, 147, "181");                     // redni broj izvoda
        Put(record, 150, "FIRMA DOO");               // naziv računa
        Put(record, 185, "BEOGRAD");                 // mesto
        Put(record, 205, "100123456");               // poreski broj
        Put(record, 219, "1");                       // tip sloga

        return new string(record);
    }

    private static string Detail(
        string counterpartyAccount,
        string name,
        string purpose,
        string paymentCode,
        string amount,
        char direction,
        string reference,
        string reklamacija)
    {
        var record = new char[220];

        Array.Fill(record, ' ');

        Put(record, 0, counterpartyAccount);   // račun
        Put(record, 18, name);                 // naziv primaoca
        Put(record, 53, "BEOGRAD");            // mesto primaoca
        Put(record, 73, " ");                  // način izvršenja
        Put(record, 74, "00");                 // model zaduženja
        Put(record, 96, paymentCode);          // šifra plaćanja
        Put(record, 99, purpose);              // svrha plaćanja
        Put(record, 134, amount);              // iznos
        Put(record, 147, direction.ToString());
        Put(record, 148, counterpartyAccount); // broj računa primaoca
        Put(record, 166, "97");                // model poziva na broj
        Put(record, 168, reference);           // poziv na broj
        Put(record, 188, "26062026");          // datum valute
        Put(record, 196, "1432");              // vreme izvršenja
        Put(record, 200, reklamacija);         // reklamacija 1
        Put(record, 214, "0");                 // reklamacija 2
        Put(record, 219, "2");                 // tip sloga

        return new string(record);
    }

    private static void Put(char[] record, int start, string value)
    {
        for (var i = 0; i < value.Length && start + i < record.Length; i++)
            record[start + i] = value[i];
    }
}
