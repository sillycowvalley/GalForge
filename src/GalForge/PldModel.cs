using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace GalForge;

/// <summary>The parsed contents of a .pld source file.</summary>
internal sealed class PldDocument
{
    public string DeviceName = "";          // from the PLD header, e.g. "g16v8"
    public string Title = "";               // the Name property; used as the .jed note
    public string PartNo = "";              // the PartNo property; used as the signature
    public List<PinDeclaration> Pins = new();
    public List<Equation> Equations = new();
}

/// <summary>One PIN declaration: pin number, signal name, polarity.</summary>
internal sealed class PinDeclaration
{
    public int Number;
    public string Name = "";
    public bool ActiveLow;                  // leading ! in CUPL
}

/// <summary>
/// One output equation: a sum (OR) of product terms (AND of literals).
/// Registered vs combinational and output polarity drive OLMC config fuses.
/// A .oe equation carries the target's output-enable product term instead of
/// its logic; the compiler pairs it with the target's output equation. The
/// .ar/.sp equations (22V10 only) carry the global async-reset / sync-preset
/// product term; the parser records them, the compilers enforce the rules.
/// </summary>
internal sealed class Equation
{
    public string Target = "";              // output signal name
    public bool Registered;                 // .d suffix (registered) vs combinational
    public bool OutputEnable;               // .oe suffix (per-macrocell output enable)
    public bool AsyncReset;                 // .ar suffix (22V10 global async reset)
    public bool SyncPreset;                 // .sp suffix (22V10 global sync preset)
    public bool ActiveLow;                  // complemented output (leading !)
    public List<ProductTerm> Terms = new(); // OR of these
}

/// <summary>A single AND term: each literal asserted (true) or negated (false).</summary>
internal sealed class ProductTerm
{
    public Dictionary<string, bool> Literals = new();
}

/// <summary>
/// Stage 1: parse CUPL/PLD source into a <see cref="PldDocument"/>.
///
/// Scope:
///   - header properties (Name / PartNo / ... / Device);
///   - PIN declarations, with optional leading '!' for active-low;
///   - equations: any expression over NOT '!', AND '&', OR '#',
///     XOR '$' and parentheses, with CUPL precedence (highest to lowest):
///     ! , & , # , $. The expression is flattened to a sum-of-products here
///     (XOR expanded, De Morgan applied to negated products) so the fuse
///     mapper downstream only ever sees an OR of AND-of-literals. Flat SOP
///     inputs compile bit-for-bit as before.
///   - number literals: CUPL radixed constants ('b'0, 'h'1, 'd'0 ...) are
///     recognised and REJECTED with a hard error directing to the GND / VCC
///     keywords (a constant like 'b'0 is easy to mis-compile silently, so
///     it is never accepted). Multi-bit values and 'x' don't-cares belong to
///     field notation, likewise unsupported;
///   - output extensions: '.d' marks a registered output, '.oe' gives an
///     output its per-macrocell output-enable term, '.ar' and '.sp' carry the
///     22V10's global async-reset / sync-preset terms. Which GAL mode or
///     device these select, and their constraints, are the compiler's
///     business; the parser just records them. Any other extension is
///     rejected.
///   - comments: '//' to end of line, and '/* ... */' NON-NESTING (CUPL rule:
///     the first '*/' closes the block).
///
/// Not yet handled (deferred, each will register an error if encountered):
///   - output extensions other than .d / .oe / .ar / .sp;
///   - set/range/index notation, function calls;
///   - '$' PREPROCESSOR directives ($DEFINE / $REPEAT / $MACRO ...). Note this
///     is distinct from the '$' XOR operator, which IS supported above.
/// </summary>
internal static class PldParser
{
    private static readonly HashSet<string> HeaderKeywords =
        new(StringComparer.OrdinalIgnoreCase)
    {
        "NAME", "PARTNO", "DATE", "REVISION", "DESIGNER",
        "COMPANY", "ASSEMBLY", "LOCATION", "DEVICE",
    };

    private static readonly Regex PinRx =
        new(@"^PIN\s+(\d+)\s*=\s*(!?)\s*([A-Za-z_]\w*)$", RegexOptions.IgnoreCase);

    private static readonly Regex NameRx = new(@"^[A-Za-z_]\w*$");

    public static PldDocument Parse(string source, List<string> errors)
    {
        var doc = new PldDocument();
        var pendingEquations = new List<string>();
        string code = StripComments(source);

        foreach (string segment in code.Split(';'))
        {
            string st = CollapseWhitespace(segment);
            if (st.Length == 0) continue;

            int space = st.IndexOf(' ');
            string first = space < 0 ? st : st.Substring(0, space);

            if (HeaderKeywords.Contains(first))
            {
                string value = space < 0 ? "" : st.Substring(space + 1).Trim();
                ApplyHeader(doc, first, value);
            }
            else if (first.Equals("PIN", StringComparison.OrdinalIgnoreCase))
            {
                ParsePin(doc, st, errors);
            }
            else if (st.Contains('='))
            {
                // Deferred: equations are processed after the whole file has
                // been scanned, so PIN declarations are complete and targets
                // can be classified as pins vs intermediate nodes.
                pendingEquations.Add(st);
            }
            else
            {
                errors.Add($"Unrecognised statement: \"{Shorten(st)}\".");
            }
        }

        ProcessEquations(doc, pendingEquations, errors);

        if (doc.DeviceName.Length == 0)
            errors.Add("No Device specified in the .pld header.");
        if (doc.Equations.Count == 0)
            errors.Add("No output equations found.");

        return doc;
    }

    private static void ApplyHeader(PldDocument doc, string keyword, string value)
    {
        if (keyword.Equals("DEVICE", StringComparison.OrdinalIgnoreCase))
            doc.DeviceName = value;
        else if (keyword.Equals("NAME", StringComparison.OrdinalIgnoreCase))
            doc.Title = value;
        else if (keyword.Equals("PARTNO", StringComparison.OrdinalIgnoreCase))
            doc.PartNo = value;
    }

    private static void ParsePin(PldDocument doc, string st, List<string> errors)
    {
        Match m = PinRx.Match(st);
        if (!m.Success)
        {
            errors.Add($"Malformed PIN declaration: \"{Shorten(st)}\".");
            return;
        }
        doc.Pins.Add(new PinDeclaration
        {
            Number = int.Parse(m.Groups[1].Value),
            ActiveLow = m.Groups[2].Value == "!",
            Name = m.Groups[3].Value,
        });
    }


    /// <summary>
    /// Phase 2 of parsing: classify each stored '=' statement as a real
    /// output equation (target is a declared pin) or an intermediate NODE
    /// definition (plain NAME = expr where NAME is not a pin). Node bodies
    /// are substituted -- as parenthesised sub-expressions -- into every
    /// expression that references them, iterating so nodes may reference
    /// nodes, then the real equations compile exactly as before. Files
    /// containing no nodes take a byte-identical path.
    ///
    /// Rules: a node target carries no '.d/.oe/.ar/.sp' extension and no
    /// leading '!' (polarity belongs on pins; write the complement into the
    /// referencing expression instead). GND and VCC are reserved. Circular
    /// definitions are reported, as are nodes that nothing references.
    /// </summary>
    private static void ProcessEquations(PldDocument doc, List<string> pending, List<string> errors)
    {
        var pinNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PinDeclaration p in doc.Pins) pinNames.Add(p.Name);

        var nodes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var nodeUsed = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var realStatements = new List<string>();

        foreach (string st in pending)
        {
            int eq = st.IndexOf('=');
            string lhs = st.Substring(0, eq).Trim();
            string rhs = st.Substring(eq + 1).Trim();

            // Mirror ParseEquation's target derivation: extension first, then '!'.
            string bare = lhs;
            bool hasExt = false;
            int dot = bare.IndexOf('.');
            if (dot >= 0) { hasExt = true; bare = bare.Substring(0, dot).Trim(); }
            bool hasBang = bare.StartsWith("!");
            if (hasBang) bare = bare.Substring(1).Trim();

            if (pinNames.Contains(bare) || !IsName(bare))
            {
                realStatements.Add(st);     // pins (and malformed targets) go the old path
                continue;
            }

            // An undeclared target: a node definition, if it is a plain one.
            if (hasExt || hasBang)
            {
                errors.Add($"Output '{bare}' has no PIN declaration ('!' and extensions are not valid on an intermediate node): \"{Shorten(st)}\".");
                continue;
            }
            if (bare.Equals("GND", StringComparison.OrdinalIgnoreCase)
                || bare.Equals("VCC", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"'{bare}' is a reserved constant and cannot be a node: \"{Shorten(st)}\".");
                continue;
            }
            if (!nodes.TryAdd(bare, rhs))
            {
                errors.Add($"Node '{bare}' is defined more than once.");
                continue;
            }
            nodeUsed[bare] = false;
        }

        if (nodes.Count > 0)
        {
            // Expand node bodies to a fixed point (nodes may reference nodes).
            var nameRx = new Regex(@"[A-Za-z_]\w*");
            string ExpandOnce(string s, out bool changed)
            {
                bool any = false;
                string result = nameRx.Replace(s, m =>
                {
                    // A radix character inside a number literal ('b'0) sits
                    // between two single quotes; it is not a name reference.
                    if (m.Index > 0 && s.Length > m.Index + m.Length
                        && s[m.Index - 1] == '\'' && s[m.Index + m.Length] == '\'')
                        return m.Value;
                    if (nodes.TryGetValue(m.Value, out string? body))
                    {
                        any = true;
                        nodeUsed[m.Value] = true;
                        return "( " + body + " )";
                    }
                    return m.Value;
                });
                changed = any;
                return result;
            }

            foreach (string name in new List<string>(nodes.Keys))
            {
                string body = nodes[name];
                int depth = 0;
                bool changed = true;
                while (changed && depth++ < 32)
                    body = ExpandOnce(body, out changed);
                if (changed)
                {
                    errors.Add($"Circular node definition involving '{name}'.");
                    return;
                }
                nodes[name] = body;
            }

            for (int i = 0; i < realStatements.Count; i++)
            {
                int eq = realStatements[i].IndexOf('=');
                string lhs = realStatements[i].Substring(0, eq);
                string rhs = ExpandOnce(realStatements[i].Substring(eq + 1), out _);
                realStatements[i] = lhs + "=" + rhs;
            }

            foreach (KeyValuePair<string, bool> kv in nodeUsed)
                if (!kv.Value)
                    errors.Add($"Node '{kv.Key}' is defined but never referenced.");
            if (errors.Count > 0) return;
        }

        foreach (string st in realStatements)
            ParseEquation(doc, st, errors);
    }

    private static void ParseEquation(PldDocument doc, string st, List<string> errors)
    {
        int eq = st.IndexOf('=');
        string lhs = st.Substring(0, eq).Trim();
        string rhs = st.Substring(eq + 1).Trim();

        // Optional output extension: '.d' (registered output), '.oe'
        // (per-macrocell output enable), '.ar' / '.sp' (22V10 global reset /
        // preset). Anything else is still rejected.
        bool registered = false;
        bool outputEnable = false;
        bool asyncReset = false;
        bool syncPreset = false;
        int dot = lhs.IndexOf('.');
        if (dot >= 0)
        {
            string suffix = lhs.Substring(dot + 1).Trim();
            lhs = lhs.Substring(0, dot).Trim();
            if (suffix.Equals("d", StringComparison.OrdinalIgnoreCase))
                registered = true;
            else if (suffix.Equals("oe", StringComparison.OrdinalIgnoreCase))
                outputEnable = true;
            else if (suffix.Equals("ar", StringComparison.OrdinalIgnoreCase))
                asyncReset = true;
            else if (suffix.Equals("sp", StringComparison.OrdinalIgnoreCase))
                syncPreset = true;
            else
            {
                errors.Add($"Output extension '.{suffix}' not supported (only .d, .oe, .ar and .sp): \"{Shorten(st)}\".");
                return;
            }
        }

        bool activeLow = lhs.StartsWith("!");
        string target = (activeLow ? lhs.Substring(1) : lhs).Trim();
        if (!IsName(target))
        {
            errors.Add($"Bad equation target: \"{Shorten(lhs)}\".");
            return;
        }
        if (activeLow && (outputEnable || asyncReset || syncPreset))
        {
            errors.Add($"Output '{target}': '!' is not valid on a .oe/.ar/.sp equation.");
            return;
        }

        // Flatten the whole right-hand side (with !, &, #, $ and parentheses)
        // into a sum-of-products. Each product term is a name->polarity map.
        List<Dictionary<string, bool>>? sop =
            ExprParser.ParseToSop(rhs, errors, Shorten(st), out bool sawConstant);
        if (sop == null) return;

        // A deliberate GND/VCC constant is a driven low/high output. It is legal
        // on a combinational logic output, but not on .oe/.ar/.sp (those want a
        // real product term). Both constants are expressed through the same
        // false-row convention every ordinary equation already uses:
        //
        //   VCC -> one empty product term (a single all-blown, always-true
        //          row), output buffer active-high: the pin drives 1.
        //   GND -> NO product terms at all: every row of the block stays
        //          intact (each false), the OR is empty, and with the buffer
        //          at active-high the pin drives 0. Nothing relies on an
        //          all-blown row reading TRUE.
        //
        // A leading '!' on the target flips the drive via the ordinary
        // polarity XOR, so "!X = GND" drives 1 and "!X = VCC" drives 0.
        //
        // Honour the constant only when the expression collapsed to a genuine
        // constant: an empty sum (GND, all terms vanished) or a single empty
        // term (VCC). A GND/VCC mixed with real terms (e.g. "GND # T0") lands in
        // the normal path below, where it simplifies naturally.
        bool isConstZero = sawConstant && sop.Count == 0;
        bool isConstOne = sawConstant && sop.Count == 1 && sop[0].Count == 0;
        if (isConstZero || isConstOne)
        {
            if (outputEnable || asyncReset || syncPreset)
            {
                errors.Add($"Output '{target}': GND/VCC is not valid on a .oe/.ar/.sp equation.");
                return;
            }
            var constEq = new Equation
            {
                Target = target,
                ActiveLow = activeLow,      // the '!' only; the low/high sense
                Registered = registered,    // lives in the terms themselves
            };
            if (isConstOne)
                constEq.Terms.Add(new ProductTerm());   // empty term = always-true row
            doc.Equations.Add(constEq);
            return;
        }

        // Degenerate results have no fuse-array representation as a plain SOP:
        // an empty sum is constant 0; a term with no literals is constant 1.
        if (sop.Count == 0)
        {
            errors.Add($"Expression reduces to constant 0 (no product terms): \"{Shorten(st)}\". Use GND for a deliberate driven-low output.");
            return;
        }
        foreach (var termLits in sop)
        {
            if (termLits.Count == 0)
            {
                errors.Add($"Expression reduces to a constant (always true): \"{Shorten(st)}\". Use VCC for a deliberate driven-high output.");
                return;
            }
        }

        var equation = new Equation
        {
            Target = target,
            ActiveLow = activeLow,
            Registered = registered,
            OutputEnable = outputEnable,
            AsyncReset = asyncReset,
            SyncPreset = syncPreset,
        };
        foreach (var termLits in sop)
        {
            var term = new ProductTerm();
            foreach (var kv in termLits)
                term.Literals[kv.Key] = kv.Value;   // true = asserted, false = negated
            equation.Terms.Add(term);
        }

        doc.Equations.Add(equation);
    }

    // ---- CUPL comments: '//' to EOL, '/* */' NON-NESTING (first '*/' closes) ----
    private static string StripComments(string s)
    {
        var sb = new StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) i++;
                i += 2;                 // skip the closing */ (or run off the end)
                sb.Append(' ');
                continue;
            }
            if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] != '\n') i++;
                continue;
            }
            sb.Append(s[i]);
            i++;
        }
        return sb.ToString();
    }

    private static string CollapseWhitespace(string s) =>
        Regex.Replace(s, @"\s+", " ").Trim();

    private static bool IsName(string s) => NameRx.IsMatch(s);

    private static string Shorten(string s) =>
        s.Length > 44 ? s.Substring(0, 44) + "..." : s;

    /// <summary>
    /// Recursive-descent parser for a CUPL right-hand-side expression, producing
    /// a flattened sum-of-products. Grammar (loosest to tightest binding, which
    /// is CUPL precedence highest-to-lowest reversed): XOR '$', OR '#', AND '&',
    /// unary NOT '!', then a NAME or a parenthesised sub-expression.
    ///
    /// A product term is a Dictionary&lt;name,bool&gt; (true = asserted literal,
    /// false = negated). A sum-of-products is a List of those (an OR of ANDs).
    ///   - OR  concatenates the two lists (source order preserved, so existing
    ///         flat-SOP files keep their exact term ordering and fuse output).
    ///   - AND is a cartesian merge of terms; a term that would contain both a
    ///         literal and its complement (x &amp; !x) is dropped as constant 0.
    ///   - NOT is De Morgan: !(t1 # t2 # ...) = !t1 &amp; !t2 &amp; ...
    ///   - XOR(a,b) = (a &amp; !b) # (!a &amp; b).
    /// </summary>
    private sealed class ExprParser
    {
        private const string Ops = "!&#$()";

        private readonly List<string> toks;
        private readonly List<string> errors;
        private readonly string ctx;
        private int pos;
        private bool failed;

        // Set when the expression used a GND or VCC constant keyword, so the
        // caller can allow the resulting (otherwise-rejected) degenerate SOP.
        public bool SawConstant;

        private ExprParser(List<string> toks, List<string> errors, string ctx)
        {
            this.toks = toks;
            this.errors = errors;
            this.ctx = ctx;
        }

        public static List<Dictionary<string, bool>>? ParseToSop(
            string rhs, List<string> errors, string ctx, out bool sawConstant)
        {
            sawConstant = false;
            List<string>? toks = Tokenize(rhs, errors, ctx);
            if (toks == null) return null;

            var p = new ExprParser(toks, errors, ctx);
            var sop = p.ParseXor();
            if (p.failed) return null;
            if (p.pos != toks.Count)
            {
                errors.Add($"Unexpected '{toks[p.pos]}' in \"{ctx}\".");
                return null;
            }
            sawConstant = p.SawConstant;
            return sop;
        }

        // ---- tokenizer: names and the single-char operators ! & # $ ( ) ----
        private static List<string>? Tokenize(string s, List<string> errors, string ctx)
        {
            var toks = new List<string>();
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (Ops.IndexOf(c) >= 0) { toks.Add(c.ToString()); i++; continue; }
                if (c == '_' || char.IsLetter(c))
                {
                    int start = i++;
                    while (i < s.Length && (s[i] == '_' || char.IsLetterOrDigit(s[i]))) i++;
                    toks.Add(s.Substring(start, i - start));
                    continue;
                }
                if (c == '\'')
                {
                    string? constTok = ReadNumberLiteral(s, ref i, errors, ctx);
                    if (constTok == null) return null;
                    toks.Add(constTok);
                    continue;
                }
                errors.Add($"Bad character '{c}' in \"{ctx}\".");
                return null;
            }
            if (toks.Count == 0)
            {
                errors.Add($"Empty expression in \"{ctx}\".");
                return null;
            }
            return toks;
        }

        // ---- CUPL radixed number literal: 'b'0, 'h'1, 'd'0 ... -------------
        // Recognised only to be REJECTED with a hard error. A 'b'0 read
        // loosely leaves an output stuck high with no warning, so every
        // number literal in an equation ends in the same clear rejection
        // the degenerate-SOP path gives,
        // directing the author to the unambiguous GND / VCC keywords.
        // Advances i past the literal so the message can show it in full.
        private static string? ReadNumberLiteral(string s, ref int i, List<string> errors, string ctx)
        {
            int start = i;                                   // s[i] == '\''
            i++;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && Ops.IndexOf(s[i]) < 0)
                i++;
            errors.Add($"Number literal \"{Shorten(s.Substring(start, i - start))}\" in \"{ctx}\": "
                + "number literals are not supported in equations. "
                + "Use GND for a deliberate driven-low output or VCC for driven-high.");
            return null;
        }

        private string? Peek() => pos < toks.Count ? toks[pos] : null;

        // A token is a name (operand) if its first char is not an operator glyph.
        private static bool IsNameTok(string t) => Ops.IndexOf(t[0]) < 0;

        // xor := or ( '$' or )*
        private List<Dictionary<string, bool>> ParseXor()
        {
            var left = ParseOr();
            while (Peek() == "$") { pos++; left = Xor(left, ParseOr()); }
            return left;
        }

        // or := and ( '#' and )*
        private List<Dictionary<string, bool>> ParseOr()
        {
            var left = ParseAnd();
            while (Peek() == "#") { pos++; left = Or(left, ParseAnd()); }
            return left;
        }

        // and := not ( '&' not )*
        private List<Dictionary<string, bool>> ParseAnd()
        {
            var left = ParseNot();
            while (Peek() == "&") { pos++; left = And(left, ParseNot()); }
            return left;
        }

        // not := '!' not | primary
        private List<Dictionary<string, bool>> ParseNot()
        {
            if (Peek() == "!") { pos++; return Not(ParseNot()); }
            return ParsePrimary();
        }

        // primary := NAME | '(' xor ')'
        private List<Dictionary<string, bool>> ParsePrimary()
        {
            string? t = Peek();
            if (t == null) { Fail("expected an operand"); return Zero(); }
            if (t == "(")
            {
                pos++;
                var inner = ParseXor();
                if (Peek() != ")") { Fail("missing ')'"); return Zero(); }
                pos++;
                return inner;
            }
            if (IsNameTok(t))
            {
                pos++;
                // GND and VCC are reserved constant operands: a deliberate driven
                // low / high. GND flattens to the empty OR (constant 0); VCC to
                // the single empty AND (constant 1). SawConstant lets the caller
                // permit the otherwise-rejected degenerate SOP, distinguishing an
                // intentional constant from an accidental one such as x & !x.
                if (t.Equals("GND", StringComparison.OrdinalIgnoreCase))
                {
                    SawConstant = true;
                    return Zero();
                }
                if (t.Equals("VCC", StringComparison.OrdinalIgnoreCase))
                {
                    SawConstant = true;
                    return One();
                }
                return Lit(t, true);
            }
            Fail($"unexpected '{t}'");
            return Zero();
        }

        private void Fail(string why)
        {
            if (!failed) errors.Add($"Malformed expression ({why}) in \"{ctx}\".");
            failed = true;
            pos = toks.Count;   // halt the recursion
        }

        // ---- sum-of-products primitives ----
        private static List<Dictionary<string, bool>> Zero() => new();      // empty OR = const 0
        private static List<Dictionary<string, bool>> One() =>              // one empty AND = const 1
            new() { new Dictionary<string, bool>() };
        private static List<Dictionary<string, bool>> Lit(string name, bool asserted) =>
            new() { new Dictionary<string, bool> { [name] = asserted } };

        private static List<Dictionary<string, bool>> Or(
            List<Dictionary<string, bool>> a, List<Dictionary<string, bool>> b)
        {
            var r = new List<Dictionary<string, bool>>(a);
            r.AddRange(b);
            return r;
        }

        private static List<Dictionary<string, bool>> And(
            List<Dictionary<string, bool>> a, List<Dictionary<string, bool>> b)
        {
            var r = new List<Dictionary<string, bool>>();
            foreach (var ta in a)
            {
                foreach (var tb in b)
                {
                    var merged = new Dictionary<string, bool>(ta);
                    bool contradiction = false;
                    foreach (var kv in tb)
                    {
                        if (merged.TryGetValue(kv.Key, out bool existing))
                        {
                            if (existing != kv.Value) { contradiction = true; break; }
                        }
                        else merged[kv.Key] = kv.Value;
                    }
                    if (!contradiction) r.Add(merged);
                }
            }
            return r;
        }

        private static List<Dictionary<string, bool>> Not(List<Dictionary<string, bool>> a)
        {
            // !(t1 # t2 # ...) = !t1 & !t2 & ...  where !term = OR of negated literals.
            var acc = One();
            foreach (var term in a)
            {
                if (term.Count == 0) return Zero();   // !(constant 1) = constant 0
                var negTerm = new List<Dictionary<string, bool>>();
                foreach (var kv in term)
                    negTerm.Add(new Dictionary<string, bool> { [kv.Key] = !kv.Value });
                acc = And(acc, negTerm);
            }
            return acc;
        }

        private static List<Dictionary<string, bool>> Xor(
            List<Dictionary<string, bool>> a, List<Dictionary<string, bool>> b)
        {
            // a $ b = (a & !b) # (!a & b)
            return Or(And(a, Not(b)), And(Not(a), b));
        }
    }
}