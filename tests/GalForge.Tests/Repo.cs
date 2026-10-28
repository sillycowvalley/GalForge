using System;
using System.IO;

namespace GalForge.Tests;

/// <summary>Paths into the repository the tests run from.</summary>
internal static class Repo
{
    public static readonly string Root = FindRoot();

    public static string Examples(string folder) => Path.Combine(Root, "examples", folder);

    private static string FindRoot()
    {
        for (DirectoryInfo? d = new(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "GalForge.sln")))
                return d.FullName;
        throw new InvalidOperationException("GalForge.sln not found above " + AppContext.BaseDirectory);
    }
}
