# Reggie-16

Reggie-16 is a 16-bit computer built from 74-series TTL, with its own operating system and
languages. These are the sources of its 35 programmable parts, copied as they are from the
project (whose files call this compiler by its earlier name, BlinkyJED), each beside the
`.jed` GalForge makes of it. The tests hold every one to its `.jed` byte for byte.

```
galforge examples/Reggie16 -o somewhere
```

compiles the lot. A `_V2` file is the second revision of the board its first-revision
twin sits on; both are kept because both boards exist.

| Source | Part | Job |
| --- | --- | --- |
| `ALU_R16.PLD` | 22V10 | ALU function decoder for the whole 16-bit ALU board |
| `CARRY_R16.PLD` | 22V10 | carry lookahead (in place of a '182) and signed overflow |
| `ZDET_R16.PLD` | 22V10 | zero detect over the full word, and the post-shift flag source |
| `FLAGS_R16.PLD` | 22V10 | both flag banks of the flag unit |
| `BBUS_R16.PLD`, `BBUS_R16_V2.PLD` | 22V10 | immediate assembly on the B bus |
| `R16_COND.PLD` | 22V10 | instruction decode 1 of 5: branch condition and flag capture |
| `R16_SEQ.PLD`, `R16_SEQ_V2.PLD` | 22V10 | instruction decode 2 of 5: register port B, ALU result, memory strobes, IR load |
| `R16_EXEC.PLD`, `R16_EXEC_V2.PLD` | 22V10 | instruction decode 3 of 5: ALU function code, register write, port A select |
| `R16_IMM.PLD`, `R16_IMM_V2.PLD` | 22V10 | instruction decode 4 of 5: immediates, shifter fill, halt and trap requests |
| `R16_MISC.PLD`, `R16_MISC_V2.PLD` | 22V10 | instruction decode 5 of 5: address phase, interrupt strobes, entry steering |
| `R16_STD.PLD` | 22V10 | the second board's stores with displacement and write address select |
| `R16_CAUSE.PLD` | 22V10 | cause port decode |
| `RFSEQ_R16.PLD` | 16V8 | register file write sequence for four SRAMs |
| `CLKSHAPE_R16.PLD` | 22V10 | clock divider, duty shaper and halt gate |
| `CLKV2_R16.PLD` | 22V10 | the whole second-revision clock generator |
| `TWAIT_R16.PLD` | 16V8 | the bounded wait beside the T-state counter |
| `MAPDEC_R16.PLD` | 22V10 | MMU address-space decode: fixed 16K, ROM window, I/O window, /WAIT |
| `MMUCTL_R16.PLD` | 22V10 | MMU paged-side control: array strobes, mapping mode, faults |
| `BUSWIN_R16.PLD` | 22V10 | peripheral-bus window decode and the tick's strobes |
| `IODEC_R16.PLD` | 22V10 | peripheral board: I/O window, UART select, high-byte driver |
| `CFDEC_R16.PLD` | 22V10 | peripheral board: tick strobes and the CompactFlash task file |
| `IRQGLU_R16.PLD` | 16V8 | peripheral board: the /IRQ wire-OR stage |
| `GPIODEC_R16.PLD` | 22V10 | GPIO card decode |
| `LEDDEC_R16.PLD` | 22V10 | 16-bit LED port decode |
| `SNDDEC_R16.PLD` | 22V10 | sound card (YM2149F) decode |
| `VBUS_R16.PLD` | 22V10 | video card bus decode |
| `HTIM_R16.PLD` | 22V10 | video card horizontal timing |
| `VTIM_R16.PLD` | 22V10 | video card vertical timing |
| `VARB_R16.PLD` | 22V10 | video card text-memory arbiter |
| `VSHF_R16.PLD` | 22V10 | video card pixel shifter |
