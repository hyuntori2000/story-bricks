"""Builds Assets/StoryBricks/Audio/SFX/story_sfx_judy_moon.wav: every story sound effect on one track,
placed at the times of the narration recording (narration_judy_moon). Edit the times at the bottom and re-run."""
import numpy as np, wave, os
sr=44100; N=int(61*sr); out=np.zeros(N); rng=np.random.default_rng(7)
def add(sig,t,gain=1.0):
    i=int(t*sr); n=min(len(sig),N-i)
    if n>0: out[i:i+n]+=sig[:n]*gain
def bell(f,dur,amp=1.0):
    t=np.arange(int(dur*sr))/sr; s=np.zeros_like(t)
    for m,a,d in [(1,1,1),(2.76,.55,.7),(5.4,.3,.45),(8.93,.15,.3)]:
        s+=a*np.sin(2*np.pi*f*m*t+rng.uniform(0,6.28))*np.exp(-t/(dur*0.22*d))
    s[:88]*=np.linspace(0,1,88); return s*amp
def jingle(t0,dur,n,gain):
    for _ in range(n): add(bell(rng.uniform(2300,3900),rng.uniform(0.18,0.4),rng.uniform(0.5,1.0)),t0+rng.uniform(0,dur),gain)
def noiseband(dur,lo,hi):
    n=int(dur*sr); X=np.fft.rfft(rng.standard_normal(n)); f=np.fft.rfftfreq(n,1/sr)
    w=np.exp(-0.5*((np.log(np.maximum(f,1))-np.log(np.sqrt(lo*hi)))/(np.log(hi/lo)/2.2))**2); y=np.fft.irfft(X*w,n); return y/np.abs(y).max()
def whoosh(dur,lo=250,hi=2200):
    t=np.arange(int(dur*sr))/sr; e=np.exp(-0.5*((t-dur*0.42)/(dur*0.2))**2)
    return (noiseband(dur,lo,hi)*(1-0.6*e)+noiseband(dur,lo*2.2,hi*2.2)*0.6*e)*e
def hum(dur,f0,amp_env):
    t=np.arange(int(dur*sr))/sr
    s=np.sin(2*np.pi*f0*t)+0.6*np.sin(2*np.pi*2*f0*t+0.4)+0.35*np.sin(2*np.pi*3*f0*t+1.1)+0.15*np.sin(2*np.pi*4*f0*t)
    return s*(1+0.12*np.sin(2*np.pi*0.9*t))*amp_env(t)/2.1
def thud():
    t=np.arange(int(0.5*sr))/sr; f=170*np.exp(-t*7)+85
    return np.sin(2*np.pi*np.cumsum(f)/sr)*np.exp(-t/0.11)+0.3*noiseband(0.5,200,900)*np.exp(-t/0.03)
def twang():
    t=np.arange(int(0.45*sr))/sr; f=260*(1+0.35*np.exp(-t*18))
    s=(np.sin(2*np.pi*np.cumsum(f)/sr)+0.4*np.sin(4*np.pi*np.cumsum(f)/sr))*np.exp(-t/0.12); s[:60]*=np.linspace(0,1,60); return s

# ---- cue sheet (seconds into the narration) ----
add(hum(4.2,110,lambda t: np.clip(t/1.0,0,1)*np.clip((4.2-t)/1.6,0,1)),11.6,0.22)   # "every night it hummed"
add(whoosh(2.6),18.6,0.42); add(whoosh(3.0,200,1500),20.4,0.16)                        # "Whoosh! The wind..."
add(whoosh(2.4,300,2600),22.7,0.26)                                                    # "Up went the kite"
add(twang(),24.5,0.22)                                                                 # "The string pulled"
jingle(27.1,0.42,8,0.085); jingle(27.75,0.42,8,0.085); jingle(28.4,1.2,6,0.05)         # "Jingle, jingle"
for t0 in np.arange(29.9,35.4,0.38):                                                   # "smaller, and smaller"
    k=1-(t0-29.9)/5.6; jingle(t0,0.3,max(1,int(4*k)),0.06*k**1.6+0.004)
add(whoosh(5.5,180,1200),29.8,0.07)
add(thud(),35.8,0.42)                                                                  # "bump"
e1=lambda t: np.clip(t/1.6,0,1)*np.clip((10.6-t)/1.8,0,1)*(0.75+0.25*(np.exp(-0.5*((t-6.7)/0.35)**2)+np.exp(-0.5*((t-7.8)/0.35)**2)+np.exp(-0.5*((t-8.9)/0.45)**2)))
add(hum(10.6,165,e1),38.1,0.19)                                                        # the string hums; swells on "Soft. Cold. Humming."
add(twang(),51.6,0.22)                                                                 # "one little tug back"
jingle(56.15,0.42,8,0.08); jingle(56.8,0.42,8,0.08)                                    # "Jingle, jingle, all the way home"
for t0 in np.arange(57.5,59.9,0.4):
    k=1-(t0-57.5)/2.6; jingle(t0,0.3,max(1,int(4*k)),0.05*k+0.004)

out=np.clip(out,-0.98,0.98)
dst=os.path.join(os.path.dirname(os.path.abspath(__file__)),"..","Assets","StoryBricks","Audio","SFX","story_sfx_judy_moon.wav")
w=wave.open(dst,"wb"); w.setnchannels(1); w.setsampwidth(2); w.setframerate(sr); w.writeframes((out*32767).astype(np.int16).tobytes()); w.close()
print("peak %.2f"%np.abs(out).max())
