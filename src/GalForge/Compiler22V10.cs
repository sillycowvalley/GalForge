using System;
using System.Collections.Generic;

namespace GalForge;

/// <summary>
/// Stage 3 for the GAL22V10/ATF22V10. Geometry per <see cref="Gal22V10"/>
/// (the data sheet's layout, proven fuse-by-fuse against WinCUPL golds for both a
/// combinational and a registered reference design -- see
/// docs/GAL22V10_Geometry.md).
///
/// No global modes: each macrocell is configured by its own S0/S1 pair.
///   S1 = 1 combinational, 0 registered.
///   S0 = 1 active-high,  0 active-low (an output-buffer polarity select;
///        the array always holds the positive-logic expression).
/// Output polarity is the XOR of the PIN-declaration '!' and the equation
/// LHS '!', exactly as on the V8 path.
///
/// Rows: row 0 is the global AR (asynchronous reset) product term, row 131
/// the global SP (synchronous preset) term; between them sit ten OLMC blocks
/// (pins 23 down to 14), each [1 OE row + 8..16 logic rows]. An absent .oe
/// leaves the OE row all-1s (a product of nothing = always enabled); an
/// unused macrocell's all-0 (intact) OE row can never be true, so erased
/// cells never drive -- no special-casing needed.
///
/// Extensions:
///   .d   registered output (D input = the equation's SOP).
///   .oe  per-macrocell output enable, a single product term. Valid on both
///        combinational AND registered outputs (every 22V10 macrocell owns
///        its own OE row; there is no global /OE pin on this family).
///   .ar / .sp  the global reset/preset terms, CUPL style: stated once per
///        registered output, a single product term, and identical across
///        outputs (the device has ONE physical AR row and ONE SP row).
///        Only valid on registered (.d) outputs.
///
/// Feedback (the column pair of a macrocell pin taps different points
/// depending on S1, so literal polarity differs by what it references):
///   - A REGISTERED output's feedback comes from the register (/Q on the
///     even column), independent of OE and of the pin's polarity buffer:
///     the literal polarity INVERTS, and the declared '!' does NOT fold in.
///   - A COMBINATIONAL output's feedback comes from the PIN, so a declared
///     '!' folds into the literal like any input's.
///   - A macrocell pin read as a plain input (no equation) needs S1 = 1 so
///     its feedback taps the pin rather than a never-clocked register; S0 is
///     set to 1 (don't-care for an undriven cell). Functionally required;
///     the S0 choice for this input-cell case is the one detail without a
///     WinCUPL gold yet.
///
/// Pin 1 is both the register clock and array input line 0, so unlike the
/// V8 registered mode it MAY appear in equations. Pins 12/24 are GND/VCC.
/// The UES (5828..5891) is emitted erased (all 1s) -- same programmer-verify
/// rationale as the V8 signature policy.
/// </summary>
internal static class Compiler22V10
{
    public static FuseMap22V10 Compile(PldDocument doc, Gal22V10 device, List<string> errors)
    {
        var map = new FuseMap22V10(device);
        int cols = Gal22V10.Columns;

        // UES erased.
        for (int i = 0; i < map.Ues.Length; i++) map.Ues[i] = 1;

        // name -> declaration (pin number + declared polarity)
        var declOf = new Dictionary<string, PinDeclaration>(StringComparer.OrdinalIgnoreCase);
        foreach (PinDeclaration p in doc.Pins)
        {
            if (!declOf.TryAdd(p.Name, p))
                errors.Add($"Pin name '{p.Name}' is declared more than once.");
        }

        // Partition the equations.
        var mains = new List<Equation>();
        var oes = new List<Equation>();
        var ars = new List<Equation>();
        var sps = new List<Equation>();
        foreach (Equation eq in doc.Equations)
        {
            if (eq.OutputEnable) oes.Add(eq);
            else if (eq.AsyncReset) ars.Add(eq);
            else if (eq.SyncPreset) sps.Add(eq);
            else mains.Add(eq);
        }

        var mainOf = new Dictionary<string, Equation>(StringComparer.OrdinalIgnoreCase);
        foreach (Equation eq in mains)
            mainOf.TryAdd(eq.Target, eq);   // duplicates reported at OLMC mapping

        var registeredTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Equation eq in mains)
            if (eq.Registered) registeredTargets.Add(eq.Target);

        // ---- .ar / .sp: single term, registered targets only, identical ----
        ProductTerm? CheckGlobal(List<Equation> list, string ext, string what)
        {
            ProductTerm? term = null;
            foreach (Equation eq in list)
            {
                if (!mainOf.TryGetValue(eq.Target, out Equation? main))
                {
                    errors.Add($"Output '{eq.Target}.{ext}' has no matching output equation.");
                    continue;
                }
                if (!main.Registered)
                {
                    errors.Add($"Output '{eq.Target}.{ext}': .{ext} is only valid on a registered (.d) output.");
                    continue;
                }
                if (eq.Terms.Count != 1)
                {
                    errors.Add($"Output '{eq.Target}.{ext}': the {what} is a single product term (got {eq.Terms.Count}).");
                    continue;
                }
                if (term == null)
                    term = eq.Terms[0];
                else if (!SameTerm(term, eq.Terms[0]))
                    errors.Add($"Output '{eq.Target}.{ext}': all .{ext} terms must be identical (the device has one global {ext.ToUpperInvariant()} row).");
            }
            return term;
        }
        ProductTerm? arTerm = CheckGlobal(ars, "ar", "async reset");
        ProductTerm? spTerm = CheckGlobal(sps, "sp", "sync preset");

        // ---- .oe: one per target, exactly one product term -----------------
        var oeOf = new Dictionary<string, ProductTerm>(StringComparer.OrdinalIgnoreCase);
        foreach (Equation oe in oes)
        {
            if (!mainOf.ContainsKey(oe.Target))
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

        // Clear the connected input columns of one product-term row (see the
        // feedback-polarity rules in the class comment).
        void SetLiteral(string eqTarget, int rowBase, string sig, bool asserted)
        {
            if (!declOf.TryGetValue(sig, out PinDeclaration? decl))
            {
                errors.Add($"Output '{eqTarget}': undeclared signal '{sig}'.");
                return;
            }
            int col = Gal22V10.PinToFuse[decl.Number - 1];
            if (col < 0)
            {
                errors.Add($"Output '{eqTarget}': pin {decl.Number} ('{sig}') cannot be an input here.");
                return;
            }

            int negation = asserted ? 0 : 1;
            if (registeredTargets.Contains(sig))
                negation ^= 1;                 // register tap: /Q on the even column
            else if (decl.ActiveLow)
                negation ^= 1;                 // pin tap: fold the declared polarity

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

        if (arTerm != null) ProgramRow(".ar", Gal22V10.ArRow, arTerm);
        if (spTerm != null) ProgramRow(".sp", Gal22V10.SpRow, spTerm);

        // ---- map each output ------------------------------------------------
        var usedOlmc = new HashSet<int>();

        foreach (Equation eq in mains)
        {
            if (!declOf.TryGetValue(eq.Target, out PinDeclaration? targetDecl))
            {
                errors.Add($"Output '{eq.Target}' has no PIN declaration.");
                continue;
            }
            int pin = targetDecl.Number;
            if (pin < Gal22V10.FirstOlmcPin || pin > Gal22V10.LastOlmcPin)
            {
                errors.Add($"Output '{eq.Target}' (pin {pin}) is not an output-capable pin ({Gal22V10.FirstOlmcPin}-{Gal22V10.LastOlmcPin}).");
                continue;
            }

            int olmc = Gal22V10.LastOlmcPin - pin;
            if (!usedOlmc.Add(olmc))
            {
                errors.Add($"Output '{eq.Target}' (pin {pin}) is driven by more than one equation.");
                continue;
            }

            int maxTerms = Gal22V10.BlockTermCount[olmc];
            if (eq.Terms.Count > maxTerms)
            {
                errors.Add($"Output '{eq.Target}': {eq.Terms.Count} product terms exceed the {maxTerms} available on pin {pin}.");
                continue;
            }

            int startRow = Gal22V10.BlockStartRow[olmc];

            // OE row (row 0 of the block): the .oe term, or all-1s = enabled.
            if (oeOf.TryGetValue(eq.Target, out ProductTerm? oeTerm))
            {
                ProgramRow(eq.Target, startRow, oeTerm);
            }
            else
            {
                int baseIdx = startRow * cols;
                for (int c = 0; c < cols; c++) map.Logic[baseIdx + c] = 1;
            }

            for (int t = 0; t < eq.Terms.Count; t++)
                ProgramRow(eq.Target, startRow + 1 + t, eq.Terms[t]);

            // S bits: pair index = OLMC index (pin 23 first), S0 even, S1 odd.
            bool effectiveActiveLow = eq.ActiveLow ^ targetDecl.ActiveLow;
            map.SBits[2 * olmc] = (byte)(effectiveActiveLow ? 0 : 1);   // S0
            map.SBits[2 * olmc + 1] = (byte)(eq.Registered ? 0 : 1);    // S1
        }

        // A macrocell pin read as an input (no equation of its own) must have
        // S1 = 1 so its feedback taps the PIN; the erased default (S1 = 0)
        // would tap a never-clocked register instead.
        var inputOlmcs = new HashSet<int>();
        foreach (Equation eq in doc.Equations)
        {
            foreach (ProductTerm term in eq.Terms)
            {
                foreach (string sig in term.Literals.Keys)
                {
                    if (declOf.TryGetValue(sig, out PinDeclaration? d)
                        && d.Number >= Gal22V10.FirstOlmcPin && d.Number <= Gal22V10.LastOlmcPin
                        && !mainOf.ContainsKey(sig))
                    {
                        inputOlmcs.Add(Gal22V10.LastOlmcPin - d.Number);
                    }
                }
            }
        }
        foreach (int olmc in inputOlmcs)
        {
            if (usedOlmc.Contains(olmc)) continue;
            map.SBits[2 * olmc] = 1;       // S0 don't-care for an undriven cell
            map.SBits[2 * olmc + 1] = 1;   // S1 = 1: combinational feedback = the pin
        }

        return map;
    }

    private static bool SameTerm(ProductTerm a, ProductTerm b)
    {
        if (a.Literals.Count != b.Literals.Count) return false;
        foreach (KeyValuePair<string, bool> kv in a.Literals)
        {
            if (!b.Literals.TryGetValue(kv.Key, out bool v) || v != kv.Value)
                return false;
        }
        return true;
    }
}
