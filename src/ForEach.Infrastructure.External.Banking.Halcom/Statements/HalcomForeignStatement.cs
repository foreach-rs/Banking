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
/// **The tags are scanned, not matched by a pattern.** The file is XML-shaped without being XML —
/// no root, no declaration — and a regular expression with a lazy body and a backreference is
/// quadratic on a file that never closes its tags: half a megabyte of <c>&lt;KONTO&gt;</c> openers
/// timed out rather than answering. A linear scan reads the same records from a well-formed file
/// and answers "no movements" to the pathological one in milliseconds.
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

    /// <summary>The three record kinds this reader knows. Anything else is skipped.</summary>
    private static readonly HashSet<string> RecordTags =
        new(StringComparer.Ordinal) { "CLIENTPROFILE", "KONTO", "PPTPROMET" };

    internal static HalcomStatementRead Read(string content)
    {
        var records = Records(content);

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

        // One balance record describes one movement. Two movements sharing a KONTO have one
        // before/after pair between them, and a pair says which way the *net* went — not which
        // way each of them did. Booking both with the net direction is a payment out recorded as
        // money in, so a shared group refuses the file; see below.
        var sharing = movements
            .Select(movement => movement.GetValueOrDefault("P_GI"))
            .Where(group => !string.IsNullOrEmpty(group))
            .GroupBy(group => group, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key!)
            .ToList();

        if (sharing.Count > 0)
        {
            return new HalcomStatementRead(
                HalcomStatementKind.ForeignStatement, [],
                "Više stavki deviznog izvoda deli isti KONTO zapis (" + string.Join(", ", sharing)
                + "), pa se smer knjiženja pojedinačne stavke ne može utvrditi. Izvod se ne uvozi.");
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

    // ---- the scan ------------------------------------------------------------------------------

    /// <summary>One tag in the text: its name, whether it closes, and where it starts and ends.</summary>
    private readonly record struct Tag(string Name, bool Closing, int Start, int End);

    /// <summary>
    /// The records in file order, each with its fields.
    ///
    /// A record is a known opening tag and the first closing tag of the same name after it — the
    /// same reading a lazy pattern would give — and an opening tag that is never closed is skipped
    /// rather than searched for again from every later position. The tags are listed once and the
    /// closers looked up by name, which is what keeps a file of nothing but openers linear.
    /// </summary>
    private static List<(string Tag, Dictionary<string, string> Fields)> Records(string content)
    {
        var tags = Tags(content);
        var closers = Closers(tags);

        var records = new List<(string Tag, Dictionary<string, string> Fields)>();

        for (var index = 0; index < tags.Count; index++)
        {
            var tag = tags[index];

            if (tag.Closing || !RecordTags.Contains(tag.Name)) continue;

            var close = NextCloser(closers, tag.Name, index);

            if (close < 0) continue;

            records.Add((tag.Name, Fields(content, tags, index + 1, close)));

            // Everything up to the closer belongs to this record, so the scan resumes after it.
            index = close;
        }

        return records;
    }

    /// <summary>
    /// The fields between two tag positions: every opening tag paired with the first closer of the
    /// same name before the record ends, its value trimmed, and blank values left out — a present
    /// but empty element says nothing.
    /// </summary>
    private static Dictionary<string, string> Fields(string content, List<Tag> tags, int from, int to)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        var closers = Closers(tags, from, to);

        for (var index = from; index < to; index++)
        {
            var tag = tags[index];

            if (tag.Closing) continue;

            var close = NextCloser(closers, tag.Name, index);

            if (close < 0) continue;

            var value = content[tag.End..tags[close].Start].Trim();

            if (value.Length > 0) fields[tag.Name] = value;

            index = close;
        }

        return fields;
    }

    /// <summary>
    /// Every tag in the text, in order. A tag is <c>&lt;</c>, an optional <c>/</c>, a name of
    /// letters, digits and underscores, then <c>&gt;</c>; a <c>&lt;</c> that is not one is text.
    /// </summary>
    private static List<Tag> Tags(string content)
    {
        var tags = new List<Tag>();

        var at = 0;

        while ((at = content.IndexOf('<', at)) >= 0)
        {
            var cursor = at + 1;

            var closing = cursor < content.Length && content[cursor] == '/';

            if (closing) cursor++;

            var nameStart = cursor;

            while (cursor < content.Length && IsNameCharacter(content[cursor], cursor == nameStart))
                cursor++;

            if (cursor > nameStart && cursor < content.Length && content[cursor] == '>')
            {
                tags.Add(new Tag(content[nameStart..cursor], closing, at, cursor + 1));

                at = cursor + 1;
            }
            else
            {
                at++;
            }
        }

        return tags;

        static bool IsNameCharacter(char c, bool first) =>
            char.IsAsciiLetter(c) || c == '_' || (!first && char.IsAsciiDigit(c));
    }

    /// <summary>The positions of the closing tags, by name, in order — over the whole list.</summary>
    private static Dictionary<string, List<int>> Closers(List<Tag> tags) =>
        Closers(tags, 0, tags.Count);

    /// <summary>The positions of the closing tags, by name, in order, within a range.</summary>
    private static Dictionary<string, List<int>> Closers(List<Tag> tags, int from, int to)
    {
        var closers = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        for (var index = from; index < to; index++)
        {
            if (!tags[index].Closing) continue;

            if (!closers.TryGetValue(tags[index].Name, out var positions))
                closers[tags[index].Name] = positions = [];

            positions.Add(index);
        }

        return closers;
    }

    /// <summary>
    /// The first closing tag of this name after a position, or −1. A binary search over the
    /// positions listed once, so finding every record's end costs the file a logarithm each rather
    /// than a scan each.
    /// </summary>
    private static int NextCloser(Dictionary<string, List<int>> closers, string name, int after)
    {
        if (!closers.TryGetValue(name, out var positions)) return -1;

        var found = positions.BinarySearch(after + 1);

        if (found < 0) found = ~found;

        return found < positions.Count ? positions[found] : -1;
    }
}
