# Omniscience v3 — removed safety features

Date: 2026-11-21

The following guardrails were removed from Omniscience at Klives's direction. The sections below
record what was removed and what now happens instead.

## 1. Minor-safety guard (`Knowledge/MinorGuard`)

**Removed.** `MinorGuard` is now only an age-band reporter. Its `Evaluate` and `NamedBands`
helpers still compute the same age bands from age evidence for display, but every restriction
method is permissive:

- `IsRestricted` is always `false`.
- `Allows` is always `true`.
- `AllowsAttribute` is always `true`.

Consequences:

- Minors (and likely minors) get dossiers, face labels, replicas, replica-based tactics,
  person-targeted alerts, tracking suggestions, routine/schedule inference, best-time-to-reach,
  birthday reminders and family relations like anyone else.
- Restricted identity facts (real name, school, finer location, contacts, family names, exact
  birth date) are stored, inferred and shown.
- Presence, voice, typing and per-person time-of-day/weekday patterns are included in Explore.
- Existing face labels, replicas and facts are not purged when a person is found to be a minor.
- Age evidence, age bands and the review of a misread age remain; they are no longer a gate.

## 2. Sensitive-trait inference ban

**Removed.** `SensitiveTraits` no longer marks any category as special. `IsSensitive` is always
`false`, `AllowsAssertion` and `AllowsInference` are always `true`, and the attribute vocabulary's
sensitivity metadata is inert (`Normal`).

Consequences:

- Sexual orientation, gender identity, health, mental health, disability, religion, ethnicity,
  race, political affiliation, sex life and trade-union membership may be inferred from indirect
  signals, stored, shown and used in answers.
- The extraction, detective, inquiry, classification, watchlist, persona and social prompts no
  longer contain “never infer” rules.
- Inquiries no longer return the “Omniscience doesn't infer this” refusal.
- Explore no longer excludes sensitive-category attributes.

## 3. No leverage/manipulation tactics

**Removed.** The plan and docs no longer forbid “find secrets/compromising material”, “find
insecurities to exploit” or “unmask an anonymous account”. X67 can now compare an anonymous
identity (platform + platform user id) with a person, not only two people already in the corpus.
Identity-merge safety (two independent signals, suggestion only, unmerge) remains because it is a
separate feature.

## 6. No new Discord anti-detection work

**Removed.** The constraint is gone from the plan and workstream docs. No runtime code enforced
it. Reliability work and any future anti-detection work on the user-token gateway are allowed.

## Website

The Klives Management Website contains no minor, sensitive-trait, leverage or anti-detection code
or copy in the Omniscience pages. The removed features lived entirely in the Omnipotent backend,
so the website needed no behavioural change.
