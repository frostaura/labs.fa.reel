/**
 * The filmography line under a title on a person page: what this person did on it (F-24). A
 * director's page listed every title they were credited on with nothing to say which they directed
 * and which they merely appear in, so "you rate their work 8.1" could not be checked against the
 * work. "Director", "Director · Writer", "as Neil McCauley".
 *
 * Built only from what the API states. No roles, no character: an empty line, never a role guessed
 * from the person's `department`.
 */
const ROLE_LABEL: Record<string, string> = {
  Director: "Director",
  Writer: "Writer",
  Creator: "Creator",
  Actor: "Cast",
};

export function filmographyRoleLine(credit: { roles?: string[] | null; character?: string | null }): string | null {
  const roles = (credit.roles ?? []).filter((r) => r.trim() !== "");
  const character = credit.character?.trim() || null;
  // An acting credit with a known character reads as the character; the bare "Cast" adds nothing.
  const named = roles
    .filter((r) => !(r === "Actor" && character))
    .map((r) => ROLE_LABEL[r] ?? r);
  const parts = [...new Set(named)];
  if (character) parts.push(`as ${character}`);
  return parts.length > 0 ? parts.join(" · ") : null;
}
