# GalForge Reference Manual

**A CUPL-subset logic compiler for the GAL16V8, GAL20V8 and GAL22V10**

GalForge compiles a CUPL-subset `.pld` source file into a JESD3 (`.jed`) fuse map for
the GAL16V8, GAL20V8, and GAL22V10 (and their ATF/fuse-compatible equivalents). It is a
deliberately small, single-purpose reimplementation of the classic CUPL flow: parse,
resolve device, build a sum-of-products fuse array, emit JEDEC. Its `*`-field area is laid
out the way WinCUPL lays it out, and it programs the same logic as WinCUPL's fuse for fuse:
each macrocell gets the same product terms (perhaps in another row order) and every
architecture fuse agrees ([§11](#11-divergences-from-wincupl)).

This manual is the authoritative description of what the compiler actually does. Every
rule below is drawn from the compiler source, not from CUPL in general — where GalForge
and stock CUPL/WinCUPL differ, that difference is called out explicitly.

---

## Table of contents

1. [Command-line interface](#1-command-line-interface)
2. [The compilation pipeline](#2-the-compilation-pipeline)
3. [The `.pld` source language](#3-the-pld-source-language)
   - 3.1 [File structure](#31-file-structure)
   - 3.2 [Header properties](#32-header-properties)
   - 3.3 [PIN declarations](#33-pin-declarations)
   - 3.4 [Equations and expressions](#34-equations-and-expressions)
   - 3.5 [Operator precedence and SOP flattening](#35-operator-precedence-and-sop-flattening)
   - 3.6 [Constants: `GND` and `VCC`](#36-constants-gnd-and-vcc)
   - 3.7 [Number literals are rejected](#37-number-literals-are-rejected)
   - 3.8 [Intermediate nodes](#38-intermediate-nodes)
   - 3.9 [Output extensions](#39-output-extensions-d-oe-ar-sp)
   - 3.10 [Comments](#310-comments)
4. [Target devices](#4-target-devices)
5. [Polarity model](#5-polarity-model)
6. [GAL16V8 / GAL20V8 compilation](#6-gal16v8--gal20v8-compilation)
7. [GAL22V10 compilation](#7-gal22v10-compilation)
8. [Feedback rules](#8-feedback-rules)
9. [JEDEC output format](#9-jedec-output-format)
10. [Console output: warnings and the fit report](#10-console-output-warnings-and-the-fit-report)
11. [Divergences from WinCUPL](#11-divergences-from-wincupl)
12. [What is not supported](#12-what-is-not-supported)
13. [Error message catalogue](#13-error-message-catalogue)
14. [Appendix A — fuse-map geometry](#appendix-a--fuse-map-geometry)
15. [Appendix B — pin-to-column tables](#appendix-b--pin-to-column-tables)

---

## 1. Command-line interface

GalForge is a console application. It takes one input — a single `.pld` file or a folder
of them — and writes one `.jed` per input.

```
galforge <input.pld> [-o <output>] [-d <device>]
galforge <folder>    [-o <folder>] [-d <device>]
galforge --version
```

### Options

| Option | Meaning |
| --- | --- |
| `-o <output>` | Output `.jed` file, **or** an existing folder to write into. With no `-o`, the `.jed` is written alongside the input with its extension changed to `.jed`. |
| `-d <device>` | Target device, overriding the `.pld` header. One of `G16V8`, `G20V8`, `G22V10`. |
| `-h`, `--help`, `/?`, `-?` | Print usage and exit 0. |
| `-v`, `--version` | Print `GalForge <version>` and exit 0. |

### Output-path resolution

For each input file the `.jed` destination is chosen as follows:

- **No `-o`** — alongside the source, extension changed to `.jed`.
- **`-o <existing folder>`** — that folder, named `<input-without-extension>.jed`.
- **`-o <filename>`** — that path, used exactly as given (single-file input only).

### Folder input

A folder input compiles **every `*.pld` in the directory, non-recursively**, matching the
extension in any letter case (`ALU.PLD` too, on every platform), in case-insensitive
filename order. When the input is a folder, any `-o` must itself be an
existing folder — a single filename cannot hold many outputs and is rejected. A folder
with no `.pld` files is an error.

### Exit codes

`0` on success; `1` on any failure (bad arguments, a missing input, or a compile error).
For a folder, the exit code is `1` if **any** file failed; the run continues through the
remaining files regardless, printing a `--- filename ---` banner per file and a final
`Compiled N of M file(s)[, K failed].` summary.

### On success

For each compiled file GalForge prints:

```
GAL22V10: 5892 fuses -> C:\path\ALU_R16.jed
```

followed by any [unused-pin warnings](#10-console-output-warnings-and-the-fit-report) and
the [fit report](#10-console-output-warnings-and-the-fit-report).

---

## 2. The compilation pipeline

Four stages, run in order; the first stage to record an error aborts the file.

```
.pld source
   -> Stage 1  PldParser.Parse       tokens  -> pin map + boolean equations
   -> Stage 2  TargetDevice.Resolve  name    -> G16V8 / G20V8 / G22V10
   -> Stage 3  Compiler.Compile      SOP     -> device fuse array
   -> Stage 4  JedecWriter.Write     fuses   -> JESD3 .jed text
.jed output
```

| Stage | Responsibility |
| --- | --- |
| **1 Parse** | Strip comments, split on `;`, read header properties and PIN declarations, expand intermediate nodes, flatten every equation's right-hand side to a sum-of-products. |
| **2 Resolve** | Map the device name (from the header, or `-d`) to a family. |
| **3 Compile** | Select the GAL mode (V8) or program per-macrocell config (22V10); place product terms into fuse rows; set polarity and architecture bits. |
| **4 Emit** | Render the fuse array as `.jed`, WinCUPL-aligned in the `*`-field area. |

The parser and the compilers deliberately split concerns: **the parser records syntax; the
compilers enforce device semantics.** For example, the parser happily records a `.ar`
extension on any device; it is Stage 3 that rejects `.ar` on a V8 part.

---

## 3. The `.pld` source language

### 3.1 File structure

Source is a free-form stream of **statements terminated by `;`**. Whitespace (including
newlines) is insignificant except as a token separator — a statement may span lines and is
collapsed to single spaces before parsing. Each statement is classified by its first token:

- a **header keyword** → a header property;
- the word **`PIN`** → a pin declaration;
- anything containing **`=`** → an equation (or node definition);
- anything else → the error `Unrecognised statement: "..."`.

A valid file must specify a device and contain at least one output equation; otherwise
`No Device specified in the .pld header.` and/or `No output equations found.` are raised.

### 3.2 Header properties

The recognised header keywords are:

```
NAME  PARTNO  DATE  REVISION  DESIGNER  COMPANY  ASSEMBLY  LOCATION  DEVICE
```

All nine are accepted and consume their statement without error, but only three are
retained and only two reach the output:

| Keyword | Stored as | Used for |
| --- | --- | --- |
| `DEVICE` | `DeviceName` | Stage 2 device resolution (unless overridden by `-d`). |
| `NAME` | `Title` | The `Name:` line in the `.jed` free-text header. |
| `PARTNO` | `PartNo` | Captured but **not emitted** — see below. |
| the other six | — | Ignored (accepted for CUPL-source compatibility). |

> **Note on `PARTNO`.** Stock CUPL writes the first characters of `PartNo` into the user
> signature (UES). GalForge always emits the signature/UES **erased** (see
> [§11](#11-divergences-from-wincupl)), so `PARTNO` never reaches the fuse map. It is parsed
> only so existing CUPL headers compile unchanged.

Header values are free text up to the terminating `;`. Example header:

```
Name     ALU_REGGIE;
PartNo   ALU_REGGIE_01;
Device   g16v8;
```

### 3.3 PIN declarations

```
PIN <number> = [!] <name> ;
```

The grammar is exactly: the word `PIN` (case-insensitive), a decimal pin number, `=`, an
optional single leading `!` for active-low, then a signal name. A name begins with a letter
or underscore and continues with letters, digits, or underscores. Anything that does not
match this shape yields `Malformed PIN declaration: "..."`.

```
PIN 2  = FC0;      /* active-high input on pin 2   */
PIN 14 = !CN;      /* active-low  output on pin 14 */
```

The leading `!` records the pin's **declared polarity**. This folds into the output
polarity of any equation targeting the pin, and into the literal polarity wherever the
signal is read as an input — see [§5](#5-polarity-model).

A signal name declared on two pins is caught at compile time:
`Pin name 'X' is declared more than once.`

### 3.4 Equations and expressions

An equation assigns a boolean expression to a target:

```
<target>[.ext] = <expression> ;
```

The target is a declared pin name, optionally prefixed with `!` (output complement) and
optionally suffixed with an [output extension](#39-output-extensions-d-oe-ar-sp). A target
that is **not** a declared pin and is a plain name (no `!`, no extension) is treated as an
[intermediate node](#38-intermediate-nodes) instead.

The right-hand side is any boolean expression over the operators below. It is flattened to
a sum-of-products (an OR of AND-of-literals) during parsing, so the fuse mapper downstream
only ever sees that canonical form.

### 3.5 Operator precedence and SOP flattening

| Operator | Meaning | Binding |
| --- | --- | --- |
| `!` | NOT | tightest |
| `&` | AND | |
| `#` | OR | |
| `$` | XOR | loosest |
| `( )` | grouping | — |

So `!A & B # C` parses as `((!A) & B) # C`, and `A # B $ C` as `(A # B) $ C`. This is the
precedence the compiler implements (highest-to-lowest `!`, `&`, `#`, `$`); parenthesise
whenever intent could be ambiguous to a reader.

Flattening is exact and order-preserving:

- **OR** concatenates term lists, preserving source order — a file already written as flat
  SOP keeps its exact term ordering and therefore its exact fuse output.
- **AND** is a cartesian merge of terms. A term that would contain both a literal and its
  complement (`x & !x`) is a contradiction and is dropped.
- **NOT** is De Morgan: `!(t1 # t2 # ...)` becomes `!t1 & !t2 & ...`.
- **XOR** expands to `(a & !b) # (!a & b)`.

Because flattening can collapse an expression to a constant, two degenerate results are
caught and redirected to the explicit constant keywords:

- An empty sum (no product terms) → `Expression reduces to constant 0 ... Use GND ...`.
- A term with no literals (always true) → `Expression reduces to a constant (always true) ... Use VCC ...`.

This is intentional: a bare constant should be written as `GND`/`VCC`, never arrived at by
accident through an expression that happens to cancel.

### 3.6 Constants: `GND` and `VCC`

`GND` and `VCC` are reserved constant operands for a deliberately driven output:

| Keyword | SOP meaning | Fuse encoding (buffer active-high) |
| --- | --- | --- |
| `GND` | empty OR (constant 0) | **no** product terms — every row of the block stays intact; the OR is empty; the pin drives 0. |
| `VCC` | single empty AND (constant 1) | one all-blown (always-true) row; the pin drives 1. |

A leading `!` on the target flips the drive through the ordinary polarity XOR
([§5](#5-polarity-model)), so `!X = GND` drives **1** and `!X = VCC` drives **0**.

```
BKILL = GND;       /* pin driven hard low  */
READY = VCC;       /* pin driven hard high */
```

A constant is honoured **only** when the whole expression collapses to it. `GND` or `VCC`
mixed into a larger expression (e.g. `GND # T0`) simplifies naturally in the normal path.
`GND`/`VCC` are **not** valid on a `.oe`, `.ar`, or `.sp` equation (those require a real
product term): `Output 'X': GND/VCC is not valid on a .oe/.ar/.sp equation.`

`GND`/`VCC` are also reserved against use as node names.

> This all-blown-row-equals-true convention for `VCC`, and empty-OR for `GND`, is the single
> encoding the whole toolchain relies on. It replaces an older all-blown-row-with-inverted-
> polarity trick for driven-low outputs.

### 3.7 Number literals are rejected

CUPL radixed number literals — `'b'0`, `'h'1`, `'d'0`, and so on — are **recognised in
equations only to be rejected**, with a hard error:

```
Number literal "'b'0" in "BKILL = 'b'0": number literals are not supported
in equations. Use GND for a deliberate driven-low output or VCC for driven-high.
```

The tokenizer sees the leading `'`, consumes the whole literal so it can quote it back in
full, and fails. This is a deliberate guard: a silent mis-generation from `'b'0` once
produced a stuck output, so every numeric constant in an equation is steered to the
unambiguous `GND`/`VCC` keywords instead. Multi-bit values and `'x'` don't-cares (which
belong to CUPL field notation) are likewise unsupported.

> **Compatibility note.** `'b'0` is legal in stock WinCUPL; `GND`/`VCC` as keywords are not
> standard CUPL. This is a genuine dialect divergence — a source file cannot be fed to both
> tools unchanged if it uses either construct for a driven constant.

### 3.8 Intermediate nodes

A `=` statement whose target is a plain name that is **not** a declared pin defines an
**intermediate node** — a named sub-expression, substituted into every expression that
references it.

```
PIN 15 = M;
PIN 16 = S0;

arith = !FC3 & FC2;        /* node: not a pin  */

M  = arith # FC3 & FC0;    /* 'arith' expands in place */
S0 = arith & !FC1;
```

Rules:

- A node target carries **no** `.d`/`.oe`/`.ar`/`.sp` extension and **no** leading `!`.
  Polarity belongs on pins; write the complement into the referencing expression instead.
  (`'!' and extensions are not valid on an intermediate node`.)
- `GND` and `VCC` are reserved and cannot be node names.
- A node may reference another node; bodies expand to a fixed point (up to 32 levels). A
  cycle is reported: `Circular node definition involving 'X'.`
- A node defined but never referenced is an error:
  `Node 'X' is defined but never referenced.`
- Each node body is substituted as a parenthesised group, so precedence is preserved.

### 3.9 Output extensions: `.d`, `.oe`, `.ar`, `.sp`

An output name may take one dotted extension. The parser records it; the compilers enforce
where each is legal.

| Ext | Meaning | Valid on |
| --- | --- | --- |
| *(none)* | combinational output | all devices |
| `.d` | registered output (D input = the SOP) | all devices |
| `.oe` | per-macrocell output-enable term (exactly one product term) | V8 complex/registered combinational cells; **every** 22V10 macrocell |
| `.ar` | global asynchronous reset term | **22V10 only**, registered outputs |
| `.sp` | global synchronous preset term | **22V10 only**, registered outputs |

Any other extension is rejected at parse time:
`Output extension '.X' not supported (only .d, .oe, .ar and .sp)`. A `!` is not allowed on a
`.oe`/`.ar`/`.sp` equation.

Device-specific constraints (single-term `.oe`, identical global `.ar`/`.sp` terms, `.ar`/
`.sp` only on registered outputs, `.oe` unavailable on a V8 registered output) are covered
in [§6](#6-gal16v8--gal20v8-compilation) and [§7](#7-gal22v10-compilation).

### 3.10 Comments

Two forms, following the CUPL rules:

- `//` to end of line.
- `/* ... */` block comments, **non-nesting** — the first `*/` closes the block.

Double-slash inside a block, or a block that runs off the end of the file, behaves per those
rules (a block with no closing `*/` simply consumes to EOF).

---

## 4. Target devices

The device is taken from the `DEVICE` header, or from `-d` which overrides it. Resolution
normalises the name (upper-case, spaces removed) and matches on the **family core**:

| If the name contains | Resolves to | QF (fuses) | QP (pins) |
| --- | --- | --- | --- |
| `16V8` | GAL16V8 | 2194 | 20 |
| `20V8` | GAL20V8 | 2706 | 24 |
| `22V10` | GAL22V10 | 5892 | 24 |

This accepts every CUPL spelling and architecture suffix that selects the same fuse map:
bare names (`G16V8`), vendor prefixes (`GAL`/`ATF`/`P`), and mode suffixes (`S`/`A`/`AS`/`MS`,
as in `G16V8S`, `G20V8A`). **The suffix's mode hint is ignored** — GalForge selects the GAL
mode itself from what the design needs ([§6](#6-gal16v8--gal20v8-compilation)). `22V10`
covers `g22v10`, `GAL22V10`, and the fuse-compatible `ATF22V10` alike.

An empty name gives `No target device given (-d) or found in the .pld header.`; any other
name gives `Unsupported device: 'X'. Only the 16V8, 20V8 and 22V10 families are supported.`

---

## 5. Polarity model

Output polarity is the **XOR of two CUPL notations**:

```
effective active-low  =  (PIN-declaration '!')  XOR  (equation-LHS '!')
```

So a pin declared low with a plain equation is active-low; declaring it low **and** putting
`!` on the equation cancels back to active-high. On the V8 an effectively active-high output
sets its XOR fuse; on the 22V10 the S0 bit encodes it (S0 = 1 active-high, 0 active-low). In
both cases the AND array always holds the positive-logic expression; only the output-buffer
polarity changes.

The **same declared `!` also folds into literal polarity** when a signal is read as an
input: a signal declared active-low carries the inverted sense on its pin, so referencing it
connects the complement column, as WinCUPL does. (Feedback from a
registered output is the one case where the declared `!` does *not* fold — see
[§8](#8-feedback-rules).)

---

## 6. GAL16V8 / GAL20V8 compilation

The V8 families share one code path and one 64-row AND array (16V8: 64×32; 20V8: 64×40).
Both cover all three GAL configurations, and **the mode is chosen automatically** from what
the design uses — the CUPL device suffix is ignored.

### Mode selection

| Mode | SYN / AC0 | Chosen when | Terms/output |
| --- | --- | --- | --- |
| **1 Simple** | 1 / 0 | nothing below applies | 8 |
| **2 Complex** | 1 / 1 | any `.oe`, **or** combinational feedback (an output read as an input), **or** an input on a pin with no simple-mode feedback line | 7 (row 0 is the OE term) |
| **3 Registered** | 0 / 1 | any `.d` equation | registered cell: 8; combinational cell: 7 |

### Pin roles

| Role | 16V8 | 20V8 |
| --- | --- | --- |
| Output-capable (OLMC) pins | 12–19 | 15–22 |
| Global clock (mode 3) | 1 | 1 |
| Global `/OE` (mode 3) | 11 | 13 |
| No simple-mode feedback line → forces complex if used as input | 15, 16 | 18, 19 |
| Output-only in complex (no feedback; cannot be an input) | 12, 19 | 15, 22 |

In **registered mode**, the clock pin and the `/OE` pin cannot appear in any equation
(each raises a specific error). A **registered output** (AC1 = 0) owns all 8 rows of its
block and is enabled by the global `/OE`, so `.oe` is not available on it
(`a registered output uses the global /OE pin; .oe is not available`). A **combinational
output inside registered mode** (AC1 = 1) behaves like a complex-mode cell: OE term in row
0, 7 logic terms.

### Term budgets

- Simple: 8 product terms per output.
- Complex, and combinational cells in registered mode: row 0 is the output-enable term (a
  supplied `.oe`, or all-1s meaning always-enabled), leaving **7** logic terms.
- Registered cell: all **8** rows are logic.

Exceeding the budget is an error naming the count and the limit.

### Architecture and OLMC bits

- **XOR** fuse per OLMC set for an effectively active-high output.
- **PT** (product-term disable) is all-1s in every mode.
- **AC1**: simple → 1 marks a spare OLMC as an input, 0 a used output; complex → 1 for every
  cell; registered → 0 marks a registered cell, 1 a combinational or spare one.
- **SYN/AC0** per the mode table above.

### `.oe` on the V8

One `.oe` per target, exactly one product term, and the target must have a matching output
equation. A missing/duplicate/multi-term `.oe` is an error. `.oe` is meaningful only on a
complex-mode cell or a combinational cell in registered mode; on a simple-mode design the
presence of any `.oe` is itself what forces complex mode.

---

## 7. GAL22V10 compilation

The 22V10 (and fuse-compatible ATF22V10) has **no global modes**: each of the ten
macrocells is configured independently by its own S0/S1 pair.

| Bit | 1 | 0 |
| --- | --- | --- |
| **S1** | combinational | registered |
| **S0** | active-high | active-low |

The AND array is 132 rows × 44 columns:

- **Row 0** — the global asynchronous-reset (AR) product term.
- **Rows 1–130** — ten OLMC blocks in pin order **23 down to 14**, each `[1 OE row, then its
  logic rows]`.
- **Row 131** — the global synchronous-preset (SP) product term.

Per-macrocell logic term counts vary by position (OLMC index = `23 − pin`):

| OLMC index | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Pin | 23 | 22 | 21 | 20 | 19 | 18 | 17 | 16 | 15 | 14 |
| Logic terms | 8 | 10 | 12 | 14 | 16 | 16 | 14 | 12 | 10 | 8 |

### Output-enable

Every macrocell owns its own OE row — there is **no global `/OE` pin** on this family — so
`.oe` is valid on **both** combinational and registered outputs. Exactly one product term.
An absent `.oe` leaves the OE row all-1s (a product of nothing = always enabled). An unused
macrocell keeps its erased (all-0) OE row, which can never be true, so erased cells never
drive — no special-casing needed.

### Global `.ar` / `.sp`

The device has exactly **one** physical AR row and **one** SP row, so these are stated
CUPL-style: once per registered output, a **single product term**, and **identical** across
every output that states them. They are valid **only on registered (`.d`) outputs**.
Violations are reported specifically (no matching output, not registered, more than one
term, or terms that differ between outputs).

### Pins

Pins **12** and **24** are GND/VCC. Pin **1** is both the register clock and array input
line 0, so — unlike V8 registered mode — it **may** appear in equations. Output-capable
pins are **14–23**; a target outside that range, or driven by more than one equation, is an
error.

### Input-only macrocells

A macrocell pin read as a plain input (no equation of its own) is given **S1 = 1** so its
feedback taps the pin rather than a never-clocked register, and **S0 = 1** (don't-care for
an undriven cell). The S0 choice here is the one detail not yet pinned to a WinCUPL gold.

---

## 8. Feedback rules

Where a macrocell pin is read as an input, the column its literal connects to — and hence
the literal's polarity handling — depends on what the pin's feedback taps:

- **Registered output's feedback** comes from the register (`/Q`, the even column),
  independent of OE and of the pin's polarity buffer. The literal polarity **inverts**, and
  the declared `!` does **not** fold in.
- **Combinational output's feedback** comes from the pin, so a declared `!` folds into the
  literal like any input's.
- **A plain input on a macrocell pin** taps the pin; on the 22V10 this requires S1 = 1 (see
  [§7](#7-gal22v10-compilation)).

On the V8, feedback literals are programmed like any other pin literal — the per-mode
pin-to-column tables ([Appendix B](#appendix-b--pin-to-column-tables)) already encode where
each OLMC's feedback lands in each configuration.

---

## 9. JEDEC output format

Stage 4 renders the fuse map as `.jed` text. The `*`-field area — the part a device
programmer reads — is laid out the way WinCUPL lays it out: the same fields, in the same order,
with the same spacing and the same 32-fuse records. Line endings are CRLF throughout, with a single trailing LF after the
transmission checksum.

Structure, in order:

1. **STX**, then free-text header (GalForge's own, before the first `*`):
   ```
   Used Program:   GalForge
   Name:           <Title, if any>
   Device:         <device name>
   Pins:           <n:name tokens, sorted by pin, '!' kept for active-low>
   ```
   Pin lines wrap at column 76, each wrapped line repeating the `Pins:` label so a reader
   can parse line-by-line. These names are carried so downstream tools (a simulator's GAL
   symbol, a chip-label printer) can recover signal names — standard JEDEC has no pin-name
   field.
2. `*QP<pins> ` — package pin count (trailing space, as WinCUPL writes it).
3. `*QF<fuses> ` — fuse count (trailing space).
4. `*G0 ` — security fuse off.
5. `*F0 ` — default fuse state (unlisted fuses are 0).
6. `*L<nnnnn> <bits>` — fuse records. The **entire** linear map is tiled into 32-fuse,
   32-aligned chunks; chunks that are entirely default (all 0) are omitted; the address is
   5 digits; the final chunk may be short.
7. `*C<hhhh>` — fuse checksum: 16-bit sum of all fuses packed 8 per byte, LSB first,
   upper-case hex.
8. `*` immediately followed by **ETX** (no line break between).
9. **Transmission checksum** — 16-bit sum of every byte from STX to ETX inclusive,
   upper-case hex — then a single trailing LF.

The one rule "tile the linear fuse map into 32-fuse chunks, skip all-zero chunks"
reproduces WinCUPL's logic *and* architecture records for every supported family, because
each family's region bases already fall on the same fuse numbers in the flattened map (see
[Appendix A](#appendix-a--fuse-map-geometry)).

> A value of 1 in the map emits `1` in the `.jed`; 0 emits `0`. The default (unlisted) state
> is 0, declared by `*F0`. The meaning of 1 vs 0 is the device's; the mapper sets fuses to
> match it.

---

## 10. Console output: warnings and the fit report

### Unused-pin warnings

After a successful compile, any declared pin that no equation targets or references is
flagged (non-fatal):

```
warning: pin 7 ('A7') is declared but never used.
```

An intentionally reserved input — wired on the board for a future revision — is legitimate;
the warning *is* the documentation of that intent.

### Fit report

A per-output table generated from the same parsed document the fuse map came from, so it
cannot drift from the JEDEC:

```
  Pin  Output        Terms  Max  Kind  Polarity  OE
  15   M             4      8    comb  act-high  -
  14   CN            3      8    comb  act-low   -
  ...
  Spare output pins: 17, 18
```

| Column | Meaning |
| --- | --- |
| **Pin / Output** | pin number and target name |
| **Terms** | product terms this output uses |
| **Max** | product-term budget for that cell (V8: 8 simple / 8 registered-`.d` / else 7; 22V10: the per-block count) |
| **Kind** | `reg` (registered) or `comb` (combinational) |
| **Polarity** | `act-high` / `act-low`, the XOR of the pin and equation `!` |
| **OE** | `term` if the output has a `.oe`, else `-` |

The **Max** column is read from the compiled map itself (SYN/AC0 for the V8), so it reflects
the mode the compiler actually chose. Output-capable pins that no PIN declaration names are
listed at the end as spare; a pin declared as an input is in use, though no equation drives
it.

---

## 11. Divergences from WinCUPL

GalForge programs the same logic as WinCUPL, but a few things differ **by design**:

| Area | GalForge | Why |
| --- | --- | --- |
| **User signature / UES** | emitted **erased** (all 1s) | Some programmers (observed: Xgecu T48 / APP 13.16 on GAL16V8D) verify but do not program the UES region; any 0 bit there aborts the burn with a verify error. An all-1s UES matches the erased state and verifies whether or not the programmer writes it. The UES is a cosmetic label; nothing functional reads it. |
| **Product-term row order** | may differ within a macrocell | The OR of a macrocell's terms does not depend on which row holds which term; GalForge places them in source order. |
| **Fuse checksum `*C`** | differs when WinCUPL wrote a signature | It sums every fuse, and the signature fuses differ (row above). |
| **Free-text header** | GalForge's own block before the first `*` | Not a `*` line, so it does not affect the fuse area or the `*C` fuse checksum. |
| **Transmission checksum** | differs from WinCUPL | Correct: it is a checksum over the *entire* transmission, header and all, and the header text differs. |
| **Number literals** | rejected; use `GND`/`VCC` | WinCUPL accepts `'b'0` etc.; GalForge steers driven constants to explicit keywords ([§3.7](#37-number-literals-are-rejected)). |
| **Device mode suffix** | ignored; mode chosen automatically | The suffix only told CUPL which mode to auto-pick; GalForge decides itself ([§4](#4-target-devices), [§6](#6-gal16v8--gal20v8-compilation)). |

The fuse-map geometry for all three families is the published GAL layout, validated against
WinCUPL 5.0a `.jed` golds for every mode of every family (`examples/Basic/WinCUPL`). The
test `ABasicExampleMatchesWinCuplFuseForFuse` holds each example to its gold: every fuse past
the array equal (the signature apart, which GalForge leaves erased), and each macrocell's
product terms the same set.

---

## 12. What is not supported

Each of these registers an error if encountered:

- **Output extensions** other than `.d`, `.oe`, `.ar`, `.sp`.
- **`.ar` / `.sp` on a V8 part** (22V10-only).
- **`GND`/`VCC` on a `.oe`/`.ar`/`.sp` equation** (those need a real product term).
- **Number literals** in equations (`'b'0`, `'h'F`, `'d'0`, `'x'` don't-cares, multi-bit
  values) — [§3.7](#37-number-literals-are-rejected).
- **Set / range / index notation** and **function calls** (`[A0..7]`, field operations).
- **`$` preprocessor directives** (`$DEFINE`, `$REPEAT`, `$MACRO`, …). Note this is distinct
  from the `$` **XOR operator**, which *is* supported.
- **Recursive folder input** — folder compilation is one level, non-recursive.

Intermediate **nodes** ([§3.8](#38-intermediate-nodes)) cover the common case that a CUPL
author would otherwise reach for `$DEFINE` to solve.

---

## 13. Error message catalogue

Grouped by the stage that raises them. `"..."` is the offending statement (truncated to 44
characters); bracketed parts vary.

### Stage 1 — parser

| Message |
| --- |
| `Unrecognised statement: "..."` |
| `Malformed PIN declaration: "..."` |
| `No Device specified in the .pld header.` |
| `No output equations found.` |
| `Output 'X' has no PIN declaration ('!' and extensions are not valid on an intermediate node): "..."` |
| `'X' is a reserved constant and cannot be a node: "..."` |
| `Node 'X' is defined more than once.` |
| `Circular node definition involving 'X'.` |
| `Node 'X' is defined but never referenced.` |
| `Output extension '.X' not supported (only .d, .oe, .ar and .sp): "..."` |
| `Bad equation target: "..."` |
| `Output 'X': '!' is not valid on a .oe/.ar/.sp equation.` |
| `Output 'X': GND/VCC is not valid on a .oe/.ar/.sp equation.` |
| `Expression reduces to constant 0 (no product terms): "...". Use GND for a deliberate driven-low output.` |
| `Expression reduces to a constant (always true): "...". Use VCC for a deliberate driven-high output.` |
| `Number literal "..." in "...": number literals are not supported in equations. Use GND for a deliberate driven-low output or VCC for driven-high.` |
| `Bad character 'c' in "..."` |
| `Empty expression in "..."` |
| `Malformed expression (expected an operand) in "..."` |
| `Malformed expression (missing ')') in "..."` |
| `Unexpected 'tok' in "..."` |

### Stage 2 — device resolution

| Message |
| --- |
| `No target device given (-d) or found in the .pld header.` |
| `Unsupported device: 'X'. Only the 16V8, 20V8 and 22V10 families are supported.` |

### Stage 3 — compile (both paths)

| Message |
| --- |
| `Pin name 'X' is declared more than once.` |
| `Output 'X.oe' has no matching output equation.` |
| `Output 'X' has more than one .oe equation.` |
| `Output 'X.oe': the output enable is a single product term (got N).` |
| `Output 'X': undeclared signal 'S'.` |
| `Output 'X': pin N ('S') cannot be an input here.` |
| `Output 'X' has no PIN declaration.` |
| `Output 'X' (pin N) is not an output-capable pin (first-last).` |
| `Output 'X' (pin N) is driven by more than one equation.` |

### Stage 3 — V8 only

| Message |
| --- |
| `Output 'X': .ar is only supported on the GAL22V10.` (and `.sp`) |
| `Output 'X': pin N is the global clock in registered mode and cannot be used in an equation.` |
| `Output 'X': pin N is the global /OE in registered mode and cannot be used in an equation.` |
| `Output 'X': pin N ('S') has no feedback in complex mode.` |
| `Output 'X': N product terms exceed the M available per output[ (one term is the output enable)].` |
| `Output 'X': a registered output uses the global /OE pin; .oe is not available.` |

### Stage 3 — 22V10 only

| Message |
| --- |
| `Output 'X.ar' has no matching output equation.` (and `.sp`) |
| `Output 'X.ar': .ar is only valid on a registered (.d) output.` (and `.sp`) |
| `Output 'X.ar': the async reset is a single product term (got N).` (and `.sp`, sync preset) |
| `Output 'X.ar': all .ar terms must be identical (the device has one global AR row).` (and `.sp`/SP) |
| `Output 'X': N product terms exceed the M available on pin P.` |

---

## Appendix A — fuse-map geometry

### GAL16V8 — QF 2194, 20 pins, array 64 × 32

| Region | Base | Size |
| --- | --- | --- |
| AND array (logic) | 0 | 2048 |
| XOR (polarity) | 2048 | 8 |
| Signature (UES) | 2056 | 64 |
| AC1 | 2120 | 8 |
| PT (product-term disable) | 2128 | 64 |
| SYN | 2192 | 1 |
| AC0 | 2193 | 1 |

### GAL20V8 — QF 2706, 24 pins, array 64 × 40

| Region | Base | Size |
| --- | --- | --- |
| AND array (logic) | 0 | 2560 |
| XOR | 2560 | 8 |
| Signature (UES) | 2568 | 64 |
| AC1 | 2632 | 8 |
| PT | 2640 | 64 |
| SYN | 2704 | 1 |
| AC0 | 2705 | 1 |

### GAL22V10 — QF 5892, 24 pins, array 132 × 44

| Region | Base | Size |
| --- | --- | --- |
| AND array (logic) | 0 | 5808 |
| S0/S1 pairs (pin 23 first; S0 even, S1 odd) | 5808 | 20 |
| UES | 5828 | 64 |

Within the 22V10 array: `fuse n = row n / 44, column n % 44`. Row 0 is AR; rows 1–130 are the
ten OLMC blocks (each an OE row then its logic rows); row 131 is SP. OLMC block start rows
(index = `23 − pin`) are `1, 10, 21, 34, 49, 66, 83, 98, 111, 122`. WinCUPL emits QF5892
(the UES is present); the array is identical to a QF5828 map.

**Linear order for JEDEC emission.** V8: Logic, XOR, Sig, AC1, PT, SYN, AC0. 22V10: Logic,
S-bits, UES.

---

## Appendix B — pin-to-column tables

Array-input column for each pin, by mode (index = pin − 1; `-1` = no column / GND / VCC).
The true line is at the listed column, its complement at column + 1. They are the devices'
fixed input routing, as the data sheets' logic diagrams draw it.

### GAL16V8

| Pin | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11 | 12 | 13 | 14 | 15 | 16 | 17 | 18 | 19 | 20 |
| --- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- |
| Simple (1) | 2 | 0 | 4 | 8 | 12 | 16 | 20 | 24 | 28 | -1 | 30 | 26 | 22 | 18 | -1 | -1 | 14 | 10 | 6 | -1 |
| Complex (2) | 2 | 0 | 4 | 8 | 12 | 16 | 20 | 24 | 28 | -1 | 30 | -1 | 26 | 22 | 18 | 14 | 10 | 6 | -1 | -1 |
| Registered (3) | -1 | 0 | 4 | 8 | 12 | 16 | 20 | 24 | 28 | -1 | -1 | 30 | 26 | 22 | 18 | 14 | 10 | 6 | 2 | -1 |

### GAL20V8

| Pin | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11 | 12 | 13 | 14 | 15 | 16 | 17 | 18 | 19 | 20 | 21 | 22 | 23 | 24 |
| --- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- |
| Simple (1) | 2 | 0 | 4 | 8 | 12 | 16 | 20 | 24 | 28 | 32 | 36 | -1 | 38 | 34 | 30 | 26 | 22 | -1 | -1 | 18 | 14 | 10 | 6 | -1 |
| Complex (2) | 2 | 0 | 4 | 8 | 12 | 16 | 20 | 24 | 28 | 32 | 36 | -1 | 38 | 34 | -1 | 30 | 26 | 22 | 18 | 14 | 10 | -1 | 6 | -1 |
| Registered (3) | -1 | 0 | 4 | 8 | 12 | 16 | 20 | 24 | 28 | 32 | 36 | -1 | -1 | 38 | 34 | 30 | 26 | 22 | 18 | 14 | 10 | 6 | 2 | -1 |

### GAL22V10

Single fixed routing (no modes); index = pin − 1; `-1` = GND/VCC (pins 12, 24):

| Pin | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11 | 12 | 13 | 14 | 15 | 16 | 17 | 18 | 19 | 20 | 21 | 22 | 23 | 24 |
| --- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- | -- |
| Column | 0 | 4 | 8 | 12 | 16 | 20 | 24 | 28 | 32 | 36 | 40 | -1 | 42 | 38 | 34 | 30 | 26 | 22 | 18 | 14 | 10 | 6 | 2 | -1 |

---

*This manual describes GalForge as implemented in `src/GalForge`: `Program.cs`, `PldModel.cs`,
`Compiler.cs`, `Compiler22V10.cs`, `TargetDevice.cs`, `FuseMap.cs`, and `JedecWriter.cs`.*
