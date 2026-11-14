# JBL LIVE PRO+ TWS — control protocol

Notes on the protocol used by the *JBL Headphones* Android app (5.38.2) for the
LIVE PRO+ TWS (BES chipset, product id `2043`). Everything here was verified against real
earbuds (firmware 5.2.0).

## Transport

Bluetooth LE GATT, advertised name `JBL LIVE PRO+ TWS-LE` (random address).

| | UUID |
|---|---|
| Service | `65786365-6c70-6f69-6e74-2e636f6d0000` |
| Notify (earbuds → host) | `65786365-6c70-6f69-6e74-2e636f6d0001` |
| Write (host → earbuds) | `65786365-6c70-6f69-6e74-2e636f6d0002` |

Advertisement service data `0xFDDF`: `43 20 <color id> <crc16>`.

## Packet format

```
AA <cmd> <len> <payload…>        (no checksum)
```

Simple writes are acknowledged with `AA 00 02 <cmd> <status>` (`00` = OK).

## Commands

| Function | Request | Reply / notes |
|---|---|---|
| Device info | `AA 11 00` | `AA 12 …`: name (16 B), pid (LE), color id, battery, MAC (reversed), firmware (3 B) |
| Full status | `AA 21 01 30` | `AA 22 0C 30 anc aa autoOff eq multiAI ? ota autoPlay tws …` |
| Battery | `AA 25 01 00` | `AA 25 0D 01 chgL chgR lvlL lvlR chgCase lvlCase mvL(2) mvR(2) mvCase(2)` — `FF` = unknown |
| In-ear | `AA 21 01 41` | `AA 22 03 41 L R` (1 = in ear) |
| ANC on / off | `AA 31 01 01` / `AA 31 01 00` | |
| Ambient | `AA 32 01 xx` | `00` off, `01` TalkThru, `02` Ambient Aware |
| Auto power off | `AA 33 01 xx` | bit 7 = enabled, bits 0–6 = minutes |
| Auto play/pause | `AA 35 01 0x` | |
| EQ preset | `AA 40 01 xx` | `00` Off, `01` Jazz, `02` Vocal, `03` Bass, `04` = custom active |
| Gestures (read) | `AA 77 02 01 FF` | `AA 77 len 02 (gesture action)*` |
| Gesture (set) | `AA 77 03 00 <gesture> <action>` | echo `AA 77 03 02 <gesture> <action>` |
| Find My Buds | `AA 36 01 xx` | `10/11` start right/left, `00/01` stop right/left |
| Beep status | `AA 23 00` | `AA 24 01 xx`: `01` both, `02` right, `03` left, `00` none |
| Seal test | `AA 21 01 42` | after ~8–10 s: `AA 22 03 42 L R` (1 = good seal) |
| Smart Audio (read) | `AA 82 00` | `AA 83 08 aac(2) sbc(2) latency(2) x(2)` |
| Smart Audio (set) | `AA 81 08 aac sbc latency FF FF` | values (LE): Normal 197/41/279, Audio 256/53/200, Video 197/41/150 |

Gesture ids: left tap `06`, double `07`, triple `08`; right tap `09`, double `0A`, triple `0B`;
tap-and-hold `0C` (left) / `0E` (right).
Actions: `08` play/pause, `05` next, `06` previous, `01` vol+, `02` vol−, `0B` ANC↔Ambient,
`09` ANC→Ambient→Off, `04` TalkThru, `A1` voice assistant, `00` none.

Color ids: `0` black, `1` white, `2` blue, `9` beige, `10` pink, `11` chrome, `12` rose gold.

### Custom equalizer

Write `AA 41` in chunks: header `AA 41 <len> <chunks>`, then chunks of up to 79 bytes prefixed
by the chunk index, then `AA 05 41`. Each part is acknowledged with `AA 00 02 41 00`.

Payload: `index(4) category(1=graphic) calib(f32) sampleRate(48) gainL(f32) gainR(f32)
bandCount(i32)` followed per band by `type(i32=1) gain(f32) fc(f32) q(f32=1.0)`
for 32, 64, 125, 250, 500, 1k, 2k, 4k, 8k and 16k Hz (±6 dB). The official app computes
`calib` with a native library; `0` works.

Read with `AA 42 01 01`: header `AA 43 <len> <chunks>`, then raw chunks, each confirmed with
`AA 01 02 43 00`, ending with `AA 04 43`. The read-back omits `calib`.

## Smart Audio & Video and Windows

Changing the mode makes the earbuds restart their audio link. Windows loses the A2DP (stereo)
endpoint and routes audio to the Hands-Free endpoint at 100 % volume, which sounds loud and
distorted. The app restores audio by disconnecting every audio profile and reconnecting
(`KSPROPSETID_BtAudio`, `KSPROPERTY_ONESHOT_DISCONNECT` / `RECONNECT`), and caps the
volume while it comes back.
