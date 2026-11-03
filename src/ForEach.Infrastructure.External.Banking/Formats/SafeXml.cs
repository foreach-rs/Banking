using System.Xml;
using System.Xml.Linq;

namespace ForEach.Infrastructure.External.Banking.Formats;

/// <summary>
/// Loading XML that arrived from outside, with the document type declaration refused.
///
/// **Why this is a named helper rather than <see cref="XDocument.Parse(string)"/>.** That call is
/// safe today — <c>XmlReader</c>'s default <see cref="DtdProcessing"/> is
/// <see cref="DtdProcessing.Prohibit"/>, so a <c>&lt;!DOCTYPE&gt;</c> throws — but it is safe by
/// *default* rather than by decision, and a default is what somebody changes when a file fails to
/// load. A bank statement is a file a person uploads from a machine we do not control; an entity
/// declaration in one is at best a broken export and at worst an XXE read of whatever the server
/// can reach. Saying so here means the guarantee has a place to be tested.
///
/// No Serbian bank export uses a DTD. Refusing one costs nothing and closes the case.
/// </summary>
public static class SafeXml
{
    /// <summary>
    /// The document, or null when the text is not XML — or carries a DTD.
    ///
    /// Null rather than an exception because every caller here is a reader whose contract is that
    /// an unrecognisable file is answered rather than thrown at: the person at the import screen
    /// chose the wrong file, and that is a sentence rather than a stack trace.
    /// </summary>
    public static XDocument? Load(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,

            // Nothing is resolved from outside the string we were handed. Belt and braces: with
            // DTDs prohibited there is no declaration left to resolve, but an XmlResolver of null
            // means a future setting change cannot quietly re-open the door.
            XmlResolver = null,
            IgnoreWhitespace = false
        };

        try
        {
            using var reader = XmlReader.Create(new StringReader(text), settings);

            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
