# Ask Reel golden run — baseline-99c0761-latency

- Engine: v1 · API build 99c0761 · runner ba27944 · snapshot `reel-2026-10-23` · artifact `0536acbf-a642-4a26-a68d-19a0b527c484` (v101)
- 40 scenarios, 60 turns, concurrency 2, 1235 s wall, $0.1313 from the ledger
- Latency gates do NOT apply (build slots not held during the run) — latency below is reported, not gated
- Model key: unrecorded · scenario fingerprint `e84f9e89e829c196`
- Started 2026-10-24T18:51:33.506Z, finished 2026-10-24T19:12:08.737Z

## Gates

| Metric | Value | n | Gate | Status |
|---|---|---|---|---|
| Constraint precision@8 (facet scenarios) | 0.414 | 35 | >= 0.9 | FAIL |
| Honest-empty compliance | 0 | 4 | >= 1 | FAIL |
| Reference accuracy | 0 | 5 | >= 0.95 | FAIL |
| Critique compliance | 0 | 1 | >= 0.9 | FAIL |
| Act classification | — | 0 | >= 0.9 | no data |
| Conflict detection | 0.667 | 3 | >= 0.9 | FAIL |
| Grounding violations | 54 | 60 | <= 0 | FAIL |
| Replies echoing the user's message | 38 | 60 | <= 0 | FAIL |
| Fallback rate | 0.717 | 60 | <= 0.01 | FAIL |
| Standing-exclusion leaks (shown titles outside the eligibility truth) | 0 | 60 | <= 0 | pass |
| Picks against the request's own media filter | 0 | 60 | <= 0 | pass |
| Trakt calls from the eval stack | 0 | 60 | <= 0 | pass |
| Turn latency p50 (ms, concurrency ≤ 2) | 40927 | 60 | <= 8000 | not-gated (build slots not held during the run) |
| Turn latency p95 (ms, concurrency ≤ 2) | 53018 | 60 | <= 15000 | not-gated (build slots not held during the run) |
| First card p50 (ms, concurrency ≤ 2) | 14778 | 60 | <= 4000 | not-gated (build slots not held during the run) |
| First card p95 (ms, concurrency ≤ 2) | 20344 | 60 | <= 7000 | not-gated (build slots not held during the run) |
| Cost per turn (USD) | 0.002 | 60 | <= 0.005 | pass |

## All metrics

| Metric | Value | n |
|---|---|---|
| constraintPrecisionAt8 | 0.414 | 35 |
| negationPrecisionAt8 | 0.875 | 7 |
| honestEmptyCompliance | 0 | 4 |
| referenceAccuracy | 0 | 5 |
| critiqueCompliance | 0 | 1 |
| actClassification | — | 0 |
| conflictDetection | 0.667 | 3 |
| modeAccuracy | 0 | 8 |
| noRepeatCompliance | 1 | 6 |
| groundingViolations | 54 | 60 |
| echoReplies | 38 | 60 |
| fallbackRate | 0.717 | 60 |
| exclusionLeaks | 0 | 60 |
| mediaFilterViolations | 0 | 60 |
| mediaMismatches | 34 | 60 |
| mustNotViolations | 17 | 60 |
| hedgingWhyLines | 243 | 60 |
| nonLatinWhyLines | 1 | 60 |
| missedSupply | 0 | 60 |
| failedTurns | 0 | 60 |
| failedTurnRate | 0 | 60 |
| notRunTurns | 0 | 60 |
| turnLatencyMs | p50 40927 / p95 53018 | 60 |
| serverLatencyMs | p50 40856 / p95 52946 | 60 |
| firstCardMs | p50 14778 / p95 20344 | 60 |
| costPerTurnUsd | 0.002 | 60 |
| traktCalls | 0 | 60 |
| exposuresWritten | 721 | 60 |

## By category

| Category | Turns | Checks failed / run | Fallback turns |
|---|---|---|---|
| ambiguous | 1 | 0 / 0 | 1 |
| chitchat | 5 | 3 / 4 | 4 |
| conflict | 4 | 5 / 7 | 2 |
| critique | 6 | 7 / 8 | 5 |
| empty-supply | 8 | 12 / 13 | 6 |
| facet | 50 | 16 / 24 | 35 |
| item-question | 11 | 12 / 13 | 9 |
| media-switch | 8 | 3 / 4 | 5 |
| mood | 6 | 0 / 0 | 6 |
| mood-vs-exclusion | 3 | 0 / 0 | 2 |
| multilingual | 1 | 0 / 0 | 1 |
| negation | 5 | 0 / 0 | 5 |
| new-topic | 5 | 1 / 3 | 5 |
| ordinal | 18 | 14 / 19 | 16 |
| pagination | 4 | 1 / 3 | 4 |
| reference-title | 1 | 0 / 0 | 1 |
| seen-title | 1 | 0 / 0 | 0 |
| title-lookup | 2 | 3 / 3 | 1 |
| tv | 1 | 0 / 0 | 0 |

## Cost (ledger delta over the run)

| Provider | Model | Purpose | Calls | Prompt tok | Completion tok | USD |
|---|---|---|---|---|---|---|
| Tmdb | — | — | 395 | 0 | 0 | — |
| OpenRouter | openai/gpt-6-luna | search-interpret | 38 | 24298 | 10295 | 0.0076 |
| OpenRouter | openai/text-embedding-3-small | embed | 127 | 2906 | 0 | 0.0001 |
| OpenRouter | openai/gpt-6-luna | search-agent | 60 | 60164 | 22001 | 0.0170 |
| OpenRouter | openai/gpt-6-luna | rerank-ask | 221 | 349450 | 143339 | 0.1066 |

## Per-scenario diffs (turns with a failed check, a leak, a fallback or a grounding violation)

### ninja-critique (facet, empty-supply, critique, ordinal, item-question)
- T1 “ninja movies” — 24 picks, supply 0 eligible of 87
  - failed: honestEmpty, mode
  - off-topic in first 8: Triple Threat; Man of Tai Chi; Blade of the 47 Ronin; Lady Bloodfight; Tornado; Black Lotus; Weekend in Taipei; Gunpowder Milkshake
  - fallback: stub reply
  - reply echoes the user's message
- T2 “none of the above are ninja movies. I want ninja movies” — 10 picks, supply 0 eligible of 87
  - failed: honestEmpty, mode, critique
  - off-topic in first 8: Darc; Jack Hunter and the Star of Heaven; Tracker; Under the Hood; The Brave One; Five Fingers; Kicks; To End All Wars
- T3 “more like the second one you showed me first time round” — 15 picks
  - failed: reference
  - fallback: stub reply
  - reply echoes the user's message
- T4 “wait, is Blade of the 47 Ronin actually a ninja film or a samurai film?” — 7 picks
  - failed: mode
  - fallback: stub reply
  - reply echoes the user's message

### serial-killer-caught (facet, negation)
- T1 “dark thriller serial killer but that gets caught at the end” — 8 picks, precision@8 0.50, supply 92 eligible of 704
  - off-topic in first 8: Code of a Killer; Prey; In the Dark; The Dark
  - fallback: stub reply; 1 stub why-lines
  - grounding: stub why on Code of a Killer
  - reply echoes the user's message
- T2 “more thriller and less horror. I don't want ghosts I want serial killers” — 7 picks, precision@8 0.71, supply 92 eligible of 704
  - ruled-out titles shown: Prodigal Son
  - off-topic in first 8: When Strangers Appear
  - fallback: stub reply
  - reply echoes the user's message

### negation-only-horror (negation)
- T1 “I don't want horror, gore, zombies or vampires” — 6 picks, precision@8 1
  - fallback: stub reply
  - reply echoes the user's message

### eighties-scifi (conflict, empty-supply, facet)
- T1 “80s sci-fi movies” — 16 picks, supply 0 eligible of 87
  - failed: honestEmpty, mode
  - off-topic in first 8: 2081; Xeno; Slash/Back; Code 8 Part II; Monsters; Cosmic Chaos; Doctor Who: The Next Doctor; Proximity

### psych-thriller-tell-more (facet, item-question, ordinal)
- T1 “a psychological thriller movie” — 6 picks, precision@8 0.67, supply 833 eligible of 7550
  - off-topic in first 8: Suspiria; Hitchcock
  - fallback: stub reply
  - reply echoes the user's message
- T2 “tell me more about the first one” — 8 picks
  - failed: mode, reference
  - fallback: stub reply
  - reply echoes the user's message

### scooby-doo (title-lookup, conflict, empty-supply)
- T1 “Scooby doo” — 14 picks, supply 0 eligible of 73
  - failed: honestEmpty, mode, conflict
  - off-topic in first 8: Bixler High Private Eye; Treasure Buddies; Are You Afraid of the Dark?; Mostly Ghostly 3: One Night in Doom House; Medium; Scorpion; Doom Patrol; The Banana Splits Movie
  - fallback: stub reply
  - reply echoes the user's message

### fantasy-then-mind-benders (facet, new-topic)
- T1 “fantasy or mystery movies” — 11 picks
  - fallback: stub reply
  - reply echoes the user's message
- T2 “Ok let's do mind benders instead. dark ones” — 11 picks, precision@8 0.75
  - wrong media: Mr. Mercedes; Tell Me a Story; Chambers; Persons Unknown; Creeped Out; Dig
  - ruled-out titles shown: A Dark Song; The Dark; Creeped Out
  - fallback: stub reply; 1 stub why-lines
  - grounding: stub why on Persons Unknown
  - reply echoes the user's message

### cerebral-heist-ordinals (facet, ordinal, pagination)
- T1 “a cerebral heist movie with a twist ending” — 24 picks, precision@8 0.50, supply 40 eligible of 543
  - off-topic in first 8: Sharper; No Sudden Move; Thick as Thieves; All the Devils Are Here
  - fallback: stub reply
  - reply echoes the user's message
- T2 “more like the second one” — 11 picks
  - failed: reference
  - fallback: stub reply; 3 stub why-lines
  - grounding: stub why on Dolphin Tale 2; stub why on Donovan's Echo; stub why on Life-Size 2
  - reply echoes the user's message
- T3 “actually go back to cerebral heists with a twist” — 7 picks, precision@8 0.14, supply 40 eligible of 543
  - wrong media: The Capture; Collateral; Ice; Trackers; Somewhere Between; Prey; Motive
  - off-topic in first 8: The Capture; Collateral; Trackers; Somewhere Between; Prey; Motive
  - fallback: stub reply
  - reply echoes the user's message
- T4 “more of those please” — 16 picks, precision@8 0, supply 40 eligible of 543
  - wrong media: Tell Me You Love Me; The Client List; Good Girls; 666 Park Avenue; For the People
  - off-topic in first 8: Chosen; Dirty Pretty Things; Love Finds You in Charm; Tell Me You Love Me; The Fits; The Client List; The Ones Below; Love Finds You In Sugarcreek
  - fallback: stub reply; 5 stub why-lines
  - grounding: stub why on Love Finds You in Charm; stub why on Tell Me You Love Me; stub why on The Fits; stub why on The Client List; stub why on Love Finds You In Sugarcreek
  - reply echoes the user's message

### space-isolation-chain (facet, media-switch, chitchat, item-question, ordinal)
- T1 “space movies about isolation and silence” — 7 picks, precision@8 0.29, supply 12 eligible of 213
  - wrong media: Origin; Dark Matter; Extant; The Twilight Zone
  - off-topic in first 8: Alien: Containment; Origin; Rakka; The Survivalist; The Twilight Zone
  - fallback: stub reply; 1 stub why-lines
  - grounding: stub why on Alien: Containment
  - reply echoes the user's message
- T2 “not that, older” — 20 picks, precision@8 0, supply 39 eligible of 302
  - off-topic in first 8: We Still Kill the Old Way; Chosen; Edie; 12 and Holding; Anne of Green Gables; Unforgotten; Too Late; Lucky
  - fallback: stub reply; 6 stub why-lines
  - grounding: stub why on Edie; stub why on 12 and Holding; stub why on Anne of Green Gables; stub why on Unforgotten; stub why on Too Late; stub why on Lucky
  - reply echoes the user's message
- T3 “same but as a series” — 7 picks, precision@8 0, supply 27 eligible of 89
  - off-topic in first 8: American Crime Story; The Missing; Channel Zero; Tales of the City; The Romanoffs; Tyler Perry's If Loving You Is Wrong; Tell Me You Love Me
  - fallback: stub reply
  - reply echoes the user's message
- T4 “what did I ask you before?” — 12 picks
  - failed: mode
  - fallback: 6 stub why-lines
  - grounding: stub why on Donovan's Echo; stub why on An Adventure in Space and Time; stub why on Session 9; stub why on War of the Worlds; stub why on Room 8; stub why on The Vast of Night
- T5 “why did you pick the first one?” — 6 picks
  - failed: mode, reference

### afrikaans-light-friday (multilingual, mood, mood-vs-exclusion)
- T1 “Ek soek iets snaaks en liggies vir 'n Vrydagaand, niks hartseer nie” — 15 picks, precision@8 1
  - fallback: stub reply; 3 stub why-lines
  - grounding: stub why on A Patch of Fog; stub why on Go!; stub why on Parked
  - reply echoes the user's message

### long-winter-evening (mood)
- T1 “I want a film that feels like a long winter evening: slow, deliberate, with a camera that lingers, where the dialogue is sparse and the silences say more than the words” — 14 picks
  - fallback: stub reply
  - reply echoes the user's message

### gritty-crime-chain (facet, ordinal, new-topic)
- T1 “gritty crime stories set in a big city” — 11 picks, precision@8 0.88, supply 930 eligible of 5472
  - off-topic in first 8: Mob City
  - fallback: stub reply; 5 stub why-lines
  - grounding: stub why on Mob City; stub why on City on a Hill; stub why on Bomb City; stub why on State Property; stub why on APB
  - reply echoes the user's message
- T2 “more like the second one” — 11 picks
  - failed: reference
  - fallback: stub reply; 3 stub why-lines
  - grounding: stub why on Dolphin Tale 2; stub why on Donovan's Echo; stub why on Life-Size 2
  - reply echoes the user's message
- T3 “and something older” — 17 picks
  - fallback: stub reply; 4 stub why-lines
  - grounding: stub why on Edie; stub why on Adoration; stub why on The Living and the Dead; stub why on Chosen
  - reply echoes the user's message
  - non-Latin script in English copy: The Old Oak: Ken Loach’s 2023 pub drama centers on community tensions around Syrian refugees, but its contemporary social focus is a poor match for your action-adventure sl倾

### quiet-character-dramas (facet, mood)
- T1 “quiet character dramas” — 8 picks, precision@8 1, supply 2822 eligible of 17985
  - fallback: stub reply
  - reply echoes the user's message

### heist-2010s-then-comedy (facet, conflict)
- T2 “actually something funny instead, a comedy heist” — 9 picks, precision@8 1
  - fallback: stub reply
  - reply echoes the user's message

### ugly (ambiguous)
- T1 “ugly” — 11 picks
  - fallback: stub reply
  - reply echoes the user's message

### super-mind-bender (facet, mood)
- T1 “not sure what I want but sort of dark thriller. a super-mind-bender” — 7 picks, precision@8 0.43, supply 833 eligible of 7566
  - off-topic in first 8: Rellik; A Dark Song; The Dark; Creeped Out
  - fallback: stub reply; 2 stub why-lines
  - grounding: stub why on Rellik; stub why on Down
  - reply echoes the user's message

### scifi-mystery-tv-older (facet, media-switch)
- T3 “older stuff is fine” — 15 picks
  - fallback: 6 stub why-lines
  - grounding: stub why on 5ive Days to Midnight; stub why on FlashForward; stub why on Travelers; stub why on Life on Mars; stub why on The Terror; stub why on Continuum

### john-wick-funnier (mood-vs-exclusion, reference-title)
- T1 “like John Wick but funnier” — 6 picks, precision@8 1
  - fallback: stub reply; 2 stub why-lines
  - grounding: stub why on The American; stub why on The Player
  - reply echoes the user's message

### rainy-sunday-no-superheroes (mood, negation)
- T1 “a feel-good movie for a rainy Sunday” — 14 picks
  - fallback: 1 stub why-lines
  - grounding: stub why on Swallows and Amazons
- T2 “no superheroes please” — 16 picks, precision@8 0.38
  - wrong media: Sanctuary; NOS4A2; Heroes; Marvel's Runaways; Doom Patrol; Heroes Reborn; The Gifted; Watchmen; Titans; Powers; Supergirl; Batwoman
  - ruled-out titles shown: Special; Heroes; Death of a Superhero; Marvel's Runaways; Doom Patrol; Heroes Reborn; The Gifted; Watchmen; Titans; Powers; Marvel One-Shot: Item 47; Supergirl; Batwoman
  - fallback: stub reply
  - reply echoes the user's message

### heist-more-heart (facet, critique)
- T1 “heist movies” — 24 picks, precision@8 0.63, supply 40 eligible of 543
  - off-topic in first 8: Thick as Thieves; Rob the Mob; The Crew
  - fallback: stub reply
  - reply echoes the user's message
- T2 “not that, something with more heart” — 14 picks, precision@8 0, supply 57 eligible of 582
  - off-topic in first 8: Short Term 12; Stronger; Heavyweight; A Question of Faith; 12 and Holding; The Air I Breathe; Fathers and Daughters; Lilting
  - fallback: stub reply; 1 stub why-lines
  - grounding: stub why on 12 and Holding
  - reply echoes the user's message

### samurai-films (facet, empty-supply)
- T1 “samurai films” — 8 picks, precision@8 0, supply 1 eligible of 76
  - off-topic in first 8: Kumiko, the Treasure Hunter; Seraphim Falls; Rescue Dawn; Tracker; Jack Hunter and the Star of Heaven; To End All Wars; The Librarian: Quest for the Spear; Shanghai
  - fallback: 1 stub why-lines
  - grounding: stub why on Kumiko, the Treasure Hunter

### time-loop (facet)
- T1 “time loop movies” — 7 picks, precision@8 0.43, supply 5 eligible of 67
  - off-topic in first 8: Doctor Who: Time Crash; Doctor Who: Twice Upon a Time; Parallels; From Time to Time
  - fallback: stub reply
  - reply echoes the user's message

### survival-at-sea (facet)
- T1 “survival at sea” — 11 picks, precision@8 0.25, supply 3 eligible of 72
  - off-topic in first 8: Against the Sun; Black Sails; Treasure Island; The Last Ship; Sanctum; Sea Patrol
  - fallback: stub reply
  - reply echoes the user's message

### ninja-tv (facet, empty-supply)
- T1 “ninja shows” — 12 picks, precision@8 0.13, supply 1 eligible of 23
  - off-topic in first 8: Warrior; Nikita; Painkiller Jane; Kamen Rider: Dragon Knight; Breakout Kings; Knightfall; Blade: The Series
  - fallback: stub reply; 1 stub why-lines
  - grounding: stub why on Star Trek: Short Treks
  - reply echoes the user's message

### facet-zombie (facet)
- T1 “zombie movies” — 12 picks, precision@8 0.75, supply 16 eligible of 305
  - off-topic in first 8: The Crazies; Carriers
  - fallback: stub reply
  - reply echoes the user's message

### facet-cyberpunk (facet)
- T1 “cyberpunk films” — 7 picks, precision@8 0.29, supply 3 eligible of 106
  - off-topic in first 8: Cypher; The Final Cut; Code 46; 2149: The Aftermath; Cyberbully
  - fallback: 1 stub why-lines
  - grounding: stub why on Repo Men

### facet-ai-robot (facet)
- T1 “films about artificial intelligence” — 6 picks, precision@8 0.17, supply 10 eligible of 372
  - off-topic in first 8: Alien: Specimen; Rakka; Repo Men; Light of My Life; Parallels
  - fallback: stub reply
  - reply echoes the user's message

### facet-mafia (facet)
- T1 “mafia movies” — 13 picks, precision@8 0.75, supply 37 eligible of 591
  - off-topic in first 8: State Property; Illegal Tender
  - fallback: stub reply; 1 stub why-lines
  - grounding: stub why on The Kitchen
  - reply echoes the user's message

### facet-con-artist (facet)
- T1 “con artist movies” — 9 picks, precision@8 0.13, supply 3 eligible of 81
  - off-topic in first 8: Counterfeiters; Flawless; Rob the Mob; The Good Thief; Thick as Thieves; Finding Steve McQueen; Red-Eyed
  - fallback: stub reply
  - reply echoes the user's message

