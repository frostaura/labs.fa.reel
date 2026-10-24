import { useState } from "react";
import { Link, useLocation, useParams } from "react-router";
import { RotateCw, Star } from "lucide-react";
import { useGetPersonQuery, useRatePersonMutation, useUnratePersonMutation } from "../store/api";
import { profileUrl } from "../lib/tmdbImages";
import PosterImage from "../components/rec/PosterImage";
import ScoreBadges from "../components/rec/ScoreBadges";
import { titleFailure } from "../components/title/titleRouting";

/** Initials for the avatar fallback — first letters of the first two words of a name. */
function initials(name: string): string {
  return name.split(" ").map((w) => w[0]).slice(0, 2).join("");
}

/**
 * Round profile photo with the same failure contract as PosterImage (which cannot be reused here —
 * it is locked to the 2:3 poster box): a missing path *and* a URL that 404s both degrade to
 * initials rather than the browser's broken-image glyph. TMDB profile paths go stale often enough
 * that the `onError` half is not theoretical.
 */
function PersonAvatar({ name, profilePath }: { name: string; profilePath: string | null }) {
  const [failed, setFailed] = useState(false);
  const url = profileUrl(profilePath);

  return (
    <div className="h-24 w-24 rounded-full overflow-hidden bg-fa-ink-3 shrink-0">
      {url && !failed ? (
        <img
          src={url}
          alt={name}
          onError={() => setFailed(true)}
          className="h-full w-full object-cover"
        />
      ) : (
        <div className="h-full w-full flex items-center justify-center bg-gradient-to-br from-fa-ink-2 to-fa-ink-3 text-fa-frost-dim select-none">
          {initials(name)}
        </div>
      )}
    </div>
  );
}

/** Actor/person page: who they are, your explicit rating, the derived affinity, filmography. */
export default function Person() {
  const { personId = "" } = useParams();
  const location = useLocation();
  const { data: person, isLoading, isError, error, isFetching, refetch } = useGetPersonQuery(personId, { skip: !personId });
  const [ratePerson] = useRatePersonMutation();
  const [unratePerson] = useUnratePersonMutation();

  // A 404 is an answer and any other failure is a retryable one; both used to read as "Loading…"
  // forever (F-9).
  if (!person && (isError || !personId)) {
    const failure = personId ? titleFailure(error) : "not-found";
    return (
      <div className="fa-card p-8 text-center space-y-4" role="alert" data-testid={`person-${failure}`}>
        <p className="fa-body text-fa-frost-bright">
          {failure === "not-found" ? "Reel has no person at this address." : "Couldn't load this person — try again."}
        </p>
        <div className="flex items-center justify-center gap-2">
          {failure === "error" && (
            <button type="button" onClick={() => void refetch()} disabled={isFetching} className="fa-button min-h-11" data-testid="person-retry">
              <RotateCw className="h-4 w-4" />
              {isFetching ? "Retrying…" : "Retry"}
            </button>
          )}
          <Link to="/home" className="fa-button-ghost min-h-11">
            Back to Home
          </Link>
        </div>
      </div>
    );
  }

  if (isLoading || !person) {
    return <div className="fa-body text-fa-frost-dim py-10 text-center">Loading…</div>;
  }

  return (
    <div className="space-y-6">
      <header className="flex items-start gap-4">
        <PersonAvatar name={person.name} profilePath={person.profilePath} />
        <div className="space-y-2">
          <h1 className="text-2xl font-light text-fa-frost-bright">{person.name}</h1>
          {person.department && <p className="fa-caption text-fa-frost-dim">{person.department}</p>}
          {person.derivedAffinity != null && (
            <p className="fa-caption text-fa-frost-dim">
              You rate their work <span className="text-fa-frost">{person.derivedAffinity.toFixed(1)}</span> on average
              {" "}across {person.ratedTitleCount} {person.ratedTitleCount === 1 ? "title" : "titles"}.
            </p>
          )}
        </div>
      </header>

      {/* Explicit rating control */}
      <section className="fa-card p-4 space-y-2">
        <p className="fa-overline text-fa-frost-dim flex items-center gap-1.5">
          <Star className="h-3.5 w-3.5" /> your rating
        </p>
        <div className="flex gap-1 flex-wrap">
          {Array.from({ length: 10 }, (_, i) => i + 1).map((value) => (
            <button
              key={value}
              onClick={() => ratePerson({ personId, rating: value })}
              className={`h-9 w-8 rounded-md border text-sm tabular-nums transition ${
                person.userRating === value
                  ? "border-fa-frost bg-fa-frost/20 text-fa-frost-bright"
                  : "border-fa-edge bg-fa-glass text-fa-frost hover:border-fa-frost/40"
              }`}
              data-testid="person-rate-value"
            >
              {value}
            </button>
          ))}
          {person.userRating != null && (
            <button onClick={() => unratePerson({ personId })} className="fa-button-ghost text-fa-frost-dim ml-2">
              Clear
            </button>
          )}
        </div>
        <p className="fa-caption text-fa-frost-dim/70">Rating someone you love nudges their titles up across your feed.</p>
      </section>

      {/* Filmography */}
      {person.filmography.length > 0 && (
        <section className="space-y-3">
          <h2 className="fa-section-title text-base">In your library &amp; beyond</h2>
          <div className="grid grid-cols-3 sm:grid-cols-5 md:grid-cols-6 gap-4">
            {person.filmography.map((t) => (
              <Link
                key={t.titleId}
                to={`/title/${t.mediaType.toLowerCase()}/${t.tmdbId}`}
                state={{ backgroundLocation: location }}
                className="group block focus:outline-none"
              >
                <div className="relative overflow-hidden rounded-lg transition-transform duration-200 group-hover:scale-[1.03]">
                  {/* The shared poster component, not hand-rolled markup: it carries the shimmer,
                      the gradient-initials fallback for a null path, and the onError that keeps a
                      stale TMDB URL from rendering the browser's broken-image glyph. */}
                  <PosterImage path={t.posterPath} alt={t.name} size="w185" />
                  {/* The shared stack; an explicit rating replaces the prediction (its rule). */}
                  <ScoreBadges userRating={t.userRating} predictedRating={t.predictedRating} />
                </div>
                <p className="mt-2 fa-caption text-fa-frost-bright truncate" title={t.name}>{t.name}</p>
                <p className="fa-caption text-fa-frost-dim">{t.year ?? ""}</p>
              </Link>
            ))}
          </div>
        </section>
      )}
    </div>
  );
}
