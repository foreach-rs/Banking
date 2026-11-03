namespace ForEach.Infrastructure.External.Banking.Statements;

/// <summary>Which way an entry moved the account.</summary>
public enum StatementDirection
{
    /// <summary>Money in. A customer paying us.</summary>
    Credit,

    /// <summary>Money out. Us paying somebody, or a bank charge.</summary>
    Debit
}

/// <summary>
/// One line of an account statement.
/// </summary>
/// <param name="Amount">
/// Always positive. The sign lives in <see cref="Direction"/> rather than in the number, because
/// that is how camt.053 states it and because a host that reads the amount alone and forgets the
/// indicator produces a credit where a charge belongs — silently, and in the direction that
/// flatters the balance.
/// </param>
/// <param name="Direction">
/// Which way the money moved. The sign that <paramref name="Amount"/> deliberately does not carry.
/// </param>
/// <param name="BookingDate">
/// The day the bank booked the entry — the date a statement line is filed under, so never null.
/// </param>
/// <param name="ValueDate">The day the money counts from, where the bank states one separately.</param>
/// <param name="Reference">
/// The payment reference — poziv na broj on a Serbian transfer. This is what matches a payment to
/// an invoice, so it is read from the structured field first and from the free-text remittance
/// only as a fallback: banks put it in either, and the structured one is the one the payer typed
/// into a labelled box.
/// </param>
/// <param name="CounterpartyAccount">
/// The other side's account number, as the bank wrote it. Kept unnormalised: it is matched against
/// what somebody typed into a contact record, and stripping characters here would move the
/// disagreement somewhere nobody is looking.
/// </param>
/// <param name="Description">
/// The bank's free text for the line — purpose, remittance information, whatever the format
/// offers. For a person to read once the structured fields have been tried.
/// </param>
/// <param name="CounterpartyName">The other side's name, as the bank wrote it.</param>
/// <param name="BankReference">
/// The bank's own reference for this entry. The only thing that makes importing the same
/// statement twice detectable, so it is carried even though nothing on a screen shows it.
/// </param>
public sealed record StatementEntry(
    decimal Amount,
    StatementDirection Direction,
    DateOnly BookingDate,
    DateOnly? ValueDate,
    string? Reference,
    string? Description,
    string? CounterpartyName,
    string? CounterpartyAccount,
    string? BankReference);

/// <summary>
/// One account statement — an izvod — as the bank issued it.
/// </summary>
/// <param name="AccountNumber">
/// The account the statement is for, as the bank wrote it. Null where the format does not say.
/// </param>
/// <param name="Currency">The account's currency, as an alphabetic ISO code. Empty on <see cref="Empty"/>.</param>
/// <param name="Number">
/// The statement's sequence number for the year, which is how everybody refers to it: "izvod 143".
/// Null where the bank did not state one.
/// </param>
/// <param name="OpeningBalance">
/// Signed, unlike an entry: a balance genuinely can be negative, and the sign is the fact rather
/// than a direction. Both balances are carried because they are what a person checks the import
/// against — if the opening plus the entries does not reach the closing, something was not read.
/// </param>
/// <param name="StatementDate">The day the statement closed. Null where the bank did not state one.</param>
/// <param name="ClosingBalance">
/// The balance the statement closed with, signed like the opening one. Null where the bank did not
/// state it.
/// </param>
/// <param name="Entries">
/// The lines, in the bank's order. Empty is a quiet day — or a file that was not understood, which
/// is what <see cref="Reconciles"/> tells apart.
/// </param>
public sealed record BankStatement(
    string? AccountNumber,
    string Currency,
    string? Number,
    DateOnly? StatementDate,
    decimal? OpeningBalance,
    decimal? ClosingBalance,
    IReadOnlyList<StatementEntry> Entries)
{
    /// <summary>Nothing was read — an empty statement, or a file that is not one.</summary>
    public static BankStatement Empty { get; } =
        new(null, string.Empty, null, null, null, null, []);

    /// <summary>
    /// What the entries add up to: credits less debits. The figure to compare against
    /// <see cref="ClosingBalance"/> minus <see cref="OpeningBalance"/> — and the reason both
    /// balances are on this record at all.
    /// </summary>
    public decimal NetMovement =>
        Entries.Sum(e => e.Direction == StatementDirection.Credit ? e.Amount : -e.Amount);

    /// <summary>
    /// Whether the entries account for the movement the balances describe.
    ///
    /// False is the signal that something in the file was not understood — a shape of entry this
    /// reader skipped, or a balance type it read as the wrong one. On a bank statement that is
    /// worth saying out loud rather than importing what was recognised and reporting success:
    /// a payment silently missing from an import is a customer chased for money they have paid.
    ///
    /// Null when the bank stated neither balance, which is honest: there is nothing to reconcile
    /// against, and answering false would report a fault nobody can find.
    /// </summary>
    public bool? Reconciles =>
        OpeningBalance is { } opening && ClosingBalance is { } closing
            ? decimal.Round(closing - opening, 2) == decimal.Round(NetMovement, 2)
            : null;
}
