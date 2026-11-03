namespace ForEach.Infrastructure.External.Banking.Payments;

/// <summary>How quickly the money must move, which the bank reads as a priority band.</summary>
public enum PaymentPriority
{
    /// <summary>Žiro kliring — the ordinary net settlement. What almost every payment is.</summary>
    Clearing,

    /// <summary>RTGS — gross settlement in real time. Costs more and is asked for deliberately.</summary>
    Urgent
}

/// <summary>
/// One payment order — a virman — in the application's own words, before any bank's format.
///
/// **The counterpart of <see cref="Statements.BankStatement"/>, and it exists for the same reason.**
/// Every bank writes this document differently and none of them disagree about what it *is*: who
/// pays, who is paid, how much, on what date, quoting what. So the shape is stated once here and
/// each bank project turns it into that bank's file.
///
/// **This is not the host's virman entity.** It carries no identity, no status and no link to an
/// invoice — a payment order in a file is a instruction, and the record of having decided to send
/// it belongs to whoever is keeping the books. Putting a database key on this record would make the
/// library know about the host, which is the dependency the seam exists to prevent.
/// </summary>
/// <param name="OrderingAccount">
/// Račun nalogodavca — the payer's eighteen-digit account, with or without its dashes. Carried
/// plain because the banks disagree about the dashes, and each writer formats it its own way.
/// </param>
/// <param name="OrderingName">Naziv nalogodavca — the payer, as the bank should print it.</param>
/// <param name="OrderingPlace">Mesto nalogodavca — the payer's town. Mandatory in every format.</param>
/// <param name="OrderingRegistrationNumber">
/// Matični broj of the payer. Mandatory in the ROL format and not derivable from anything else on
/// the order, which is why it is on the neutral record rather than in a bank adapter.
/// </param>
/// <param name="BeneficiaryAccount">
/// Račun korisnika — the beneficiary's eighteen-digit account, under the same rule as
/// <paramref name="OrderingAccount"/>. The field a wrong digit in moves money to a stranger, so
/// every writer counts the digits before writing it.
/// </param>
/// <param name="BeneficiaryName">Naziv korisnika — who is paid.</param>
/// <param name="BeneficiaryPlace">Mesto korisnika — the beneficiary's town.</param>
/// <param name="Amount">
/// Positive, with at most two decimals. Anything else is refused rather than rounded: a rounded
/// amount is a different payment.
/// </param>
/// <param name="Currency">
/// Dinars, and only dinars — a domestic order carries nothing else, and every writer refuses any
/// other currency and points at <see cref="ForeignPaymentOrder"/> instead.
/// </param>
/// <param name="ValueDate">Datum valute — the day the bank is asked to execute the order.</param>
/// <param name="PurposeCode">
/// Šifra plaćanja, the three-digit NBS code. Not defaulted anywhere in this library: the code
/// changes what the payment *is* to the authority collecting the statistics, and a default would be
/// a claim nobody made.
/// </param>
/// <param name="Purpose">
/// Svrha doznake — what the payment is for, in words. Mandatory: a bank will take an order with no
/// purpose and the person reading their own statement next month will not know what it was.
/// </param>
/// <param name="DebitReferenceModel">
/// The model — 97, or blank — qualifying <paramref name="DebitReference"/>. Refused when given
/// without a reference: the bank validates a 97 model's control digits, and there would be nothing
/// to validate.
/// </param>
/// <param name="DebitReference">
/// Poziv na broj zaduženja — our own reference for this payment, if we use one.
/// </param>
/// <param name="CreditReferenceModel">
/// The model qualifying <paramref name="CreditReference"/>, under the same rule as the debit one.
/// </param>
/// <param name="CreditReference">
/// Poziv na broj odobrenja — the beneficiary's reference, which is what names the invoice being
/// paid. This is the field the supplier's own ledger matches on, so a wrong one is money that
/// arrives and is not credited to us.
/// </param>
/// <param name="OwnReference">
/// Our own number for the order, for finding it again among the bank's confirmations. Optional,
/// and each format caps its length differently.
/// </param>
/// <param name="Priority">
/// <see cref="PaymentPriority.Clearing"/> unless somebody deliberately asked for urgent.
/// </param>
/// <param name="ValueDateUntil">
/// Datum valute do — the last day the bank may still execute the order, when a window is given.
/// Only the ROL format carries it; written as the value date itself when absent, and refused when
/// earlier than it.
/// </param>
/// <param name="BeneficiaryAddress">
/// The beneficiary's street address. Optional, and unused by the XML formats — the Halcom
/// fixed-width order carries a field for it, and a real file shows it populated.
/// </param>
public sealed record PaymentOrder(
    string OrderingAccount,
    string OrderingName,
    string OrderingPlace,
    string OrderingRegistrationNumber,
    string BeneficiaryAccount,
    string BeneficiaryName,
    string BeneficiaryPlace,
    decimal Amount,
    string Currency,
    DateOnly ValueDate,
    string PurposeCode,
    string Purpose,
    string? DebitReferenceModel = null,
    string? DebitReference = null,
    string? CreditReferenceModel = null,
    string? CreditReference = null,
    string? OwnReference = null,
    PaymentPriority Priority = PaymentPriority.Clearing,
    DateOnly? ValueDateUntil = null,
    string? BeneficiaryAddress = null);

/// <summary>
/// The outcome of writing a payment file.
///
/// **A refusal is a list of sentences rather than an exception**, because the whole batch is
/// prepared by a person who then has to fix it: telling them the first thing wrong, thirty times,
/// is thirty round trips. And it is a refusal rather than a best effort because of what this file
/// is — a bank that *accepts* an order with a truncated account number moves real money to
/// somewhere real. A rejected file costs a morning; an accepted wrong one costs the money.
/// </summary>
/// <param name="Content">The file, when there is one. Null when anything was refused.</param>
/// <param name="Refusals">
/// One sentence per problem, naming the order it belongs to. Empty exactly when
/// <paramref name="Content"/> is present.
/// </param>
public sealed record PaymentFile(string? Content, IReadOnlyList<string> Refusals)
{
    /// <summary>Whether there is a file to send. False exactly when something was refused.</summary>
    public bool Written => Content is not null;

    /// <summary>No file — only the sentences saying why.</summary>
    public static PaymentFile Refused(IReadOnlyList<string> refusals) => new(null, refusals);

    /// <summary>A written file, with nothing refused.</summary>
    public static PaymentFile Of(string content) => new(content, []);
}
