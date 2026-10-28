using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GalForge.Tests;

/// <summary>The command line: what it writes where, what it prints, its exit codes.</summary>
[TestClass]
public class CommandLineTests
{
    private string work = "";

    [TestInitialize]
    public void MakeWorkFolder()
    {
        work = Path.Combine(Path.GetTempPath(), "galforge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
    }

    [TestCleanup]
    public void RemoveWorkFolder() => Directory.Delete(work, recursive: true);

    private (int Exit, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        int exit = Program.Run(args, stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private string CopyExample(string name)
    {
        string to = Path.Combine(work, name);
        File.Copy(Path.Combine(Repo.Examples("Basic"), name), to);
        return to;
    }

    [TestMethod]
    public void AFileCompilesBesideItself()
    {
        string pld = CopyExample("gal16v8_simple.pld");
        var r = Run(pld);
        Assert.AreEqual(0, r.Exit, r.Err);
        Assert.IsTrue(File.Exists(Path.Combine(work, "gal16v8_simple.jed")));
        StringAssert.Contains(r.Out, "GAL16V8: 2194 fuses");
        StringAssert.Contains(r.Out, "Pin  Output        Terms  Max  Kind  Polarity  OE");
    }

    [TestMethod]
    public void APinDeclaredAsAnInputIsNotSpare()
    {
        // LEDDEC_R16 takes A11..A15 on the output-capable pins 14..18 and
        // drives only pin 23.
        string pld = Path.Combine(work, "LEDDEC_R16.PLD");
        File.Copy(Path.Combine(Repo.Examples("Reggie16"), "LEDDEC_R16.PLD"), pld);
        var r = Run(pld);
        Assert.AreEqual(0, r.Exit, r.Err);
        StringAssert.Contains(r.Out, "  Spare output pins: 19, 20, 21, 22" + Environment.NewLine);
    }

    [TestMethod]
    public void OutputCanNameAFile()
    {
        string pld = CopyExample("gal16v8_simple.pld");
        string jed = Path.Combine(work, "renamed.jed");
        Assert.AreEqual(0, Run(pld, "-o", jed).Exit);
        Assert.IsTrue(File.Exists(jed));
    }

    [TestMethod]
    public void AFolderCompilesEveryPldInAnyCase()
    {
        string src = Path.Combine(work, "src");
        string outDir = Path.Combine(work, "out");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(outDir);
        File.Copy(Path.Combine(Repo.Examples("Basic"), "gal16v8_simple.pld"), Path.Combine(src, "ONE.PLD"));
        File.Copy(Path.Combine(Repo.Examples("Basic"), "gal20v8_simple.pld"), Path.Combine(src, "two.pld"));

        var r = Run(src, "-o", outDir);
        Assert.AreEqual(0, r.Exit, r.Err);
        StringAssert.Contains(r.Out, "Compiled 2 of 2 file(s).");
        Assert.IsTrue(File.Exists(Path.Combine(outDir, "ONE.jed")));
        Assert.IsTrue(File.Exists(Path.Combine(outDir, "two.jed")));
    }

    [TestMethod]
    public void AFolderNeedsAFolderForItsOutput()
    {
        var r = Run(Repo.Examples("Basic"), "-o", Path.Combine(work, "missing"));
        Assert.AreEqual(1, r.Exit);
        StringAssert.Contains(r.Err, "-o must be an existing output folder");
    }

    [TestMethod]
    public void AFolderWithAFailureExitsOne()
    {
        File.WriteAllText(Path.Combine(work, "bad.pld"), "Name t; Device g16v8; PIN 2 = a; PIN 19 = y; y = q;");
        CopyExample("gal16v8_simple.pld");
        var r = Run(work);
        Assert.AreEqual(1, r.Exit);
        StringAssert.Contains(r.Out, "Compiled 1 of 2 file(s), 1 failed.");
        StringAssert.Contains(r.Err, "undeclared signal 'q'");
    }

    [TestMethod]
    public void AMissingInputExitsOne()
    {
        var r = Run(Path.Combine(work, "nothing.pld"));
        Assert.AreEqual(1, r.Exit);
        StringAssert.Contains(r.Err, "Input path not found");
    }

    [TestMethod]
    public void AnUnknownArgumentExitsOne()
    {
        var r = Run(CopyExample("gal16v8_simple.pld"), "-x");
        Assert.AreEqual(1, r.Exit);
        StringAssert.Contains(r.Err, "Unknown argument: -x");
    }

    [TestMethod]
    public void HelpExitsZeroAndNoArgumentsExitOne()
    {
        Assert.AreEqual(0, Run("--help").Exit);
        var r = Run();
        Assert.AreEqual(1, r.Exit);
        StringAssert.Contains(r.Out, "galforge <input.pld>");
    }

    [TestMethod]
    public void VersionPrintsTheVersion()
    {
        var r = Run("--version");
        Assert.AreEqual(0, r.Exit);
        Assert.AreEqual("GalForge 1.0.0", r.Out.Trim());
    }
}
