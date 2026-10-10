#!/usr/bin/env python3
"""Build rat-trap's embedded feedback cues from CC0 source audio.

The three cues - capture started, capture stopped, result saved - are compiled into the binary rather
than played from the operating system's sound scheme. That has three reasons, and the first is the one
that forced the change:

  1. The obvious system sounds for begin/end are DeviceConnect and DeviceDisconnect, which are the
     hardware-insert and hardware-remove events. They read as a USB device being plugged in, so using
     them for "capture started" is actively misleading.
  2. A system alias is a per-machine setting. It can be missing, customised, or map to the same file as
     something else - on this developer's machine FaxBeep, MailBeep and Notification.Mail all point at
     Windows Notify Email.wav - so "three distinct cues" was not something a system-alias scheme could
     guarantee.
  3. Referencing the system's own media files from a shipped program is not something that can be
     redistributed. Source audio here is CC0, so the result can be.

Run with --analyse to print the measured features of every candidate and exit without writing anything.

    python tools/build_sounds.py --pack <extracted pack dir> --out src/RatTrap/Audio/SoundCues.g.cs
    python tools/build_sounds.py --pack <pack dir> --analyse

Requires `soundfile` (which brings libsndfile and numpy). It is a build-time tool only: nothing it needs
is required to build or run rat-trap, and the generated file is committed so a normal build never runs it.
"""

from __future__ import annotations

import argparse
import base64
import glob
import io
import os
import sys
import wave

import numpy as np
import soundfile as sf

# --- the chosen cues -------------------------------------------------------------------------------
# Selection is by the pack's own semantic families and by measurement, not by guessing at waveforms.
#
# The analysis output is what decided this. `close_NNN` is `open_NNN` played backwards: identical
# duration, identical RMS, identical spectral centroid to three digits (open_002 0.302 s / 0.155 / 8856 Hz
# against close_002 0.302 s / 0.155 / 8858 Hz), with the pitch sweep reversed - open_002 rises 1270 -> 4055
# Hz and close_002 falls 3916 -> 1191 Hz. A begin/end pair with one timbre and opposite direction is
# exactly what a start/stop cue should be, so variant 2 of each is used.
#
# `confirmation_001` is the save cue because it is dark: a 696 Hz centroid against roughly 8.9 kHz for the
# pair, so it cannot be mistaken for either of them even at low volume or through a laptop speaker.
CHOICES = {
    "start": ("open", 1),
    "stop": ("close", 1),
    "save": ("confirmation", 0),
}

# Pitch is shifted by resampling, which is the "pitch and compress" treatment that was asked for. The
# library already supplies the rise and the fall, so these factors do not create that - they add a small
# deliberate asymmetry on top of it: the start cue plays faster, so it gets shorter and brighter, and the
# stop cue plays slower, so it gets longer and duller. The save cue is left alone, because it is already
# the distinct one and shifting it would only move it towards the pair.
PITCH = {"start": 1.10, "stop": 0.91, "save": 1.0}

TARGET_RATE = 22050
TARGET_RMS = 0.18           # equal perceived loudness: peak normalisation would make the dark save cue
PEAK_CEILING = 0.95         # sound louder than the bright pair for the same peak
FADE_SECONDS = 0.004        # kills the click at either end that a hard cut leaves behind
TRIM_THRESHOLD = 0.02       # fraction of peak treated as silence when trimming
MAX_SECONDS = 0.60          # a feedback cue longer than this stops being feedback


def load_mono(path: str) -> tuple[np.ndarray, int]:
    audio, rate = sf.read(path, dtype="float32", always_2d=True)
    return audio.mean(axis=1), rate


def trim_silence(x: np.ndarray, threshold: float = TRIM_THRESHOLD) -> np.ndarray:
    peak = float(np.max(np.abs(x))) if x.size else 0.0
    if peak <= 0:
        return x
    above = np.nonzero(np.abs(x) > threshold * peak)[0]
    return x[above[0]:above[-1] + 1] if above.size else x


def dominant_frequency(segment: np.ndarray, rate: int) -> float:
    """Frequency of the strongest partial above 80 Hz, which for these blips is the fundamental."""
    if segment.size < 64:
        return 0.0
    windowed = segment * np.hanning(segment.size)
    spectrum = np.abs(np.fft.rfft(windowed))
    frequencies = np.fft.rfftfreq(segment.size, 1 / rate)
    audible = frequencies > 80
    if not audible.any():
        return 0.0
    return float(frequencies[int(np.argmax(np.where(audible, spectrum, 0.0)))])


def features(path: str) -> dict:
    x, rate = load_mono(path)
    x = trim_silence(x)
    if x.size == 0:
        return {"duration": 0.0, "rms": 0.0, "peak": 0.0, "centroid": 0.0,
                "first": 0.0, "second": 0.0, "trend": "flat"}
    windowed = x * np.hanning(x.size)
    spectrum = np.abs(np.fft.rfft(windowed))
    frequencies = np.fft.rfftfreq(x.size, 1 / rate)
    centroid = float((spectrum * frequencies).sum() / max(spectrum.sum(), 1e-9))
    half = x.size // 2
    first = dominant_frequency(x[:half], rate)
    second = dominant_frequency(x[half:], rate)
    if first and second:
        trend = "rise" if second > first * 1.08 else ("fall" if first > second * 1.08 else "flat")
    else:
        trend = "flat"
    return {"duration": x.size / rate, "rms": float(np.sqrt(np.mean(x ** 2))),
            "peak": float(np.max(np.abs(x))), "centroid": centroid,
            "first": first, "second": second, "trend": trend}


def resample(x: np.ndarray, ratio: float) -> np.ndarray:
    """
    Resample by `ratio` (output length / input length) in the frequency domain.

    Band-limited resampling rather than linear interpolation, for two reasons. Downsampling by dropping
    samples folds everything above the new Nyquist back into the audible band, and these cues are bright
    sweeps, so the fold would be audible as grit rather than as the quiet, duller sound the pitch shift is
    supposed to produce. Truncating the spectrum instead removes those components, which is what a
    resampler is for. It is also exact for a signal this short, so there is no filter ringing on the
    transient at the start of a cue.

    One step handles the pitch shift and the sample-rate conversion together, because they are the same
    operation: playing a signal faster raises its pitch and shortens it, and storing it at a lower rate
    only changes how many samples describe the result.
    """
    n = x.size
    if n == 0:
        return x.copy()
    m = max(8, int(round(n * ratio)))
    if m == n:
        return x.copy()

    spectrum = np.fft.rfft(x)
    target_bins = m // 2 + 1
    trimmed = np.zeros(target_bins, dtype=complex)
    keep = min(spectrum.size, target_bins)
    trimmed[:keep] = spectrum[:keep]
    # The 1/n and 1/m factors that irfft and rfft apply do not cancel when the length changes.
    return (np.fft.irfft(trimmed, n=m) * (m / n)).astype(np.float32)


def prepare(path: str, pitch: float) -> tuple[np.ndarray, int]:
    x, rate = load_mono(path)
    x = trim_silence(x)
    x = resample(x, TARGET_RATE / (rate * pitch))

    if x.size > int(MAX_SECONDS * TARGET_RATE):
        x = x[:int(MAX_SECONDS * TARGET_RATE)]

    # Equal perceived loudness rather than equal peak. A dark sound has more energy for the same peak, so
    # peak normalisation would make the save cue noticeably louder than the start/stop pair and would make
    # the pair itself uneven after the pitch shift changed their durations. The ceiling then keeps the
    # result from clipping.
    rms = float(np.sqrt(np.mean(x ** 2))) if x.size else 0.0
    if rms > 0:
        x = x * (TARGET_RMS / rms)
    peak = float(np.max(np.abs(x))) if x.size else 0.0
    if peak > PEAK_CEILING:
        x = x * (PEAK_CEILING / peak)

    fade = max(1, int(FADE_SECONDS * TARGET_RATE))
    if x.size > 2 * fade:
        x[:fade] *= np.linspace(0.0, 1.0, fade, dtype=np.float32)
        x[-fade:] *= np.linspace(1.0, 0.0, fade, dtype=np.float32)

    return x.astype(np.float32), TARGET_RATE


def to_wav_bytes(x: np.ndarray, rate: int) -> bytes:
    """16-bit mono PCM, which every Windows version decodes from memory without a codec."""
    pcm = np.clip(x, -1.0, 1.0)
    pcm = (pcm * 32767.0).astype("<i2")
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as handle:
        handle.setnchannels(1)
        handle.setsampwidth(2)
        handle.setframerate(rate)
        handle.writeframes(pcm.tobytes())
    return buffer.getvalue()


def analyse(pack: str) -> None:
    print(f'{"name":22} {"dur(s)":>7} {"rms":>6} {"peak":>6} {"centroid":>9} '
          f'{"f first":>8} {"f second":>9} {"trend":>6}')
    for family in ("open", "close", "confirmation", "toggle", "switch", "select"):
        paths = sorted(glob.glob(os.path.join(pack, "**", f"{family}_*.ogg"), recursive=True))
        if not paths:
            continue
        print(f"-- {family} " + "-" * 60)
        for path in paths:
            f = features(path)
            print(f'{os.path.basename(path)[:-4]:22} {f["duration"]:7.3f} {f["rms"]:6.3f} '
                  f'{f["peak"]:6.3f} {f["centroid"]:9.0f} {f["first"]:8.0f} {f["second"]:9.0f} '
                  f'{f["trend"]:>6}')


def find(pack: str, family: str, index: int) -> str:
    paths = sorted(glob.glob(os.path.join(pack, "**", f"{family}_*.ogg"), recursive=True))
    if not paths:
        sys.exit(f"no {family}_*.ogg under {pack}")
    if index >= len(paths):
        sys.exit(f"{family} has {len(paths)} variants, index {index} does not exist")
    return paths[index]


def emit(names: dict[str, bytes], out_path: str) -> None:
    lines = [
        "// <auto-generated>",
        "//     Generated by tools/build_sounds.py from CC0 source audio. Do not edit by hand;",
        "//     edit the generator and re-run it, so that the provenance below stays true.",
        "//",
        "//     Source: Kenney Interface Sounds (CC0 1.0), https://kenney.nl/assets/interface-sounds",
        "//     See THIRD-PARTY-NOTICES.md for the licence text.",
        "//",
        "//     Each cue is a complete mono 16-bit PCM WAV image, played with PlaySound's SND_MEMORY so",
        "//     that no file, no codec and no entry in the system sound scheme is needed at run time.",
        "// </auto-generated>",
        "",
        "namespace RatTrap.Audio;",
        "",
        "/// <summary>The three feedback cues, as complete WAV images.</summary>",
        "internal static class SoundCues",
        "{",
    ]
    for name in ("Start", "Stop", "Save"):
        payload = names[name.lower()]
        lines.append(f"    /// <summary>{name} cue, {len(payload)} bytes of WAV.</summary>")
        lines.append(f"    public const string {name}Base64 =")
        encoded = base64.b64encode(payload).decode("ascii")
        for offset in range(0, len(encoded), 100):
            chunk = encoded[offset:offset + 100]
            terminator = ";" if offset + 100 >= len(encoded) else " +"
            lines.append(f'        "{chunk}"{terminator}')
        lines.append("")
    lines.append("}")
    lines.append("")

    with open(out_path, "w", encoding="utf-8", newline="\n") as handle:
        handle.write("\n".join(lines))
    print(f"wrote {out_path} ({os.path.getsize(out_path):,} bytes)")


def verify(role: str, payload: bytes) -> str:
    """
    Read the produced bytes back with an independent WAV reader and assert the format.

    Written because the first version of this generator lied about the sample rate: it decimated the audio
    but kept declaring the source rate, so the file played an octave high and at twice the speed, and the
    only printed evidence was a duration that agreed with the wrong rate. The generator now checks the
    artifact instead of trusting the numbers it just computed.
    """
    with wave.open(io.BytesIO(payload)) as handle:
        channels, width, rate, frames = (handle.getnchannels(), handle.getsampwidth(),
                                         handle.getframerate(), handle.getnframes())
    problems = []
    if channels != 1:
        problems.append(f"{channels} channels, expected 1")
    if width != 2:
        problems.append(f"{width * 8}-bit, expected 16-bit")
    if rate != TARGET_RATE:
        problems.append(f"{rate} Hz, expected {TARGET_RATE}")
    duration = frames / rate if rate else 0.0
    if not 0 < duration <= MAX_SECONDS:
        problems.append(f"{duration:.3f}s is outside 0..{MAX_SECONDS}s")
    if not payload.startswith(b"RIFF") or payload[8:12] != b"WAVE":
        problems.append("not a RIFF/WAVE image")
    if problems:
        sys.exit(f"{role}: " + "; ".join(problems))
    return f"{duration:.3f}s {rate} Hz {channels}ch {width * 8}bit"


def decode_generated(path: str) -> dict[str, bytes]:
    """Pull the three base64 payloads back out of the generated C# file."""
    import re

    text = open(path, encoding="utf-8").read()
    payloads = {}
    for name in ("Start", "Stop", "Save"):
        match = re.search(name + r'Base64 =\s*((?:\s*"[^"]*"\s*\+?)+);', text)
        if not match:
            sys.exit(f"{path}: no {name}Base64 payload found")
        payloads[name] = base64.b64decode("".join(re.findall(r'"([^"]*)"', match.group(1))))
    return payloads


def zero_crossing_rate(x: np.ndarray, rate: int) -> float:
    """Sign changes per second, after removing the mean so a DC offset cannot fake a low rate."""
    centred = x - x.mean()
    return float(np.count_nonzero(np.diff(np.signbit(centred)))) / max(x.size, 1) * rate


def spectral_centroid(x: np.ndarray, rate: int) -> float:
    spectrum = np.abs(np.fft.rfft(x * np.hanning(x.size)))
    frequencies = np.fft.rfftfreq(x.size, 1 / rate)
    return float((spectrum * frequencies).sum() / max(spectrum.sum(), 1e-9))


def verify_generated(path: str) -> int:
    """
    Check the properties that make these cues useful, on the artifact rather than on the intent.

    The reason this exists as a mode of its own: "start and stop are a matched pair and the save cue is
    distinguishable" is the requirement, and reading the generator's CHOICES table does not demonstrate it.
    The first generated set was wrong in a way no table could show - it declared 44100 Hz while holding
    22050 Hz audio, so every cue played an octave high and at twice the speed.
    """
    payloads = decode_generated(path)
    print(f'{"cue":6} {"bytes":>7} {"dur(s)":>7} {"rate":>6} {"zcr first":>10} {"zcr second":>11} '
          f'{"centroid":>9} {"rms":>7}')

    measured = {}
    for name, payload in payloads.items():
        with wave.open(io.BytesIO(payload)) as handle:
            if (handle.getnchannels(), handle.getsampwidth(), handle.getframerate()) != (1, 2, TARGET_RATE):
                sys.exit(f"{name}: expected mono 16-bit {TARGET_RATE} Hz, got "
                         f"{handle.getnchannels()}ch {handle.getsampwidth() * 8}bit "
                         f"{handle.getframerate()} Hz")
            rate = handle.getframerate()
            samples = np.frombuffer(handle.readframes(handle.getnframes()), dtype="<i2")
            x = samples.astype(np.float64) / 32768.0
        if not payload.startswith(b"RIFF") or payload[8:12] != b"WAVE":
            sys.exit(f"{name}: not a RIFF/WAVE image")

        half = x.size // 2
        measured[name] = {
            "duration": x.size / rate,
            "first": zero_crossing_rate(x[:half], rate),
            "second": zero_crossing_rate(x[half:], rate),
            "centroid": spectral_centroid(x, rate),
            "rms": float(np.sqrt(np.mean(x ** 2))),
            "bytes": payload,
        }
        m = measured[name]
        print(f'{name:6} {len(payload):7,} {m["duration"]:7.3f} {rate:6} {m["first"]:10.0f} '
              f'{m["second"]:11.0f} {m["centroid"]:9.0f} {m["rms"]:7.4f}')

    problems = []
    start, stop, save = measured["Start"], measured["Stop"], measured["Save"]
    if not start["second"] > start["first"]:
        problems.append("the start cue does not sweep upward")
    if not stop["second"] < stop["first"]:
        problems.append("the stop cue does not sweep downward")
    if not save["centroid"] < min(start["centroid"], stop["centroid"]):
        problems.append("the save cue is not darker than the start/stop pair, so it is not clearly distinct")
    if len({start["bytes"], stop["bytes"], save["bytes"]}) != 3:
        problems.append("two cues are byte-identical")
    for name, m in measured.items():
        if m["rms"] < 0.01:
            problems.append(f"the {name} cue is effectively silent")
        if not 0 < m["duration"] <= MAX_SECONDS:
            problems.append(f"the {name} cue is {m['duration']:.3f}s long")

    if problems:
        for problem in problems:
            print(f"FAIL {problem}")
        return 1

    print("ok   start sweeps up, stop sweeps down, save is darker than both, all three distinct")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--pack", help="directory of the extracted CC0 pack")
    parser.add_argument("--out", help="generated C# file to write")
    parser.add_argument("--analyse", action="store_true", help="print candidate features and exit")
    parser.add_argument("--verify", metavar="GENERATED_CS",
                        help="check an already generated C# file and exit")
    args = parser.parse_args()

    if args.verify:
        return verify_generated(args.verify)

    if args.analyse:
        if not args.pack:
            parser.error("--analyse needs --pack")
        analyse(args.pack)
        return 0

    if not args.pack:
        parser.error("--pack is required unless --verify is given")
    if not args.out:
        parser.error("--out is required unless --analyse or --verify is given")

    payloads: dict[str, bytes] = {}
    for role, (family, index) in CHOICES.items():
        path = find(args.pack, family, index)
        x, rate = prepare(path, PITCH[role])
        payload = to_wav_bytes(x, rate)
        payloads[role] = payload
        print(f"{role:6} <- {os.path.basename(path):22} pitch x{PITCH[role]:.2f}  "
              f"verified {verify(role, payload)}  {len(payload):,} bytes")

    emit(payloads, args.out)
    total = sum(len(p) for p in payloads.values())
    print(f"total audio {total:,} bytes across {len(payloads)} cues")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
