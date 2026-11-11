# SPDX-License-Identifier: CC0-1.0
"""Original procedural score for Collector. Uses no recordings or third-party samples."""
from array import array
from pathlib import Path
import math, random, struct, sys, wave

RATE = 22050
DURATION = 96
N = RATE * DURATION
left, right = array('f', [0]) * N, array('f', [0]) * N
rng = random.Random(731409)
TAU = math.tau

def frequency(midi):
    return 440 * 2 ** ((midi - 69) / 12)

def note(start, duration, midi, amplitude, pan=0, kind='pad'):
    f = frequency(midi)
    gain_l, gain_r = math.sqrt((1-pan)/2), math.sqrt((1+pan)/2)
    offset = round(start * RATE)
    for j in range(round(duration * RATE)):
        t = j / RATE
        if kind == 'pad':
            attack = min(1, t/2.5)
            release = min(1, max(0, (duration-t)/3.8))
            env = math.sin(attack * math.pi/2)**2 * math.sin(release * math.pi/2)**2
            value = (math.sin(TAU*f*t) + .17*math.sin(TAU*f*2*t+.25)
                     + .08*math.sin(TAU*f*3*t)) * env
            value *= 1 + .025 * math.sin(TAU*.11*t)
        else:
            env = (1-math.exp(-t*35)) * math.exp(-t/1.45) * max(0, 1-t/duration)
            value = (math.sin(TAU*f*t+1.4*math.exp(-t*2.3)*math.sin(TAU*f*2*t))
                     + .15*math.sin(TAU*f*3*t)*math.exp(-t*2)) * env
        value *= amplitude
        i = (offset+j) % N
        left[i] += value*gain_l
        right[i] += value*gain_r

# Slow, open voicings: D minor(add9), Bb major7, F(add9), C(sus2),
# G minor9, Bb(add9), D minor9, A(sus4). No quotation of an existing tune.
chords = [(38,57,60,64), (34,53,57,62), (41,57,60,67), (36,55,62,67),
          (43,58,62,69), (34,53,60,65), (38,57,60,65), (33,55,62,64)]
for bar, chord in enumerate(chords):
    for k, midi in enumerate(chord):
        note(bar*12, 17, midi, .037 if k else .025, (-.5,-.18,.22,.52)[k])

# Deliberately sparse celesta-like gestures, separated by room for the pads.
phrases = [(74,81,76,77), (77,81,74,72), (72,79,81,77), (74,79,76,72),
           (79,82,81,74), (77,84,81,77), (81,77,76,74), (76,74,69,74)]
for bar, phrase in enumerate(phrases):
    for step, midi in enumerate(phrase):
        start = bar*12 + (1.0,3.5,6.25,9.1)[step]
        pan = rng.uniform(-.65,.65)
        note(start, 5, midi, .047, pan, 'bell')
        # Quiet stereo echoes synthesized from the same note, not samples.
        note(start+.43, 4, midi, .010, -pan, 'bell')
        note(start+.86, 3, midi, .004, pan, 'bell')

# Keep the master quiet; the same file is used on both desktop platforms.
peak = max(max(abs(x) for x in left), max(abs(x) for x in right))
gain = .20 / max(peak, .001)
pcm = bytearray(N*4)
for i in range(N):
    fade = min(1, i/(RATE*.8), (N-1-i)/(RATE*.8))
    l = max(-1,min(1,left[i]*gain*fade)); r = max(-1,min(1,right[i]*gain*fade))
    struct.pack_into('<hh', pcm, i*4, round(l*32767), round(r*32767))
path = Path(__file__).resolve().parents[1]/'src/Collector.Desktop/Assets/Audio/moonlit-archive.wav'
with wave.open(str(path),'wb') as w:
    w.setnchannels(2); w.setsampwidth(2); w.setframerate(RATE); w.writeframes(pcm)
print(f'{path}: {DURATION}s, stereo PCM, {RATE}Hz, peak <= 0.20')
