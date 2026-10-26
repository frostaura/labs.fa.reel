/**
 * Every assigned ISO 3166-1 alpha-2 code — the same 249 the server accepts (WS-8's RegionCodes, which
 * answers 400 `region_invalid` for anything else). The old picker offered 13 hand-picked codes, so
 * most of the world could not set where it watches (H-10).
 */
export const REGION_CODES = (
  "AD AE AF AG AI AL AM AO AQ AR AS AT AU AW AX AZ BA BB BD BE BF BG BH BI BJ BL BM BN BO BQ BR BS BT BV BW BY BZ " +
  "CA CC CD CF CG CH CI CK CL CM CN CO CR CU CV CW CX CY CZ DE DJ DK DM DO DZ EC EE EG EH ER ES ET FI FJ FK FM FO FR " +
  "GA GB GD GE GF GG GH GI GL GM GN GP GQ GR GS GT GU GW GY HK HM HN HR HT HU ID IE IL IM IN IO IQ IR IS IT JE JM JO JP " +
  "KE KG KH KI KM KN KP KR KW KY KZ LA LB LC LI LK LR LS LT LU LV LY MA MC MD ME MF MG MH MK ML MM MN MO MP MQ MR MS MT " +
  "MU MV MW MX MY MZ NA NC NE NF NG NI NL NO NP NR NU NZ OM PA PE PF PG PH PK PL PM PN PR PS PT PW PY QA RE RO RS RU RW " +
  "SA SB SC SD SE SG SH SI SJ SK SL SM SN SO SR SS ST SV SX SY SZ TC TD TF TG TH TJ TK TL TM TN TO TR TT TV TW TZ UA UG " +
  "UM US UY UZ VA VC VE VG VI VN VU WF WS YE YT ZA ZM ZW"
).split(" ");

export interface RegionOption {
  code: string;
  name: string;
}

/**
 * The regions with their names in the reader's own language (`Intl.DisplayNames`, the locale), sorted
 * by name. Falls back to the bare code where the runtime has no name for it.
 */
export function regionOptions(locale?: string): RegionOption[] {
  let names: Intl.DisplayNames | null = null;
  try {
    names = new Intl.DisplayNames(locale ? [locale] : undefined, { type: "region" });
  } catch {
    names = null;
  }
  return REGION_CODES.map((code) => ({ code, name: names?.of(code) ?? code })).sort((a, b) =>
    a.name.localeCompare(b.name, locale),
  );
}

/**
 * Matches by code ("za", "ZA") or by any word start of the name ("south", "afr"), best first: an
 * exact code, then a name that starts with the query, then a word inside the name.
 */
export function filterRegions(options: RegionOption[], query: string): RegionOption[] {
  const q = query.trim().toLowerCase();
  if (q.length === 0) return options;
  const rank = (o: RegionOption): number => {
    const name = o.name.toLowerCase();
    if (o.code.toLowerCase() === q) return 0;
    if (name.startsWith(q)) return 1;
    if (name.split(/[\s,()-]+/).some((word) => word.startsWith(q))) return 2;
    return -1;
  };
  return options
    .map((o) => ({ o, r: rank(o) }))
    .filter((x) => x.r >= 0)
    .sort((a, b) => a.r - b.r)
    .map((x) => x.o);
}
