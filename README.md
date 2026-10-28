# GalForge

[![build](https://github.com/sillycowvalley/GalForge/actions/workflows/build.yml/badge.svg)](https://github.com/sillycowvalley/GalForge/actions/workflows/build.yml)

**A small, readable CUPL compiler for the GAL16V8, GAL20V8 and GAL22V10.**

GalForge turns a CUPL-style `.pld` file into a JEDEC `.jed` fuse map you can burn into a
GAL with an ordinary programmer. It covers the three classic parts (and their Atmel ATF
equivalents) in every mode they have: simple, complex and registered on the 16V8 and 20V8,
and per-macrocell registered or combinational logic, output enables, asynchronous reset and
synchronous preset on the 22V10.

Why another one? WinCUPL still works, mostly, but it is a 1990s Windows program that is
no longer maintained, its error messages are cryptic, and it will happily compile a design
that does not do what you meant. GalForge is about 2,000 lines of C# you can read in an
evening, runs anywhere .NET runs, refuses ambiguous input with a plain message, and
programs the same logic as WinCUPL: an example of every mode of every part is checked
against a WinCUPL 5.0a fuse map on every build.

It has built every GAL in [Reggie-16](examples/Reggie16), a 16-bit TTL computer with 35
programmable parts on it.

## Getting it

Download `galforge-<version>-win-x64.exe` from the
[Releases](https://github.com/sillycowvalley/GalForge/releases) page and rename it
`galforge.exe`. It is one self-contained file: there is nothing to install. Linux and macOS
builds sit beside it.

If you have the .NET 8 SDK, it also installs as a .NET tool, which puts `galforge` on your
path and updates with one command:

```
dotnet tool install -g GalForge
```

To build it yourself, open `GalForge.sln` in Visual Studio 2022 and build; Test Explorer
runs the tests. Right-click the GalForge project and choose Publish to make the single
`.exe` (it lands in `publish\win-x64`). Without Visual Studio, `dotnet build` and
`dotnet test` in the repository folder do the same with the .NET 8 SDK.

## A first design

`decoder.pld`, a 2-to-4 decoder with an active-low enable:

```
Name     decoder;
PartNo   DEC24;
Device   g16v8;

/* A 2-to-4 decoder with an active-low enable. */

PIN 2  = A0;
PIN 3  = A1;
PIN 4  = !EN;

PIN 19 = !Y0;
PIN 18 = !Y1;
PIN 17 = !Y2;
PIN 16 = !Y3;

Y0 = EN & !A1 & !A0;
Y1 = EN & !A1 &  A0;
Y2 = EN &  A1 & !A0;
Y3 = EN &  A1 &  A0;
```

```
galforge decoder.pld
```

```
GAL16V8: 2194 fuses -> decoder.jed

  Pin  Output        Terms  Max  Kind  Polarity  OE
  19   Y0            1      8    comb  act-low   -
  18   Y1            1      8    comb  act-low   -
  17   Y2            1      8    comb  act-low   -
  16   Y3            1      8    comb  act-low   -
  Spare output pins: 12, 13, 14, 15
```

The fit report tells you how full each macrocell is, how GalForge configured it, and which
output pins you still have. `decoder.jed` is ready for the programmer.

Point GalForge at a folder and it compiles every `.pld` in it:

```
galforge Source -o Output
```

## What it understands

| | GAL16V8 / GAL20V8 | GAL22V10 |
| --- | --- | --- |
| Operators | `!` `&` `#` `$` and parentheses, CUPL precedence | same |
| Constants | `GND`, `VCC` | same |
| Intermediate nodes | yes: a name with no `PIN` is substituted where it is used | same |
| `.d` registered outputs | yes (registered mode, pin 1 clock, global /OE) | yes, per macrocell |
| `.oe` output enables | yes (complex mode, or combinational cells in registered mode) | yes, every macrocell |
| `.ar` / `.sp` | no (the parts have none) | yes, one global term each |
| Mode selection | automatic, from what the design uses | per macrocell |
| Product terms | 8 per output (7 where a row is the output enable) | 8 to 16, by pin |

Active-low pins are declared with `!` as in CUPL, and both the declaration and the equation
may carry one: the output's polarity is the XOR of the two.

What it refuses, each with an error saying so: number literals such as `'b'0` (write `GND`
or `VCC`), field and range notation (`[A0..7]`), `$DEFINE` and the other preprocessor
directives, and any output extension other than the four above. The
[manual](docs/GalForge_Manual.md) has the full language, every error message, and the fuse
maps of all three parts.

## Programming the chip

The `.jed` loads into any programmer that reads JEDEC files. On the XGecu T48 or TL866II+,
open it in Xgpro with the matching device selected (for example `GAL16V8D` or
`ATF22V10C`) and program. [Afterburner](https://github.com/ole00/afterburner) (an Arduino
GAL programmer) reads it too.

GalForge leaves the user signature (UES) erased, because some programmers verify that
region without writing it and then fail the burn.

## How it is checked

- `examples/Basic` holds a design for every mode of every part, each with the `.jed`
  GalForge writes and the `.jed` WinCUPL 5.0a wrote. Each macrocell must carry the same
  product terms as WinCUPL's, and every configuration fuse must match.
- `examples/Reggie16` holds the 35 GALs of a working computer; each must compile to the
  `.jed` beside it, byte for byte.
- The tests also run the command line, and hold a dozen of the errors the manual lists to
  their messages.

GitHub Actions runs them all on Windows and Linux for every push.

## Thanks

GalForge follows the CUPL language of WinCUPL (Microchip, formerly Atmel and Logical
Devices). The fuse maps are those of the Lattice GAL data sheets; GALasm and its
predecessor GALer by Christian Habermann, and Galette by Simon Frankau, are where the
author first read and checked them.

## Reporting a problem

If GalForge refuses a design it should take, or a chip doesn't do what its source says,
[open an issue](https://github.com/sillycowvalley/GalForge/issues/new/choose): the form
asks for what's needed to reproduce it. [CONTRIBUTING.md](CONTRIBUTING.md) covers building,
testing and pull requests.

## Licence

MIT. See [LICENSE](LICENSE).
