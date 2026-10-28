# Contributing to GalForge

Bug reports and pull requests are welcome. For a design that won't compile or compiles
wrong, the issue form asks for what's needed to reproduce it; a WinCUPL `.jed` of the same
source, if you have one, is the quickest way to find the difference.

## Building and testing

Open `GalForge.sln` in Visual Studio 2022, build, and run the tests from Test Explorer. With
only the .NET 8 SDK, `dotnet test` in the repository folder does the same. The tests take a
few seconds and must all pass before a pull request is merged; GitHub Actions runs them on
Windows and Linux.

## What a change brings with it

- **A test.** Every change in behaviour comes with a test that fails without it.
- **A WinCUPL gold for anything that moves fuses.** A change to what lands in the fuse map
  adds or updates an example in `examples/Basic`: the `.pld`, the `.jed` GalForge writes,
  and the `.jed` WinCUPL 5.0a writes in `examples/Basic/WinCUPL`.
  `ABasicExampleMatchesWinCuplFuseForFuse` then holds the two together.
- **Golds regenerated, not edited.** `.jed` files are stored exactly as written (their
  checksum counts the CRs), so write them with GalForge rather than by hand.
- **The manual.** A change to the language, the command line or a message changes
  `docs/GalForge_Manual.md` in the same pull request, error catalogue included.

## Style

Match the code around you: C#, nullable enabled, warnings are errors, and comments that
explain why rather than what.
