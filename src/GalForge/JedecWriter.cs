using System.Globalization;
using System.Text;

namespace GalForge;

/// <summary>
/// Stage 4: render a <see cref="FuseMapBase"/> as JEDEC (.jed) text. The '*' field
/// area -- the part a device programmer actually reads -- is emitted byte-for-byte
/// the way WinCUPL lays it out, so the two tools' hex areas diff clean:
///
///   *QPnn         pin count   (trailing space, as WinCUPL writes it)
///   *QFnnnn       fuse count  (trailing space)
///   *G0           security fuse off (trailing space)
///   *F0           default fuse state (trailing space)
///   *Lnnnnn bits  fuses in 32-fuse, 32-aligned chunks over the whole map;
///                 all-zero chunks omitted; 5-digit address; final chunk may be short
///   *Chhhh        fuse checksum (upper-case, 16-bit, 8 fuses/byte LSB-first)
///   *             closing field, immediately followed by ETX
///
/// One rule -- "tile the linear fuse map into 32-fuse chunks, skip all-zero
/// chunks" -- reproduces WinCUPL's logic AND architecture records for every
/// supported family, because each family's region bases (XOR/SIG/AC1/PT/SYN/
/// AC0 on the V8s; S bits and UES on the 22V10) already fall on the same fuse
/// numbers in the flattened map.
///
/// The free-text header before the first '*' stays GalForge's own (it is not a
/// '*' line). The transmission checksum after ETX therefore differs from WinCUPL,
/// which is correct: it is a checksum over the entire transmission, header and all.
/// Line endings are CRLF throughout, with a single trailing LF after the
/// transmission checksum -- matching WinCUPL.
/// </summary>
internal static class JedecWriter
{
    private const char Stx = '\u0002';
    private const char Etx = '\u0003';
    private const string Nl = "\r\n";
    private const int Chunk = 32;   // fuses per *L record, 32-aligned (WinCUPL convention)

    public static string Write(FuseMapBase map, TargetDevice device, PldDocument doc)
    {
        var sb = new StringBuilder();

        // Design-specification note: free text after STX, before the first '*'.
        sb.Append(Stx).Append(Nl);
        sb.Append("Used Program:   GalForge").Append(Nl);
        if (doc.Title.Length > 0)
            sb.Append("Name:           ").Append(doc.Title).Append(Nl);
        sb.Append("Device:         ").Append(device.Name).Append(Nl);

        // PIN declarations from the .pld source, so downstream tools (a
        // simulator's GAL symbol, a chip-label printer) can recover signal names --
        // standard JEDEC has no pin-name field. This is free header text: it
        // affects only the transmission checksum (recomputed below), never the
        // fuse area or the *C fuse checksum, so the '*' field area still diffs
        // byte-identical against WinCUPL. Active-low pins keep CUPL's leading
        // '!'. Wrapped lines
        // each repeat the "Pins:" label so readers can parse line-by-line.
        AppendPinLines(sb, doc);

        sb.Append(Nl);

        // Global fields, in WinCUPL order: QP, QF, G, F (each with a trailing space).
        sb.Append("*QP").Append(device.PinCount).Append(' ').Append(Nl);
        sb.Append("*QF").Append(device.FuseCount).Append(' ').Append(Nl);
        sb.Append("*G0 ").Append(Nl);
        sb.Append("*F0 ").Append(Nl);

        // Fuse array: 32-fuse, 32-aligned chunks over the whole map; chunks that
        // are entirely default (0) are omitted; the final chunk may be short.
        byte[] all = map.ToLinear();
        for (int baseAddr = 0; baseAddr < all.Length; baseAddr += Chunk)
        {
            int len = System.Math.Min(Chunk, all.Length - baseAddr);
            if (AnySet(all, baseAddr, len))
                AppendChunk(sb, baseAddr, all, len);
        }

        // Fuse checksum: 16-bit sum of all fuses packed 8 per byte, LSB first.
        sb.Append("*C")
          .Append(FuseChecksum(all).ToString("X4", CultureInfo.InvariantCulture))
          .Append(Nl);

        // Closing field: ETX immediately follows the '*', no line break between.
        sb.Append('*').Append(Etx);

        // Transmission checksum: 16-bit sum of every byte from STX..ETX inclusive.
        sb.Append(TransmissionChecksum(sb).ToString("X4", CultureInfo.InvariantCulture));
        sb.Append('\n');               // single trailing LF, as WinCUPL writes

        return sb.ToString();
    }

    /// <summary>
    /// Emit the PIN declarations as "Pins:" header lines, sorted by pin
    /// number, each token "number:name" (leading '!' preserved for
    /// active-low). Long pin sets wrap onto further lines that repeat the
    /// "Pins:" label, keeping every line independently parseable. Emits
    /// nothing when the source declared no pins.
    /// </summary>
    private static void AppendPinLines(StringBuilder sb, PldDocument doc)
    {
        if (doc.Pins.Count == 0) return;

        var pins = new System.Collections.Generic.List<PinDeclaration>(doc.Pins);
        pins.Sort((a, b) => a.Number.CompareTo(b.Number));

        const string label = "Pins:           ";   // value column aligned with the lines above
        const int wrapColumn = 76;

        var line = new StringBuilder(label);
        foreach (PinDeclaration pin in pins)
        {
            string token = pin.Number.ToString(CultureInfo.InvariantCulture)
                + ":" + (pin.ActiveLow ? "!" : "") + pin.Name;
            if (line.Length > label.Length && line.Length + 1 + token.Length > wrapColumn)
            {
                sb.Append(line).Append(Nl);
                line = new StringBuilder(label);
            }
            if (line.Length > label.Length) line.Append(' ');
            line.Append(token);
        }
        if (line.Length > label.Length) sb.Append(line).Append(Nl);
    }

    private static void AppendChunk(StringBuilder sb, int addr, byte[] bits, int count)
    {
        sb.Append("*L").Append(addr.ToString("D5", CultureInfo.InvariantCulture)).Append(' ');
        for (int i = 0; i < count; i++) sb.Append((char)('0' + bits[addr + i]));
        sb.Append(Nl);
    }

    private static bool AnySet(byte[] bits, int start, int count)
    {
        for (int i = 0; i < count; i++)
            if (bits[start + i] != 0) return true;
        return false;
    }

    private static int FuseChecksum(byte[] all)
    {
        int sum = 0;
        for (int i = 0; i < all.Length; i++)
            if (all[i] != 0) sum += 1 << (i & 7);
        return sum & 0xFFFF;
    }

    private static int TransmissionChecksum(StringBuilder sb)
    {
        int sum = 0;
        for (int i = 0; i < sb.Length; i++) sum += sb[i];
        return sum & 0xFFFF;
    }
}