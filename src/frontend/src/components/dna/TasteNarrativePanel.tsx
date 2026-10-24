import { useState } from "react";
import { Pencil, RefreshCw, Undo2 } from "lucide-react";
import {
  useGetNarrativeQuery,
  useRegenerateNarrativeMutation,
  useSaveNarrativeMutation,
} from "../../store/api";
import LoadFailed from "../settings/LoadFailed";
import SaveStatus from "../settings/SaveStatus";
import { saveFailureMessage, type SaveMessage } from "../settings/saveOutcome";

/**
 * What Reel believes about your taste, in words — and your correction to it.
 *
 * This is the top of the Taste DNA page rather than a footnote on it, because it is the only place
 * in the product that states, in plain language, what every downstream model call is told about
 * you. The charts below describe the data; this describes the person the data was read as.
 *
 * Editable is the point, not a nicety. A recommender that infers something wrong about you and
 * gives you no way to say so is one you stop arguing with and start ignoring. An edit wins
 * everywhere and survives every rewrite except the one you ask for.
 */
export default function TasteNarrativePanel() {
  const { data, isLoading, isError, isFetching, refetch } = useGetNarrativeQuery(undefined, {
    // While a rewrite is queued, poll so the new text arrives without a reload. Stops on its own
    // the moment the job clears.
    pollingInterval: 4000,
    skipPollingIfUnfocused: true,
  });
  const [save, { isLoading: saving }] = useSaveNarrativeMutation();
  const [regenerate, { isLoading: queueing }] = useRegenerateNarrativeMutation();

  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState("");
  // A failed save or revert keeps the editor open with the user's words in it, and says so. Both
  // used to close the editor whatever happened, so a correction that never saved was simply lost.
  const [saveFailure, setSaveFailure] = useState<{ message: SaveMessage; retry: () => void } | null>(null);

  const commit = async (text: string | null) => {
    setSaveFailure(null);
    try {
      await save({ text }).unwrap();
      setEditing(false);
    } catch (error: unknown) {
      const message = saveFailureMessage(error);
      const what = text === null ? "Couldn't go back to Reel's version" : "Couldn't save your words";
      const why = message.kind === "rejected" ? `: ${message.text}` : " — nothing changed.";
      setSaveFailure({ message: { ...message, text: `${what}${why}` }, retry: () => void commit(text) });
    }
  };

  // Adopt the server's text whenever we are not mid-edit. Guarded on `editing` so a poll landing
  // while the user is typing cannot overwrite what they are writing. Derived during render rather
  // than in an effect — an effect would paint the stale draft for a frame after each poll lands.
  const serverText = data?.text ?? "";
  const [syncedText, setSyncedText] = useState(serverText);
  if (!editing && serverText !== syncedText) {
    setSyncedText(serverText);
    setDraft(serverText);
  }

  if (isLoading) {
    return <div className="fa-card h-40 reel-shimmer" data-testid="narrative-loading" />;
  }

  // A failed read is not "no writer configured": without this branch the panel below told a user
  // whose request merely failed that Reel has no language model.
  if (isError && !data) {
    return (
      <LoadFailed
        what="what Reel believes about your taste"
        onRetry={() => void refetch()}
        retrying={isFetching}
        variant="inline"
        testId="narrative-error"
      />
    );
  }

  // No narrative yet, and no writer configured to produce one. Say so plainly rather than showing
  // an empty panel that looks broken.
  if (!data?.text) {
    return (
      <section className="fa-card p-5 space-y-2" data-testid="taste-narrative">
        <h2 className="fa-section-title">Your taste, in words</h2>
        <p className="fa-body text-fa-frost-dim">
          {data?.writerAvailable
            ? "Reel hasn’t written this yet — it lands with your next nightly pass."
            : "Reel can’t write this without a language model configured."}
        </p>
        {data?.writerAvailable && (
          <button
            type="button"
            onClick={() => regenerate()}
            disabled={queueing || data.regenerating}
            data-testid="narrative-regenerate"
            className="fa-button-ghost inline-flex items-center gap-2 disabled:opacity-50"
          >
            <RefreshCw className={`h-4 w-4 ${data.regenerating ? "animate-spin" : ""}`} />
            {data.regenerating ? "Writing…" : "Write it now"}
          </button>
        )}
      </section>
    );
  }

  const written = data.generatedAt
    ? new Date(data.generatedAt).toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" })
    : null;

  return (
    <section className="fa-card p-5 space-y-3" data-testid="taste-narrative">
      <div className="flex items-start justify-between gap-3">
        <div>
          <h2 className="fa-section-title">Your taste, in words</h2>
          <p className="fa-caption text-fa-frost-dim mt-1" data-testid="narrative-provenance">
            {data.isEdited ? "your words · " : ""}
            last written from {data.ratingsCount.toLocaleString()} ratings
            {written ? ` on ${written}` : ""}
            {/* Stale is stated, not hidden. It is still the best description Reel has, and saying
                so is cheaper than quietly showing something that no longer fits. */}
            {data.isStale ? " · your taste has moved since" : ""}
          </p>
        </div>
        <div className="flex items-center gap-2 shrink-0">
          {!editing && (
            <button
              type="button"
              onClick={() => setEditing(true)}
              data-testid="narrative-edit"
              className="fa-button-ghost inline-flex items-center gap-2"
            >
              <Pencil className="h-4 w-4" /> Edit
            </button>
          )}
          <button
            type="button"
            onClick={() => regenerate()}
            disabled={queueing || data.regenerating || !data.writerAvailable}
            data-testid="narrative-regenerate"
            title="Rewrite from scratch — this clears your edits"
            className="fa-button-ghost inline-flex items-center gap-2 disabled:opacity-50"
          >
            <RefreshCw className={`h-4 w-4 ${data.regenerating ? "animate-spin" : ""}`} />
            {data.regenerating ? "Writing…" : "Regenerate"}
          </button>
        </div>
      </div>

      {editing ? (
        <div className="space-y-2">
          <textarea
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            rows={8}
            maxLength={4000}
            data-testid="narrative-editor"
            className="w-full rounded-lg border border-fa-edge bg-fa-ink-2 p-3 fa-body text-fa-frost leading-relaxed focus:outline-none focus:ring-2 focus:ring-fa-frost/40"
          />
          <p className="fa-caption text-fa-frost-dim">
            Reel reads this before every pick it makes for you. Correcting it here changes what it looks for.
          </p>
          <div className="flex items-center gap-2">
            <button
              type="button"
              disabled={saving}
              data-testid="narrative-save"
              onClick={() => void commit(draft)}
              className="fa-button border-fa-frost/40 text-fa-frost disabled:opacity-50"
            >
              {saving ? "Saving…" : "Save"}
            </button>
            <button
              type="button"
              data-testid="narrative-cancel"
              onClick={() => {
                setDraft(data.text ?? "");
                setSaveFailure(null);
                setEditing(false);
              }}
              className="fa-button-ghost"
            >
              Cancel
            </button>
            {data.isEdited && (
              <button
                type="button"
                data-testid="narrative-revert"
                title="Drop your edits and go back to what Reel wrote"
                onClick={() => void commit(null)}
                className="fa-button-ghost inline-flex items-center gap-2"
              >
                <Undo2 className="h-4 w-4" /> Revert to Reel’s version
              </button>
            )}
          </div>
          <SaveStatus message={saveFailure?.message ?? null} onRetry={saveFailure?.retry} retrying={saving} testId="narrative-save-status" />
        </div>
      ) : (
        <p className="fa-body text-fa-frost leading-relaxed max-w-3xl" data-testid="narrative-text">
          {data.text}
        </p>
      )}
    </section>
  );
}
