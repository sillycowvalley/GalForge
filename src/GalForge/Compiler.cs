using System;
using System.Collections.Generic;

namespace GalForge;

/// <summary>
/// Stage 3: map parsed equations onto a device fuse map. Dispatches by
/// family: the 16V8/20V8 path lives here; the 22V10 is
/// <see cref="Compiler22V10"/>.
///
/// The 16V8/20V8 path covers all three GAL configurations, laid out as
/// WinCUPL lays them out:
///
///   - Logic starts all-1; a used product-term row keeps its 1s except the
///     connected input columns, which are cleared to 0. Every other row stays 0
///     (the writer omits all-zero rows).
///   - output pin -> OLMC index (pin - first OLMC pin); product term t -> row
///     ToOlmc[olmc] + t; literal -> column PinToFuse[pin-1], true line at col,
///     complement at col+1. The PinToFuse table is per-mode, because the GAL
///     re-routes feedback differently in each configuration.
///   - XOR[7-olmc] = 1 per active-high output; PT all 1 in every mode.
///
/// Output polarity is the XOR of the two CUPL notations: a '!' on the PIN
/// declaration and a '!' on the equation's left-hand side each flip it
/// (declared-low + plain equation = active low; both = active high again).
/// A literal referencing a declared-active-low signal likewise connects the
/// complement column (the pin carries the inverted sense), matching
/// WinCUPL's pin-polarity fold. All of it is WinCUPL-gold-validated: the
/// gal16v8_polarity gold proves the XOR-of-notations rule (including the
/// double-negation cancel), and the gal20v8_polarity gold proves the literal
/// fold for feedback from an active-low REGISTERED output, both within its
/// own .d equation and read by another equation.
///
/// Mode is selected automatically from what the design needs (the CUPL device
/// suffix is ignored, as before):
///
///   MODE 1 (simple)     SYN=1 AC0=0.  Chosen when nothing below applies.
///                       8 product terms per output. Spare OLMCs become
///                       inputs (AC1=1).
///   MODE 2 (complex)    SYN=1 AC0=1.  Forced by a per-macrocell .oe, by
///                       combinational feedback (an output used as an input),
///                       or by an input on a pin with no simple-mode feedback
///                       line (16V8: 15,16 ; 20V8: 18,19). AC1=1 for every
///                       cell. Row 0 of each used OLMC block is its
///                       output-enable term (all-1s = always enabled), rows
///                       1..7 are the logic, so 7 terms per output. The end
///                       macrocells (16V8: 12,19 ; 20V8: 15,22) have no
///                       feedback and cannot be used as inputs.
///   MODE 3 (registered) SYN=0 AC0=1.  Forced by any .d equation. Pin 1 is
///                       the global clock and pin 11 (16V8) / 13 (20V8) the
///                       global /OE; neither may appear in an equation.
///                       A registered output (AC1=0) owns all 8 rows of its
///                       block and is enabled by the global /OE, so .oe is
///                       not available on it. A combinational output inside
///                       registered mode (AC1=1) works like a complex-mode
///                       cell: OE term in row 0, 7 logic terms.
///
/// Feedback literals are programmed like any other pin literal; the per-mode
/// column tables already encode where each OLMC's feedback lands, matching
/// what WinCUPL emits (validated against WinCUPL .jed references for
/// all six sample designs).
///
/// The 22V10-only extensions .ar and .sp are rejected here with a clear
/// error. Still not handled (rejected by the parser): other output
/// extensions, set/range/index notation, and '$' preprocessor directives.
/// </summary>
internal static class Compiler
{
    private enum GalMode { Simple = 1, Complex = 2, Registered = 3 }

    // Row offset per OLMC index: OLMC 0 -> row 56 ... OLMC 7 -> row 0.
    private static readonly int[] ToOlmc = { 56, 48, 40, 32, 24, 16, 8, 0 };

    // PinToFuse per mode. Index = pin-1; -1 = no column for that pin in that
    // mode. Values are the device's fixed input-to-column routing (the logic
    // diagram in the GAL data sheet), proven against the WinCUPL golds in
    // examples/Basic/WinCUPL.
    private static readonly int[] PinToFuse16Mode1 =
        { 2, 0, 4, 8, 12, 16, 20, 24, 28, -1, 30, 26, 22, 18, -1, -1, 14, 10, 6, -1 };
    private static readonly int[] PinToFuse16Mode2 =
        { 2, 0, 4, 8, 12, 16, 20, 24, 28, -1, 30, -1, 26, 22, 18, 14, 10, 6, -1, -1 };
    private static readonly int[] PinToFuse16Mode3 =
        { -1, 0, 4, 8, 12, 16, 20, 24, 28, -1, -1, 30, 26, 22, 18, 14, 10, 6, 2, -1 };

    private static readonly int[] PinToFuse20Mode1 =
        { 2, 0, 4, 8, 12, 16, 20, 24, 28, 32, 36, -1, 38, 34, 30, 26, 22, -1, -1, 18, 14, 10, 6, -1 };
    private static readonly int[] PinToFuse20Mode2 =
        { 2, 0, 4, 8, 12, 16, 20, 24, 28, 32, 36, -1, 38, 34, -1, 30, 26, 22, 18, 14, 10, -1, 6, -1 };
    private static readonly int[] PinToFuse20Mode3 =
        { -1, 0, 4, 8, 12, 16, 20, 24, 28, 32, 36, -1, -1, 38, 34, 30, 26, 22, 18, 14, 10, 6, 2, -1 };

    public static FuseMapBase Compile(PldDocument doc, TargetDevice device, List<string> errors)
    {
        if (device is Gal22V10 g22)
            return Compiler22V10.Compile(doc, g22, errors);
        return CompileV8(doc, (GalV8Device)device, errors);
    }

    private static FuseMap CompileV8(PldDocument doc, GalV8Device device, List<string> errors)
    {
        var map = new FuseMap(device);   // Logic/Xor/Sig/Ac1/Pt all 0, Syn/Ac0 0

        bool is16 = device is Gal16V8;
        int firstOlmcPin = is16 ? 12 : 15;
        int lastOlmcPin = is16 ? 19 : 22;
        int clockPin = 1;                                    // CLK in mode 3
        int globalOePin = is16 ? 11 : 13;                    // /OE in mode 3
        int[] specialPins = is16 ? new[] { 15, 16 } : new[] { 18, 19 };
        int[] outputOnlyComplexPins = is16 ? new[] { 12, 19 } : new[] { 15, 22 };
        int cols = device.Columns;

        // name -> declaration (pin number + declared polarity)
        var declOf = new Dictionary<string, PinDeclaration>(StringComparer.OrdinalIgnoreCase);
        foreach (PinDeclaration p in doc.Pins)
        {
            if (!declOf.TryAdd(p.Name, p))
                errors.Add($"Pin name '{p.Name}' is declared more than once.");
        }

        // The global AR/SP rows exist only on the 22V10.
        foreach (Equation eq in doc.Equations)
        {
            if (eq.AsyncReset || eq.SyncPreset)
                errors.Add($"Output '{eq.Target}': .{(eq.AsyncReset ? "ar" : "sp")} is only supported on the GAL22V10.");
        }

        // Partition: output equations vs their .oe (output-enable) equations.
        var mains = new List<Equation>();
        var oes = new List<Equation>();
        foreach (Equation eq in doc.Equations)
        {
            if (eq.AsyncReset || eq.SyncPreset) continue;   // reported above
            (eq.OutputEnable ? oes : mains).Add(eq);
        }

        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Equation eq in mains) targets.Add(eq.Target);

        // ---- Mode selection ------------------------------------------------
        GalMode mode = GalMode.Simple;
        foreach (Equation eq in mains)
            if (eq.Registered) { mode = GalMode.Registered; break; }
        if (mode == GalMode.Simple)
        {
            bool forceComplex = oes.Count > 0;
            foreach (Equation eq in mains)
            {
                foreach (ProductTerm term in eq.Terms)
                {
                    foreach (string sig in term.Literals.Keys)
                    {
                        if (targets.Contains(sig))
                            forceComplex = true;             // combinational feedback
                        if (declOf.TryGetValue(sig, out PinDeclaration? d) && Array.IndexOf(specialPins, d.Number) >= 0)
                            forceComplex = true;             // input with no mode-1 feedback line
                    }
                }
            }
            if (forceComplex) mode = GalMode.Complex;
        }

        int[] pinToFuse = is16
            ? (mode == GalMode.Simple ? PinToFuse16Mode1 : mode == GalMode.Complex ? PinToFuse16Mode2 : PinToFuse16Mode3)
            : (mode == GalMode.Simple ? PinToFuse20Mode1 : mode == GalMode.Complex ? PinToFuse20Mode2 : PinToFuse20Mode3);

        // Architecture word; PT is all 1 in every mode.
        map.Syn = (byte)(mode == GalMode.Registered ? 0 : 1);
        map.Ac0 = (byte)(mode == GalMode.Simple ? 0 : 1);
        for (int i = 0; i < map.Pt.Length; i++) map.Pt[i] = 1;

        // ---- .oe equations: one per target, exactly one product term -------
        var oeOf = new Dictionary<string, ProductTerm>(StringComparer.OrdinalIgnoreCase);
        foreach (Equation oe in oes)
        {
            if (!targets.Contains(oe.Target))
            {
                errors.Add($"Output '{oe.Target}.oe' has no matching output equation.");
                continue;
            }
            if (oeOf.ContainsKey(oe.Target))
            {
                errors.Add($"Output '{oe.Target}' has more than one .oe equation.");
                continue;
            }
            if (oe.Terms.Count != 1)
            {
                errors.Add($"Output '{oe.Target}.oe': the output enable is a single product term (got {oe.Terms.Count}).");
                continue;
            }
            oeOf[oe.Target] = oe.Terms[0];
        }

        // Clear the connected input columns of one product-term row. A signal
        // declared active-low carries the inverted sense on its pin, so the
        // literal polarity folds the declaration, as WinCUPL does.
        void SetLiteral(string eqTarget, int rowBase, string sig, bool asserted)
        {
            if (!declOf.TryGetValue(sig, out PinDeclaration? decl))
            {
                errors.Add($"Output '{eqTarget}': undeclared signal '{sig}'.");
                return;
            }
            int litPin = decl.Number;
            if (mode == GalMode.Registered && litPin == clockPin)
            {
                errors.Add($"Output '{eqTarget}': pin {clockPin} is the global clock in registered mode and cannot be used in an equation.");
                return;
            }
            if (mode == GalMode.Registered && litPin == globalOePin)
            {
                errors.Add($"Output '{eqTarget}': pin {globalOePin} is the global /OE in registered mode and cannot be used in an equation.");
                return;
            }
            if (mode == GalMode.Complex && Array.IndexOf(outputOnlyComplexPins, litPin) >= 0)
            {
                errors.Add($"Output '{eqTarget}': pin {litPin} ('{sig}') has no feedback in complex mode.");
                return;
            }

            int col = pinToFuse[litPin - 1];
            if (col < 0)
            {
                errors.Add($"Output '{eqTarget}': pin {litPin} ('{sig}') cannot be an input here.");
                return;
            }

            int negation = (asserted ? 0 : 1) ^ (decl.ActiveLow ? 1 : 0);
            map.Logic[rowBase + col + negation] = 0;   // clear -> connect this input
        }

        // Program one row from a product term: start all-1, connect literals.
        void ProgramRow(string eqTarget, int row, ProductTerm term)
        {
            int baseIdx = row * cols;
            for (int c = 0; c < cols; c++) map.Logic[baseIdx + c] = 1;
            foreach (KeyValuePair<string, bool> lit in term.Literals)
                SetLiteral(eqTarget, baseIdx, lit.Key, lit.Value);
        }

        // ---- map each output ------------------------------------------------
        var usedOlmc = new HashSet<int>();
        var registeredOlmc = new HashSet<int>();

        foreach (Equation eq in mains)
        {
            if (!declOf.TryGetValue(eq.Target, out PinDeclaration? targetDecl))
            {
                errors.Add($"Output '{eq.Target}' has no PIN declaration.");
                continue;
            }
            int pin = targetDecl.Number;
            if (pin < firstOlmcPin || pin > lastOlmcPin)
            {
                errors.Add($"Output '{eq.Target}' (pin {pin}) is not an output-capable pin ({firstOlmcPin}-{lastOlmcPin}).");
                continue;
            }

            int olmc = pin - firstOlmcPin;
            if (!usedOlmc.Add(olmc))
            {
                errors.Add($"Output '{eq.Target}' (pin {pin}) is driven by more than one equation.");
                continue;
            }
            if (eq.Registered) registeredOlmc.Add(olmc);

            // Row budget: a registered OLMC owns all 8 rows; a combinational
            // OLMC in modes 2/3 gives its first row to the output-enable term.
            bool hasOeRow = !eq.Registered && mode != GalMode.Simple;
            int maxTerms = hasOeRow ? 7 : 8;
            if (eq.Terms.Count > maxTerms)
            {
                errors.Add($"Output '{eq.Target}': {eq.Terms.Count} product terms exceed the {maxTerms} available per output" +
                           (hasOeRow ? " (one term is the output enable)." : "."));
                continue;
            }

            int startRow = ToOlmc[olmc];

            if (hasOeRow)
            {
                if (oeOf.TryGetValue(eq.Target, out ProductTerm? oeTerm))
                {
                    ProgramRow(eq.Target, startRow, oeTerm);
                }
                else
                {
                    // No .oe -> all-1s row = a product of nothing = always enabled.
                    int baseIdx = startRow * cols;
                    for (int c = 0; c < cols; c++) map.Logic[baseIdx + c] = 1;
                }
            }
            else if (eq.Registered && oeOf.ContainsKey(eq.Target))
            {
                errors.Add($"Output '{eq.Target}': a registered output uses the global /OE pin; .oe is not available.");
            }

            int firstLogicRow = startRow + (hasOeRow ? 1 : 0);
            for (int t = 0; t < eq.Terms.Count; t++)
                ProgramRow(eq.Target, firstLogicRow + t, eq.Terms[t]);

            // Output polarity: the XOR of the PIN-declaration '!' and the
            // equation-LHS '!'. An effectively active-high output sets its
            // XOR fuse.
            bool effectiveActiveLow = eq.ActiveLow ^ targetDecl.ActiveLow;
            if (!effectiveActiveLow)
                map.Xor[GalV8Device.XorSize - 1 - olmc] = 1;
        }

        // AC1: mode 1 -> spare OLMC pins become inputs, matching WinCUPL;
        // mode 2 -> 1 for every cell; mode 3 -> 0 marks a registered cell,
        // 1 a combinational or spare one.
        for (int olmc = 0; olmc < GalV8Device.Ac1Size; olmc++)
        {
            byte ac1 = mode switch
            {
                GalMode.Simple => (byte)(usedOlmc.Contains(olmc) ? 0 : 1),
                GalMode.Complex => 1,
                _ => (byte)(registeredOlmc.Contains(olmc) ? 0 : 1),
            };
            map.Ac1[GalV8Device.Ac1Size - 1 - olmc] = ac1;
        }

        // User signature: emitted ERASED (all 1s), deliberately diverging from
        // WinCUPL, which writes the first 8 chars of PartNo here. Some device
        // programmers (observed: Xgecu T48, APP 13.16, GAL16V8D) do not program
        // the UES region from the fuse buffer but still verify it against the
        // buffer; any 0 bit in the file's UES then aborts the burn at fuse 2056
        // with "Verify Error". An all-1s UES matches the erased state, so it
        // verifies whether the programmer skips the region or programs it.
        // The UES is a purely cosmetic label; nothing functional reads it.
        for (int i = 0; i < map.Sig.Length; i++)
            map.Sig[i] = 1;

        return map;
    }
}