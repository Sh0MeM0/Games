#!/usr/bin/env python3
"""Synthesises the app's background music: an original, calm lo-fi loop.

Everything is generated from code (no samples), so there are no third-party rights.
The loop is seamless: notes and reverb that ring past the end wrap around to the start.

Usage: python3 tools/gen_music.py out.wav
Then:  ffmpeg -i out.wav -c:a libvorbis -q:a 3 res/raw/music.ogg
"""
import sys
import wave

import numpy as np

SR = 44100
BPM = 82.0
BEAT = 60.0 / BPM
BAR = 4 * BEAT
BARS = 32
N = int(round(BARS * BAR * SR))
rng = np.random.default_rng(7)


def hz(midi):
    return 440.0 * 2 ** ((midi - 69) / 12.0)


# chords as MIDI notes (voicing for pad / e-piano), bass root
NOTE = {'C': 0, 'C#': 1, 'D': 2, 'D#': 3, 'E': 4, 'F': 5, 'F#': 6, 'G': 7, 'G#': 8, 'A': 9, 'A#': 10, 'B': 11}
CHORDS = {
    'Dmaj7': ([62, 66, 69, 73], 'D'),
    'Bm7': ([59, 62, 66, 69], 'B'),
    'Gmaj7': ([59, 62, 66, 67], 'G'),
    'A7sus': ([57, 62, 64, 67], 'A'),
    'Em7': ([59, 62, 64, 67], 'E'),
    'F#m7': ([61, 64, 66, 69], 'F#'),
    'A6': ([57, 61, 64, 66], 'A'),
}
A_PART = ['Dmaj7', 'Bm7', 'Gmaj7', 'A7sus'] * 2
B_PART = ['Em7', 'F#m7', 'Gmaj7', 'A6', 'Bm7', 'Gmaj7', 'Em7', 'A7sus']
PROGRESSION = A_PART + B_PART + A_PART + B_PART  # 32 bars

left = np.zeros(N)
right = np.zeros(N)


def add(sig, start, gain=1.0, pan=0.0, buf=None):
    """Adds a mono signal at time `start` (seconds), wrapping around the loop end."""
    i0 = int(round(start * SR)) % N
    n = len(sig)
    lg = gain * np.sqrt(0.5 * (1 - pan))
    rg = gain * np.sqrt(0.5 * (1 + pan))
    tgt = buf if buf is not None else (left, right)
    idx = (np.arange(n) + i0) % N
    np.add.at(tgt[0], idx, sig * lg)
    np.add.at(tgt[1], idx, sig * rg)


def env_adsr(n, a, d, s, r_start, r):
    t = np.arange(n) / SR
    e = np.where(t < a, t / max(a, 1e-4), s + (1 - s) * np.exp(-(t - a) / max(d, 1e-4)))
    rel = np.clip((t - r_start) / max(r, 1e-4), 0, 1)
    return e * (1 - rel) ** 2


def lowpass(x, cutoff):
    # gentle 1st-order low-pass, applied circularly via FFT (keeps the loop seamless)
    X = np.fft.rfft(x)
    f = np.fft.rfftfreq(len(x), 1 / SR)
    return np.fft.irfft(X / np.sqrt(1 + (f / cutoff) ** 4), len(x))


def highpass(x, cutoff):
    X = np.fft.rfft(x)
    f = np.fft.rfftfreq(len(x), 1 / SR)
    h = (f / cutoff) ** 2 / np.sqrt(1 + (f / cutoff) ** 4)
    return np.fft.irfft(X * h, len(x))


# ---------------------------------------------------------------- instruments
def epiano(freq, dur):
    n = int((dur + 1.6) * SR)
    t = np.arange(n) / SR
    index = 1.1 * np.exp(-t * 4.0)
    mod = np.sin(2 * np.pi * freq * t)
    car = np.sin(2 * np.pi * freq * t + index * mod)
    tine = 0.22 * np.sin(2 * np.pi * freq * 4.0 * t) * np.exp(-t * 14)
    amp = np.minimum(t / 0.004, 1) * np.exp(-t * 1.3)
    rel = np.clip((t - dur) / 0.35, 0, 1)
    return (car + tine) * amp * (1 - rel) ** 2


def pad_note(freq, dur, detune):
    n = int((dur + 1.2) * SR)
    t = np.arange(n) / SR
    sig = np.zeros(n)
    for k in range(1, 7):
        f = freq * k * (1 + detune)
        sig += np.sin(2 * np.pi * f * t + rng.uniform(0, 6.28)) / k ** 1.6
    return sig * env_adsr(n, 0.9, 1.0, 1.0, dur, 1.0)


def bass(freq, dur):
    n = int((dur + 0.3) * SR)
    t = np.arange(n) / SR
    sig = np.sin(2 * np.pi * freq * t) + 0.25 * np.sin(4 * np.pi * freq * t) + 0.08 * np.sin(6 * np.pi * freq * t)
    return sig * env_adsr(n, 0.012, 0.5, 0.7, dur, 0.15)


def bell(freq, dur=1.6):
    n = int(dur * SR)
    t = np.arange(n) / SR
    sig = np.sin(2 * np.pi * freq * t) + 0.25 * np.sin(2 * np.pi * freq * 2.005 * t) * np.exp(-t * 3)
    return sig * np.minimum(t / 0.003, 1) * np.exp(-t * 2.6)


def kick():
    n = int(0.45 * SR)
    t = np.arange(n) / SR
    f = 48 + 75 * np.exp(-t * 28)
    ph = 2 * np.pi * np.cumsum(f) / SR
    return np.sin(ph) * np.exp(-t * 7.5) * np.minimum(t / 0.002, 1)


def snare():
    n = int(0.3 * SR)
    t = np.arange(n) / SR
    noise = rng.standard_normal(n)
    noise = np.convolve(noise, np.ones(6) / 6, 'same')  # soften
    body = np.sin(2 * np.pi * 190 * t) * np.exp(-t * 30)
    return (0.55 * noise * np.exp(-t * 16) + 0.5 * body) * np.minimum(t / 0.002, 1)


def hat():
    n = int(0.09 * SR)
    t = np.arange(n) / SR
    noise = rng.standard_normal(n)
    noise = noise - np.convolve(noise, np.ones(3) / 3, 'same')  # crude high-pass
    return noise * np.exp(-t * 55)


# ---------------------------------------------------------------- arrangement
pad_buf = (np.zeros(N), np.zeros(N))
drum_buf = (np.zeros(N), np.zeros(N))
SWING = 0.10 * BEAT

for bar, name in enumerate(PROGRESSION):
    notes, root = CHORDS[name]
    t0 = bar * BAR
    b_part = 8 <= bar < 16 or 24 <= bar < 32

    # pad: whole bar, wide stereo
    for m in notes:
        add(pad_note(hz(m), BAR, -0.0025), t0, 0.050, -0.6, pad_buf)
        add(pad_note(hz(m), BAR, +0.0025), t0, 0.050, +0.6, pad_buf)

    # e-piano comping: beat 1 and the "and" of 2
    for k, (beat, length, vel) in enumerate(((0, 1.4, 1.0), (1.5 + SWING / BEAT, 1.8, 0.8))):
        for i, m in enumerate(notes):
            strum = i * 0.012
            add(epiano(hz(m), length * BEAT), t0 + beat * BEAT + strum, 0.085 * vel, -0.15 + 0.1 * i)

    # bass: root on 1, fifth/octave movement
    r = NOTE[root] + 36 if NOTE[root] + 36 >= 38 else NOTE[root] + 48
    pattern = ((0, r, 1.5), (2.5 + SWING / BEAT, r, 0.5), (3, r + 7, 0.9))
    for beat, m, length in pattern:
        add(bass(hz(m), length * BEAT), t0 + beat * BEAT, 0.15, 0.0)

    # gentle bell arpeggio (more notes in the B part)
    arp = notes + [notes[1] + 12]
    steps = (0, 1, 2, 3, 4, 3, 2, 1) if b_part else (0, 2, 4, 2)
    step_len = 0.5 if b_part else 1.0
    for s, idx in enumerate(steps):
        if not b_part and s == 3 and bar % 2:
            continue
        beat = s * step_len
        if step_len == 0.5 and s % 2 == 1:
            beat += SWING / BEAT
        add(bell(hz(arp[idx] + 12)), t0 + beat * BEAT, 0.050, 0.35 if s % 2 else -0.35)

    # drums
    for beat in (0, 2.5 + SWING / BEAT) if bar % 4 == 3 else (0, 2):
        add(kick(), t0 + beat * BEAT, 0.30, 0.0, drum_buf)
    for beat in (1, 3):
        add(snare(), t0 + beat * BEAT, 0.13, 0.05, drum_buf)
    for e in range(8):
        beat = e * 0.5 + (SWING / BEAT if e % 2 else 0)
        vel = 0.12 if e % 2 == 0 else 0.08
        add(hat(), t0 + beat * BEAT, vel * (0.85 + 0.3 * rng.random()), 0.3, drum_buf)

pad_l = lowpass(pad_buf[0], 1800)
pad_r = lowpass(pad_buf[1], 1800)
left += pad_l
right += pad_r
left += lowpass(drum_buf[0], 11000)
right += lowpass(drum_buf[1], 11000)

# ---------------------------------------------------------------- reverb (circular convolution)
ir_len = int(2.2 * SR)
t = np.arange(ir_len) / SR
ir_l = rng.standard_normal(ir_len) * np.exp(-t * 2.8)
ir_r = rng.standard_normal(ir_len) * np.exp(-t * 2.8)
ir_l, ir_r = lowpass(np.pad(ir_l, (0, N - ir_len)), 3500), lowpass(np.pad(ir_r, (0, N - ir_len)), 3500)
ir_l /= np.sqrt(np.sum(ir_l ** 2)); ir_r /= np.sqrt(np.sum(ir_r ** 2))
wet_l = np.fft.irfft(np.fft.rfft(left) * np.fft.rfft(ir_l), N)
wet_r = np.fft.irfft(np.fft.rfft(right) * np.fft.rfft(ir_r), N)
left = left + 0.22 * wet_l
right = right + 0.22 * wet_r

# ---------------------------------------------------------------- master
left = highpass(lowpass(left, 12000), 35)
right = highpass(lowpass(right, 12000), 35)
mix = np.stack([left, right], axis=1)
rms = np.sqrt(np.mean(mix ** 2))
mix *= 10 ** (-18 / 20) / rms              # RMS about -18 dBFS
mix = np.tanh(mix * 1.2) / 1.2             # soft limiter
peak = np.max(np.abs(mix))
if peak > 0.89:
    mix *= 0.89 / peak
pcm = (mix * 32767).astype('<i2')

out = sys.argv[1] if len(sys.argv) > 1 else 'music.wav'
with wave.open(out, 'wb') as w:
    w.setnchannels(2)
    w.setsampwidth(2)
    w.setframerate(SR)
    w.writeframes(pcm.tobytes())
final_rms = np.sqrt(np.mean((pcm / 32768.0) ** 2))
print('length %.1f s, peak %.1f dBFS, rms %.1f dBFS, wrap jump %.4f' % (
    N / SR, 20 * np.log10(np.max(np.abs(pcm)) / 32768.0), 20 * np.log10(final_rms),
    np.max(np.abs(mix[0] - mix[-1]))))
