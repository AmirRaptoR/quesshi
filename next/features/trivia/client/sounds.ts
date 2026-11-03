import { SOUNDS_KEY, soundsOn } from "../../../client/sounds";
import { stored } from "../../../client/storage";

/**
 * Today's six cues, synthesised with Web Audio so they need no files. A context made before the
 * first gesture would stay silent, so it is made on the first tap or key, and a player who turned sounds off hears nothing.
 */
let context: AudioContext | null = null;
if (typeof document !== "undefined") {
  const arm = () => {
    if (context) return;
    try {
      context = new AudioContext();
      void context.resume();
    } catch {
      // Web Audio unsupported: every cue stays silent.
    }
  };
  document.addEventListener("pointerdown", arm, { once: true });
  document.addEventListener("keydown", arm, { once: true });
}

function note(ctx: AudioContext, freq: number, start: number, duration: number, type: OscillatorType, peak: number) {
  const osc = ctx.createOscillator();
  const gain = ctx.createGain();
  osc.type = type;
  osc.frequency.value = freq;
  osc.connect(gain).connect(ctx.destination);
  const t = ctx.currentTime + start;
  gain.gain.setValueAtTime(0, t);
  gain.gain.linearRampToValueAtTime(peak, t + 0.005);
  gain.gain.exponentialRampToValueAtTime(0.0001, t + duration);
  osc.start(t);
  osc.stop(t + duration + 0.02);
}

const cues: Record<string, (ctx: AudioContext) => void> = {
  correct: (c) => { note(c, 660, 0, 0.12, "sine", 0.15); note(c, 880, 0.1, 0.16, "sine", 0.15); },
  wrong: (c) => note(c, 160, 0, 0.25, "triangle", 0.15),
  roundStart: (c) => note(c, 880, 0, 0.09, "sine", 0.1),
  tick: (c) => note(c, 1200, 0, 0.05, "square", 0.08),
  win: (c) => { note(c, 523, 0, 0.12, "sine", 0.15); note(c, 659, 0.1, 0.12, "sine", 0.15); note(c, 784, 0.2, 0.18, "sine", 0.15); },
  loss: (c) => { note(c, 440, 0, 0.14, "sine", 0.15); note(c, 330, 0.12, 0.2, "sine", 0.15); },
};

export function play(cue: "correct" | "wrong" | "roundStart" | "tick" | "win" | "loss") {
  if (!context || !soundsOn(stored.get(SOUNDS_KEY))) return;
  try {
    cues[cue]!(context);
  } catch {
    // No sound is never worth an error.
  }
}
