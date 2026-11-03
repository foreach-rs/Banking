using ForEach.Infrastructure.External.Banking.Statements;

namespace ForEach.Infrastructure.External.Banking.UnitTests.Statements;

/// <summary>
/// Reading an MT940 statement.
///
/// The sample is Raiffeisen's own published example, trimmed to the lines that matter and with its
/// balances left intact — because the balances are what prove the one genuinely ambiguous rule in
/// the format.
///
/// **`RC` reverses a credit and is therefore a debit.** The specification's wording — "RC - storno
/// u korist" — reads the other way, and the arithmetic settles it: the example opens at
/// 11.373.667,73, closes at 12.035.969,73, and only reconciles if RC takes money out. Read the
/// wrong way, a payment and its cancellation are both counted as money in and the customer is
/// credited for a receipt the bank took back.
/// </summary>
public class Mt940ReaderTests
{
    [Fact]
    public void A_statement_carries_its_account_number_number_and_balances()
    {
        var statement = Mt940Reader.Read(DinarSample);

        // Field 25 runs the account and the numeric currency code together: 18 digits then 941.
        Assert.Equal("265110031000008888", statement.AccountNumber);
        Assert.Equal("RSD", statement.Currency);

        // "00181/001" — the number people quote, without the sequence and without the padding.
        Assert.Equal("181", statement.Number);

        Assert.Equal(11_373_667.73m, statement.OpeningBalance);
        Assert.Equal(12_035_969.73m, statement.ClosingBalance);
    }

    [Fact]
    public void The_entries_account_for_the_movement_between_the_balances()
    {
        // The assertion the whole reader turns on, and the one that proves RC is a debit: with RC
        // read as a credit this is 667.102,00 against a stated movement of 662.302,00.
        Assert.True(Mt940Reader.Read(DinarSample).Reconciles);
    }

    [Fact]
    public void A_reversal_moves_the_account_the_opposite_way_to_the_letter_it_carries()
    {
        var entries = Mt940Reader.Read(DinarSample).Entries;

        Assert.Equal(StatementDirection.Credit, entries[0].Direction);
        Assert.Equal(StatementDirection.Credit, entries[1].Direction);

        // RC — the storno of the credit above it, same amount and same reference.
        Assert.Equal(StatementDirection.Debit, entries[2].Direction);
        Assert.Equal(2_400m, entries[2].Amount);
    }

    [Fact]
    public void The_legacy_dinar_code_on_the_balances_becomes_RSD()
    {
        // The balances are stated in CSD, the code that preceded RSD. Left alone it matches no
        // bank account on file.
        var statement = Mt940Reader.Read(
            DinarSample.Replace("265110031000008888941", "265110031000008888", StringComparison.Ordinal));

        Assert.Equal("RSD", statement.Currency);
    }

    [Fact]
    public void An_entry_takes_its_payer_and_reference_from_the_line_dialect_of_field_86()
    {
        var entry = Mt940Reader.Read(DinarSample).Entries[0];

        Assert.Equal(new DateOnly(2006, 6, 26), entry.BookingDate);
        Assert.Equal("GORDANA MILICEVIC, UZICE", entry.CounterpartyName);

        // ?30 is the bank code and ?31 the account "without the bank code and without leading
        // zeros" — so a domestic account is the two put back together.
        Assert.Equal("265161032000000278", entry.CounterpartyAccount);

        // ?22 is the zaduženja reference; this line carries no ?23, so it falls back.
        Assert.Equal("1359", entry.Reference);

        Assert.Contains("ZA POMEN KURLAGIC VESNE", entry.Description!, StringComparison.Ordinal);
        Assert.Contains("Šifra plaćanja: 189", entry.Description!, StringComparison.Ordinal);

        // The bank's own transaction reference, which is what makes a re-import detectable.
        Assert.Equal("5341062239261114", entry.BankReference);
    }

    [Fact]
    public void An_entry_reads_the_inline_dialect_of_field_86_too()
    {
        // The devizni statement writes its subfields inline as 'NN rather than one ?NN per line.
        // A reader knowing only one dialect returns lines with no payer and no purpose, which
        // looks like a bank that sent no details rather than a file half-read.
        var entry = Mt940Reader.Read(ForeignSample).Entries[1];

        Assert.Equal(4_990m, entry.Amount);
        Assert.Equal(StatementDirection.Credit, entry.Direction);
        Assert.Equal("COMPANY LTD 120 SAKSAGANSKOGO STR., 01032, BELGRADE SERBIA", entry.CounterpartyName);

        Assert.Contains(
            "PAYMENT FOR GOODS ACC.CONTRACT 1007", entry.Description!, StringComparison.Ordinal);

        // After "//" is the bank's reference; before it, the customer's.
        Assert.Equal("110111425549", entry.BankReference);
    }

    [Fact]
    public void A_foreign_counterparty_account_is_not_glued_to_a_SWIFT_code()
    {
        // ?30 is a bank code on a domestic statement and a SWIFT address on a devizni one.
        // Concatenating the second produces "INGBUAUK123456789…" — a string that matches no
        // account anywhere and looks like one.
        var entry = Mt940Reader.Read(ForeignSample).Entries[1];

        Assert.Equal("123456789123456789", entry.CounterpartyAccount);
    }

    [Fact]
    public void A_foreign_statement_takes_its_currency_from_the_slash_in_field_25()
    {
        var statement = Mt940Reader.Read(ForeignSample);

        Assert.Equal("265100000000066666", statement.AccountNumber);
        Assert.Equal("EUR", statement.Currency);
        Assert.Equal("136", statement.Number);
        Assert.Equal(213_852.85m, statement.OpeningBalance);
    }

    [Fact]
    public void NONREF_is_not_a_reference()
    {
        // What the standard writes when there is none. A host matching on it would match every
        // unreferenced payment to the same nothing.
        var entry = Mt940Reader.Read(ForeignSample).Entries[0];

        Assert.Null(entry.BankReference);
    }

    [Fact]
    public void Amounts_use_a_comma_and_are_read_by_rule_rather_than_by_culture()
    {
        // SWIFT writes 11373667,73. A machine set to English reads that as 1137366773 — out by a
        // factor of a hundred, on whichever server happens to run the import.
        Assert.Equal(11_373_667.73m, Mt940Reader.Read(DinarSample).OpeningBalance);
    }

    [Fact]
    public void A_file_holding_several_statements_yields_each_of_them()
    {
        var statements = Mt940Reader.ReadAll(DinarSample + "\r\n" + DinarSample);

        Assert.Equal(2, statements.Count);
        Assert.All(statements, statement => Assert.Equal("181", statement.Number));
    }

    [Fact]
    public void A_continuation_line_stays_with_the_field_it_continues()
    {
        // A long counterparty name runs onto the next line, which carries no tag.
        var entry = Mt940Reader.Read(DinarSample).Entries[3];

        Assert.Contains("TODOROVICC KURTESS DRAGAN", entry.CounterpartyName!, StringComparison.Ordinal);
        Assert.Contains("BEOGRAD - ZA FIRMA DOO", entry.CounterpartyName!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a statement")]
    [InlineData("<?xml version=\"1.0\"?><Document />")]
    public void Anything_that_is_not_MT940_is_answered_rather_than_thrown_at(string? content)
    {
        Assert.Empty(Mt940Reader.ReadAll(content));
        Assert.False(Mt940Reader.Recognises(content));
    }

    [Fact]
    public void Another_SWIFT_message_is_not_claimed_as_a_statement()
    {
        // :20: alone appears in every SWIFT message. Claiming an MT101 and reading it as an izvod
        // is worse than not recognising it.
        Assert.False(Mt940Reader.Recognises(":20:SOMEREF\r\n:30:060626\r\n"));
    }

    /// <summary>
    /// Raiffeisen's published dinar example, trimmed to seven entries. The balances are theirs and
    /// are left alone — they are what proves the reversal rule.
    /// </summary>
    private const string DinarSample =
        ":20:MT94006243798001\r\n"
        + ":25:265110031000008888941\r\n"
        + ":28C:00181/001\r\n"
        + ":60F:C060625CSD11373667,73\r\n"
        + ":61:060626C2400,00NMSC5341062239261114\r\n"
        + ":86:189\r\n?20ZA POMEN KURLAGIC VESNE\r\n?21189\r\n?221359\r\n?23\r\n?30265\r\n"
        + "?31161032000000278\r\n?32GORDANA MILICEVIC, UZICE\r\n?33UZICE\r\n"
        + ":61:060626C2400,00NMSC5341062239256056\r\n"
        + ":86:121\r\n?20ZA POMEN\r\n?21121\r\n?221359\r\n?23\r\n?30265\r\n"
        + "?31161032000000278\r\n?32GORDANA MILICEVIC, UZICE\r\n?33UZICE\r\n"
        + ":61:060626RC2400,00NMSC5341062239256056\r\n"
        + ":86:121\r\n?20ZA POMEN\r\n?21121\r\n?221359\r\n?23\r\n?30265\r\n"
        + "?31161032000000278\r\n?32GORDANA MILICEVIC, UZICE\r\n?33UZICE\r\n"
        + ":61:060626D5608,00NMSC5163062239316405\r\n"
        + ":86:166\r\n?20ISPLATA GOTOVINE\r\n?21166\r\n?2245\r\n?23\r\n?30265\r\n"
        + "?31161032000000278\r\n?32TODOROVICC KURTESS DRAGAN, STANISLAVA SREMCHEVICCA 5/22, \r\n"
        + "BEOGRAD - ZA FIRMA DOO,Adresa bb,\r\n"
        + ":61:060626D7000,00NMSC5163062239316693\r\n"
        + ":86:166\r\n?20ISPLATA GOTOVINE\r\n?30265\r\n?31161032000000278\r\n"
        + ":61:060626D80000,00NMSC5163062239316928\r\n"
        + ":86:166\r\n?20ISPLATA GOTOVINE\r\n?30265\r\n?31161032000000278\r\n"
        + ":61:060626C752510,00NTRF5163062239313384\r\n"
        + ":86:165\r\n?20UPLATA PAZARA\r\n?21165\r\n?2245\r\n?2310.08.2006\r\n?30265\r\n"
        + "?31161032000000278\r\n?32FIRMA DOO, Adresa bb\r\n"
        + ":62M:C060626CSD12035969,73\r\n";

    /// <summary>Their devizni example — the inline field-86 dialect, a SWIFT bank and an IBAN.</summary>
    private const string ForeignSample =
        ":20:MT94006243802001\r\n"
        + ":25:265100000000066666/EUR\r\n"
        + ":28C:00136/001\r\n"
        + ":60F:C060624EUR213852,85\r\n"
        + ":61:060626D163,75NTRFNONREF\r\n"
        + ":86:340'00PLACCANJE ZA SLUZZBENA PUTOVANJA'10'20POVRAC.EFEKT.'21'22'23'30'31'32'33\r\n"
        + ":61:060626C4990,00S100001KID0608091680//110111425549\r\n"
        + ":86:112'00NAPLATA ZA IZVOZ ROBE PO IZVRSENOM IZVOZU'10"
        + "'20PAYMENT FOR GOODS ACC.CONTRACT 1007 DD 15.03.2007.'21'22'23'24'25'26'27"
        + "'30INGBUAUK'31123456789123456789"
        + "'32COMPANY LTD 120 SAKSAGANSKOGO STR., 01032, BELGRADE SERBIA'33\r\n"
        + ":62F:C060626EUR218679,10\r\n";
}
