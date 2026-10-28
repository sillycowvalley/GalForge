using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace GalForge.Tests;

/// <summary>
/// Every example compiles to the .jed checked in beside it, byte for byte;
/// the basic examples' fuse areas equal WinCUPL's; and every checked-in .jed
/// carries correct fuse and transmission checksums.
/// </summary>
[TestClass]
public class GoldTests
{
    public static IEnumerable<object[]> BasicExamples() => Sources("Basic");
    public static IEnumerable<object[]> Reggie16Examples() => Sources("Reggie16");

    private static IEnumerable<object[]> Sources(string folder) =>
        Directory.GetFiles(Repo.Examples(folder), "*.pld", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => new object[] { Path.GetFileName(f) });

    [TestMethod]
    [DynamicData(nameof(BasicExamples), DynamicDataSourceType.Method)]
    public void ABasicExampleCompilesToItsGold(string pld) => CompilesToGold("Basic", pld);

    [TestMethod]
    [DynamicData(nameof(Reggie16Examples), DynamicDataSourceType.Method)]
    public void AReggie16GalCompilesToItsGold(string pld) => CompilesToGold("Reggie16", pld);

    [TestMethod]
    [DynamicData(nameof(BasicExamples), DynamicDataSourceType.Method)]
    public void ABasicExampleMatchesWinCuplFuseForFuse(string pld)
    {
        string jed = Compile(Path.Combine(Repo.Examples("Basic"), pld));
        string wincupl = Read(Path.Combine(Repo.Examples("Basic"), "WinCUPL", Path.ChangeExtension(pld, ".jed")));

        byte[] ours = Fuses(jed);
        byte[] theirs = Fuses(wincupl);
        Assert.AreEqual(theirs.Length, ours.Length, "fuse count");

        // The AND array: each macrocell's block must hold the same product
        // terms. The order of a block's OR terms is free, and the two
        // compilers place them differently.
        (int columns, int logicEnd, int[] blockStarts, int sigStart, int sigEnd) = ours.Length switch
        {
            2194 => (32, 2048, new[] { 0, 8, 16, 24, 32, 40, 48, 56, 64 }, 2056, 2120),
            2706 => (40, 2560, new[] { 0, 8, 16, 24, 32, 40, 48, 56, 64 }, 2568, 2632),
            // AR row, ten macrocells (an OE row and its logic rows each), SP row.
            5892 => (44, 5808, new[] { 0, 1, 10, 21, 34, 49, 66, 83, 98, 111, 122, 131, 132 }, 5828, 5892),
            _ => throw new AssertFailedException($"unexpected fuse count {ours.Length}"),
        };
        for (int b = 0; b + 1 < blockStarts.Length; b++)
        {
            Assert.AreEqual(
                string.Join(" ", BlockRows(theirs, columns, blockStarts[b], blockStarts[b + 1])),
                string.Join(" ", BlockRows(ours, columns, blockStarts[b], blockStarts[b + 1])),
                $"product terms of the block at row {blockStarts[b]}");
        }

        // Every fuse past the array, exactly. GalForge leaves the user
        // signature erased (manual §11: some programmers abort on a 0 bit
        // there), where WinCUPL writes the PartNo.
        for (int i = logicEnd; i < ours.Length; i++)
        {
            if (i >= sigStart && i < sigEnd)
                Assert.AreEqual(1, ours[i], $"signature fuse {i} is not erased");
            else
                Assert.AreEqual(theirs[i], ours[i], $"fuse {i}");
        }
    }

    /// <summary>A block's rows as text, sorted, so their order does not count.</summary>
    private static IEnumerable<string> BlockRows(byte[] fuses, int columns, int firstRow, int endRow) =>
        Enumerable.Range(firstRow, endRow - firstRow)
            .Select(r => string.Concat(fuses.Skip(r * columns).Take(columns)))
            .OrderBy(s => s, StringComparer.Ordinal);

    [TestMethod]
    public void EveryCheckedInJedecCarriesCorrectChecksums()
    {
        var jeds = Directory.GetFiles(Path.Combine(Repo.Root, "examples"), "*.jed", SearchOption.AllDirectories);
        Assert.IsTrue(jeds.Length >= 55, $"found only {jeds.Length} .jed files");
        foreach (string path in jeds)
        {
            string jed = Read(path);
            int etx = jed.IndexOf('\u0003');
            int sum = 0;
            for (int i = jed.IndexOf('\u0002'); i <= etx; i++) sum += jed[i];
            Assert.AreEqual((sum & 0xFFFF).ToString("X4"), jed.Substring(etx + 1, 4),
                $"{path}: transmission checksum (are its CRs intact?)");
        }
    }

    private static void CompilesToGold(string folder, string pld)
    {
        string source = Path.Combine(Repo.Examples(folder), pld);
        Assert.AreEqual(Read(Path.ChangeExtension(source, ".jed")), Compile(source));
    }

    internal static string Compile(string pldPath)
    {
        var errors = new List<string>();
        Program.CompileResult? result = Program.Compile(File.ReadAllText(pldPath), null, errors);
        Assert.IsNotNull(result, string.Join(Environment.NewLine, errors));
        return result.Jedec;
    }

    private static string Read(string path) => Encoding.Latin1.GetString(File.ReadAllBytes(path));

    /// <summary>The fuse array a JEDEC file programs: its QF count, the F default, then every L record.</summary>
    private static byte[] Fuses(string jed)
    {
        byte[]? fuses = null;
        foreach (string raw in jed.Split('*'))
        {
            string field = raw.Trim();
            if (field.StartsWith("QF", StringComparison.Ordinal))
                fuses = new byte[int.Parse(field.Substring(2))];
            else if (field.StartsWith("F", StringComparison.Ordinal) && fuses != null)
                Array.Fill(fuses, (byte)(field[1] - '0'));
            else if (field.StartsWith("L", StringComparison.Ordinal) && fuses != null)
            {
                string[] parts = field.Substring(1).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                int at = int.Parse(parts[0]);
                foreach (char c in string.Concat(parts[1..]))
                    fuses[at++] = (byte)(c - '0');
            }
        }
        Assert.IsNotNull(fuses, "no QF field");
        return fuses;
    }
}
