using System.Text;
using System.Xml;

namespace ForEach.Infrastructure.External.Banking.Rol.Payments;

/// <summary>
/// The envelope both ROL payment orders are written in.
///
/// Shared because it is the one thing the dinar and devizni documents genuinely agree about, and
/// because getting it wrong fails the same way for both: the bank's importer reads **UTF-16 LE with
/// a byte-order mark**, CRLF line endings, tab indentation and **no XML declaration**. That is not
/// cosmetic and not a preference — a UTF-8 file looks perfect in an editor and is refused on upload,
/// and an importer reading the first bytes expecting <c>&lt;Nalog</c> or <c>&lt;Client</c> is one a
/// declaration would break.
///
/// The field sets differ completely and are not shared; see each writer.
/// </summary>
internal static class RolDocument
{
    /// <summary>UTF-16 LE. The bytes are settled by the writers' <c>Bytes</c> methods.</summary>
    internal static readonly Encoding FileEncoding =
        new UnicodeEncoding(bigEndian: false, byteOrderMark: true);

    /// <summary>
    /// A batch, laid out exactly as the bank's own bundle examples are.
    ///
    /// Not pretty-printed, and that is the point: their file opens
    /// <c>&lt;Client&gt;&lt;PaketZaSlanje&gt;</c> on one line, puts each <c>&lt;Nalog&gt;</c> at
    /// column zero, and closes <c>&lt;/PaketZaSlanje&gt;&lt;/Client&gt;</c> run together. An
    /// indented wrapper is identical to an XML parser and different on the wire, and this is a file
    /// that moves money into an importer nobody here has seen the inside of — the same reason the
    /// empty elements are written paired rather than self-closing.
    /// </summary>
    internal static string Batch(IReadOnlyList<(string FileName, Action<XmlWriter> Body)> orders)
    {
        var text = new StringBuilder();

        text.Append("<Client><PaketZaSlanje>\r\n");

        foreach (var (fileName, body) in orders)
        {
            text.Append(Write(writer =>
            {
                writer.WriteStartElement("Nalog");
                writer.WriteAttributeString("ImeFajla", fileName);
                body(writer);
                writer.WriteEndElement();
            }));

            text.Append("\r\n");
        }

        text.Append("</PaketZaSlanje></Client>");

        return text.ToString();
    }

    internal static string Write(Action<XmlWriter> body)
    {
        var text = new StringBuilder();

        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = true,
            IndentChars = "\t",
            NewLineChars = "\r\n",

            // Only tells the writer what it may emit; the bytes come from FileEncoding above. Kept
            // the same so the two cannot disagree about what is representable.
            Encoding = FileEncoding
        };

        using (var writer = XmlWriter.Create(text, settings))
        {
            body(writer);
            writer.Flush();
        }

        return text.ToString();
    }
}
