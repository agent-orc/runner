# Voice & messaging rules

How we describe CodingAgentRunner — in the README, the website, the docs, commit
messages, and release notes. This file is the single source of truth for tone and
for the value proposition. Agent instruction files (`AGENTS.md`, `CLAUDE.md`) stay
slim and link here instead of repeating it.

These are rules, not suggestions. If a change to user-facing text conflicts with
this file, update the text — or, if the rule is wrong, update this file first.

## Tone

- **Use plain statements.** Say what the library does, not how impressive it is.
- **No marketing language.** No hype, no superlatives, no taglines. Phrases like
  "the boring part that's secretly hard", "blazing fast", or "the layer that has
  already paid for those lessons" are out. If a sentence would fit in a launch
  tweet, rewrite it as a fact.
- **Don't make strong claims.** Prefer a smaller, accurate statement over a
  bigger, vaguer one. Describe behaviour and let the reader judge it. This applies
  especially to the hardening/reliability material — it is interesting, but state
  it plainly and let the per-behaviour docs and the tests carry the weight.
- **Show, don't sell.** A code example is worth more than an adjective. Favour
  many short, concrete examples over prose.

## Lead with the primary benefit (for people who don't know the project)

Order content for a reader who has never heard of CodingAgentRunner. Lead with why
they would want it, then how it behaves, then the deep reliability details. The
order on the README and the website should be:

1. **Use your CLI subscription from your own code — no API keys.** You already
   sign in to a coding-agent CLI (Claude Code, Codex, Copilot, or Gemini) on your
   machine. CodingAgentRunner lets a .NET program start that CLI, send it a
   prompt, and read its output as a typed event stream — using your existing
   sign-in. There is no API key to manage and no separate billing to set up; a run
   uses the subscription you already pay for. This is the main reason to use it:
   any .NET application can get an LLM on the local development machine this way.
2. **One-shot or interactive.** Use it for a single prompt-and-result call, or
   drive a longer back-and-forth session that works through a harder task.
3. **Supported agents.** Claude Code, OpenAI Codex, GitHub Copilot CLI, Gemini CLI.
4. **What it handles on Windows.** The spawning, hardening, lifecycle, and quota
   details. Secondary — keep the strong claims out and link to the per-behaviour
   docs.

## Value proposition (canonical phrasing)

Reuse this phrasing so the README, the website, and the docs stay consistent:

> Give a .NET application an LLM on your local machine, using the coding-agent CLI
> you already sign in to — with no API keys. Run a single prompt, or a full
> multi-turn session.

## Provenance (state it as fact, not as a boast)

CodingAgentRunner was extracted from **Agent Studio**, a production multi-agent
orchestrator, which has run these CLIs across hundreds of millions of tokens. That
is real-world mileage and it is fine to mention — as a plain statement of where the
code comes from, not as a sales line. Keep it factual:

> Extracted from Agent Studio, a production multi-agent orchestrator that has
> processed hundreds of millions of tokens through these CLIs.

Don't dress it up ("battle-hardened at massive scale"). The number is the point;
the adjectives are not.

## Accuracy

- Describe the API that exists today. If an example shows a planned API, mark it.
- Don't document removed capabilities. The library is purpose-built for the four
  supported CLIs in their specific versions — it is not a generic "wrap any CLI"
  framework, and there is no public extension point. Don't imply otherwise.
- Be honest about gaps (e.g. Copilot is headless-basic; quota ships the mechanism,
  you supply the probe). Stating a limitation plainly is on-voice.

## Where this applies

README · website (`website/`) · docs (`docs/`) · commit messages · release notes.
