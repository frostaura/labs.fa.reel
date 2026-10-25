# Ask Reel golden run — baseline-99c0761-open

- Engine: v1 · API build 99c0761 · runner 8251f15 · snapshot `reel-2026-10-23` · artifact `0536acbf-a642-4a26-a68d-19a0b527c484` (v101)
- 120 scenarios, 167 turns, concurrency 4, 1632 s wall, $0.3383 from the ledger
- Latency gates do NOT apply (concurrency > 2) — latency below is reported, not gated
- Model key: unrecorded · scenario fingerprint `cab2a605e71bc860`
- Started 2026-10-24T18:08:54.347Z, finished 2026-10-24T18:36:06.119Z

## Gates

| Metric | Value | n | Gate | Status |
|---|---|---|---|---|
| Constraint precision@8 (facet scenarios) | 0.467 | 103 | >= 0.9 | FAIL |
| Honest-empty compliance | 0 | 17 | >= 1 | FAIL |
| Reference accuracy | 0 | 15 | >= 0.95 | FAIL |
| Critique compliance | 0.200 | 5 | >= 0.9 | FAIL |
| Act classification | — | 0 | >= 0.9 | no data |
| Conflict detection | 0.643 | 14 | >= 0.9 | FAIL |
| Grounding violations | 118 | 167 | <= 0 | FAIL |
| Replies echoing the user's message | 85 | 167 | <= 0 | FAIL |
| Fallback rate | 0.623 | 167 | <= 0.01 | FAIL |
| Standing-exclusion leaks (shown titles outside the eligibility truth) | 66 | 167 | <= 0 | FAIL |
| Picks against the request's own media filter | 0 | 167 | <= 0 | pass |
| Trakt calls from the eval stack | 0 | 167 | <= 0 | pass |
| Turn latency p50 (ms, concurrency ≤ 2) | 39620 | 167 | <= 8000 | not-gated (concurrency > 2) |
| Turn latency p95 (ms, concurrency ≤ 2) | 51045 | 167 | <= 15000 | not-gated (concurrency > 2) |
| First card p50 (ms, concurrency ≤ 2) | 13577 | 167 | <= 4000 | not-gated (concurrency > 2) |
| First card p95 (ms, concurrency ≤ 2) | 19969 | 167 | <= 7000 | not-gated (concurrency > 2) |
| Cost per turn (USD) | 0.002 | 167 | <= 0.005 | pass |

## All metrics

| Metric | Value | n |
|---|---|---|
| constraintPrecisionAt8 | 0.467 | 103 |
| negationPrecisionAt8 | 0.942 | 15 |
| honestEmptyCompliance | 0 | 17 |
| referenceAccuracy | 0 | 15 |
| critiqueCompliance | 0.200 | 5 |
| actClassification | — | 0 |
| conflictDetection | 0.643 | 14 |
| modeAccuracy | 0 | 30 |
| noRepeatCompliance | 1 | 15 |
| groundingViolations | 118 | 167 |
| echoReplies | 85 | 167 |
| fallbackRate | 0.623 | 167 |
| exclusionLeaks | 66 | 167 |
| mediaFilterViolations | 0 | 167 |
| mediaMismatches | 94 | 167 |
| mustNotViolations | 49 | 167 |
| hedgingWhyLines | 670 | 167 |
| nonLatinWhyLines | 2 | 167 |
| missedSupply | 0 | 167 |
| failedTurns | 0 | 167 |
| failedTurnRate | 0 | 167 |
| notRunTurns | 0 | 167 |
| turnLatencyMs | p50 39620 / p95 51045 | 167 |
| serverLatencyMs | p50 39513 / p95 50938 | 167 |
| firstCardMs | p50 13577 / p95 19969 | 167 |
| costPerTurnUsd | 0.002 | 167 |
| traktCalls | 0 | 167 |
| exposuresWritten | 2004 | 167 |

## By category

| Category | Turns | Checks failed / run | Fallback turns |
|---|---|---|---|
| ambiguous | 1 | 0 / 0 | 0 |
| chitchat | 11 | 6 / 8 | 9 |
| conflict | 15 | 29 / 38 | 12 |
| critique | 16 | 10 / 13 | 8 |
| empty-supply | 12 | 14 / 15 | 8 |
| facet | 114 | 27 / 45 | 68 |
| item-question | 22 | 23 / 24 | 17 |
| media-switch | 13 | 3 / 5 | 9 |
| mood | 10 | 0 / 0 | 7 |
| mood-vs-exclusion | 7 | 0 / 0 | 5 |
| multilingual | 1 | 0 / 0 | 1 |
| negation | 15 | 0 / 0 | 6 |
| new-topic | 5 | 1 / 3 | 5 |
| ordinal | 39 | 29 / 39 | 32 |
| pagination | 9 | 2 / 6 | 8 |
| people | 7 | 5 / 6 | 3 |
| reference-title | 2 | 0 / 0 | 2 |
| refine | 7 | 0 / 0 | 5 |
| seen-title | 1 | 0 / 0 | 0 |
| title-lookup | 5 | 4 / 4 | 4 |
| tv | 23 | 8 / 9 | 11 |
| tv-exclusion | 7 | 5 / 6 | 4 |

## Cost (ledger delta over the run)

| Provider | Model | Purpose | Calls | Prompt tok | Completion tok | USD |
|---|---|---|---|---|---|---|
| OpenRouter | openai/gpt-6-luna | search-agent | 166 | 163198 | 58374 | 0.0455 |
| OpenRouter | openai/text-embedding-3-small | embed | 353 | 8720 | 0 | 0.0002 |
| OpenRouter | openai/gpt-6-luna | rerank-ask | 591 | 913561 | 369096 | 0.2759 |
| Tmdb | — | — | 1022 | 0 | 0 | — |
| OpenRouter | openai/gpt-6-luna | search-interpret | 84 | 53545 | 22687 | 0.0167 |

## Per-scenario diffs (turns with a failed check, a leak, a fallback or a grounding violation)

### ninja-critique (facet, empty-supply, critique, ordinal, item-question)
- T1 “ninja movies” — 24 picks, supply 0 eligible of 87
  - failed: honestEmpty, mode
  - off-topic in first 8: Triple Threat; Man of Tai Chi; Blade of the 47 Ronin; One Night in Bangkok; Lady Bloodfight; Gunpowder Milkshake; Tornado; Waist Deep
  - fallback: stub reply
  - reply echoes the user's message
- T2 “none of the above are ninja movies. I want ninja movies” — 8 picks, supply 0 eligible of 87
  - failed: honestEmpty, mode, critique
  - off-topic in first 8: Darc; Jack Hunter and the Star of Heaven; Kicks; Five Fingers; Chosen; The Last Castle; Kumiko, the Treasure Hunter; The Last Movie Star
- T3 “more like the second one you showed me first time round” — 15 picks
  - failed: reference
  - fallback: stub reply
  - reply echoes the user's message
- T4 “wait, is Blade of the 47 Ronin actually a ninja film or a samurai film?” — 8 picks
  - failed: mode
  - fallback: stub reply; 3 stub why-lines
  - grounding: stub why on In Full Bloom; stub why on Ticker; stub why on Rakka
  - reply echoes the user's message

### serial-killer-caught (facet, negation)
- T2 “more thriller and less horror. I don't want ghosts I want serial killers” — 12 picks, precision@8 0.38, supply 93 eligible of 705
  - ruled-out titles shown: The Ghoul
  - off-topic in first 8: Sharper; Girl; Shirley; The Ghoul; Half Light

### negation-only-horror (negation)
- T1 “I don't want horror, gore, zombies or vampires” — 6 picks, precision@8 1
  - fallback: stub reply
  - reply echoes the user's message

### eighties-scifi (conflict, empty-supply, facet)
- T1 “80s sci-fi movies” — 10 picks, supply 0 eligible of 87
  - failed: honestEmpty, mode
  - off-topic in first 8: Code 8 Part II; Proximity; Slash/Back; 2073; Monster Hunters; Xeno; Cosmic Chaos; Resident Evil

### tense-heist (facet)
- T1 “tense heist thrillers” — 24 picks, precision@8 0.63, supply 58 eligible of 583
  - off-topic in first 8: All the Devils Are Here; Sharper; Rev
  - fallback: stub reply
  - reply echoes the user's message

### psych-thriller-tell-more (facet, item-question, ordinal)
- T2 “tell me more about the first one” — 8 picks
  - failed: mode, reference
  - fallback: stub reply; 6 stub why-lines
  - grounding: stub why on Tell Me You Love Me; stub why on Z: The Beginning of Everything; stub why on The Thirteenth Tale; stub why on Tell Me a Story; stub why on Chosen; stub why on Kiss Me First
  - reply echoes the user's message

### scooby-doo (title-lookup, conflict, empty-supply)
- T1 “Scooby doo” — 14 picks, supply 0 eligible of 73
  - failed: honestEmpty, mode, conflict
  - off-topic in first 8: Bixler High Private Eye; Are You Afraid of the Dark?; Treasure Buddies; Medium; Mostly Ghostly 3: One Night in Doom House; The Banana Splits Movie; Benji; Scorpion
  - fallback: stub reply
  - reply echoes the user's message

### fantasy-then-mind-benders (facet, new-topic)
- T1 “fantasy or mystery movies” — 11 picks
  - fallback: stub reply
  - reply echoes the user's message
- T2 “Ok let's do mind benders instead. dark ones” — 8 picks, precision@8 0.75
  - ruled-out titles shown: A Dark Song; The Dark
  - fallback: 1 stub why-lines
  - grounding: stub why on A Dark Song

### cerebral-heist-ordinals (facet, ordinal, pagination)
- T1 “a cerebral heist movie with a twist ending” — 24 picks, precision@8 0.63, supply 41 eligible of 544
  - off-topic in first 8: No Sudden Move; Sharper; All the Devils Are Here
  - fallback: stub reply
  - reply echoes the user's message
- T2 “more like the second one” — 11 picks
  - failed: reference
  - fallback: stub reply; 2 stub why-lines
  - grounding: stub why on Switched for Christmas; stub why on A Second Chance: Rivals!
  - reply echoes the user's message
- T3 “actually go back to cerebral heists with a twist” — 9 picks, precision@8 0.25, supply 41 eligible of 544
  - wrong media: Trackers; Thief; Prey; The Capture; Somewhere Between; Ice; Collateral; Luther
  - off-topic in first 8: Thick as Thieves; Trackers; Prey; The Capture; Somewhere Between; Collateral
  - fallback: stub reply
  - reply echoes the user's message
- T4 “more of those please” — 16 picks, precision@8 0, supply 41 eligible of 544
  - wrong media: Tell Me You Love Me; The Client List; Good Girls; 666 Park Avenue; For the People
  - off-topic in first 8: Chosen; Dirty Pretty Things; Love Finds You in Charm; Tell Me You Love Me; The Fits; The Client List; The Ones Below; Love Finds You In Sugarcreek
  - fallback: stub reply; 5 stub why-lines
  - grounding: stub why on Love Finds You in Charm; stub why on Tell Me You Love Me; stub why on The Fits; stub why on The Client List; stub why on Love Finds You In Sugarcreek
  - reply echoes the user's message

### space-isolation-chain (facet, media-switch, chitchat, item-question, ordinal)
- T1 “space movies about isolation and silence” — 7 picks, precision@8 0.29, supply 12 eligible of 213
  - wrong media: Origin; Dark Matter; Extant; The Twilight Zone
  - off-topic in first 8: Alien: Containment; Origin; Rakka; The Survivalist; The Twilight Zone
  - fallback: stub reply
  - reply echoes the user's message
- T2 “not that, older” — 20 picks, precision@8 0, supply 39 eligible of 302
  - off-topic in first 8: Chosen; Edie; Never Grow Old; We Still Kill the Old Way; Too Late; Anne of Green Gables; Lucky; The Ballad of Lefty Brown
  - fallback: stub reply
  - reply echoes the user's message
- T3 “same but as a series” — 7 picks, precision@8 0, supply 27 eligible of 89
  - off-topic in first 8: American Crime Story; The Missing; Channel Zero; Tales of the City; Tyler Perry's If Loving You Is Wrong; The Romanoffs; Tell Me You Love Me
  - fallback: stub reply
  - reply echoes the user's message
- T4 “what did I ask you before?” — 11 picks
  - failed: mode
  - fallback: 3 stub why-lines
  - grounding: stub why on Donovan's Echo; stub why on War of the Worlds; stub why on Room 8
- T5 “why did you pick the first one?” — 8 picks
  - failed: mode, reference

### afrikaans-light-friday (multilingual, mood, mood-vs-exclusion)
- T1 “Ek soek iets snaaks en liggies vir 'n Vrydagaand, niks hartseer nie” — 14 picks, precision@8 1
  - fallback: stub reply
  - reply echoes the user's message

### long-winter-evening (mood)
- T1 “I want a film that feels like a long winter evening: slow, deliberate, with a camera that lingers, where the dialogue is sparse and the silences say more than the words” — 13 picks
  - non-Latin script in English copy: The Quiet: Dot’s deafness makes silence central to this brooding family thriller, though its abuse and bullying themes may make the mood harsher than your winter-evening想.

### gritty-crime-chain (facet, ordinal, new-topic)
- T1 “gritty crime stories set in a big city” — 11 picks, precision@8 0.88, supply 935 eligible of 5493
  - off-topic in first 8: Mob City
  - fallback: stub reply
  - reply echoes the user's message
- T2 “more like the second one” — 11 picks
  - failed: reference
  - fallback: stub reply; 2 stub why-lines
  - grounding: stub why on Switched for Christmas; stub why on A Second Chance: Rivals!
  - reply echoes the user's message
- T3 “and something older” — 17 picks
  - fallback: stub reply
  - reply echoes the user's message

### quiet-character-dramas (facet, mood)
- T1 “quiet character dramas” — 6 picks, precision@8 1, supply 2839 eligible of 18081
  - fallback: stub reply
  - reply echoes the user's message

### genuinely-funny (mood-vs-exclusion)
- T1 “something genuinely funny” — 15 picks, precision@8 1
  - fallback: stub reply
  - reply echoes the user's message

### heist-2010s-then-comedy (facet, conflict)
- T1 “a tense heist thriller from the 2010s, nothing too long” — 10 picks, precision@8 0.25, supply 10 eligible of 175
  - wrong media: Inside Men; Trackers; Ice; Prey; Collateral; The Bletchley Circle
  - off-topic in first 8: Premium Rush; Free Fire; Traders; Trackers; Prey; Collateral
  - fallback: stub reply
  - reply echoes the user's message
- T2 “actually something funny instead, a comedy heist” — 8 picks, precision@8 1
  - fallback: stub reply
  - reply echoes the user's message

### super-mind-bender (facet, mood)
- T1 “not sure what I want but sort of dark thriller. a super-mind-bender” — 8 picks, precision@8 0.50, supply 841 eligible of 7587
  - off-topic in first 8: The Terror; The Dark; A Dark Song; Creeped Out
  - fallback: stub reply
  - reply echoes the user's message

### scifi-mystery-tv-older (facet, media-switch)
- T1 “a slow-burn sci-fi mystery” — 24 picks, precision@8 1, supply 576 eligible of 4805
  - fallback: stub reply
  - reply echoes the user's message
- T2 “actually TV” — 16 picks, precision@8 0.13, supply 391 eligible of 1801
  - off-topic in first 8: Top Boy; Top Gear; Good Girls; The Newsroom; Good Girls Revolt; Good Trouble; Queer as Folk
  - fallback: stub reply
  - reply echoes the user's message

### john-wick-funnier (mood-vs-exclusion, reference-title)
- T1 “like John Wick but funnier” — 7 picks, precision@8 1
  - fallback: stub reply
  - reply echoes the user's message

### rainy-sunday-no-superheroes (mood, negation)
- T1 “a feel-good movie for a rainy Sunday” — 14 picks
  - fallback: stub reply
  - reply echoes the user's message
- T2 “no superheroes please” — 16 picks, precision@8 0.38
  - wrong media: Sanctuary; NOS4A2; Heroes; Marvel's Runaways; Heroes Reborn; Watchmen; Powers; Titans; Supergirl; Doom Patrol; The Gifted; Batwoman
  - ruled-out titles shown: Death of a Superhero; Heroes; Marvel's Runaways; Special; Heroes Reborn; Watchmen; Powers; Titans; Supergirl; Doom Patrol; The Gifted; Marvel One-Shot: Item 47; Batwoman
  - fallback: stub reply
  - reply echoes the user's message

### heist-more-heart (facet, critique)
- T2 “not that, something with more heart” — 11 picks, precision@8 0, supply 58 eligible of 583
  - off-topic in first 8: Short Term 12; A Question of Faith; Heavyweight; Lilting; Test; The Air I Breathe; Nurses; Somewhere
  - fallback: stub reply
  - reply echoes the user's message

### time-loop (facet)
- T1 “time loop movies” — 6 picks, precision@8 0.50, supply 6 eligible of 68
  - off-topic in first 8: From Time to Time; Doctor Who: Twice Upon a Time; Parallels
  - fallback: stub reply
  - reply echoes the user's message

### ninja-tv (facet, empty-supply)
- T1 “ninja shows” — 12 picks, precision@8 0.13, supply 1 eligible of 23
  - off-topic in first 8: Warrior; Nikita; Blood & Treasure; Knightfall; Breakout Kings; Mutant X; Painkiller Jane
  - fallback: stub reply
  - reply echoes the user's message

### facet-cyberpunk (facet)
- T1 “cyberpunk films” — 16 picks, precision@8 0.25, supply 3 eligible of 106
  - off-topic in first 8: Cypher; Code 8 Part II; Robot Apocalypse; 2073; The Final Cut; Cosmic Chaos
  - fallback: stub reply
  - reply echoes the user's message

### facet-ai-robot (facet)
- T1 “films about artificial intelligence” — 6 picks, precision@8 0.17, supply 10 eligible of 372
  - off-topic in first 8: Repo Men; Rakka; Parallels; Alien: Specimen; Light of My Life
  - fallback: stub reply
  - reply echoes the user's message

### facet-noir (facet)
- T1 “neo-noir films” — 19 picks, precision@8 0.25, supply 21 eligible of 341
  - off-topic in first 8: Emily the Criminal; Sharper; Inside Man; Absolute Anger; Small Town Crime; Finestkind
  - fallback: stub reply
  - reply echoes the user's message

### facet-submarine (facet)
- T1 “submarine movies” — 11 picks, precision@8 0.25, supply 3 eligible of 60
  - off-topic in first 8: Sanctum; Seal Team Eight: Behind Enemy Lines; Master and Commander: The Far Side of the World; Against the Sun; All Is Lost; Swallows and Amazons
  - fallback: stub reply; 3 stub why-lines
  - grounding: stub why on On the Beach; stub why on Seal Team Eight: Behind Enemy Lines; stub why on Master and Commander: The Far Side of the World
  - reply echoes the user's message

### facet-witch (facet)
- T1 “witch horror” — 7 picks, precision@8 0.57, supply 53 eligible of 357
  - off-topic in first 8: A Dark Song; The Haunting Hour: Don't Think About It; Terrifier
  - fallback: 1 stub why-lines
  - grounding: stub why on Dreams in the Witch House

### facet-werewolf-tv (facet, tv)
- T1 “werewolf series” — 12 picks, precision@8 0.88, supply 15 eligible of 20
  - off-topic in first 8: Van Helsing
  - fallback: 1 stub why-lines
  - grounding: stub why on Van Helsing

### facet-cult (facet)
- T1 “movies about cults” — 24 picks, precision@8 0.63, supply 21 eligible of 178
  - off-topic in first 8: V/H/S/94; Older Gods; Godless: The Eastfield Exorcism
  - fallback: stub reply
  - reply echoes the user's message

### facet-pandemic-tv (facet, tv)
- T1 “pandemic shows” — 11 picks, precision@8 0.63, supply 13 eligible of 27
  - off-topic in first 8: The Hot Zone; Z Nation; Channel Zero
  - fallback: stub reply; 1 stub why-lines
  - grounding: stub why on The Hot Zone
  - reply echoes the user's message

### facet-found-footage (facet)
- T1 “found footage horror” — 6 picks, precision@8 0.33, supply 42 eligible of 324
  - off-topic in first 8: Creep; Feast; Terrifier; Polaroid
  - fallback: 1 stub why-lines
  - grounding: stub why on Unfriended: Dark Web

### facet-alien (facet)
- T1 “alien invasion movies” — 6 picks, precision@8 1, supply 38 eligible of 514
  - fallback: stub reply; 2 stub why-lines
  - grounding: stub why on Alien: Specimen; stub why on Alien: Containment
  - reply echoes the user's message

### facet-serial-killer-tv (facet, tv)
- T1 “serial killer series” — 13 picks, precision@8 0.63, supply 39 eligible of 106
  - off-topic in first 8: Code of a Killer; Whitechapel; Killing Eve
  - fallback: 6 stub why-lines
  - grounding: stub why on Rillington Place; stub why on Killing Eve; stub why on Dirty John; stub why on Dexter; stub why on Scream: The TV Series; stub why on The Terror

### facet-genre-scifi-tv (facet, tv)
- T1 “sci-fi series” — 17 picks, precision@8 1, supply 391 eligible of 1801
  - fallback: stub reply
  - reply echoes the user's message

### empty-kaiju (empty-supply, facet)
- T1 “kaiju movies” — 6 picks, precision@8 0, supply 1 eligible of 94
  - off-topic in first 8: Jack Hunter and the Star of Heaven; Rakka; Alien: Containment; Overlord; Alive in Joburg; Monsters
  - fallback: 3 stub why-lines
  - grounding: stub why on Jack Hunter and the Star of Heaven; stub why on Alien: Containment; stub why on Overlord

### empty-dragon (empty-supply, facet)
- T1 “dragon movies” — 11 picks, precision@8 0, supply 3 eligible of 187
  - off-topic in first 8: Basilisk: The Serpent King; Voyage of the Unicorn; Princess; The Kid Who Would Be King; The Fall; MirrorMask; No Such Thing; Within the Shadow of the Tree: Dark Dreams
  - fallback: stub reply
  - reply echoes the user's message

### empty-buddy-cop (empty-supply, facet)
- T1 “buddy cop movies” — 9 picks, supply 0 eligible of 70
  - failed: honestEmpty, mode
  - off-topic in first 8: Miami Vice; Dragged Across Concrete; Black and Blue; 21 Bridges; Harsh Times; Courageous; Brooklyn's Finest; Premium Rush
  - fallback: stub reply
  - reply echoes the user's message

### conflict-romcom (conflict)
- T1 “romantic comedies” — 15 picks, supply 0 eligible of 4882
  - failed: honestEmpty, mode
  - off-topic in first 8: Love Finds You in Charm; Lorelei; The 7.39; Adoration; Famous in Love; Love Is Strange; Tyler Perry's If Loving You Is Wrong; Love Finds You In Sugarcreek
  - fallback: stub reply
  - reply echoes the user's message

### conflict-anime (conflict)
- T1 “anime” — 9 picks, supply 0 eligible of 2
  - failed: honestEmpty, mode
  - off-topic in first 8: Anna; Lady Bloodfight; Man of Tai Chi; Chosen; Tracker; Rakka; Darc; Kicks

### conflict-space-docs (conflict)
- T1 “documentaries about space” — 14 picks, supply 0 eligible of 1972
  - failed: honestEmpty, mode
  - off-topic in first 8: Mission to Mars; Alien: Containment; Proximity; Cosmic Chaos; 2073; Monster Hunters; Last and First Men; Alien: Romulus

### conflict-animated-kids (conflict)
- T1 “animated movies for kids” — 17 picks, supply 0 eligible of 4149
  - failed: honestEmpty, mode
  - off-topic in first 8: Treasure Buddies; Where the Wild Things Are; Alex & Me; Swallows and Amazons; Free Willy: Escape from Pirate's Cove; Benji; The Good Witch's Family; Duma
  - fallback: stub reply
  - reply echoes the user's message

### conflict-korean-thrillers (conflict)
- T1 “korean thrillers” — 7 picks, supply 0 eligible of 1061
  - failed: honestEmpty, mode, conflict
  - off-topic in first 8: TransSiberian; Inhale; Skin Trade; When Strangers Appear; Traders; Spree; Kicks
  - fallback: 1 stub why-lines
  - grounding: stub why on Inhale

### conflict-french-films (conflict)
- T1 “french films” — 12 picks, supply 0 eligible of 2674
  - failed: honestEmpty, mode, conflict
  - off-topic in first 8: Femme Fatale; Swimming Pool; Gypsy; Baptiste; Birdsong; Les Misérables; Hereafter; Cinema 16: European Short Films (U.S. Edition)
  - fallback: stub reply
  - reply echoes the user's message

### conflict-standup (conflict)
- T1 “stand-up comedy specials” — 16 picks, supply 0 eligible of 152
  - failed: honestEmpty, mode
  - off-topic in first 8: Special; Talking Dead; Manifesto; Stumptown; Stander; Standing Up; Channel Zero; Coma
  - fallback: stub reply
  - reply echoes the user's message

### conflict-low-rated (conflict)
- T1 “so-bad-it's-good movies rated under 5” — 14 picks, supply 0 eligible of 2463
  - failed: honestEmpty, mode, conflict
  - off-topic in first 8: The Banana Splits Movie; The Last Movie Star; Feast; Mean Creek; Green Street Hooligans; The Young Cannibals; The Loved Ones; The Business
  - fallback: stub reply; 7 stub why-lines
  - grounding: stub why on The Last Movie Star; stub why on Feast; stub why on Mean Creek; stub why on Green Street Hooligans; stub why on The Loved Ones; stub why on The Business; stub why on Kicks
  - reply echoes the user's message

### conflict-sitcoms-tv (conflict, tv, tv-exclusion)
- T1 “sitcoms” — 17 picks, supply 0 eligible of 2370
  - failed: honestEmpty, mode, conflict
  - off-topic in first 8: Good Girls; Parenthood; Queer as Folk; 8 Out of 10 Cats; Famous in Love; L.A.'s Finest; Southland; Station 19
  - fallback: stub reply; 1 stub why-lines
  - grounding: stub why on Tell Me You Love Me
  - reply echoes the user's message

### conflict-nature-docuseries-tv (conflict, tv, tv-exclusion)
- T1 “nature documentary series” — 17 picks, supply 0 eligible of 394
  - failed: honestEmpty, mode
  - off-topic in first 8: Running Wild with Bear Grylls; You vs. Wild; Bear Grylls: Escape From Hell; Dual Survival; Get Out Alive with Bear Grylls; Naked and Afraid; Primeval: New World; Primeval
  - fallback: 1 stub why-lines
  - grounding: stub why on Zoo

### no-conflict-control (conflict, facet)
- T1 “tense heist movies from the last ten years” — 24 picks, precision@8 0.63, supply 41 eligible of 544
  - off-topic in first 8: All the Devils Are Here; Blowback; Sharper
  - fallback: stub reply
  - reply echoes the user's message

### neg-action-no-superheroes (negation, facet)
- T1 “action movies but nothing with superheroes” — 24 picks, precision@8 1, supply 692 eligible of 7667
  - ruled-out titles shown: Code 8 Part II; Under the Hood; Death of a Superhero; Shadow Master

### neg-horror-no-zombies-vampires (negation, facet)
- T1 “horror but no zombies or vampires” — 9 picks, precision@8 0.38, supply 448 eligible of 4858
  - off-topic in first 8: Channel Zero; Fear Itself; Masters of Horror; Creepshow; The Terror
  - fallback: stub reply
  - reply echoes the user's message

### neg-crime-no-mafia (negation, facet)
- T1 “crime dramas, no mafia stuff” — 8 picks, precision@8 0.63, supply 935 eligible of 5493
  - ruled-out titles shown: Gangster No. 1; Rob the Mob; Mob Town

### neg-war-not-wwii (negation, facet)
- T1 “war movies but not World War II” — 12 picks, precision@8 0, supply 145 eligible of 1002
  - ruled-out titles shown: The Codes of War; Hart's War; To End All Wars; The Eastern Front; Letters from Iwo Jima; Wolf Hound; Beast of War; Ghosts of War
  - off-topic in first 8: Tomorrow, When the War Began; Hounds of War

### neg-space-no-aliens (negation, facet)
- T1 “space movies with no aliens” — 12 picks, precision@8 0.13, supply 39 eligible of 302
  - ruled-out titles shown: Proximity; Alien: Romulus; Alien: Containment; Rakka; Xeno
  - off-topic in first 8: Cosmic Chaos; UFO; Space Mom; 2073; Proximity; The Vast of Night

### neg-followup-no-superheroes (negation, refine)
- T2 “no superheroes please” — 16 picks, precision@8 0.25, supply 399 eligible of 6062
  - wrong media: Sanctuary; NOS4A2; Heroes; Marvel's Runaways; Heroes Reborn; Watchmen; Powers; Titans; Supergirl; Doom Patrol; The Gifted; Batwoman
  - ruled-out titles shown: Death of a Superhero; Heroes; Marvel's Runaways; Special; Heroes Reborn; Watchmen; Powers; Titans; Supergirl; Doom Patrol; The Gifted; Marvel One-Shot: Item 47; Batwoman
  - off-topic in first 8: NOS4A2; Death of a Superhero; Heroes; Special; Heroes Reborn
  - fallback: stub reply
  - reply echoes the user's message
- T3 “and nothing with aliens either” — 7 picks, precision@8 0.29, supply 399 eligible of 6062
  - wrong media: NCIS; Luther; Outcasts; Origin; Star Trek: Short Treks
  - off-topic in first 8: Luther; Outcasts; Origin; UFO; Two Harbors
  - fallback: stub reply
  - reply echoes the user's message

### ord-third-one (ordinal, facet)
- T1 “spy thrillers” — 8 picks, precision@8 0.38, supply 63 eligible of 434
  - off-topic in first 8: Spooks: The Greater Good; Salting the Battlefield; Echelon Conspiracy; Our Kind of Traitor; Turks & Caicos
  - fallback: 1 stub why-lines
  - grounding: stub why on Tinker Tailor Soldier Spy
- T2 “more like the third one” — 13 picks
  - failed: reference
  - fallback: stub reply; 9 stub why-lines
  - grounding: stub why on Certain Women; stub why on Law & Order: Criminal Intent; stub why on Doctor Who: Twice Upon a Time; stub why on Broken; stub why on The Jester: Chapter 3; stub why on Falling Water; stub why on Down; stub why on The Three Investigators in The Secret of Skeleton Island; stub why on Somewhere Between
  - reply echoes the user's message

### ord-first-one (ordinal, facet)
- T2 “something like the first one but darker” — 12 picks
  - failed: reference
  - fallback: stub reply; 10 stub why-lines
  - grounding: stub why on In the Dark; stub why on Super Dark Times; stub why on Within the Shadow of the Tree: Dark Dreams; stub why on A Dark Matter; stub why on Dark Light; stub why on A Dark Song; stub why on The Dark; stub why on Unfriended: Dark Web; stub why on Black Summer; stub why on Down
  - reply echoes the user's message

### ord-last-tv (ordinal, facet)
- T2 “like the second one but shorter” — 16 picks
  - failed: reference
  - fallback: 5 stub why-lines
  - grounding: stub why on The Corner; stub why on White Collar; stub why on Shades of Blue; stub why on Dexter; stub why on New Blood

### ord-across-turns (ordinal, facet)
- T1 “hitman movies” — 7 picks, precision@8 0.29, supply 35 eligible of 547
  - off-topic in first 8: Standoff; Criminal; Sniper: Ultimate Kill; Ticker; Sniper: Ghost Shooter
  - fallback: stub reply
  - reply echoes the user's message
- T2 “now spy movies” — 14 picks, precision@8 0.63, supply 18 eligible of 327
  - off-topic in first 8: Spooks: The Greater Good; Traitor; The Fix
  - fallback: stub reply
  - reply echoes the user's message
- T3 “go back to the hitman ones — more like the first of those” — 10 picks
  - failed: reference
  - fallback: stub reply
  - reply echoes the user's message

### crit-not-scary (critique, facet)
- T1 “horror movies” — 10 picks, precision@8 1, supply 448 eligible of 4858
  - fallback: 1 stub why-lines
  - grounding: stub why on Return

### crit-not-heists (critique, facet)
- T1 “heist movies” — 24 picks, precision@8 0.63, supply 58 eligible of 583
  - off-topic in first 8: Thick as Thieves; Rob the Mob; The Crew
  - fallback: stub reply
  - reply echoes the user's message
- T2 “none of these are actually heists” — 14 picks, precision@8 0.50, supply 58 eligible of 583
  - failed: critique
  - off-topic in first 8: RocknRolla; The Escapist; The Player; StartUp
  - fallback: stub reply
  - reply echoes the user's message

### crit-not-spy (critique, facet)
- T2 “none of those are spy films, I want spies” — 20 picks, precision@8 0.13, supply 63 eligible of 434
  - failed: critique
  - off-topic in first 8: The Girl in the Spider's Web; Sniper: Rogue Mission; One Ranger; Turks & Caicos; Salting the Battlefield; Weekend in Taipei; Running Scared

### crit-zombie-not (critique, facet)
- T2 “half of these have no zombies in them” — 17 picks, precision@8 0, supply 27 eligible of 345
  - failed: critique
  - off-topic in first 8: Light as a Feather; Stake Land; Being Human; Ginger Snaps 2: Unleashed; Being Human; Goosebumps: Scary House; Mostly Ghostly 3: One Night in Doom House; Herman
  - fallback: stub reply; 12 stub why-lines
  - grounding: stub why on Light as a Feather; stub why on Being Human; stub why on Ginger Snaps 2: Unleashed; stub why on Being Human; stub why on Goosebumps: Scary House; stub why on Mostly Ghostly 3: One Night in Doom House; stub why on Herman; stub why on Lowlifes; stub why on Them; stub why on October Faction; stub why on Monsterland; stub why on Night Teeth
  - reply echoes the user's message

### iq-runtime (item-question, ordinal)
- T1 “submarine movies” — 11 picks, precision@8 0.25, supply 5 eligible of 68
  - off-topic in first 8: Sanctum; Seal Team Eight: Behind Enemy Lines; Master and Commander: The Far Side of the World; Against the Sun; All Is Lost; Swallows and Amazons
  - fallback: stub reply; 3 stub why-lines
  - grounding: stub why on On the Beach; stub why on Seal Team Eight: Behind Enemy Lines; stub why on Master and Commander: The Far Side of the World
  - reply echoes the user's message
- T2 “how long is the second one?” — 11 picks
  - failed: mode, reference
  - fallback: 3 stub why-lines
  - grounding: stub why on 25th Hour; stub why on 12 Monkeys; stub why on From Time to Time

### iq-series-or-movie (item-question, ordinal)
- T1 “time travel stories” — 14 picks, precision@8 1, supply 49 eligible of 459
  - fallback: stub reply
  - reply echoes the user's message
- T2 “is the third one a series or a movie?” — 10 picks
  - failed: mode, reference

### iq-scary (item-question, ordinal)
- T1 “witch movies” — 10 picks, precision@8 0.88, supply 53 eligible of 357
  - off-topic in first 8: A Dark Song
  - fallback: stub reply
  - reply echoes the user's message
- T2 “is the first one actually scary?” — 12 picks
  - failed: mode, reference
  - fallback: stub reply; 2 stub why-lines
  - grounding: stub why on Return; stub why on The Terror
  - reply echoes the user's message

### iq-why-picked (item-question, ordinal)
- T1 “hacker thrillers” — 10 picks, precision@8 0.50, supply 17 eligible of 121
  - off-topic in first 8: Criminal; Intelligence; Cypher; Fugitive at 17
  - fallback: stub reply
  - reply echoes the user's message
- T2 “why did you pick the second one?” — 14 picks
  - failed: mode, reference
  - fallback: stub reply; 6 stub why-lines
  - grounding: stub why on 1 Chance 2 Dance; stub why on A Second Chance; stub why on One of Us; stub why on Fingersmith; stub why on Switched at Birth; stub why on The Ones Below
  - reply echoes the user's message

### iq-named-show (item-question, title-lookup, tv)
- T1 “what is Better Call Saul about?” — 15 picks
  - failed: mode
  - fallback: stub reply
  - reply echoes the user's message

### people-villeneuve (people)
- T1 “Denis Villeneuve films” — 7 picks, supply 0 eligible of 13
  - failed: honestEmpty, mode
  - exclusion leaks: Dune: Part Two; Dune; Blade Runner 2049; Arrival; Sicario; Enemy; Prisoners
  - fallback: stub reply
  - reply echoes the user's message

### people-ridley-scott (people)
- T1 “films directed by Ridley Scott” — 11 picks, precision@8 1, supply 2 eligible of 29
  - exclusion leaks: The Dog Stars; Gladiator II; Alien: Covenant; The Martian; Prometheus; American Gangster; Hannibal; Gladiator

### people-statham (people)
- T1 “Jason Statham action movies” — 25 picks, supply 0 eligible of 46
  - failed: honestEmpty, mode
  - exclusion leaks: Mutiny; Shelter; A Working Man; The Beekeeper; Expend4bles; Meg 2: The Trench; Fast X; Operation Fortune: Ruse de Guerre; F9; Wrath of Man; The Meg; The Fate of the Furious; Mechanic: Resurrection; Furious 7; The Expendables 3; Homefront; Fast & Furious 6; Hummingbird; Parker; The Expendables 2; Safe; Killer Elite; Blitz; The Mechanic; The Expendables
  - off-topic in first 8: Fast X

### people-pedro-pascal (people, tv)
- T1 “shows with Pedro Pascal” — 15 picks, precision@8 0.63, supply 4 eligible of 6
  - exclusion leaks: The Mandalorian; Game of Thrones; The Mentalist
  - off-topic in first 8: The Book of Boba Fett; The Kelly Clarkson Show; Homeland

### people-theron (people)
- T1 “Charlize Theron movies” — 13 picks, precision@8 0.75, supply 6 eligible of 52
  - exclusion leaks: The Odyssey; Apex; Fast X; Doctor Strange in the Multiverse of Madness; F9; The Old Guard; Atomic Blonde; The Fate of the Furious; The Huntsman: Winter's War; Mad Max: Fury Road; Prometheus; The Road
  - off-topic in first 8: Fast X; Doctor Strange in the Multiverse of Madness

### lookup-three-billboards (title-lookup)
- T1 “Three Billboards Outside Ebbing, Missouri” — 15 picks
  - fallback: stub reply
  - reply echoes the user's message

### lookup-like-hurt-locker (reference-title, facet)
- T1 “something like The Hurt Locker” — 18 picks, precision@8 0.75, supply 81 eligible of 815
  - wrong media: Generation Kill; The Long Road Home; Our Girl; SIX; The Unit; House of Saddam; Valor
  - off-topic in first 8: The Kingdom; Basic
  - fallback: stub reply
  - reply echoes the user's message

### lookup-black-mirror (title-lookup, tv)
- T1 “Black Mirror” — 9 picks
  - fallback: stub reply
  - reply echoes the user's message

### media-switch-roundtrip (media-switch, facet)
- T1 “space movies” — 9 picks, precision@8 0.63, supply 12 eligible of 213
  - wrong media: Mars; Extant; Dark Matter; Defying Gravity; Origin; Masters of Science Fiction; Ascension; The Twilight Zone
  - off-topic in first 8: Origin; The Vast of Night; Masters of Science Fiction
  - fallback: stub reply
  - reply echoes the user's message
- T2 “now TV shows like that” — 20 picks, precision@8 0.38, supply 27 eligible of 89
  - off-topic in first 8: Firefly; Falling Skies; Continuum; Terra Nova; Battlestar Galactica
  - fallback: 2 stub why-lines
  - grounding: stub why on Star Trek: Enterprise; stub why on Star Trek: Short Treks
- T3 “back to movies” — 15 picks, precision@8 0, supply 12 eligible of 213
  - off-topic in first 8: Chosen; Never Back Down: No Surrender; Rest Stop: Don't Look Back; The Returned; Backdraft 2; Touchback; Stalked by My Doctor: The Return; The Return
  - fallback: stub reply
  - reply echoes the user's message

### mood-cozy (mood-vs-exclusion, mood)
- T1 “a cozy feel-good movie” — 16 picks, precision@8 1
  - fallback: stub reply
  - reply echoes the user's message

### mood-cry (mood-vs-exclusion, mood)
- T1 “something to cry to” — 12 picks, precision@8 1
  - fallback: stub reply
  - reply echoes the user's message

### tvx-music (tv-exclusion, tv)
- T1 “music shows” — 10 picks, precision@8 1
  - fallback: stub reply
  - reply echoes the user's message

### tvx-war (tv-exclusion, tv)
- T1 “war drama series” — 16 picks, precision@8 0.63, supply 64 eligible of 187
  - off-topic in first 8: Valor; Britannia; NCIS
  - fallback: 1 stub why-lines
  - grounding: stub why on Birdsong

### chit-capabilities (chitchat)
- T1 “what can you do?” — 16 picks
  - failed: mode
  - fallback: stub reply
  - reply echoes the user's message

### chit-thanks (chitchat, facet)
- T1 “werewolf movies” — 9 picks, precision@8 0.13, supply 7 eligible of 116
  - off-topic in first 8: Feral; Vampire; Dark Light; Wolf Creek; Shadow of the Vampire; Wolf Creek 2; The Dark
  - fallback: 1 stub why-lines
  - grounding: stub why on Feral
- T2 “thanks, that's perfect” — 16 picks
  - failed: mode
  - fallback: stub reply; 6 stub why-lines
  - grounding: stub why on Pin Cushion; stub why on I Smile Back; stub why on The Christmas Note; stub why on A Gift Wrapped Christmas; stub why on The Fits; stub why on Sweetbitter
  - reply echoes the user's message

### refine-recent (facet, refine)
- T1 “kidnapping thrillers” — 6 picks, precision@8 0.33, supply 62 eligible of 535
  - wrong media: The Missing; Ransom; Kidnapped
  - off-topic in first 8: Abducted: The Mary Stauffer Story; Whisper; Ransom; Daddy's Little Girl
  - fallback: stub reply
  - reply echoes the user's message
- T2 “only ones from the last five years” — 14 picks, precision@8 0, supply 62 eligible of 535
  - wrong media: I Know What You Did Last Summer; The Survivors; Amazing Stories; The One; Missing You; Apples Never Fall; We Were Liars; The Last Thing He Told Me
  - off-topic in first 8: All the Devils Are Here; The Last Rodeo; I Know What You Did Last Summer; Moments: Six; Last and First Men; The Survivors; Amazing Stories; Red Roses Never Die
  - fallback: stub reply
  - reply echoes the user's message

### pagination-more (facet, pagination)
- T1 “martial arts movies” — 11 picks, precision@8 0.50, supply 16 eligible of 551
  - off-topic in first 8: Female Fight Squad; A Prayer Before Dawn; In Full Bloom; Bad Ass 2: Bad Asses
  - fallback: stub reply
  - reply echoes the user's message
- T2 “show me more” — 14 picks, precision@8 0, supply 16 eligible of 551
  - wrong media: Somewhere Between; NCIS; Creepshow; Torchwood; Light as a Feather; Eye Candy; Luther; CSI: Crime Scene Investigation; Pretty Little Liars: The Perfectionists; Grey's Anatomy; Tell Me You Love Me; Queer Eye; Tidying Up with Marie Kondo; 8 Out of 10 Cats
  - off-topic in first 8: Somewhere Between; NCIS; Creepshow; Torchwood; Light as a Feather; Eye Candy; Luther; CSI: Crime Scene Investigation
  - fallback: stub reply; 1 stub why-lines
  - grounding: stub why on Tell Me You Love Me
  - reply echoes the user's message

### refine-shorter (facet, refine)
- T1 “courtroom dramas” — 17 picks, precision@8 0.63, supply 90 eligible of 536
  - off-topic in first 8: The Trials of Cate McCall; Return to Innocence; Lockdown
  - fallback: 1 stub why-lines
  - grounding: stub why on Interstate 60

### tv-question (item-question, tv, ordinal)
- T1 “sci-fi series” — 14 picks, precision@8 1, supply 391 eligible of 1801
  - non-Latin script in English copy: Heroes: With ordinary people discovering superpowers and racing to prevent disaster, you get an urgent sci-fi ensemble, though its superhero angle is a recent weaker点.
- T2 “how many seasons does the first one have?” — 9 picks
  - failed: mode, reference
  - fallback: 2 stub why-lines
  - grounding: stub why on The Killing; stub why on Top of the Lake

### people-ordinal (people, ordinal)
- T1 “Liam Neeson movies” — 12 picks, precision@8 1, supply 5 eligible of 81
  - exclusion leaks: Ice Road: Vengeance; In the Land of Saints and Sinners; Retribution; Memory; Blacklight; The Ice Road; The Marksman; Honest Thief; Star Wars: The Rise of Skywalker; Widows; The Commuter
  - fallback: stub reply
  - reply echoes the user's message
- T2 “more like the third one” — 18 picks
  - failed: reference
  - fallback: 2 stub why-lines
  - grounding: stub why on The Kitchen; stub why on Small Town Crime

### chitchat-midstream (chitchat, pagination, facet)
- T1 “dinosaur movies” — 11 picks, precision@8 0.38, supply 5 eligible of 113
  - off-topic in first 8: Discovering Dinotopia; Free Willy: Escape from Pirate's Cove; Legends of the Hidden Temple; Where the Wild Things Are; Duma
  - fallback: stub reply
  - reply echoes the user's message
- T2 “lol” — 15 picks
  - failed: mode
- T3 “more of those” — 12 picks, precision@8 0, supply 5 eligible of 113
  - wrong media: Light as a Feather; The Client List
  - off-topic in first 8: The Ones Below; The Air I Breathe; Light as a Feather; Alex & Me; 12 and Holding; Love Finds You In Sugarcreek; The Loved Ones; Lucky Them
  - fallback: stub reply
  - reply echoes the user's message

