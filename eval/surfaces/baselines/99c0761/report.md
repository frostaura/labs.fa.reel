# Surface harness — surfaces-99c0761

- API build 99c0761 · snapshot `reel-2026-10-23` · artifact `unrecorded` · one Fresh build (Succeeded) · 300 s
- Jobs drained before the first fetch: not recorded (recording predates the drain) · model key: unrecorded
- Exposures written by the run: 282; Trakt calls: 0; cost $0.0262

- **Coverage: 1 surface(s) have NO data** — digest: no cards. Their zeros below are not measurements.

## Gates (task 8 targets)

| Gate | Value | Target | Status |
|---|---|---|---|
| Exclusion leaks on suggestion surfaces | 22 | <= 0 | FAIL |
| Stub why-lines (share of suggestion cards) | 0.209 | <= 0 | FAIL |
| Cross-rail repeats on the feed page | 0 | <= 0 | pass |
| Why-lines hedging against their own pick | 29 | <= 0 | FAIL |
| Non-Latin script in English why-lines | 1 | <= 0 | FAIL |
| Watched or rated titles on Saved and the Watch-tonight pin | 116 | <= 0 | FAIL |
| Surfaces answering with an HTTP error | 0 | <= 0 | pass |
| Trakt calls from the eval stack | 0 | <= 0 | pass |

| Metric | Value |
|---|---|
| Suggestion cards fetched | 273 |
| Stub rate (why-lines in the stub family) | 0.209 |
| Exclusion leaks (suggestion surfaces) | 22 |
| Cross-rail repeats (feed page) | 0 |
| Evidence share (anchored + Similar, n=126) | 0.667 |
| Random-pair cosine p90 (evidence bar) | 0.386 |
| Why-lines hedging against their own pick | 29 |
| Why-lines with non-Latin script | 1 |
| Watched or rated titles on Saved / Watch tonight | 116 |
| False copy claims | not measured yet (task 9) |

## Per surface

| Surface | Blocks | HTTP errors | Empty blocks | Cards | Stub why | Hedging | Non-Latin | Leaks | Anchored | Evidence pass | Watched or rated (lookup surfaces) |
|---|---|---|---|---|---|---|---|---|---|---|---|
| feed-hero | 1 | 0 | 0 | 5 | 0 | 1 | 1 | 0 | 0 | 0 | 0 |
| feed-row | 4 | 0 | 0 | 40 | 8 | 1 | 0 | 0 | 30 | 16 | 0 |
| more | 6 | 0 | 0 | 72 | 17 | 5 | 0 | 0 | 36 | 15 | 0 |
| genre-rail | 8 | 0 | 0 | 96 | 17 | 22 | 0 | 22 | 0 | 0 | 0 |
| similar | 5 | 0 | 0 | 60 | 15 | 0 | 0 | 0 | 60 | 53 | 0 |
| digest | 1 | 0 | 1 | 0 | 0 | 0 | 0 | 0 | 0 | 0 | 0 |
| saved | 1 | 0 | 0 | 150 | 0 | 0 | 0 | 0 | 0 | 0 | 116 |
| typeahead | 10 | 0 | 0 | 32 | 0 | 0 | 0 | 0 | 0 | 0 | 15 |

## Leaks (first 20 per surface)

- genre-rail: Night of the Living Dead: Re-Animation (genre:action)
- genre-rail: NCIS: New Orleans (genre:action)
- genre-rail: Munich (genre:thriller)
- genre-rail: World Trade Center (genre:thriller)
- genre-rail: Unfaithful (genre:thriller)
- genre-rail: Shrek the Third (genre:adventure)
- genre-rail: Over the Hedge (genre:adventure)
- genre-rail: Cars 3 (genre:adventure)
- genre-rail: Frozen II (genre:adventure)
- genre-rail: Puss in Boots (genre:adventure)
- genre-rail: Madagascar 3: Europe's Most Wanted (genre:adventure)
- genre-rail: Ice Age: Dawn of the Dinosaurs (genre:adventure)
- genre-rail: Star Wars Resistance (genre:adventure)
- genre-rail: The Fare (genre:science-fiction)
- genre-rail: The Orville (genre:science-fiction)
- genre-rail: Freaks of Nature (genre:science-fiction)
- genre-rail: Shrek the Third (genre:fantasy)
- genre-rail: Frozen II (genre:fantasy)
- genre-rail: Goosebumps 2: Haunted Halloween (genre:fantasy)
- genre-rail: Be Cool, Scooby-Doo! (genre:fantasy)

## Hedging why-lines (first 40)

- tonights-picks · Law & Order: Criminal Intent: You may enjoy the tense Major Case Squad pursuit of killers through criminal psychology, though this procedural series is less aligned with your film-first偏好.
- because-you-loved:The Family Man · Where the Wild Things Are: Like The Family Man, it explores family through an imagined alternate life; your adventure and fantasy tastes suit Max’s voyage, though its pace is slow.
- deep-cuts (page 2) · Spartacus: Spartacus’s brutal arena battles bring the intensity you enjoy, though its ongoing series format and heavy drama are less ideal.
- tonights-picks (page 2) · Spartacus: You may like Spartacus’s arena battles and intense fight-driven story, though this brutal historical drama is a series and lately drama rates lower for you.
- tonights-picks (page 2) · Chicago Fire: You may enjoy Chicago Fire’s high-stakes firefighter and paramedic rescues, though its ongoing drama is a series rather than your usual film pick.
- new-for-you · The Patient: You get political murder intrigue and an amnesiac patient’s mounting suspicion, though its mystery focus may be less to your taste.
- new-for-you · Verity: You may find Lowen’s disturbing discoveries in the Crawfords’ home compelling, but this psychological mystery is a less certain match.
- genre:action · Feral: On a woods hike, six medical students fight a feral infection; you may enjoy the survival stakes, though the creature horror dominates.
- genre:action · When Strangers Appear: You get a tense roadside-diner standoff with murderers, though its thriller and mystery pull outweigh the action.
- genre:action · Night of the Living Dead: Re-Animation: You get a toxic-waste zombie outbreak at a family mortuary, but its horror-comedy angle may offer less of the action you seek.
- genre:action · Detour: Your drama side may connect with a grieving law student’s dilemma, but the stepfather plot sounds more psychological than action-driven.
- genre:thriller · Munich: Munich’s Mossad revenge mission gives you tense, high-stakes pursuit, though its historical drama may feel less brisk.
- genre:thriller · World Trade Center: World Trade Center’s trapped-officers survival story brings gripping urgency, but its heavy historical drama may be less your usual thrill.
- genre:crime · Three Billboards Outside Ebbing, Missouri: You get a tense murder case and Mildred’s bold campaign, though the film leans more into drama than action.
- genre:crime · Animal Kingdom: You may enjoy J Cody’s dangerous entry into Smurf’s criminal clan, though this family-crime series is a less direct fit than a film.
- genre:crime · The Americans: You may like the Cold War cat-and-mouse of KGB agents Elizabeth and Philip, though this series favors sustained drama over brisk action.
- genre:crime · The Dead Girl: You get an intriguing death investigation whose intersecting lives add emotional weight, but its mystery-drama focus may feel slow for you.
- genre:crime · Mystery 101: You get an amateur murder investigation, but professor Amy’s crime-fiction sleuthing points toward mystery over your stronger action tastes.
- genre:science-fiction · Glass: David Dunn tracking a man with 24 personalities gives you supernatural action and sci-fi, though the drama-heavy tone may land less well.
- genre:science-fiction · Anon: A near-future murder hunt and vanished woman suit your crime-sci-fi tastes, though its mystery focus may be less rewarding.
- genre:science-fiction · The Fare: The vanishing passenger gives you a sci-fi mystery hook, but this cab-ride thriller offers less of the action you favor.
- genre:science-fiction · Wynonna Earp: Wynonna’s demon-hunting brings you action and supernatural adventure, but the series format is less suited to your film-first habits.
- genre:science-fiction · Freaks of Nature: You may enjoy its alien-apocalypse team-up, but the comedy-heavy humans-versus-monsters setup is less aligned with your tastes.
- genre:science-fiction · Freakish: You get survival action as students fight mutant creatures, though this series format is a weaker fit than your usual films.
- genre:mystery · The House of the Devil: A strange babysitting job and clients’ terrifying secret deliver a tense mystery, though the horror-leaning, slow-burn setup may test your patience.
- genre:mystery · Mystery 101: Professor Amy races detective Travis to solve a student’s murder, but its cozy crime setup offers less action and intensity than you prefer.
- genre:mystery · A Film with Me in It: You may enjoy the bizarre accidental deaths and murder-suspicion puzzle, though its comedy is less aligned with your tastes.
- genre:mystery · The Dead Girl: The intersecting lives around a young woman’s death create a layered mystery, but its drama-heavy tone may not land as strongly for you.
- genre:mystery · White Collar: Neal Caffrey’s cons and FBI cases bring crime-solving intrigue, though this recurring series is less suited to your film-first habits.

## Jobs that ran during the harness

- not recorded in this recording (it predates job recording; see provenance.md beside it if present)

## Ledger delta

| Provider | Model | Purpose | Calls | Prompt | Completion |
|---|---|---|---|---|---|
| OpenRouter | openai/gpt-6-luna | rerank-feed | 13 | 24791 | 8518 |
| Tmdb | — | — | 87 | 0 | 0 |
| OpenRouter | openai/gpt-6-luna | lens | 17 | 33815 | 10696 |
| OpenRouter | openai/gpt-6-luna | rerank-page | 19 | 29369 | 15620 |
