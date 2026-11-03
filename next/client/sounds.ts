/** Whether sounds play: kept only in this browser, as today. */
export const SOUNDS_KEY = "quesshi.sounds";

/** Silence is never the unannounced default: anything but "0" is on. */
export const soundsOn = (stored: string | null): boolean => stored !== "0";
export const storedSounds = (on: boolean): string => (on ? "1" : "0");
