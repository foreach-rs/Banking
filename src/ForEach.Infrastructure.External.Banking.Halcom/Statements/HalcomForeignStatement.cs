using System.Text.RegularExpressions;
using ForEach.Infrastructure.External.Banking.Formats;
using ForEach.Infrastructure.External.Banking.Statements;

namespace ForEach.Infrastructure.External.Banking.Halcom.Statements;

/// <summary>
/// Reads a Halcom **devizni** statement — the tag-based export that opens
/// <c>&lt;CLIENTPROFILE&gt;</c>.
///
/// **Derived from a real file, not from a specification**, because none was available. That is the
/// same footing the fixed-width layout is on, and it means the limits below are real rather than
/// cautious.
///
/// **The file has no debit/credit flag this reader trusts.** It carries several codes that might be
/// one — <c>IDB</c>, <c>TI</c>, <c>BTO</c> — and the only sample available holds a single
/// transaction, which is a credit. There is no way to learn the debit encoding from a file that
/// contains no debit, and guessing produces a reader that books every outgoing payment as money in:
/// silently, and in the direction that flatters the balance.
///
/// **So the direction is derived rather than decoded.** Each movement appears twice — as a
/// <c>&lt;PPTPROMET&gt;</c> carrying the detail and a <c>&lt;KONTO&gt;</c> carrying the running
/// balance — joined by <c>P_GI</c> / <c>K_GI</c>. The KONTO states the balance before
/// (<c>AMM</c>), the amount (<c>VTO</c>) and the balance after (<c>PST</c>), so which way the money
/// went is arithmetic: <c>PST &gt; AMM</c> is a credit and <c>PST &lt; AMM</c> is a debit.
///
/// **A line whose direction cannot be derived refuses the whole file.** Not the line — the file.
/// Importing the movements that could be resolved and reporting success is how a payment goes
/// missing, and this is a format nobody has verified against a second sample.
///
/// **What is deliberately not read**: the poziv na broj. <c>OZK</c> carries free text that in this
/// sample happens to hold a SWIFT-style <c>/INV/</c> reference, and pulling an invoice number out
/// of it on one example would hand the matcher a value to match invoices against. A null reference
/// falls through to matching by payer and amount, which is the safe direction to be wrong in.
/// There is no statement number in the format either.
/// </summary>
public static class HalcomForeignStatement
{
    /// <summary>The marker that opens one of these files.</summary>
    internal const string Marker = "<CLIENTPROFILE>";

    private static readonly Regex RecordPattern = new(
        @"<(?<tag>CLIENTPROFILE|KONTO|PPTPROMET)>(?<body>.*?)</\k<tag>>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(2));

    private static readonly Regex FieldPattern = new(
        @"<(?<name>[A-Za-z_][A-Za-z0-9_]*)>(?<value>.*?)</\k<name>>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(2));

    internal static HalcomStatementRead Read(string content)
    {
        var records = new List<(string Tag, Dictionary<string, string> Fields)>();

        foreach (Match record in RecordPattern.Matches(content))
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (Match field in FieldPattern.Matches(record.Groups["body"].Value))
            {
                var value = field.Groups["value"].Value.Trim();

                if (value.Length > 0) fields[field.Groups["name"].Value] = value;
            }

            records.Add((record.Groups["tag"].Value, fields));
        }

        var movements = records.Where(r => r.Tag is "PPTPROMET").Select(r => r.Fields).ToList();
        var balances = records.Where(r => r.Tag is "KONTO").Select(r => r.Fields).ToList();

        if (movements.Count == 0)
        {
            return new HalcomStatementRead(
                HalcomStatementKind.ForeignStatement, [],
                "Devizni izvod ne sadrži nijednu stavku prometa.");
        }

        // The join between a movement and its running balance. One index rather than a scan per
        // line, and the first wins where a key repeats — a duplicate would otherwise pick whichever
        // record happened to be last.
        var byGroup = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        foreach (var balance in balances)
        {
            if (balance.GetValueOrDefault("K_GI") is { Length: > 0 } key)
                byGroup.TryAdd(key, balance);
        }

        var entries = new List<StatementEntry>();

        foreach (var movement in movements)
        {
            var group = movement.GetValueOrDefault("P_GI");

            var balance = group is { Length: > 0 } && byGroup.TryGetValue(group, out var found)
                ? found
                : null;

            var before = BankValues.DisplayAmount(balance?.GetValueOrDefault("AMM"));
            var after = BankValues.DisplayAmount(balance?.GetValueOrDefault("PST"));
            var amount = BankValues.DisplayAmount(movement.GetValueOrDefault("VBD"));

            // The refusal that keeps this reader honest. Without the pair there is nothing to
            // derive the direction from, and this format states none.
            if (before is null || after is null || amount is null || before == after)
            {
                return new HalcomStatementRead(
                    HalcomStatementKind.ForeignStatement, [],
                    "Za stavku deviznog izvoda nije moguće utvrditi smer knjiženja "
                    + "(nedostaje odgovarajući KONTO zapis sa stanjem pre i posle). "
                    + "Izvod se ne uvozi.");
            }

            var booked = BankValues.Date(movement.GetValueOrDefault("DOB"), BankValues.SerbianDate);

            entries.Add(new StatementEntry(
                Amount: Math.Abs(amount.Value),
                Direction: after > before ? StatementDirection.Credit : StatementDirection.Debit,
                BookingDate: booked ?? default,
                ValueDate: BankValues.Date(movement.GetValueOrDefault("DOV"), BankValues.SerbianDate),

                // Deliberately null — see the class summary.
                Reference: null,
                Description: movement.GetValueOrDefault("OZK"),
                CounterpartyName: movement.GetValueOrDefault("NPO"),

                // The devizni movement names no counterparty account in the sample; INS is the
                // bank's own instruction reference and is what makes a re-import detectable.
                CounterpartyAccount: null,
                BankReference: movement.GetValueOrDefault("INS")));
        }

        var profile = records.FirstOrDefault(r => r.Tag is "CLIENTPROFILE").Fields;

        var account = movements[0].GetValueOrDefault("SK")
                      ?? balances.FirstOrDefault()?.GetValueOrDefault("STK")
                      ?? profile?.GetValueOrDefault("C_AI");

        var statement = new BankStatement(
            AccountNumber: account,
            Currency: BankValues.Currency(
                movements[0].GetValueOrDefault("OV")
                ?? balances.FirstOrDefault()?.GetValueOrDefault("OV")),

            // The format carries no izvod number. Null rather than a date standing in for one:
            // a number that is not the bank's is one somebody will quote back at them.
            Number: null,
            StatementDate: entries.Count > 0 ? entries.Max(entry => entry.BookingDate) : null,

            // The balance before the first movement and after the last, in file order. They are
            // the KONTO chain's own ends, so `Reconciles` still asks a real question: it fails if a
            // direction was derived wrongly or a movement had no balance record.
            OpeningBalance: BankValues.DisplayAmount(balances.FirstOrDefault()?.GetValueOrDefault("AMM")),
            ClosingBalance: BankValues.DisplayAmount(balances.LastOrDefault()?.GetValueOrDefault("PST")),
            Entries: entries);

        return new HalcomStatementRead(HalcomStatementKind.ForeignStatement, [statement]);
    }
}
