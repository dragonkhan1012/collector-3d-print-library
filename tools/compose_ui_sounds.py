# SPDX-License-Identifier: CC0-1.0
"""Original UI tones, no recordings or external samples. Quiet, soft-edged PCM."""
from pathlib import Path
import math, struct, wave
out=Path(__file__).resolve().parents[1]/'src/Collector.Desktop/Assets/Audio'
rate=22050
for name,duration,tones,peak in [
    ('ui-hover.wav',.13,[(880,1),(1320,.18)],.105),
    ('ui-click.wav',.20,[(660,.85),(990,.35),(1320,.10)],.15),
]:
    samples=[]
    for i in range(round(rate*duration)):
        t=i/rate
        envelope=(1-math.exp(-t*360))*math.exp(-t*24)*(math.sin(math.pi*(1-t/duration)/2)**2)
        value=sum(gain*math.sin(math.tau*frequency*t+.18*math.sin(math.tau*frequency*2*t)*math.exp(-t*35)) for frequency,gain in tones)*envelope
        samples.append(value)
    maximum=max(abs(x) for x in samples)
    pcm=b''.join(struct.pack('<h',round(x/maximum*peak*32767)) for x in samples)
    with wave.open(str(out/name),'wb') as w:
        w.setnchannels(1);w.setsampwidth(2);w.setframerate(rate);w.writeframes(pcm)
    print(name,duration,'s')
