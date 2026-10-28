using System;
using System.Collections.Generic;

namespace GalForge;

/// <summary>
/// A programmable target: name, JEDEC fuse count (QF), and package pin count
/// (QP). Family geometry lives on the subclasses -- the 16V8 and 20V8 share
/// one SYN/AC0 region layout (<see cref="GalV8Device"/>); the 22V10 has
/// its own scheme (<see cref="Gal22V10"/>) with per-OLMC S0/S1 bits, variable
/// term counts, and global AR/SP rows instead of architecture words.
/// </summary>
internal abstract class TargetDevice
{
    public abstract string Name { get; }
    public abstract int FuseCount { get; }   // JEDEC QF
    public abstract int PinCount { get; }    // JEDEC QP (package pin count)

    public static TargetDevice? Resolve(string name, List<string> errors)
    {
        string normalized = (name ?? "").Trim().ToUpperInvariant().Replace(" ", "");

        if (normalized.Length == 0)
        {
            errors.Add("No target device given (-d) or found in the .pld header.");
            return null;
        }

        // Match the family by its part-number core. This accepts every CUPL/WinCUPL
        // spelling and architecture suffix that selects the same fuse map: bare names
        // (G16V8), vendor prefixes (GAL/ATF/P), and mode suffixes (S/A/AS/MS) such as
        // G16V8S, G20V8A, G20V8AS. The suffix only tells CUPL which mode to auto-pick;
        // GalForge decides the mode itself, so the same fuse map applies either way.
        // "22V10" likewise covers g22v10, GAL22V10, and ATF22V10 (fuse-compatible).
        if (normalized.Contains("16V8"))
            return new Gal16V8();
        if (normalized.Contains("20V8"))
            return new Gal20V8();
        if (normalized.Contains("22V10"))
            return new Gal22V10();

        errors.Add($"Unsupported device: '{name}'. Only the 16V8, 20V8 and 22V10 families are supported.");
        return null;
    }
}

/// <summary>
/// The 16V8/20V8 family layout: a uniform 64-row AND array followed by the
/// XOR (polarity), signature, AC1, product-term-disable (PT), and SYN/AC0
/// regions. All constants are the published GAL fuse-map layout:
/// 16V8 array 0..2047, XOR@2048, signature@2056,
/// AC1@2120, PT@2128, SYN@2192, AC0@2193 (2194 fuses); 20V8 shifts up for
/// its wider array.
/// </summary>
internal abstract class GalV8Device : TargetDevice
{
    // AND-array geometry.
    public abstract int Rows { get; }        // product-term rows
    public abstract int Columns { get; }     // fuses per row
    public int LogicSize => Rows * Columns;

    // Region base addresses (JEDEC fuse numbers).
    public abstract int XorAddr { get; }
    public abstract int SigAddr { get; }
    public abstract int Ac1Addr { get; }
    public abstract int PtAddr { get; }
    public abstract int SynAddr { get; }
    public abstract int Ac0Addr { get; }

    // Region sizes (shared by 16V8 and 20V8).
    public const int XorSize = 8;
    public const int SigSize = 64;
    public const int Ac1Size = 8;
    public const int PtSize = 64;
}

internal sealed class Gal16V8 : GalV8Device
{
    public override string Name => "GAL16V8";
    public override int FuseCount => 2194;
    public override int PinCount => 20;
    public override int Rows => 64;
    public override int Columns => 32;
    public override int XorAddr => 2048;
    public override int SigAddr => 2056;
    public override int Ac1Addr => 2120;
    public override int PtAddr => 2128;
    public override int SynAddr => 2192;
    public override int Ac0Addr => 2193;
}

internal sealed class Gal20V8 : GalV8Device
{
    public override string Name => "GAL20V8";
    public override int FuseCount => 2706;
    public override int PinCount => 24;
    public override int Rows => 64;
    public override int Columns => 40;
    public override int XorAddr => 2560;
    public override int SigAddr => 2568;
    public override int Ac1Addr => 2632;
    public override int PtAddr => 2640;
    public override int SynAddr => 2704;
    public override int Ac0Addr => 2705;
}

/// <summary>
/// GAL22V10 / ATF22V10 (fuse-compatible; one part covers both). No global
/// modes: each of the ten macrocells is configured by its own S0/S1 pair,
/// input routing is fixed, term counts vary per macrocell, and two global
/// product-term rows implement asynchronous reset (AR) and synchronous
/// preset (SP).
///
/// Geometry provenance: the data sheet's fuse map and logic diagram,
/// validated fuse-by-fuse against WinCUPL 5.0a .jed golds for a
/// combinational and a registered reference design (see
/// docs/GAL22V10_Geometry.md). Layout:
///
///   fuse n = array row n/44, column n%44
///   row 0             AR product term
///   rows 1..130       ten OLMC blocks in pin order 23 down to 14, each
///                     [1 OE row, then its logic rows]; logic term counts
///                     8,10,12,14,16,16,14,12,10,8
///   row 131           SP product term
///   5808..5827        S0/S1 pairs, pin 23 first (S0 even, S1 odd)
///   5828..5891        UES (Lattice electronic signature; WinCUPL emits
///                     QF5892 -- the array is identical to a QF5828 map)
/// </summary>
internal sealed class Gal22V10 : TargetDevice
{
    public override string Name => "GAL22V10";
    public override int FuseCount => 5892;
    public override int PinCount => 24;

    public const int Rows = 132;
    public const int Columns = 44;
    public const int LogicSize = Rows * Columns;   // 5808

    public const int ArRow = 0;
    public const int SpRow = 131;

    public const int FirstOlmcPin = 14;
    public const int LastOlmcPin = 23;
    public const int OlmcCount = 10;

    public const int SBitsAddr = 5808;   // S0/S1 pairs, OLMC index = 23 - pin
    public const int SBitsSize = 20;
    public const int UesAddr = 5828;
    public const int UesSize = 64;

    /// <summary>First row of each OLMC block (the OE row); index = 23 - pin.</summary>
    public static readonly int[] BlockStartRow = { 1, 10, 21, 34, 49, 66, 83, 98, 111, 122 };

    /// <summary>Logic terms per OLMC (excluding the OE row); index = 23 - pin.</summary>
    public static readonly int[] BlockTermCount = { 8, 10, 12, 14, 16, 16, 14, 12, 10, 8 };

    /// <summary>Pin -> true column (complement = +1); index = pin - 1; -1 = GND/VCC.</summary>
    public static readonly int[] PinToFuse =
        { 0, 4, 8, 12, 16, 20, 24, 28, 32, 36, 40, -1, 42, 38, 34, 30, 26, 22, 18, 14, 10, 6, 2, -1 };
}
