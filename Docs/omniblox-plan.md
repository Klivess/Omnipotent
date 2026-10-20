# OmniBlox — Development Plan

**Goal:** an autonomous, language-model-driven game studio that researches, builds, tests, publishes, markets, monetizes and maintains Roblox experiences entirely on its own, then moves on to the next one.

**In one line:** produce Roblox experiences wholly, comprehensively and automatically.

**Hard constraints:**
- **It costs nothing to run.** No paid ads, tools, APIs, assets or generators. Its only outside service is AIRouter, used within its fair-use limits (§5).
- **It makes all of its own marketing content:** icons, thumbnails, trailers, clips and copy, all made from its own games (§4.5).

_Status: plan only (October 2026). Nothing is built yet._

This plan describes what OmniBlox does and how the pieces fit together, not how to code it. Section 10 has a few pointers for the coding agent.

---

## 1. Principles

1. **End to end, hands off.** An idea goes in and a live, monetized, marketed game comes out. A human steps in only for account setup, cashing out and, at first, approving launches (§6).
2. **Always a clear goal.** At every moment the bot knows its mission, which game it's on, what stage that game is in and what "done" means for that stage. It keeps working toward that goal without stopping.
3. **Grounded, not guessing.** Research runs on live market data. Building uses a labeled asset library with real measurements and pictures. Every result is checked against reality: tests, screenshots, telemetry.
4. **Every game makes the next one better.** Each game's results feed back into how the bot researches, builds and markets.
5. **Free to run, built to earn.** The bot spends nothing, but every game still ships with monetization, so whatever the games earn is profit.
6. **Stays on Roblox's good side.** A banned account ends everything, so platform rules are a hard gate, not an afterthought.

## 2. The big picture

```
  ASSET LIBRARY  (own agent, always running)
  scrape free models -> clean -> measure -> render -> describe -> index
                   |
                   |  searched while building
                   v
  RESEARCH ----> BUILD <----> TEST ----> LAUNCH & MARKET ----> LIVE PORTFOLIO
   ^   ^                                        |              analytics +
   |   |                                        |              weekly maintenance
   |   +----------------------------------------+                    |
   |       launch finished: start the next game                      |
   |                                                                 |
   +--------------- PLAYBOOK  (lessons from every game) <------------+
```

- One game is **in production** at a time: Research, Build, Test, Launch. As soon as its launch is finished, the bot starts researching the next one.
- Launched games join the **live portfolio**. They are measured constantly and maintained weekly.
- The **Asset Library** fills up in the background, independent of whichever game is in production.
- What the bot learns goes into the **Playbook**, which every later research, build and launch reads first.

## 3. What the bot knows

Four stores that every part of the system reads from and writes to:

| Store | What's in it | Fed by | Used by |
|---|---|---|---|
| **Asset Library** | Every free asset and 3D model it has collected, fully labeled (§4.1) | The asset scraper | Build |
| **Game Kit** | Reusable, proven game systems: player data saving, shop and purchases, leaderboards, daily rewards, tutorial, settings, analytics hooks, in-game feedback | Build (good new systems get promoted into it) | Build |
| **Playbook** | Lessons learned: which genres, mechanics, prices, titles and thumbnails worked or flopped, and why | Analytics, post-mortems, maintenance | Research, Build, Launch, Maintenance |
| **Game Dossiers** | One per game: concept and evidence, design, current stage and checklist, decisions, assets used, test results, store page, products and prices, analytics, maintenance history | Every loop | Every loop |

The Game Dossier is the single source of truth for a game. It is also the clear goal the agent works toward: any agent that picks up work on a game starts by reading its dossier.

## 4. The systems

### 4.1 Asset Library (the scraper loop)

**Purpose:** give the builder a huge, searchable shelf of 3D models and assets that it actually *understands*: what each one is, how big it is, which way it faces and what it looks like.

A separate agent runs this loop continuously, in parallel with everything else. It only collects assets that are free to use.

For every asset it records as much as it can:
- **What it is:** name, a plain-language description, category and tags (tree, house, sword, lamp…), art style (low-poly, realistic, cartoon…), source, creator and asset ID.
- **Size and orientation:** dimensions in studs, bounding box, pivot, which way is front and which way is up, where it meets the ground, how many pieces it has.
- **Look:** main colors, materials and textures.
- **Pictures:** PNG renders from several angles (front, side, top, three-quarter), so the vision model can see it.
- **Written description:** generated by a vision model from the pictures and the model's structure: what it is, what it's good for, what it would look right next to.
- **Practical details:** complexity and performance cost, whether it contains scripts (and what they did), whether it's animated or interactive.
- **Quality:** usable, broken, ugly or a duplicate.
- **Track record:** which games used it and how that went (filled in over time).

**Pipeline:** find → download → clean → measure → render → describe → rate and de-duplicate → store and index.

**Cleaning matters.** Free models are a well-known source of hidden malicious scripts ("backdoors"). Scripts inside a scraped model are stripped or quarantined and reviewed. Nothing untrusted runs in a game.

**Search:** the builder asks in plain language ("low-poly pine tree, about 20 studs tall") and filters by size, style, color or category. Results come back with pictures and measurements.

**Targeting:** besides general crawling, it fetches what's needed next. If the next game is a pirate tycoon, it goes looking for ships, docks and cannons.

**Pacing:** describing every asset with a vision model makes this OmniBlox's heaviest AIRouter user, so it runs at the lowest priority, only in spare capacity (§5).

### 4.2 Research loop

**Purpose:** decide what to build next, with evidence, and how to make it sell.

It looks at:
- **Demand:** what's popular and what's rising right now: Roblox charts, player counts, favorites and like ratios, plus what Roblox players are watching and talking about on YouTube, TikTok, Reddit, X and the developer forum.
- **Competition:** how crowded each idea is, what the leaders do well and badly, and where the gaps are (a hot genre with weak games, a trend nobody has built yet).
- **Marketability:** the titles, icons, thumbnails, descriptions and update rhythms of the winners.
- **Monetization:** what top games in the genre sell, and at what prices.
- **Its own past:** the Playbook, meaning what has worked and failed for OmniBlox before.

It uses free sources only: public pages, web search and free API allowances.

It then drafts several concepts and scores each one on demand, competition, monetization potential, past lessons and **feasibility**: can the bot actually build this well with its current skills and assets? It picks the best one. Often that's a proven, in-demand genre with a fresh twist.

**Hands to Build:** a **Game Brief** in the dossier: the concept and the evidence for it, target players, the core gameplay loop, features (must-have and nice-to-have), art style and asset needs, the monetization plan, the marketing angle (title ideas, thumbnail ideas, keywords) and what "done" looks like. It also tells the asset scraper what to go and find.

### 4.3 Build loop

**Purpose:** turn the Game Brief into a real, playable game.

- **Plans first.** Breaks the brief into milestones: the core loop playable as early as possible, then the full feature set and content, then polish (UI, effects, sound, onboarding).
- **Starts from the Game Kit, not a blank page.** Saving, the shop, monetization, analytics and feedback come built in.
- **Writes the code and builds the world.** Writes the game's scripts and assembles the map from the Asset Library, using each asset's measurements, orientation and pictures to place things properly: on the ground, facing the right way, at a sensible scale, not overlapping.
- **Looks at its own work.** Regularly takes screenshots of the world from several cameras and has the vision model review them ("is that tree floating?", "is the shop button readable on a phone?").
- **Keeps every game as source files under version control,** so each change can be reviewed, tested and rolled back.
- **Grows the Game Kit.** A good new system worth reusing gets promoted into the kit for future games.

**Hands to Test:** the build, plus notes on what's in it and how it's meant to be played.

### 4.4 Test loop

**Purpose:** nothing goes public broken.

- **Runs:** the game starts cleanly, scripts run without errors, player data saves and loads.
- **Plays:** automated test players run through the core loop to prove it works. Can the obby be finished? Does the tycoon make money? Can the upgrade be bought?
- **Records:** test runs are saved as footage, so the marketing loop has real gameplay to cut clips from.
- **Buys:** every product prompts, charges (in test mode) and delivers correctly.
- **Performs:** load time, frame rate, server load and memory stay healthy, including on low-end phones, where a large share of Roblox players are.
- **Looks right:** screenshot review catches floating, clipping, broken or ugly things, and the UI works on phone, tablet, PC and console screens.
- **Follows the rules:** content and text suit Roblox and the game's maturity label.

Failures go back to Build with a clear report. Build fixes, Test re-runs, and only a game that passes everything moves on.

**Hands to Launch:** a passing build and its test report.

### 4.5 Launch & Marketing loop

**Purpose:** get the game in front of players and make them want to click, without spending anything.

**It makes all of its own marketing content** from the game itself, using free tools:
- **Icon and thumbnails:** staged shots inside the game (its best-looking spots, posed characters, dramatic camera angles and lighting), finished with its own text and graphics.
- **Trailer and clips:** scripted camera fly-throughs and recorded test-player gameplay, cut into a store trailer and short vertical clips for social media.
- **Copy:** the title, description, update notes and social posts, written in the style research found works for the genre.
- **Honest:** everything shows the real game.
- **Always improving:** it rotates variants, keeps whatever gets the best click-through, and adds what it learns to the Playbook.

**Launching:**
- **Store page:** a catchy, searchable title in line with the trend, the description, genre and tags, the icon, thumbnails and trailer.
- **Platform setup:** answers Roblox's content-maturity questionnaire honestly, creates the game's products and prices, and sets up servers and social links.
- **Publishing:** uploads the game and makes it public, optionally with a quiet soft launch first to see how real players behave before pushing hard.
- **Launch watch:** close monitoring in the first hours and days (errors, retention, click-through), with urgent problems fixed fast.

**Promotion is organic only. No paid ads.**
- titles and descriptions built to be found in search;
- regular updates and events that bring players back;
- clips posted to its own social accounts;
- cross-promotion between its own games, so each game sends players to the others.

Once launch is complete, the game moves into the live portfolio and the bot **starts researching the next game.**

### 4.6 Monetization

**Purpose:** every game earns money. The bot costs nothing to run, so whatever the games earn is profit.

- Every game ships with a microtransaction system from the Game Kit: shop UI, purchase handling and reliable delivery of what was bought.
- It uses the standard Roblox revenue streams: game passes (one-time perks), developer products (repeat purchases such as currency and boosts), paid private servers and Premium Payouts (Roblox pays for time spent by Premium members), plus new ones as Roblox adds them.
- Products and prices come from research (what sells in the genre) and are tuned with analytics.
- **Fair by design.** Roblox's audience is young. No deceptive or manipulative purchase design, and Roblox's rules (for example on paid random items) are always followed. Games earn through value: fun, convenience and cosmetics. That is also what keeps the account safe.

### 4.7 Analytics & learning

**Purpose:** know how every game is doing, and learn from it.

- **Per-game telemetry:** visits, concurrent players, session length, retention (day 1, 7 and 30), where players drop off, purchase conversion and revenue per product, likes and favorites, thumbnail click-through, errors and crashes, and in-game feedback.
- **Sources:** Roblox's own creator analytics, plus the analytics hooks built into every game (anonymous gameplay data only).
- **Post-mortems:** at set points after launch (say day 7 and day 30), the bot writes up what worked, what didn't and why, and adds the lessons to the Playbook.
- **Across the portfolio:** it compares games to find which genres, mechanics, prices, titles and thumbnail styles perform best.
- **This closes the loop.** Research reads the Playbook before choosing the next idea, and Build and Launch read it before making their choices.

### 4.8 Maintenance loop

**Purpose:** keep live games healthy and growing.

- **Scheduled weekly** for each live game: more often just after launch, less often for stable or fading games.
- **Reviews** the week's telemetry, errors, player feedback and revenue, against the week before and against similar games.
- **Decides what to do:**
  - fix bugs and errors first;
  - tune difficulty, the in-game economy and prices;
  - refresh the title, thumbnail or icon if click-through is weak;
  - ship content updates or events to bring players back, each with fresh clips;
  - or recommend **sunsetting** the game (no more effort spent on it) once it no longer earns its keep.
- **Changes go through Build and Test** before going live, the same as a new game.
- **Urgent problems don't wait.** A broken game, an error spike or revenue suddenly at zero jumps the queue.
- **Results feed back** into the dossier and the Playbook.
- **Shares time with new games.** A simple rule decides the split (promising games get more attention), so maintenance never stalls production.

### 4.9 Agent brain: goals, memory and context

This is the biggest engineering problem. A whole game is far more work than fits in a model's context window, so the agent must keep going past that limit without losing the plot.

- **The goal is always in view.** The mission, the current game, its stage and that stage's exit criteria are pinned into every context. A stage isn't done until its exit criteria are met.
- **Work in cycles with fresh context.** Each cycle starts from a curated briefing (the goal, where things stand, what's next, relevant memories), does a chunk of work, and writes a structured progress report back into the dossier. Nothing important lives only in the context window.
- **Smart compaction.** When the context fills up, history is compressed into a structured summary: what's done, decisions and why, open problems, next steps. Critical details such as IDs, file names and error messages are kept exactly.
- **Layered memory:** working memory (this task), game memory (the dossier), long-term memory (Playbook, Asset Library, Game Kit) and a full, searchable activity log for everything else.
- **Fetch, don't stuff.** The agent pulls in what it needs through search tools (assets, code, lessons, logs) instead of carrying everything around.
- **Delegate.** Focused sub-agents take self-contained jobs (build the shop UI, make the thumbnails, label a batch of assets), each with a small, clean context, while the lead agent keeps the big picture.
- **Never stuck, never spinning.** A watchdog spots stalls and loops (the same failure again and again) and forces a new approach, or escalates.
- **Verify, don't trust.** Every step is checked against reality (tests, screenshots, game output), not against the model's own claims.

### 4.10 Orchestrator

The studio's control loop.

- Runs each game's lifecycle: Research → Build ⇄ Test → Launch → Live → Sunset, with clear entry and exit criteria for every stage.
- One game in production at a time to start (configurable later), with any number in the live portfolio.
- Keeps the Asset Library agent running and schedules maintenance.
- Survives restarts: every game resumes exactly where it left off.
- Enforces time and AIRouter-usage budgets per stage and per game. When a game runs over, the agent has to cut scope or ship.

### 4.11 Owner dashboard

One place to see and steer everything:
- **Now:** the game in production, its stage and what the agent is doing right now.
- **Portfolio:** every live game with players, retention, revenue and health at a glance.
- **Earnings:** revenue per game and overall, with its cash value (§5).
- **AIRouter:** how much shared capacity OmniBlox is using, and its cache hit rate.
- **Asset Library:** browse and search the labeled assets.
- **Controls:** the approval queue (§6), pause and resume, a kill switch, and notifications for big events (launches, failures, revenue milestones).

## 5. Economics: free to run

- **The bot spends nothing.** No paid ads, tools, APIs, assets, or image or video generators. Everything it uses is free or runs on your own server.
- **AIRouter is the one shared resource, and its limit is capacity, not money.** Its fair-use rules allow three requests at a time, shared with every other agent in Omnipotent, and it has suspended access before when requests kept missing its prompt cache. So OmniBlox's real budget is AIRouter capacity:
  - it never crowds out interactive work (your conversations and live agents);
  - the asset labeler, its heaviest user, runs only in spare capacity;
  - every request is built to reuse the cache (§10).
- **One ledger.** It records AIRouter usage per game (and for overhead such as asset labeling), and revenue per game and per product.
- **What matters:** what each game earns against the AIRouter capacity it took. Games that earn get more updates. Games that don't get retired.
- **Cashing out** Robux for real money (Developer Exchange) is a manual owner step. Roblox keeps 30% of each sale and Developer Exchange pays a fixed rate per Robux, so roughly a fifth to a quarter of what players spend reaches you as cash. The dashboard shows that figure, not just Robux.

## 6. Guardrails and human gates

**Roblox's rules are a hard gate.** Before anything is published, the title, description, thumbnails, in-game text and content are checked against Roblox's Terms of Use and Community Standards:
- no copyrighted or trademarked material (other franchises' names, characters, music);
- honest store pages, with no misleading thumbnails or titles;
- content that matches the game's maturity label;
- fair monetization (§4.6);
- scraped models cleaned of untrusted scripts, and only assets that are free to use;
- anonymous gameplay telemetry only, never personal data;
- official APIs where they exist, and polite, rate-limited access where they don't;
- a minimum quality bar for launch. Fewer good games beat many empty ones, and a flood of low-effort games risks being treated as spam.

**The bot never spends money.** It never buys ads, tools, API access or anything else. These are off-limits, not something to ask approval for.

**Owner-only actions.** The bot prepares these and the owner does or approves them:
- cashing out Robux;
- setting up the Roblox account or group, identity verification, API keys and anything legal;
- at first, approving each game before it goes public. This can be relaxed once the bot has earned trust.

## 7. Build roadmap

Build order isn't pipeline order. The riskiest question, whether the agent can build a genuinely good game, gets answered early.

| Phase | Build | Done when |
|---|---|---|
| **0. Foundations** | Service shell, Roblox account and API access, a publish pipeline (game files → published game with title, description and icon), Game Dossiers, orchestrator skeleton, agent runtime | The bot publishes a simple test game end to end on its own |
| **1. Asset Library** | Scraper loop, cleaning, measuring, renders, vision descriptions, search | Thousands of labeled assets, and a plain-language search returns the right thing with pictures and sizes |
| **2. Build & Test** | Game Kit v1 (with monetization), Build loop, Test loop, visual self-review, for one simple genre from a hand-written brief | Given a brief, the bot produces a playable game that passes all its tests |
| **3. Research** | Market research, concept scoring, Game Brief writing | The bot picks its own idea, with evidence, and writes a brief the Build loop can carry out |
| **4. Launch & Market** | Store page, self-made icon, thumbnails and trailer, products and prices, compliance gate, publishing, launch watch | The first fully autonomous game is live and taking purchases |
| **5. Analytics & Maintenance** | Telemetry, post-mortems, Playbook, weekly maintenance, dashboard | The bot ships a data-driven fix or update to a live game by itself |
| **6. Full autonomy & scale** | Continuous pipeline, the ledger driving decisions, more genres, social clips, cross-promotion between its games | The studio runs unattended for weeks, spends nothing, and its games keep earning |

## 8. Measuring success

- **Output:** games launched per month, and the time and AIRouter usage from idea to launch.
- **Quality:** retention (day 1 and day 7), session length, like ratio.
- **Money:** revenue per game and in total, while spend stays at zero.
- **Good citizen:** OmniBlox stays inside AIRouter's fair-use limits and keeps its cache hit rate high.
- **Learning:** newer games beat older ones. This is the clearest sign the whole loop works.

## 9. Open decisions for the owner

- Publish under a personal account or a Roblox group? A group keeps ownership and revenue cleaner, but creating one costs a one-time 100 Robux, so a personal account is the strictly free option.
- Which genre first? (Simple and proven is safest: obby, tycoon, simulator.)
- How much of AIRouter's shared capacity OmniBlox may use, and where it ranks against your other agents.
- How long to keep approving launches by hand.
- How many games in production at once.
- Which social platforms the bot posts its clips to.

## 10. Notes for the coding agent

- OmniBlox belongs in Omnipotent next to the other Omni services (OmniTumblr, OmniGram), with its dashboard on the KM website. Settle early whether it is its own service that drives the Projects runtime, or a specialized Project with Roblox tools. The first gives a purpose-built pipeline; the second gets the most reuse.
- **Design around AIRouter's rules from day one.** Read `KliveLLM/PromptPrefixStability.cs` and the fair-use scheduler in `KliveLLM/AIRouterFairUse.cs` before designing OmniBlox's model calls, the asset labeler most of all. A suspension for cache misses hits every agent that shares the key.
- **AIRouter only.** Projects can also route model calls through OpenRouter, which is paid. Whatever runtime OmniBlox uses must be pinned to AIRouter.
- **Reuse; don't build parallel versions** of what already exists:
  - LLM calls: the AIRouter in `KliveLLM`.
  - Long-running agents, sub-agents, digests and compaction, loop recovery, budgets and schedules: the **Projects** runtime. Study it before designing §4.9, because it already solves much of it.
  - Search over assets, code and lessons: **KliveRAG**.
  - Roblox and other accounts and credentials: **AccountRegistry**.
  - Steps with no API (for example parts of Roblox's creator dashboard): the Projects computer-use desktops.
  - Trailers and clips: OmniTube's `VideoFactory`, which already wraps FFmpeg.
  - Social posting: the existing posting services (OmniTumblr, OmniGram).
- **Stratum** already uses the pattern OmniBlox needs: the agent works from a curated catalog, and a deterministic verifier checks everything it produces. Read it before designing Build and Test.
- **Prefer Roblox's official APIs and toolchain** for publishing, uploads, configuration and analytics. Roblox's creator tools change often, so check what's available now before designing around it.
