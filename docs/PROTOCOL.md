# FL980 V2 lighting protocol

Notes on how the FL-ESPORTS FL980 V2 lighting protocol works. Everything below was verified on a real keyboard unless marked otherwise.

## Device

| | |
|---|---|
| USB ID (cable) | `05AC:024F`, product string `SONiX / Gaming Keyboard`, version `0x0406` |
| USB ID (2.4 GHz receiver) | `05AC:024F`, `RONGYUAN / 2.4G Wireless Device` |
| Firmware family | RongYuan "k68", model `k68_k237` |
| Identify (`0x8F`) | `8F 32 02 ...` → id `0x0232` = 562 |

- Commands go to the **keyboard interface `MI_00`** (usage page `0x01`, usage `0x06`) as **feature reports**: report id 0 + 64 bytes.
- Windows denies `GENERIC_READ|WRITE` on keyboards. Open the device with **access = 0**: `HidD_SetFeature` / `HidD_GetFeature` still work.
- **Wired only.** The 2.4 GHz receiver does not relay feature reports, and Bluetooth doesn't expose them.
- Request/reply: `SetFeature(request)`, wait about 300 ms, then `GetFeature` returns the reply.
- **No checksum** is required. A trailing checksum byte is harmless.

## Lighting: `0x04` set / `0x84` get

```
04  mode  speed  brightness  colour  R  G  B
```

| byte | meaning |
|---|---|
| mode | see the table below |
| speed | `5 - s`, where s is 0 (slow) … 5 (fast) |
| brightness | 0 … 4 |
| colour | colour selector, see below |
| R G B | own colour, used when the selector says so |

The factory default is `04 03 02 04 00 00 C8 C8`.

### Modes

| id | effect | colour |
|---|---|---|
| 0 | off | – |
| 1 | static | palette / rainbow / RGB |
| 2 | breathing | RGB, or rainbow (new colour each breath) |
| 3 | neon (colour cycling) | – |
| 4 | rainbow wave (right → left) | – |
| 5 | ripple from the pressed key | RGB |
| 6 | raindrops (random keys) | palette / RGB |
| 7 | snake | no visible effect on this unit |
| 8 | reactive (pressed key lights up) | RGB |
| 9 | converge (from corners to the centre) | palette / RGB |
| 10 | row beam from the pressed key | RGB |
| 11 | music follow | not used |
| 12 | **custom picture** (per-key) | see below |
| 13 | music follow 3 | not used |
| 14 | per-key rainbow | – |

### Colour selector

- **Static (mode 1)** uses the **low nibble**:
  - `0` red, `1` green, `2` blue, `3` pink, `4` yellow, `5` white, `6` rainbow (left → right);
  - `7` = own colour from R G B. For example, `04 01 02 04 07 FF 00 FF` gives magenta.
- **Modes 2, 5, 8, 10** use the **high nibble**, like the official driver (`u <<= 4`):
  - only `0x70` + R G B works. Palette indices `0x00`…`0x50` show red, so send palette colours as RGB;
  - rainbow is `0x06` in breathing (new colour every breath). Modes 5, 8 and 10 have no rainbow: `0x06` and `0x80` both give red.
- RGB values below about `08 08 08` don't light up.

## Custom picture (per-key RGB)

**Upload:**
```
09 00 80 01                      header: 0x0180 = 384 bytes follow
<64 bytes> × 6                   raw RGB triples, no header, no checksum
```

LED index `i` occupies bytes `3i … 3i+2`. The LED order is in [`src/keymatrix.txt`](../src/keymatrix.txt). It has 112 slots, column by column, 6 rows each, and every slot holds its HID usage code (`10,1,0,0` = Fn, `0,0,0,0` = empty).

**Show the picture:**
```
04 0C <speed> 04 <slot> 00 C8 C8      slot byte: 00 / 10 / 20 = slot 1 / 2 / 3
```

Slot 1's factory picture is WASD plus the arrow keys.

The sharkfin-style `0x0C` 56-byte page upload (yc500 family) is **not** supported by this firmware.

## Other read-only commands that answer

| cmd | reply | meaning |
|---|---|---|
| `8F` | `8F 32 02 00 00` | model id 562 |
| `80` | `80 06 04` | firmware 0x0406 |
| `85` | `85 00 01` | profile |
| `83` | `83 00 ...` | report rate (?) |
| `87` | zeros | not supported (yc500 LEDPARAM get) |

## Do not send

`AC` `2C` `7F` `30` `01` `02` `1C` `1E`: flash erase, bootloader entry, factory reset and sensor calibration in RongYuan firmwares. Lumikey refuses to send them.

Pictures are written to flash. Don't upload more often than about once every 2 seconds. Hammering the endpoint can stall it until the keyboard is re-plugged.

## Credits

- The RongYuan protocol write-up in [dniminenn/sharkfin](https://github.com/dniminenn/sharkfin/blob/master/docs/PROTOCOL.md) was the starting point.
- Model id, mode numbering and the picture upload format were confirmed against the vendor's web configurator, then checked on hardware.
