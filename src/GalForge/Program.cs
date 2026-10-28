using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace GalForge;

/// <summary>
/// GalForge -- a CUPL-subset logic compiler for the GAL16V8, GAL20V8 and
/// GAL22V10 (and their fuse-compatible ATF parts).
///
/// Pipeline (mirrors a classic CUPL flow):
///
///     .pld source
///        -> PldParser.Parse      (tokens -> pin map + boolean equations)
///        -> TargetDevice.Resolve (G16V8 / G20V8 / G22V10)
///        -> Compiler.Compile     (SOP build + minimise + map to fuse array)
///        -> JedecWriter.Write     (fuse map -> JESD3 .jed text)
///     .jed output
///
/// The CLI accepts a single .pld file or a folder. A folder input compiles
/// every *.pld it contains (non-recursive, any letter case). The -o output may
/// be a filename or an existing folder; when it is a folder, each .jed is
/// named after its input.
/// </summary>
internal static class Program
{
    private static int Main(string[] args) => Run(args, Console.Out, Console.Error);

    internal static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length < 1 || IsHelp(args[0]))
        {
            PrintUsage(stdout);
            return args.Length < 1 ? 1 : 0;
        }
        if (args[0] is "--version" or "-v")
        {
            stdout.WriteLine($"GalForge {Version}");
            return 0;
        }

        string inputPath = args[0];
        string? outputPath = null;    // -o : may name a file OR an existing folder
        string? deviceOverride = null;

        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o" when i + 1 < args.Length:
                    outputPath = args[++i];
                    break;
                case "-d" when i + 1 < args.Length:
                    deviceOverride = args[++i];
                    break;
                default:
                    stderr.WriteLine($"Unknown argument: {args[i]}");
                    PrintUsage(stdout);
                    return 1;
            }
        }

        // Folder input: compile every *.pld in the directory.
        if (Directory.Exists(inputPath))
            return CompileFolder(inputPath, outputPath, deviceOverride, stdout, stderr);

        // Single-file input.
        if (!File.Exists(inputPath))
        {
            stderr.WriteLine($"Input path not found: {inputPath}");
            return 1;
        }

        return CompileOne(inputPath, ResolveOutputPath(inputPath, outputPath), deviceOverride, stdout, stderr);
    }

    internal static string Version =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    private static int CompileFolder(string dir, string? outputPath, string? deviceOverride,
        TextWriter stdout, TextWriter stderr)
    {
        // A whole folder produces many .jed files, so an -o output must itself be
        // a folder -- a single filename could not hold them all.
        if (outputPath != null && !Directory.Exists(outputPath))
        {
            stderr.WriteLine(
                $"When the input is a folder, -o must be an existing output folder (got: {outputPath}).");
            return 1;
        }

        // Case-blind on every platform, so FOO.PLD is found on Linux and macOS
        // just as it is on Windows.
        var match = new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive };
        string[] files = Directory.GetFiles(dir, "*.pld", match);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        if (files.Length == 0)
        {
            stderr.WriteLine($"No .pld files found in {dir}");
            return 1;
        }

        int failed = 0;
        foreach (string file in files)
        {
            stdout.WriteLine($"--- {Path.GetFileName(file)} ---");
            if (CompileOne(file, ResolveOutputPath(file, outputPath), deviceOverride, stdout, stderr) != 0)
                failed++;
            stdout.WriteLine();
        }

        int ok = files.Length - failed;
        stdout.WriteLine(failed > 0
            ? $"Compiled {ok} of {files.Length} file(s), {failed} failed."
            : $"Compiled {ok} of {files.Length} file(s).");
        return failed > 0 ? 1 : 0;
    }

    /// <summary>
    /// Decide where a given input's .jed is written:
    ///   no -o             -> alongside the source, extension changed to .jed;
    ///   -o &lt;folder&gt; (exists) -> that folder, named &lt;input&gt;.jed;
    ///   -o &lt;file&gt;         -> that path, used as given.
    /// </summary>
    private static string ResolveOutputPath(string inputFile, string? outputPath)
    {
        if (outputPath == null)
            return Path.ChangeExtension(inputFile, ".jed");
        if (Directory.Exists(outputPath))
            return Path.Combine(outputPath, Path.GetFileNameWithoutExtension(inputFile) + ".jed");
        return outputPath;
    }

    private static int CompileOne(string inputPath, string jedPath, string? deviceOverride,
        TextWriter stdout, TextWriter stderr)
    {
        var errors = new List<string>();
        CompileResult? result = Compile(File.ReadAllText(inputPath), deviceOverride, errors);
        if (result == null) return Report(errors, stderr);

        File.WriteAllText(jedPath, result.Jedec);

        stdout.WriteLine($"{result.Device.Name}: {result.Device.FuseCount} fuses -> {jedPath}");
        WarnUnusedPins(result.Document, stdout);
        PrintFitReport(result.Document, result.Device, result.Fuses, stdout);
        return 0;
    }

    internal sealed record CompileResult(PldDocument Document, TargetDevice Device, FuseMapBase Fuses, string Jedec);

    /// <summary>
    /// The whole pipeline on one source text. Returns null, with the reasons
    /// in <paramref name="errors"/>, when any stage fails.
    /// </summary>
    internal static CompileResult? Compile(string source, string? deviceOverride, List<string> errors)
    {
        // ---- Stage 1: parse -------------------------------------------------
        PldDocument doc = PldParser.Parse(source, errors);
        if (errors.Count > 0) return null;

        // ---- Stage 2: resolve target device --------------------------------
        TargetDevice? device = TargetDevice.Resolve(deviceOverride ?? doc.DeviceName, errors);
        if (device == null || errors.Count > 0) return null;

        // ---- Stage 3: compile to a fuse map --------------------------------
        FuseMapBase fuses = Compiler.Compile(doc, device, errors);
        if (errors.Count > 0) return null;

        // ---- Stage 4: emit JEDEC -------------------------------------------
        return new CompileResult(doc, device, fuses, JedecWriter.Write(fuses, device, doc));
    }

    /// <summary>
    /// Warn about declared pins that no equation targets or references.
    /// Non-fatal: an intentionally reserved input (wired on the board for a
    /// future revision) is legitimate -- the warning is the documentation.
    /// </summary>
    private static void WarnUnusedPins(PldDocument doc, TextWriter stdout)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Equation eq in doc.Equations)
        {
            referenced.Add(eq.Target);
            foreach (ProductTerm term in eq.Terms)
                foreach (string name in term.Literals.Keys)
                    referenced.Add(name);
        }
        foreach (PinDeclaration pin in doc.Pins)
            if (!referenced.Contains(pin.Name))
                stdout.WriteLine($"warning: pin {pin.Number} ('{pin.Name}') is declared but never used.");
    }

    /// <summary>
    /// Post-compile fit report: per-output product-term usage against the
    /// macrocell's budget, output kind and effective polarity, plus the spare
    /// output-capable pins. Generated from the same parsed document the fuse
    /// map came from, so it cannot drift from the JEDEC.
    /// </summary>
    private static void PrintFitReport(PldDocument doc, TargetDevice device, FuseMapBase fuses, TextWriter stdout)
    {
        var declOf = new Dictionary<string, PinDeclaration>(StringComparer.OrdinalIgnoreCase);
        foreach (PinDeclaration p in doc.Pins) declOf.TryAdd(p.Name, p);

        var oeTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Equation eq in doc.Equations)
            if (eq.OutputEnable) oeTargets.Add(eq.Target);

        bool is22 = device is Gal22V10;

        // V8 mode, read from the compiled map itself (SYN/AC0 encode it):
        // 1/0 simple, 1/1 complex, 0/1 registered. Simple gives every OLMC
        // its 8 rows; complex spends row 0 on the OE term (7); in registered
        // mode a .d output keeps 8 while a combinational cell works like
        // complex (7). Single source of truth: the fuse map, not a second
        // copy of the mode-selection rules.
        bool v8Simple = false, v8Registered = false;
        if (fuses is FuseMap v8Map)
        {
            v8Simple = v8Map.Syn == 1 && v8Map.Ac0 == 0;
            v8Registered = v8Map.Syn == 0;
        }
        int firstPin = is22 ? Gal22V10.FirstOlmcPin : (device is Gal20V8 ? 15 : 12);
        int lastPin = is22 ? Gal22V10.LastOlmcPin : (device is Gal20V8 ? 22 : 19);

        stdout.WriteLine();
        stdout.WriteLine("  Pin  Output        Terms  Max  Kind  Polarity  OE");
        var driven = new HashSet<int>();
        foreach (Equation eq in doc.Equations)
        {
            if (eq.OutputEnable || eq.AsyncReset || eq.SyncPreset) continue;
            if (!declOf.TryGetValue(eq.Target, out PinDeclaration? decl)) continue;
            driven.Add(decl.Number);

            string max = is22 && decl.Number >= Gal22V10.FirstOlmcPin && decl.Number <= Gal22V10.LastOlmcPin
                ? Gal22V10.BlockTermCount[Gal22V10.LastOlmcPin - decl.Number].ToString()
                : (v8Simple || (v8Registered && eq.Registered)) ? "8" : "7";
            bool activeLow = eq.ActiveLow ^ decl.ActiveLow;
            stdout.WriteLine(
                $"  {decl.Number,-4} {eq.Target,-13} {eq.Terms.Count,-6} {max,-4} " +
                $"{(eq.Registered ? "reg " : "comb"),-5} {(activeLow ? "act-low " : "act-high"),-9} " +
                $"{(oeTargets.Contains(eq.Target) ? "term" : "-")}");
        }

        var spares = new List<int>();
        for (int p = firstPin; p <= lastPin; p++)
            if (!driven.Contains(p)) spares.Add(p);
        if (spares.Count > 0)
            stdout.WriteLine($"  Spare output pins: {string.Join(", ", spares)}");
    }

    private static int Report(List<string> errors, TextWriter stderr)
    {
        foreach (string e in errors) stderr.WriteLine(e);
        stderr.WriteLine($"\nCompile failed: {errors.Count} error(s).");
        return 1;
    }

    private static bool IsHelp(string a) =>
        a is "-h" or "--help" or "/?" or "-?";

    private static void PrintUsage(TextWriter stdout)
    {
        stdout.WriteLine(
$@"GalForge {Version} -- CUPL-subset compiler for GAL16V8, GAL20V8 and GAL22V10

Usage:
  galforge <input.pld> [-o <output>] [-d <device>]
  galforge <folder>    [-o <folder>] [-d <device>]
  galforge --version

A folder input compiles every *.pld in it (non-recursive). If -o is an
existing folder, each .jed is written there using the input's name; if -o
is a filename it is used as-is (single-file input only); with no -o the
.jed is written alongside each source.

Options:
  -o <output>   output .jed file, OR an existing folder to write into
                (default: alongside the input, extension changed to .jed)
  -d <device>   target device, overriding the .pld header
                (G16V8 | G20V8 | G22V10)

Output:
  <name>.jed    JEDEC fuse map, programmable onto the target GAL");
    }
}
