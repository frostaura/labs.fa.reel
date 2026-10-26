import { useState } from "react";
import { Heart, Plus, X } from "lucide-react";
import {
  useAddPreferenceTagMutation,
  useGetPreferenceTagsQuery,
  useRemovePreferenceTagMutation,
} from "../../store/api";
import SaveStatus from "../ui/save-status";
import { saveFailureMessage, type SaveMessage } from "../ui/save-outcome";

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
              <button
                onClick={() => void remove(tag.id, tag.text)}
                className="text-fa-frost-dim hover:text-fa-frost-bright"
                aria-label={`Remove ${tag.text}`}
                data-testid="remove-tag"
              >
                <X className="h-3 w-3" />
              </button>
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
          className="flex-1 rounded-full bg-fa-frost/10 px-4 py-2 fa-body text-fa-frost-bright placeholder:text-fa-frost-dim/60 focus:outline-none focus:ring-1 focus:ring-fa-frost/40"
        />
        <button
          type="submit"
          disabled={draft.trim().length < 2 || adding}
          className="rounded-full bg-fa-frost/20 p-2.5 text-fa-frost-bright hover:bg-fa-frost/30 disabled:opacity-40 transition-colors"
          aria-label="Add tag"
        >
          <Plus className="h-4 w-4" />
        </button>
      </form>
      <SaveStatus message={status?.message ?? null} onRetry={status?.retry ?? undefined} retrying={adding} testId="tag-save-status" />
    </section>
  );
}
