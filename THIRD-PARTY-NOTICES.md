# Third-party notices

[**English**](THIRD-PARTY-NOTICES.md) | [中文](THIRD-PARTY-NOTICES.zh-CN.md)

rat-trap contains no third-party code. It contains **three audio cues**, and this file records where they
came from and what was done to them, because that is the only thing in the repository that did not originate
here.

## Kenney Interface Sounds

| | |
| --- | --- |
| **Work** | Interface Sounds (1.0) |
| **Author** | Kenney — <https://kenney.nl> |
| **Pack page** | <https://kenney.nl/assets/interface-sounds> |
| **Downloaded from** | `https://kenney.nl/media/pages/assets/interface-sounds/fa43c1dd4d-1677589452/kenney_interface-sounds.zip` |
| **Retrieved** | 2026-10-10 |
| **Licence** | Creative Commons Zero (CC0 1.0) — <https://creativecommons.org/publicdomain/zero/1.0/> |
| **Used** | `open_002.ogg`, `close_002.ogg`, `confirmation_001.ogg` |
| **Modified** | yes — see below |

### Licence, as distributed with the pack

```
Interface Sounds (1.0)

Created/distributed by Kenney (www.kenney.nl)
Creation date: 11-02-2020

License: (Creative Commons Zero, CC0)
http://creativecommons.org/publicdomain/zero/1.0/

This content is free to use in personal, educational and commercial projects.

Support us by crediting Kenney or www.kenney.nl (this is not mandatory)
```

CC0 places the work in the public domain: no attribution is required and the terms do not restrict
commercial use. The credit above is given because it is the right thing to do, not because the licence
demands it, and the same credit appears in the generated source file so it survives wherever that file goes.

### What was done to each of them

`tools/build_sounds.py` performs the transformation, and `tools/build_sounds.py --verify` measures the
result. Neither the original `.ogg` files nor the pack are in this repository; only the three resulting WAV
images are, embedded as base64 in `src/RatTrap/Audio/SoundCues.g.cs`.

| Cue | Source | Treatment |
| --- | --- | --- |
| Start | `open_002.ogg` | silence trimmed, resampled by ×1.10 (pitch up, shorter), 22050 Hz mono 16-bit, RMS-levelled, 4 ms fade in/out |
| Stop | `close_002.ogg` | silence trimmed, resampled by ×0.91 (pitch down, longer), 22050 Hz mono 16-bit, RMS-levelled, 4 ms fade in/out |
| Save | `confirmation_001.ogg` | silence trimmed, no pitch change, 22050 Hz mono 16-bit, RMS-levelled, 4 ms fade in/out |

The treatment is a derivative work of a CC0 original, so the result is likewise unrestricted.

### Why these three, and why not the system sounds

The first version of this feature named entries in the Windows sound scheme instead. That was replaced, and
the reasoning is worth keeping because it is not obvious:

* The two system events that mean "began" and "ended" are `DeviceConnect` and `DeviceDisconnect`, which are
  the **hardware-insert and hardware-remove** events. Used for "capture started", they sound exactly like a
  USB device being plugged in, which is actively misleading.
* A scheme alias can be absent, be remapped by the user, or point several events at one file. On the machine
  this was developed on, `FaxBeep`, `MailBeep` and `Notification.Mail` all resolve to the same WAV, so
  "three distinct cues" was not something an alias scheme could guarantee.
* Referring to the media files that ship with Windows from a program that is redistributed is not a thing
  that can be done. The source audio here is CC0, so the result can be.

Inside the pack, `open_*` and `close_*` are the same sound forwards and backwards — measured identical in
duration, RMS and spectral centroid to three digits, with the pitch sweep reversed. A begin/end pair sharing
one timbre and differing only in direction is exactly what a start/stop cue should be, which is why variant 2
of each was chosen. `confirmation_001` was chosen for the save cue because it is dark: a 692 Hz spectral
centroid against roughly 5.4 kHz for the pair, so it cannot be mistaken for either of them even at low volume
or through a laptop speaker.
