import { useState } from "react";
import { Heart, Plus, X } from "lucide-react";
import {
  useAddPreferenceTagMutation,
  useGetPreferenceTagsQuery,
  useRemovePreferenceTagMutation,
} from "../../store/api";
import SaveStatus from "../ui/save-status";
import { saveFailureMessage, type SaveMessage } from "../ui/save-outcome";
import { Button } from "../ui/button";
import { COARSE_TAP } from "../../lib/tapTarget";

/**
 * "Things you're into" — positive preference tags (e.g. "magical serial killer mysteries"). The
 * inverse of exclusion filters: they boost matching titles across the feed and Ask Reel.
 */
export default function PreferenceTags() {
  const { data: tags = [] } = useGetPreferenceTagsQuery();
  const [addTag, { isLoading: adding }] = useAddPreferenceTagMutation();
  const [removeTag] = useRemovePreferenceTagMutation();
  const [draft, setDraft] = useState("");
  // A failed add or remove says so and stays up. The draft used to be cleared BEFORE the request
  // and the error swallowed, so a tag that never saved simply vanished from the box.
  const [status, setStatus] = useState<{ message: SaveMessage; retry: (() => void) | null } | null>(null);

  const add = async (text: string) => {
    setStatus(null);
    try {
      await addTag(text).unwrap();
      // Cleared only once the server has it — and only if the user has not started typing another.
      setDraft((current) => (current.trim() === text ? "" : current));
    } catch (error: unknown) {
      const message = saveFailureMessage(error);
      const why = message.kind === "rejected" ? `: ${message.text}` : " — nothing changed.";
      setStatus({ message: { ...message, text: `Couldn't add "${text}"${why}` }, retry: () => void add(text) });
    }
  };

  const remove = async (id: string, text: string) => {
    setStatus(null);
    try {
      await removeTag(id).unwrap();
    } catch (error: unknown) {
      const message = saveFailureMessage(error);
      setStatus({ message: { ...message, text: `Couldn't remove "${text}" — it's still on.` }, retry: () => void remove(id, text) });
    }
  };

  const submit = (e: React.FormEvent) => {
    e.preventDefault();
    const text = draft.trim();
    if (text.length < 2) return;
    void add(text);
  };

  return (
    <section className="fa-card p-5 space-y-4" data-testid="preference-tags">
      <div className="flex items-center gap-2">
        <Heart className="h-4 w-4 text-fa-frost" />
        <h2 className="fa-body text-fa-frost-bright">Things you’re into</h2>
      </div>
      <p className="fa-caption text-fa-frost-dim">
        Describe a niche you love — “magical serial killer mysteries”, “slow-burn A24 horror”. Reel
        pulls matching titles in and ranks them higher, everywhere.
      </p>

      {tags.length > 0 && (
        <div className="flex flex-wrap gap-2">
          {tags.map((tag) => (
            <span
              key={tag.id}
              className="group inline-flex items-center gap-1.5 rounded-full bg-fa-frost/15 px-3 py-1 fa-caption text-fa-frost-bright"
            >
              {tag.text}
              <Button
                variant="ghost"
                size="icon-sm"
                onClick={() => void remove(tag.id, tag.text)}
                className={`h-5 w-5 rounded-full ${COARSE_TAP}`}
                aria-label={`Remove ${tag.text}`}
                data-testid="remove-tag"
              >
                <X className="h-3 w-3" />
              </Button>
            </span>
          ))}
        </div>
      )}

      <form onSubmit={submit} className="flex items-center gap-2">
        <input
          data-testid="add-tag-input"
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          placeholder="Add something you’re into…"
          maxLength={120}
          className={`flex-1 rounded-full bg-fa-frost/10 px-4 py-2 fa-body text-fa-frost-bright placeholder:text-3 focus:outline-none focus:ring-1 focus:ring-fa-frost/40 ${COARSE_TAP}`}
        />
        <Button
          type="submit"
          variant="primary"
          size="icon"
          disabled={draft.trim().length < 2 || adding}
          className={`rounded-full ${COARSE_TAP}`}
          aria-label="Add tag"
        >
          <Plus className="h-4 w-4" />
        </Button>
      </form>
      <SaveStatus message={status?.message ?? null} onRetry={status?.retry ?? undefined} retrying={adding} testId="tag-save-status" />
    </section>
  );
}
