namespace ForEach.Infrastructure.External.Banking.Payments;

/// <summary>
/// Where the money for a foreign payment comes from — the "indikator pokrića".
///
/// It is not a preference: it decides which currency the order may be denominated in and whether
/// the payer's own account is named at all, so it is a required part of the instruction rather
/// than a flag on it.
/// </summary>
public enum ForeignCoverBasis
{
    /// <summary>
    /// Paid from a foreign-currency account (<c>$</c>). The account is named and the amount is in
    /// that currency — never dinars.
    /// </summary>
    ForeignCurrencyAccount,

    /// <summary>
    /// The bank buys the currency for us (<c>D</c>). The dinar account is named and the amount is
    /// still in the foreign currency.
    /// </summary>
    PurchaseCurrency,

    /// <summary>
    /// Paid in dinars (<c>S</c>). The amount is RSD and **no account is named** — the bank takes it
    /// from the one the order arrives on.
    /// </summary>
    Dinar
}

/// <summary>Who pays the correspondent banks' charges, as SWIFT names it.</summary>
public enum ForeignChargeOption
{
    /// <summary>We pay every charge; the beneficiary receives the full amount.</summary>
    Ours,

    /// <summary>The beneficiary pays every charge, out of the amount sent.</summary>
    Beneficiary,

    /// <summary>Each side pays its own bank.</summary>
    Shared
}

/// <summary>
/// One line of the statistical breakdown a foreign payment must carry.
///
/// The National Bank is told *what the money was for*, and a payment can be for several things at
/// once — so an order carries between one and 999 of these, and together they account for the whole
/// amount.
/// </summary>
/// <param name="ReasonCode">
/// Šifra osnova plaćanja, three digits. Not defaulted anywhere in this library: it is what the
/// payment is declared to be, and a default would be a declaration nobody made.
/// </param>
/// <param name="Description">
/// Opis transakcije — what this part of the payment was for, in words. Mandatory, up to 70
/// characters.
/// </param>
/// <param name="Amount">
/// This item's share of the order: positive, at most two decimals, and the items together must
/// total the order's amount.
/// </param>
/// <param name="ContractNumber">
/// The invoice or proforma number. **Mandatory when the reason code is 112** (payment for imported
/// goods already received) and optional otherwise.
/// </param>
/// <param name="ContractYear">
/// The year of <paramref name="ContractNumber"/>. Mandatory whenever a number is given — an invoice
/// number without its year does not name an invoice.
/// </param>
public sealed record ForeignPaymentStatistic(
    string ReasonCode,
    string Description,
    decimal Amount,
    string? ContractNumber = null,
    int? ContractYear = null);

/// <summary>
/// A foreign payment order — devizni nalog za plaćanje — before any bank's format.
///
/// **A separate record from <see cref="PaymentOrder"/> rather than a widening of it.** A domestic
/// order names a beneficiary by an eighteen-digit account and a poziv na broj; this one names them
/// by an IBAN, a country, a correspondent bank and a statistical declaration to the National Bank,
/// and has no poziv na broj at all. One record carrying both would be two thirds nullable, and the
/// nullable third is the part that decides whether the money can move.
/// </summary>
/// <param name="OrderingRegistrationNumber">Matični broj of the payer, as on the dinar order.</param>
/// <param name="OrderingPlace">Mesto — the payer's town.</param>
/// <param name="BeneficiaryName">Korisnik — who is paid. Up to 70 characters.</param>
/// <param name="BeneficiaryPlace">The beneficiary's town.</param>
/// <param name="BeneficiaryCountryCode">Numeric country code, three digits.</param>
/// <param name="BeneficiaryAccount">
/// The beneficiary's account, normally an IBAN. Up to 34 characters, which is the IBAN's own
/// maximum.
/// </param>
/// <param name="Amount">
/// In <paramref name="CurrencyCode"/>. Positive, with at most two decimals, and what
/// <paramref name="Statistics"/> must add up to.
/// </param>
/// <param name="CurrencyCode">
/// The **numeric** ISO 4217 code — 978 for the euro, 941 for the dinar — because that is what the
/// file carries and what the cover-basis rule is expressed in. Taking the alphabetic code here
/// would put a currency table inside a format writer, and an incomplete one refuses payments in
/// currencies the bank supports.
/// </param>
/// <param name="ValueDate">Traženi datum valute — the day the payer asks for the money to go.</param>
/// <param name="CoverBasis">
/// Where the money comes from — see <see cref="ForeignCoverBasis"/>. Decides which currency the
/// order may be in and whether <paramref name="OrderingAccount"/> is named.
/// </param>
/// <param name="Charges">Who pays the correspondent banks — see <see cref="ForeignChargeOption"/>.</param>
/// <param name="Statistics">
/// The declaration to the National Bank: between one and 999 items that together total
/// <paramref name="Amount"/>. An order with none is refused.
/// </param>
/// <param name="OrderingAccount">
/// Partija. Required for <see cref="ForeignCoverBasis.ForeignCurrencyAccount"/> and
/// <see cref="ForeignCoverBasis.PurchaseCurrency"/>, and **must be absent** for
/// <see cref="ForeignCoverBasis.Dinar"/>.
/// </param>
/// <param name="BeneficiaryAddress">
/// The beneficiary's street address. Optional; 70 characters until the specification's limit
/// grows in November 2026, and the ROL writer says why the shorter one is enforced.
/// </param>
/// <param name="BeneficiaryContact">Kontakt korisnika — how to reach the beneficiary. Optional.</param>
/// <param name="BeneficiaryBankSwift">
/// Required unless <paramref name="BeneficiaryBankName"/> is given. One of the two must name the
/// bank, or nothing knows where the money is going.
/// </param>
/// <param name="BeneficiaryBankName">
/// The beneficiary's bank by name, for when there is no SWIFT address — the other half of the rule
/// on <paramref name="BeneficiaryBankSwift"/>.
/// </param>
/// <param name="BeneficiaryBankContact">Kontakt banke korisnika — how to reach that bank. Optional.</param>
/// <param name="IntermediaryBankSwift">
/// The correspondent bank's SWIFT address, when the beneficiary's bank is reached through one.
/// Optional.
/// </param>
/// <param name="IntermediaryBankName">The correspondent bank by name. Optional.</param>
/// <param name="IntermediaryBankAccount">
/// The beneficiary bank's account at the correspondent bank. Optional.
/// </param>
/// <param name="Details">
/// Detalji plaćanja — the free text that reaches the beneficiary, up to 140 characters. The nearest
/// thing a foreign order has to a svrha doznake, since it carries no poziv na broj.
/// </param>
/// <param name="OwnReference">Broj naloga — our own number for the order. Optional, up to 20 characters.</param>
/// <param name="CreditApplicationNumber">
/// Broj kreditne prijave — the registration number of the foreign loan this payment is made under,
/// when it is one. Optional.
/// </param>
/// <param name="CreditApplicationYear">The year of <paramref name="CreditApplicationNumber"/>.</param>
/// <param name="SpecialMark">Posebna oznaka — blank, or 1 to 7.</param>
/// <param name="PaymentInstrument">Instrument plaćanja — blank, 1 or 2.</param>
public sealed record ForeignPaymentOrder(
    string OrderingRegistrationNumber,
    string OrderingPlace,
    string BeneficiaryName,
    string BeneficiaryPlace,
    string BeneficiaryCountryCode,
    string BeneficiaryAccount,
    decimal Amount,
    string CurrencyCode,
    DateOnly ValueDate,
    ForeignCoverBasis CoverBasis,
    ForeignChargeOption Charges,
    IReadOnlyList<ForeignPaymentStatistic> Statistics,
    string? OrderingAccount = null,
    string? BeneficiaryAddress = null,
    string? BeneficiaryContact = null,
    string? BeneficiaryBankSwift = null,
    string? BeneficiaryBankName = null,
    string? BeneficiaryBankContact = null,
    string? IntermediaryBankSwift = null,
    string? IntermediaryBankName = null,
    string? IntermediaryBankAccount = null,
    string? Details = null,
    string? OwnReference = null,
    int? CreditApplicationNumber = null,
    int? CreditApplicationYear = null,
    int? SpecialMark = null,
    int? PaymentInstrument = null);
