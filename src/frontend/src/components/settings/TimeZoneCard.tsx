import { useEffect, useState } from "react";
import { Button } from "../ui/button";
import LoadFailed from "../ui/load-failed";
import SaveStatus from "../ui/save-status";
import { saveFailureMessage, savedMessage, type SaveMessage } from "../ui/save-outcome";
import { useGetSettingsQuery, useUpdateSettingsMutation } from "../../store/api";
import { COARSE_TAP } from "../../lib/tapTarget";
import SearchPicker from "./SearchPicker";
import {
  deviceTimeZone,
  filterTimeZones,
  suggestedTimeZone,
  timeZoneLabel,
  timeZoneName,
  timeZoneOffset,
  timeZoneOptions,
  timeZoneState,
} from "./timeZones";

const keyOf = (id: string) => id;
const render = (id: string) => ({ text: timeZoneName(id), note: timeZoneOffset(id) ?? undefined });

/**
 * The account's time zone (R1-17): the zone Reel counts the account's day in. The server has stored
 * one since ADD-22, but nothing on screen could set it, so every account was counted in UTC — and
 * "today" turned over at 02:00 for the founder at +2, mid-evening in the Americas.
 *
 * Shows the zone in force, and says when nobody chose it. Offers this device's zone in one tap when
 * it differs (a new account, or a traveller), and any IANA zone through the searchable picker. A
 * refused zone (400 `time_zone_invalid`) shows the server's reason and changes nothing.
 */
export default function TimeZoneCard() {
  const { data: settings, isError, isFetching, refetch } = useGetSettingsQuery();
  const [updateSettings, { isLoading: saving }] = useUpdateSettingsMutation();
  const [status, setStatus] = useState<SaveMessage | null>(null);
  const [lastTried, setLastTried] = useState<string | null>(null);
  // Not memoised by hand: the React Compiler caches these on their inputs.
  const device = deviceTimeZone();
  // What the last confirmed save stored, until the read catches up. The query hook keeps showing its
  // PREVIOUS result while the save's refetch is in flight — and for good if that refetch fails — so
  // without this the card went on saying "Not set" after "Saved." (the same fix as the email box).
  const [confirmed, setConfirmed] = useState<string | null>(null);
  const stored = timeZoneState(settings);
  if (confirmed !== null && stored.chosen?.toLowerCase() === confirmed.toLowerCase()) setConfirmed(null);
  const chosen = confirmed ?? stored.chosen;
  const inForce = confirmed ?? stored.inForce;
  const options = timeZoneOptions([device, chosen]);
  const suggestion = suggestedTimeZone(inForce, device);

  useEffect(() => {
    if (!status || status.persistent) return;
    const handle = window.setTimeout(() => setStatus(null), 1500);
    return () => window.clearTimeout(handle);
  }, [status]);

  const save = async (zone: string) => {
    setLastTried(zone);
    setStatus(null);
    try {
      const result = await updateSettings({ timeZone: zone }).unwrap();
      // Judge by what was stored, not the status: an older server answers 200 and ignores the field.
      // Case-insensitive, because the server stores the zone's own spelling of what it was sent.
      if (result.timeZone && result.timeZone.toLowerCase() === zone.toLowerCase()) {
        setConfirmed(result.timeZone);
        setStatus(savedMessage("Saved."));
      } else {
        setStatus({ ...saveFailureMessage({ status: 400 }), text: `${zone} wasn't stored — nothing changed.` });
      }
    } catch (error: unknown) {
      setStatus(saveFailureMessage(error));
    }
  };

  return (
    <section aria-labelledby="settings-time-zone-title" className="fa-card p-5 space-y-3" data-testid="settings-time-zone">
      <h2 id="settings-time-zone-title" className="fa-section-title">
        Time zone
      </h2>
      <p className="fa-caption text-3">
        Reel counts your day in this zone: today&apos;s picks and a day&apos;s Ask Reel allowance turn over at midnight here.
      </p>

      {isError && !settings ? (
        <LoadFailed what="your time zone" onRetry={() => void refetch()} retrying={isFetching} variant="inline" testId="time-zone-load-error" />
      ) : !settings ? (
        <div className="h-11 max-w-sm rounded-md reel-shimmer" data-testid="time-zone-loading" />
      ) : (
        <>
          <p className="fa-body text-fa-frost" data-testid="time-zone-current">
            {chosen
              ? `Counting your day in ${timeZoneLabel(inForce)}.`
              : `Not set — Reel is counting your day in ${timeZoneLabel(inForce)}.`}
          </p>
          {suggestion && (
            <Button
              variant={chosen ? "ghost" : "primary"}
              onClick={() => void save(suggestion)}
              disabled={saving}
              // Wraps rather than running past the card: a long zone name on a phone is wider than it.
              className={`h-auto max-w-full whitespace-normal py-2 text-left ${COARSE_TAP}`}
              data-testid="time-zone-use-device"
            >
              Use this device&apos;s zone — {timeZoneName(suggestion)}
            </Button>
          )}
          <SearchPicker
            label="Choose a time zone"
            placeholder="Search by city or region"
            listLabel="Time zones"
            noun="time zone"
            options={options}
            keyOf={keyOf}
            filter={filterTimeZones}
            render={render}
            value={chosen}
            valueText={chosen ? timeZoneName(chosen) : ""}
            onSelect={(zone) => void save(zone)}
            disabled={saving}
            testId="time-zone-select"
          />
          <SaveStatus
            message={status}
            onRetry={lastTried ? () => void save(lastTried) : undefined}
            retrying={saving}
            testId="time-zone-save-status"
          />
        </>
      )}
    </section>
  );
}
