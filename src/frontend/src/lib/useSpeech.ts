import { useCallback, useRef, useState } from "react";

/* eslint-disable @typescript-eslint/no-explicit-any */

/**
 * Maps the Web Speech API's `error` codes to something a person can act on. A silent stop was the
 * old behaviour: the mic simply stopped pulsing and the user was left guessing whether the browser
 * had heard them, whether permission was denied, or whether they had said nothing at all.
 */
function describe(code: string | undefined): string {
  switch (code) {
    case "not-allowed":
    case "service-not-allowed":
      return "Microphone access is blocked — allow it in your browser settings to search by voice.";
    case "no-speech":
      return "Didn’t catch that — try again.";
    case "audio-capture":
      return "No microphone found.";
    case "network":
      return "Voice search couldn’t reach the speech service.";
    case "aborted":
      return "";
    default:
      return "Voice search stopped unexpectedly.";
  }
}

/**
 * Thin wrapper over the Web Speech API for voice search. Gracefully degrades: `supported` is
 * false where the browser has no SpeechRecognition, so callers can hide the mic entirely. Errors
 * surface through `error` — callers are expected to render it, not swallow it.
 */
export function useSpeech(onResult: (text: string) => void) {
  const supported =
    typeof window !== "undefined" && ("SpeechRecognition" in window || "webkitSpeechRecognition" in window);
  const [listening, setListening] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const recRef = useRef<any>(null);

  const start = useCallback(() => {
    if (!supported) return;
    setError(null);
    const Ctor = (window as any).SpeechRecognition || (window as any).webkitSpeechRecognition;
    const rec = new Ctor();
    // The browser's own locale, not a hardcoded one. A hardcoded en-US transcribed an Afrikaans
    // or German speaker's title into the nearest English words and searched for those.
    rec.lang = navigator.language || "en-US";
    rec.interimResults = false;
    rec.maxAlternatives = 1;
    rec.onresult = (e: any) => {
      const text = e.results?.[0]?.[0]?.transcript;
      if (text) onResult(text);
    };
    rec.onend = () => setListening(false);
    rec.onerror = (e: any) => {
      setListening(false);
      // "aborted" is the user pressing stop — describe() returns "" for it, so nothing is shown.
      const message = describe(e?.error);
      setError(message.length > 0 ? message : null);
    };
    recRef.current = rec;
    setListening(true);
    // A browser can reject start() outright (already-started, no secure context); that throw would
    // otherwise leave the mic latched on with no explanation.
    try {
      rec.start();
    } catch {
      setListening(false);
      setError("Voice search couldn’t start.");
    }
  }, [supported, onResult]);

  const stop = useCallback(() => {
    recRef.current?.stop();
    setListening(false);
  }, []);

  return { supported, listening, error, start, stop };
}
