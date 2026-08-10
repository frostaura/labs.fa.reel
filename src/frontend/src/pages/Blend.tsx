import { useState } from "react";
import { Check, Copy, RefreshCw, Users } from "lucide-react";
import {
  useCreateBlendCodeMutation,
  useGetBlendPicksMutation,
  useLazyGetBlendPreviewQuery,
} from "../store/api";
import type { BlendCard } from "../store/feedTypes";
import PosterImage from "../components/rec/PosterImage";

function JointPickCard({ card }: { card: BlendCard }) {
  return (
    <div className="w-full space-y-2">
      <PosterImage path={card.posterPath} alt={card.name} />
      <p className="fa-body text-fa-frost-bright truncate" title={card.name}>
        {card.name}
        {card.year != null && <span className="text-fa-frost-dim"> ({card.year})</span>}
      </p>
      <p className="fa-caption text-fa-frost">
        <span className="text-fa-frost-bright">{card.forYou.toFixed(1)}</span> you ·{" "}
        <span className="text-fa-frost-bright">{card.forThem.toFixed(1)}</span> them
      </p>
    </div>
  );
}

/**
 * Two-person Blend: share your code, enter theirs, get the titles BOTH models rate highly
 * (ranked by the lower of the two predictions — a pick either of you would veto never shows).
 */
export default function Blend() {
  const [createCode, { data: codeData, isLoading: creating }] = useCreateBlendCodeMutation();
  const [copied, setCopied] = useState(false);
  const [enteredCode, setEnteredCode] = useState("");
  const [fetchPreview, preview] = useLazyGetBlendPreviewQuery();
  const [fetchPicks, picks] = useGetBlendPicksMutation();

  const code = codeData?.code;
  const normalized = enteredCode.trim().toUpperCase();

  const copyCode = async () => {
    if (!code) return;
    await navigator.clipboard.writeText(code).catch(() => undefined);
    setCopied(true);
    window.setTimeout(() => setCopied(false), 2000);
  };

  const previewCode = (e: React.FormEvent) => {
    e.preventDefault();
    if (normalized.length === 8) {
      picks.reset();
      void fetchPreview(normalized);
    }
  };

  return (
    <div className="max-w-5xl space-y-6">
      <div>
        <h1 className="text-2xl font-light text-fa-frost-bright">Blend</h1>
        <p className="fa-caption text-fa-frost-dim mt-1">
          Movie night for two — picks both your models vouch for.
        </p>
      </div>

      <div className="space-y-6 lg:grid lg:grid-cols-2 lg:gap-6 lg:space-y-0 lg:items-start">
        <section className="fa-card p-5 space-y-3" data-testid="blend-my-code">
          <h2 className="fa-section-title">Your code</h2>
          <p className="fa-caption text-fa-frost-dim">
            Share it with the person you&rsquo;re watching with. Regenerating kills the old code.
          </p>
          {code ? (
            <div className="flex items-center gap-2">
              <span className="rounded-lg bg-fa-frost/10 px-4 py-2 font-mono text-lg tracking-[0.2em] text-fa-frost-bright" data-testid="blend-code">
                {code}
              </span>
              <button type="button" onClick={copyCode} className="fa-button" aria-label="Copy code">
                {copied ? <Check className="h-4 w-4" /> : <Copy className="h-4 w-4" />}
                {copied ? "Copied" : "Copy"}
              </button>
              <button
                type="button"
                onClick={() => createCode()}
                disabled={creating}
                className="fa-button disabled:opacity-40"
                aria-label="Regenerate code"
              >
                <RefreshCw className={`h-4 w-4 ${creating ? "animate-spin" : ""}`} />
              </button>
            </div>
          ) : (
            <button type="button" onClick={() => createCode()} disabled={creating} className="fa-button-primary disabled:opacity-40">
              <Users className="h-4 w-4" /> {creating ? "Creating…" : "Get my code"}
            </button>
          )}
        </section>

        <section className="fa-card p-5 space-y-3" data-testid="blend-their-code">
          <h2 className="fa-section-title">Their code</h2>
          <form onSubmit={previewCode} className="flex items-center gap-2">
            <input
              value={enteredCode}
              onChange={(e) => setEnteredCode(e.target.value)}
              placeholder="8-character code"
              maxLength={8}
              autoCapitalize="characters"
              autoCorrect="off"
              spellCheck={false}
              className="flex-1 rounded-full bg-fa-frost/10 px-4 py-2 font-mono uppercase tracking-widest fa-body text-fa-frost-bright placeholder:text-fa-frost-dim/60 placeholder:normal-case placeholder:font-sans placeholder:tracking-normal focus:outline-none focus:ring-1 focus:ring-fa-frost/40"
            />
            <button type="submit" disabled={normalized.length !== 8 || preview.isFetching} className="fa-button disabled:opacity-40">
              {preview.isFetching ? "Looking…" : "Preview"}
            </button>
          </form>
          {preview.isError && (
            <p className="fa-caption text-fa-danger">No blend found for that code — check it, or ask them to regenerate.</p>
          )}
          {preview.data && !preview.isError && (
            <div className="flex items-center justify-between gap-3 border-t border-fa-edge/50 pt-3">
              <p className="fa-body text-fa-frost">
                <span className="text-fa-frost-bright">{preview.data.ownerName}</span>
                {" · "}
                {preview.data.eligibleBothCount} title{preview.data.eligibleBothCount === 1 ? "" : "s"} eligible for you both
              </p>
              <button
                type="button"
                onClick={() => void fetchPicks(normalized)}
                disabled={picks.isLoading}
                className="fa-button-primary disabled:opacity-40"
                data-testid="blend-get-picks"
              >
                {picks.isLoading ? "Blending…" : "Get joint picks"}
              </button>
            </div>
          )}
        </section>
      </div>

      {picks.data && (
        <section className="space-y-3" data-testid="blend-picks">
          <h2 className="fa-section-title text-base">
            You &amp; <span className="text-fa-frost-bright">{picks.data.ownerName}</span>
          </h2>
          {picks.data.items.length === 0 ? (
            <p className="fa-body text-fa-frost-dim">
              No joint picks yet — both of you need a trained model and some overlap in what you haven&rsquo;t seen.
            </p>
          ) : (
            <div className="grid grid-cols-2 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 gap-4">
              {picks.data.items.map((card) => (
                <JointPickCard key={card.titleId} card={card} />
              ))}
            </div>
          )}
        </section>
      )}
    </div>
  );
}
