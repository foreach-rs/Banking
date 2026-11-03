using ForEach.Infrastructure.External.Banking.Rol.Statements;
using ForEach.Infrastructure.External.Banking.Statements;

namespace ForEach.Infrastructure.External.Banking.Rol.UnitTests.Statements;

/// <summary>
/// Reading the ROL fixed-width TXT statement.
///
/// The two records below are **verbatim from Raiffeisen's own example**, and they describe the same
/// izvod as the XML sample beside them — same account, same number 181, same balances, and the
/// first line carries the same poziv na broj (9009206) and the same broj za reklamaciju. That
/// cross-check is worth more than any assertion here: two formats of the same statement agreeing
/// means the offsets are right rather than merely self-consistent.
///
/// The specification's nineteen detail field lengths sum to 489, which is the record width to the
/// character — so an offset that drifts fails immediately rather than reading a neighbouring field.
/// </summary>
public class RolTextStatementReaderTests
{
    [Fact]
    public void A_statement_carries_its_account_number_and_balances()
    {
        var read = RolTextStatementReader.Read(Sample);

        Assert.Equal(RolStatementKind.Dinar, read.Kind);

        var statement = read.Statement;

        Assert.Equal("265110031000008888", statement.AccountNumber);
        Assert.Equal("RSD", statement.Currency);

        // "0181" — the number people quote, without the padding.
        Assert.Equal("181", statement.Number);

        Assert.Equal(new DateOnly(2006, 6, 26), statement.StatementDate);
        Assert.Equal(11_373_667.73m, statement.OpeningBalance);
        Assert.Equal(13_976_765.67m, statement.ClosingBalance);
    }

    [Fact]
    public void The_same_izvod_reads_the_same_from_the_TXT_as_from_the_XML()
    {
        // Raiffeisen ships both for the same statement. Two independent layouts agreeing is what
        // makes the offsets trustworthy rather than merely self-consistent.
        var entry = Assert.Single(RolTextStatementReader.Read(Sample).Statement.Entries);

        Assert.Equal(363.44m, entry.Amount);
        Assert.Equal(StatementDirection.Credit, entry.Direction);
        Assert.Equal("9009206", entry.Reference);
        Assert.Equal("1131062230000090000006", entry.BankReference);
        Assert.Equal("220000000005682934", entry.CounterpartyAccount);
        Assert.StartsWith("LIDER TRADE COMPANY", entry.CounterpartyName!, StringComparison.Ordinal);
        Assert.Equal(new DateOnly(2006, 6, 26), entry.BookingDate);
    }

    [Fact]
    public void The_amount_is_netted_across_the_two_columns()
    {
        // Same rule the XML reader states: a storno is written as a negative in one of the two
        // columns, and taking whichever is non-zero books a correction as money arriving.
        var reversed = Sample.Replace(
            " 00000000000000000000.00 00000000000000000363.44",
            " 00000000000000000363.44 00000000000000000000.00",
            StringComparison.Ordinal);

        var entry = Assert.Single(RolTextStatementReader.Read(reversed).Statement.Entries);

        Assert.Equal(363.44m, entry.Amount);
        Assert.Equal(StatementDirection.Debit, entry.Direction);
    }

    [Fact]
    public void A_negative_credit_moves_the_money_back_out()
    {
        var storno = Sample.Replace(
            " 00000000000000000363.44",
            "-00000000000000000363.44",
            StringComparison.Ordinal);

        var entry = Assert.Single(RolTextStatementReader.Read(storno).Statement.Entries);

        Assert.Equal(363.44m, entry.Amount);
        Assert.Equal(StatementDirection.Debit, entry.Direction);
    }

    [Fact]
    public void The_description_carries_the_purpose_and_both_references()
    {
        var entry = Assert.Single(RolTextStatementReader.Read(Sample).Statement.Entries);

        Assert.Contains("PROMET ROBE I USLUGA", entry.Description!, StringComparison.Ordinal);
        Assert.Contains("Šifra plaćanja: 221", entry.Description!, StringComparison.Ordinal);
        Assert.Contains("Poziv na broj odobrenja", entry.Description!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_whose_trailing_spaces_were_stripped_still_reads()
    {
        // Trailing spaces are data in a fixed-width file and some transports eat them.
        var trimmed = string.Join(
            "\r\n",
            Sample.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd()));

        Assert.Equal(RolStatementKind.Dinar, RolTextStatementReader.Read(trimmed).Kind);
    }

    [Fact]
    public void A_file_of_detail_records_with_no_header_is_not_a_statement()
    {
        // No header means no balances and no izvod number — nothing this reader can vouch for.
        var detailOnly = Sample.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)[1];

        Assert.Equal(RolStatementKind.Unknown, RolTextStatementReader.Read(detailOnly).Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a statement")]
    [InlineData("<TransakcioniRacunPrivredaIzvod />")]
    public void Anything_that_is_not_this_export_is_answered_rather_than_thrown_at(string? content)
    {
        Assert.Equal(RolStatementKind.Unknown, RolTextStatementReader.Read(content).Kind);
        Assert.False(RolTextStatementReader.Recognises(content));
    }

    [Fact]
    public void A_line_with_no_readable_date_refuses_the_file_rather_than_defaulting()
    {
        // The line's own date, then the header's. With neither readable there is no day to file
        // the movement under, and the first of January 0001 is not one.
        var lines = Sample.Split("\r\n");

        lines[0] = lines[0].Remove(1, 8).Insert(1, "99/99/99");
        lines[1] = lines[1].Remove(25, 8).Insert(25, "99/99/99");

        var read = RolTextStatementReader.Read(string.Join("\r\n", lines));

        Assert.Equal(RolStatementKind.Dinar, read.Kind);
        Assert.Empty(read.Statement.Entries);
        Assert.Contains("99/99/99", read.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void An_amount_that_is_not_a_number_refuses_the_whole_file()
    {
        var read = RolTextStatementReader.Read(
            Sample.Replace(" 00000000000000000363.44", " 0000000000000000036x.44", StringComparison.Ordinal));

        Assert.Empty(read.Statement.Entries);
        Assert.Contains("36x.44", read.Refusal!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_longer_than_the_record_is_refused_rather_than_read_from_the_wrong_offsets()
    {
        Assert.Equal(RolStatementKind.Unknown, RolTextStatementReader.Read(Sample + "X").Kind);
    }

    /// <summary>
    /// Raiffeisen's published example: its header record and the first of its detail records,
    /// character for character.
    /// </summary>
    private const string Sample =
        "126/06/06265110031000008888  0000010000000000243798127801814117377777    "
        + "00000000000011373667.73 00000000000001880798.05 00000000000004483895.99 "
        + "00000000000013976765.6700002700004917/08/06143219\r\n"
        + "200000000002437981278018126/06/064117377777   265110031000008888   "
        + "00000000000000000000.00 00000000000000000363.44LIDER TRADE COMPANY BULEVAR AVNOJA 81 "
        + "NOVI BEOGRAD                                                                        "
        + "                       220000000005682934  221PROMET ROBE I USLUGA FINALNA POTROSNJA"
        + "                                                                   99               "
        + "     009009206             1131062230000090000006                    5131062230000083";
}
