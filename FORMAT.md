# The `.plcbin` format

Binary export of a [PLC Studio](https://github.com/tibortoth8807/PlcStudio) project: hardware configuration, compiled programs, program sources, tags and HMI screens. All integers are **little-endian**.

## Layout

| Offset | Size | Field |
|---|---|---|
| 0 | 4 | Magic `PLCB` (ASCII) |
| 4 | u16 | Format version: `1`, `2` or `3` |
| 6 | u16 | Section count *n* |
| 8 | 10 × *n* | Section table: `u16 type`, `u32 offset`, `u32 length` |
| … | | Section payloads, in table order |
| end − 4 | u32 | **v3 only:** CRC-32 (IEEE) of every byte before it |

**Strings** are `u16 byteLength` + UTF-8 bytes. **Long strings** (block sources) are `u32 byteLength` + UTF-8.

## Sections

| Type | Name | Since | Content |
|---|---|---|---|
| 1 | Project | v1 | `str name`; v2+: `str producer`; v3: `u32 timestamp` (Unix seconds), `16 bytes buildId` |
| 2 | Device | v1 | `str name`, `str typeId`, address\*, `str controller` (empty = none), `u16 cycleMs`, `u8 moduleCount`, modules: `str typeId`, `u8 slot`, `u16 address` |
| 3 | Program | v1 | `str plc`, `u32 n` + `i32 × n` code, `u16 n` + `f64 × n` constants, `u16 n` + `f64 × n` initial slot values, `u16 n` + symbols, `u32 main`, `i32 startup` (−1 = none) |
| 4 | Block | v1 | `str plc`, `str name`, `u8 kind` (0 OB, 1 FC, 2 DB), `u16 number`, `long str source` |
| 5 | Tags | v1 | `str plc`, `u16 n`, tags: `str name`, `u8 type`, `str address`, `str comment` |
| 6 | HMI | v2 | `str name`, `str controller`, `u8 n`, elements: `u8 kind`, `str label`, `str tag` |

\* **Address:** v1 stores `str ip`, `str mask`; v2+ stores 4 bytes IP + 4 bytes mask.

**Symbol:** `str name`, `u8 type` (0 BOOL, 1 INT, 2 REAL), `u8 kind`; kind 0 = slot: `u16 slot`; kind 1 = process image: `u8 area` (0 I, 1 Q, 2 M), `u16 byte`, `u8 bit`, `u8 word`.

**HMI element kind:** 0 button, 1 switch, 2 lamp, 3 value, 4 slider.

## Version history

| Version | Change |
|---|---|
| 1 | First version: addresses as text, no HMI |
| 2 | Binary IP addresses, producer string, HMI sections |
| 3 | Build timestamp, random build ID, CRC-32 trailer |

The v3 additions change on every export even when nothing else does – which is exactly why a compare tool needs version-dependent filters.
