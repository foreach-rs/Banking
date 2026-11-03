using ForEach.Infrastructure.External.Banking.Statements;

namespace ForEach.Infrastructure.External.Banking.Otp.Statements;

/// <summary>What shape a file turned out to be.</summary>
public enum OtpStatementFormat
{
    /// <summary>Nothing recognised it.</summary>
    Unknown,

    /// <summary>ISO 20022 camt.053 — the standard, and what the export is expected to be.</summary>
    Camt053
}

/// <summary>What an OTP upload was read as, and what it held.</summary>
/// <param name="Format">
/// Which reader answered. Carried out to the host because "we could not read this file" and "this
/// file had no transactions" are different things to tell somebody standing at an import screen,
/// and both arrive as an empty statement otherwise.
/// </param>
/// <param name="Statement">
/// What was read — the first statement where the file holds several — or
/// <see cref="BankStatement.Empty"/> when nothing recognised the file.
/// </param>
public sealed record OtpStatementRead(OtpStatementFormat Format, BankStatement Statement)
{
    /// <summary>
    /// Every statement in the file, with the one <see cref="Statement"/> keeps as the first.
    ///
    /// A camt file can carry a week of izvodi at once, and a host reading only the first would
    /// import one day and report success over the other four. Empty when nothing was read.
    /// </summary>
    public IReadOnlyList<BankStatement> Statements { get; init; } = [Statement];

    /// <summary>A read of several statements, with the first one standing as <see cref="Statement"/>.</summary>
    public OtpStatementRead(OtpStatementFormat format, IReadOnlyList<BankStatement> statements)
        : this(format, statements.Count > 0 ? statements[0] : BankStatement.Empty)
    {
        Statements = statements;
    }
}

/// <summary>
/// Reading an account statement exported from OTP banka Srbija's Business E-bank.
///
/// **Why this project exists separately from the shared reader.** OTP's e-bank offers a statement
/// as pdf, xml, txt and MT940, and the same is true of every other bank with a different set. What
/// is *shared* is the standard: camt.053 is ISO 20022, and Intesa, Raiffeisen and the rest are
/// moving to it too — so the reader for it lives in `ForEach.Infrastructure.External.Banking` and
/// is not copied here. What lives here is what only OTP does.
///
/// **Today that is one thing: recognising the file.** A file carrying the camt namespace is handed
/// straight to the shared reader. The value of the seam is what happens when it turns out not to
/// be — a bank-specific shape gets its parser here, beside this comment, and no host and no other
/// bank changes.
///
/// **What OTP actually exports is not settled, and this class should not pretend otherwise.**
/// Their Business E-bank offers pdf, xml, txt and MT940, and two other things are now known: the
/// ROL format is published by Raiffeisen as the export *both* they and OTP produce, and OTP banka
/// Srbija is on Halcom's own list of banks running Hal E-Bank. So an "OTP export" may be camt.053,
/// ROL XML, or a Halcom file, depending on which product the client uses.
///
/// That is why the host does not ask this class first and stop: it tries every format it knows and
/// takes whichever recognises the file. This one stays because camt.053 is the standard the region
/// is moving to and because the seam is where an OTP-only shape would go — not because OTP has been
/// observed to produce camt.
///
/// **What has not been verified.** Nobody has run a real OTP export through this yet. The reader
/// underneath is written against the published standard rather than against a sample, and the two
/// agree until a bank does something the standard permits and the example did not show. The
/// reconciliation check on <see cref="BankStatement.Reconciles"/> is what catches that on the first
/// real file: if the entries do not account for the movement between the balances, something was
/// not understood, and that is worth refusing an import over rather than importing what was
/// recognised and reporting success.
/// </summary>
public static class OtpStatementReader
{
    /// <summary>
    /// The marker that says a file is camt: the ISO 20022 namespace, matched loosely so every
    /// version of it counts. Looked for as text rather than parsed first, because deciding what a
    /// file is should not depend on it being valid XML.
    /// </summary>
    private const string CamtNamespaceMarker = "camt.053";

    /// <summary>
    /// Reads whatever was uploaded, and says what it was.
    ///
    /// Never throws, for the reason the shared reader does not: a renamed PDF and a truncated
    /// download are both things a person does, and neither deserves a stack trace on an import
    /// screen.
    /// </summary>
    public static OtpStatementRead Read(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return NotRecognised;

        if (!content.Contains(CamtNamespaceMarker, StringComparison.OrdinalIgnoreCase))
            return NotRecognised;

        // Carries the namespace and still cannot be read — a download cut off halfway, an entry
        // with no amount. That is not a statement, and it must not come back as a camt file with
        // nothing in it: "no transactions" is what the host would say, and the person would
        // believe it.
        if (!Camt053Reader.TryReadAll(content, out var statements)) return NotRecognised;

        // Recognised as camt and still empty: a camt file with no entries is a real thing — a day
        // with no movement — so this stays Camt053 rather than falling back to Unknown. The host
        // can then say "no transactions" instead of "unreadable file", which are different
        // sentences to somebody who has just downloaded it.
        return new OtpStatementRead(OtpStatementFormat.Camt053, statements);
    }

    private static readonly OtpStatementRead NotRecognised =
        new(OtpStatementFormat.Unknown, Array.Empty<BankStatement>());
}
