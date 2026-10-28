# GAL22V10 Fuse-Map Geometry

The GAL22V10 / ATF22V10 fuse map as GalForge writes it, proven against two WinCUPL 5.0a
golds (`examples/Basic/WinCUPL/gal22v10_combinational.jed` and
`gal22v10_registered.jed`, QF5892). Every equation in both reference designs decoded back exactly —
row offsets, OE rows, AR/SP, column routing, and S-bit addressing are all
confirmed, not inferred.

## Totals
- Array: 44 columns x 132 rows = fuses 0..5807. Fuse n: row n/44, col n%44.
- S0/S1 bits: fuses 5808..5827 (see below).
- WinCUPL emits **QF5892**: fuses 5828..5891 are the Lattice UES — parse and
  ignore. Some tools write QF5828 maps without it; a reader should accept both.

## Column routing (pin -> true column; complement = +1; even = true)
| Pin | Col | Pin | Col |
|----:|----:|----:|----:|
| 1   | 0   | 13  | 42  |
| 2   | 4   | 14  | 38  |
| 3   | 8   | 15  | 34  |
| 4   | 12  | 16  | 30  |
| 5   | 16  | 17  | 26  |
| 6   | 20  | 18  | 22  |
| 7   | 24  | 19  | 18  |
| 8   | 28  | 20  | 14  |
| 9   | 32  | 21  | 10  |
| 10  | 36  | 22  | 6   |
| 11  | 40  | 23  | 2   |
| 12/24 | — | (GND/VCC) | |

Macrocell pins 14..23: the column pair carries the **feedback**, muxed by S1:
- **S1 = 1 (combinational): even column = the PIN level** (true), odd = its
  complement. Proven by `y15 = y16 & j` decoding literally.
- **S1 = 0 (registered): even column = /Q**, odd = Q. Proven by all four
  registered equations decoding to the exact source expressions once the
  q-literals are label-inverted. Register feedback, not pin — independent of OE.

## Row layout
| Rows | Content |
|-----:|---------|
| 0 | **AR** product term (async reset, level-sensitive) |
| 1..9 | pin 23: **OE row 1**, logic rows 2..9 (8 terms) |
| 10..20 | pin 22: OE 10, logic 11..20 (10) |
| 21..33 | pin 21: OE 21, logic 22..33 (12) |
| 34..48 | pin 20: OE 34, logic 35..48 (14) |
| 49..65 | pin 19: OE 49, logic 50..65 (16) |
| 66..82 | pin 18: OE 66, logic 67..82 (16) |
| 83..97 | pin 17: OE 83, logic 84..97 (14) |
| 98..110 | pin 16: OE 98, logic 99..110 (12) |
| 111..121 | pin 15: OE 111, logic 112..121 (10) |
| 122..130 | pin 14: OE 122, logic 123..130 (8) |
| 131 | **SP** product term (sync preset, sampled at the clock edge) |

The OE row is the **first** row of each block (proven: `y21.oe = e & k`
landed on row 21).

## Term semantics (JEDEC '1' = blown/disconnected, '0' = intact/connected)
- All-blown row = term TRUE (an all-1 OE row = always enabled).
- All-intact row = term FALSE (contributes nothing; an all-0 OE row = never
  enabled = pin never driven). **This makes erased macrocells not drive with
  no special-casing** — honour the OE term and unused cells fall out free.
- WinCUPL emits F0 (default intact); only rows with content get L records.

## S0/S1 (fuses 5808..5827)
Pairs in pin order **23 down to 14**: pin 23 S0 = 5808, S1 = 5809; …
pin 14 S0 = 5826, S1 = 5827.
- **S1: 1 = combinational, 0 = registered** (also selects the feedback mux,
  above).
- **S0: 1 = active-high, 0 = active-low** (output buffer polarity; the array
  always holds the positive-logic expression — proven by `!y18` and the
  registered active-low `qb0`).
- Erased map default is S0 = S1 = 0; harmless because the all-intact OE row
  already prevents driving, but the feedback mux must still honour S1 (an
  OLMC pin used as a plain input gets S1 = 1 from the assembler).

## Registered semantics (as encoded by WinCUPL, matching the datasheet)
- D input of each registered cell = its logic-row SOP, positive logic.
- Pin = Q through the S0 polarity buffer; /Q feeds the array.
- AR row true -> all registers clear asynchronously (level-sensitive:
  evaluate on any array-input change).
- SP row true at the pin-1 rising edge -> all registers preset.
- Registered cells are still tri-stated by their own OE row.

## Reference designs (in repo, with expected behaviour in their comments)
- `gal22v10_combinational.pld` — blocks 23/21/19/18/16/15/14, 12-term row
  fill, active-low, `.oe` term, pin feedback, all 12 dedicated inputs.
- `gal22v10_registered.pld` — enabled 3-bit counter (pins 23/22/21),
  registered active-low `qb0` (pin 20), AR = rst, SP = pre, combinational
  `carry` decode of register state (pin 14).
