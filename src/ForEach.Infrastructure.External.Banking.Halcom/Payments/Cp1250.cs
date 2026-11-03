using System.Text;

namespace ForEach.Infrastructure.External.Banking.Halcom.Payments;

/// <summary>
/// Windows-1250, encoded from a table rather than from <c>Encoding.GetEncoding(1250)</c>.
///
/// The same reason the host's <c>CsvEncoding</c> decodes from a table: that call needs
/// <c>CodePagesEncodingProvider</c> registered and the encoding data present, and this library may
/// be used from a trimmed WebAssembly build where neither is guaranteed. A
/// <see cref="NotSupportedException"/> in the browser, on an export that works on the developer's
/// machine, is a worse failure than the one it would be fixing.
///
/// **A character the code page cannot represent is refused rather than replaced.** Windows-1250
/// has no Cyrillic, and a bank file with a question mark where a beneficiary's name should be is a
/// payment somebody has to explain. The caller is told which character and can fix it while the
/// order is still a draft.
/// </summary>
internal static class Cp1250
{
    /// <summary>
    /// Bytes 0x80–0xFF of Windows-1250. The Serbian letters this exists for are here: Š 0x8A,
    /// Ž 0x8E, š 0x9A, ž 0x9E, Đ 0xD0, đ 0xF0, Ć 0xC6, ć 0xE6, Č 0xC8, č 0xE8.
    /// </summary>
    private const string UpperHalf =
        "€�‚�„…†‡"
        + "�‰Š‹ŚŤŽŹ"
        + "�‘’“”•–—"
        + "�™š›śťžź"
        + " ˇ˘Ł¤Ą¦§"
        + "¨©Ş«¬­®Ż"
        + "°±˛ł´µ¶·"
        + "¸ąş»Ľ˝ľż"
        + "ŔÁÂĂÄĹĆÇ"
        + "ČÉĘËĚÍÎĎ"
        + "ĐŃŇÓÔŐÖ×"
        + "ŘŮÚŰÜÝŢß"
        + "ŕáâăäĺćç"
        + "čéęëěíîď"
        + "đńňóôőö÷"
        + "řůúűüýţ˙";

    private static readonly Dictionary<char, byte> Reverse = Build();

    private static Dictionary<char, byte> Build()
    {
        var map = new Dictionary<char, byte>();

        for (var i = 0; i < UpperHalf.Length; i++)
        {
            // The five undefined positions map to the replacement character; they are not a
            // character anybody can type and must not become an encodable byte.
            if (UpperHalf[i] is '�') continue;

            map[UpperHalf[i]] = (byte)(0x80 + i);
        }

        return map;
    }

    /// <summary>Whether every character can be written in this code page.</summary>
    internal static char? FirstUnrepresentable(string text) =>
        text.FirstOrDefault(c => c >= 0x80 && !Reverse.ContainsKey(c)) is var found && found != '\0'
            ? found
            : null;

    internal static byte[] Encode(string text)
    {
        var bytes = new byte[text.Length];

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            bytes[i] = c < 0x80
                ? (byte)c
                : Reverse.TryGetValue(c, out var mapped) ? mapped : (byte)'?';
        }

        return bytes;
    }

    /// <summary>The DOS end-of-file marker the format ends with.</summary>
    internal const byte EndOfFile = 0x1A;

    internal static byte[] WithTrailer(string text) =>
        [.. Encode(text), EndOfFile];

    internal static Encoding Utf8 => Encoding.UTF8;
}
