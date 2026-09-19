/**
 * VAPID application-server keys arrive base64url-encoded (RFC 7515 §2: `-`/`_` alphabet, no
 * padding), but `PushManager.subscribe` wants raw bytes. atob only speaks standard base64 with
 * padding, so translate the alphabet and re-pad first. Kept pure for unit testing.
 */
export function base64UrlToUint8Array(base64Url: string): Uint8Array<ArrayBuffer> {
  const padded = base64Url + "=".repeat((4 - (base64Url.length % 4)) % 4);
  const base64 = padded.replace(/-/g, "+").replace(/_/g, "/");
  const raw = atob(base64);
  // Explicit ArrayBuffer (not the ArrayBufferLike default): PushManager.subscribe's
  // applicationServerKey is BufferSource over ArrayBuffer, and TS's generic typed arrays
  // reject the wider type.
  const bytes = new Uint8Array(new ArrayBuffer(raw.length));
  for (let i = 0; i < raw.length; i++) {
    bytes[i] = raw.charCodeAt(i);
  }
  return bytes;
}
