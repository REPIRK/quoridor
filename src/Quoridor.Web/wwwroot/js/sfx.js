// Every sound here is synthesised on the spot. Nothing is downloaded, which keeps the
// page the same size it was, and the tones can be shaped to match the board rather than
// picked from whatever samples happened to be free.
//
// Browsers refuse to start audio until the page has been interacted with, so the context
// is created on the first sound and resumed if it was suspended.

let audio = null;
let master = null;
let musicBus = null;
let musicGain = null;
let musicStop = null;

// Remembered across a context that does not exist yet, so the sliders work before the
// first sound has been made.
let effectsVolume = 0.5;
let musicVolume = 0.3;

function context() {
    if (audio === null) {
        const Ctor = window.AudioContext || window.webkitAudioContext;
        if (!Ctor) return null;

        audio = new Ctor();

        master = audio.createGain();
        master.gain.value = effectsVolume;
        master.connect(audio.destination);

        // The music has its own bus so the two sliders are genuinely independent.
        musicBus = audio.createGain();
        musicBus.gain.value = musicVolume;
        musicBus.connect(audio.destination);
    }

    if (audio.state === 'suspended') audio.resume();
    return audio;
}

/// A plain tone with a soft attack and an exponential tail — the shape of every blip here.
function tone(at, { type = 'sine', from, to = from, gain = 0.14, attack = 0.006, length = 0.12 }) {
    const osc = audio.createOscillator();
    const env = audio.createGain();

    osc.type = type;
    osc.frequency.setValueAtTime(from, at);
    if (to !== from) osc.frequency.exponentialRampToValueAtTime(to, at + length);

    env.gain.setValueAtTime(0.0001, at);
    env.gain.exponentialRampToValueAtTime(gain, at + attack);
    env.gain.exponentialRampToValueAtTime(0.0001, at + length);

    osc.connect(env);
    env.connect(master);

    osc.start(at);
    osc.stop(at + length + 0.02);
}

/// A short burst of filtered noise: what gives the wall its wooden edge.
function knock(at, { cutoff = 900, gain = 0.2, length = 0.13 }) {
    const frames = Math.ceil(audio.sampleRate * length);
    const buffer = audio.createBuffer(1, frames, audio.sampleRate);
    const data = buffer.getChannelData(0);

    for (let i = 0; i < frames; i++) {
        // Decaying noise rather than a flat burst, so it lands and stops.
        data[i] = (Math.random() * 2 - 1) * Math.pow(1 - i / frames, 3);
    }

    const source = audio.createBufferSource();
    source.buffer = buffer;

    const filter = audio.createBiquadFilter();
    filter.type = 'lowpass';
    filter.frequency.value = cutoff;

    const env = audio.createGain();
    env.gain.value = gain;

    source.connect(filter);
    filter.connect(env);
    env.connect(master);

    source.start(at);
}

export function play(kind) {
    if (!context()) return;

    const now = audio.currentTime;

    switch (kind) {
        case 'move':
            tone(now, { from: 520, to: 660, gain: 0.10, length: 0.09 });
            break;

        case 'wall':
            knock(now, { cutoff: 1100, gain: 0.22, length: 0.14 });
            tone(now, { type: 'triangle', from: 180, to: 120, gain: 0.11, length: 0.16 });
            break;

        case 'win':
            tone(now, { from: 523, gain: 0.12, length: 0.18 });
            tone(now + 0.12, { from: 659, gain: 0.12, length: 0.18 });
            tone(now + 0.24, { from: 784, gain: 0.13, length: 0.42 });
            break;

        case 'lose':
            tone(now, { type: 'triangle', from: 392, gain: 0.11, length: 0.22 });
            tone(now + 0.16, { type: 'triangle', from: 294, gain: 0.11, length: 0.5 });
            break;

        case 'illegal':
            tone(now, { type: 'square', from: 150, to: 120, gain: 0.05, length: 0.08 });
            break;
    }
}

/// A slow pentatonic drift over a quiet pad. Deliberately sparse: it is meant to be
/// something you stop noticing, not a tune you end up listening to instead of thinking.
export function music(on) {
    if (!on) {
        if (musicStop) musicStop();
        musicStop = null;
        return;
    }

    if (musicStop || !context()) return;

    musicGain = audio.createGain();
    musicGain.gain.value = 0.0001;
    musicGain.gain.exponentialRampToValueAtTime(0.5, audio.currentTime + 3);
    musicGain.connect(musicBus);

    // The pad: two slightly detuned saws kept dark by a lowpass that breathes.
    const pad = audio.createGain();
    pad.gain.value = 0.035;

    const filter = audio.createBiquadFilter();
    filter.type = 'lowpass';
    filter.frequency.value = 420;
    filter.Q.value = 0.8;

    const sweep = audio.createOscillator();
    const sweepDepth = audio.createGain();
    sweep.frequency.value = 0.05;
    sweepDepth.gain.value = 140;
    sweep.connect(sweepDepth);
    sweepDepth.connect(filter.frequency);
    sweep.start();

    const drones = [110, 110.4, 164.8].map(hz => {
        const osc = audio.createOscillator();
        osc.type = 'sawtooth';
        osc.frequency.value = hz;
        osc.connect(pad);
        osc.start();
        return osc;
    });

    pad.connect(filter);
    filter.connect(musicGain);

    // A note every few seconds, from a scale that cannot land on a sour interval.
    const scale = [440, 493.88, 587.33, 659.25, 880];
    let alive = true;

    const next = () => {
        if (!alive) return;

        const osc = audio.createOscillator();
        const env = audio.createGain();
        const at = audio.currentTime;

        osc.type = 'sine';
        osc.frequency.value = scale[Math.floor(Math.random() * scale.length)];

        env.gain.setValueAtTime(0.0001, at);
        env.gain.exponentialRampToValueAtTime(0.05, at + 0.6);
        env.gain.exponentialRampToValueAtTime(0.0001, at + 3.4);

        osc.connect(env);
        env.connect(musicGain);
        osc.start(at);
        osc.stop(at + 3.6);

        setTimeout(next, 2600 + Math.random() * 3800);
    };

    setTimeout(next, 1500);

    musicStop = () => {
        alive = false;

        const end = audio.currentTime + 1.2;
        musicGain.gain.exponentialRampToValueAtTime(0.0001, end);

        setTimeout(() => {
            drones.forEach(osc => osc.stop());
            sweep.stop();
            musicGain.disconnect();
        }, 1400);
    };
}

export function setVolume(effects, music) {
    effectsVolume = Math.max(0, Math.min(1, effects));
    musicVolume = Math.max(0, Math.min(1, music));

    // Setting a volume must not be what starts the audio: a page the player has not
    // touched yet would only get a console warning for its trouble.
    if (audio === null) return;

    master.gain.value = effectsVolume;
    musicBus.gain.value = musicVolume;
}
