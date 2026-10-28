using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GalForge.Tests;

/// <summary>
/// What the compiler refuses, and the message it refuses with (the manual's
/// error catalogue, §13). Each source is a whole small design.
/// </summary>
[TestClass]
public class ErrorTests
{
    private const string Pins16 = "Name t; Device g16v8; PIN 1 = c; PIN 2 = a; PIN 3 = b; PIN 19 = y; PIN 18 = z;";
    private const string Pins22 = "Name t; Device g22v10; PIN 1 = c; PIN 2 = a; PIN 3 = b; PIN 23 = y;";

    [TestMethod]
    [DataRow(Pins16 + "y.q = a;", "Output extension '.q' not supported")]
    [DataRow(Pins16 + "y.d = a; y.ar = b;", ".ar is only supported on the GAL22V10")]
    [DataRow(Pins16 + "y = 'b'0;", "number literals are not supported")]
    [DataRow(Pins16 + "y = [a, b];", "Bad character '['")]
    [DataRow(Pins16 + "y = a & q;", "undeclared signal 'q'")]
    [DataRow(Pins16 + "y = a & !a;", "reduces to constant 0")]
    [DataRow(Pins16 + "y = a; y = b;", "is driven by more than one equation")]
    [DataRow(Pins16 + "y = a; y.oe = GND;", "GND/VCC is not valid on a .oe/.ar/.sp equation")]
    [DataRow(Pins16 + "y.d = c & a;", "is the global clock in registered mode")]
    [DataRow(Pins16 + "y = a & b # a & !b # !a & b # !a & !b & c # a & c # b & c # !c # a & b & c # !a & !b & !c;",
        "exceed the")]
    [DataRow("Name t; Device g26v12; PIN 2 = a; PIN 19 = y; y = a;", "Unsupported device: 'g26v12'")]
    [DataRow("Name t; PIN 2 = a; PIN 19 = y; y = a;", "No Device specified")]
    [DataRow(Pins22 + "y = a; y.ar = b;", ".ar is only valid on a registered (.d) output")]
    public void ADesignIsRefusedWithItsMessage(string source, string message)
    {
        var errors = new List<string>();
        Assert.IsNull(Program.Compile(source, null, errors));
        Assert.IsTrue(errors.Exists(e => e.Contains(message)),
            $"expected \"{message}\", got:\n{string.Join("\n", errors)}");
    }

    [TestMethod]
    public void TheDeviceOptionOverridesTheHeader()
    {
        var errors = new List<string>();
        Program.CompileResult? result = Program.Compile(
            "Name t; Device g16v8; PIN 2 = a; PIN 3 = b; PIN 22 = y; y = a & b;", "G20V8", errors);
        Assert.IsNotNull(result, string.Join("\n", errors));
        Assert.AreEqual("GAL20V8", result.Device.Name);
        StringAssert.Contains(result.Jedec, "*QF2706 ");
    }
}
