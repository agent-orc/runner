# CLI setup — install, sign in, diagnose

CodingAgentRunner runs coding-agent CLIs that must already be installed and
signed in on the machine. The library does not install or authenticate a CLI for
you — that is a deliberate boundary: credentials belong to the operator, not to a
library. What the library does ship is the knowledge of *what* is missing and
*how* to fix it, as a queryable API.

## Diagnosing the machine: `InspectEnvironment()`

```csharp
using CodingAgentRunner;

var report = new CliRunner().InspectEnvironment();

if (!report.AnyReady)
    Console.WriteLine(report.ToText());   // per-CLI state + the commands that fix it

// Or react programmatically:
var claude = report.For("claude")!;
if (!claude.Installed)
    logger.LogWarning("Claude Code missing. Install: {Cmd}", claude.Setup.RecommendedInstallCommand);
if (claude.Credentials == CredentialSignal.NotFound)
    logger.LogWarning("Claude Code not signed in. {Step}", claude.Setup.LoginSteps[0]);
```

`ToText()` renders a report like this (a machine with nothing installed):

```
Coding-agent CLI environment

claude      NOT INSTALLED (probed 'claude')
            install: npm install -g @anthropic-ai/claude-code
            NOT SIGNED IN — Run `claude` in a terminal; the first run opens a browser sign-in ...
            docs: https://code.claude.com/docs/en/setup
...
node        installed v24.18.0
npm         installed 11.16.0
```

What the report contains, per CLI:

- **Installed** — did the CLI answer its availability probe (`--version` for most
  CLIs)? `ResolvedPath` shows the resolved binary path; Antigravity checks the native `agy` install path first.
- **Credentials** — `Found` / `NotFound` / `Unknown`. `Found` means a credential
  *source* exists: a known credential file under the user home, or the CLI's
  API-key environment variable. It does **not** validate the credential — an
  expired token still reports `Found` and fails at run time. `Unknown` means the
  library has no known credential location for that CLI (Antigravity stores
  tokens in the OS keyring).
- **Setup** (`CliSetupInfo`) — the static fix-it knowledge: install commands,
  sign-in steps, the API-key env var, credential file locations, automation
  caveats, and the official docs URL. Also available without probing anything via
  `CliSetup.For("claude")` / `CliSetup.All`.

Report-level, it also probes **node** and **npm**, since three of the four CLIs
are npm-distributed.

Caveats:

- The probe spawns `<cli> --version` (bounded by an ~8 s timeout each, run
  concurrently) — call it at startup or on demand, not per run.
- On macOS, Claude Code stores credentials in the Keychain; the file probe can
  report `NotFound` for a machine that is actually signed in. Treat `NotFound`
  as "probably needs a sign-in", not as proof.
- The setup commands are static data, current as of this library version. The
  CLIs evolve; when a command fails, follow `DocsUrl`.

## Installing the CLIs

All commands below are scriptable (CI, provisioning, dev-container setup).

| CLI | Recommended | Alternatives |
|-----|-------------|--------------|
| Claude Code | `npm install -g @anthropic-ai/claude-code` | Windows: `irm https://claude.ai/install.ps1 \| iex` · macOS/Linux: `curl -fsSL https://claude.ai/install.sh \| bash` · `winget install Anthropic.ClaudeCode` |
| Codex | `npm install -g @openai/codex` | Windows: `irm https://chatgpt.com/codex/install.ps1 \| iex` · macOS/Linux: `curl -fsSL https://chatgpt.com/codex/install.sh \| sh` · `brew install --cask codex` |
| Gemini (deprecated here) | `npm install -g @google/gemini-cli` | `brew install gemini-cli` |
| Antigravity (`agy`) | Windows: `irm https://antigravity.google/cli/install.ps1 \| iex` | macOS/Linux: `curl -fsSL https://antigravity.google/cli/install.sh \| bash` — installs `agy` |

The npm installs need Node.js (the report checks for it). After an install, a
new terminal (or a PATH refresh) is needed before the command resolves.

## Signing in

Each CLI authenticates once per machine; CodingAgentRunner then reuses that
session (that is the library's core premise — your subscription, no API keys).

- **Claude Code** — run `claude`, complete the browser sign-in. Credential file:
  `~/.claude/.credentials.json` (Windows/Linux; macOS uses the Keychain).
- **Codex** — run `codex login`, browser sign-in with the ChatGPT account.
  Credential file: `~/.codex/auth.json`.
- **Gemini** — run `gemini`, choose the Google-account sign-in. Credential file:
  `~/.gemini/oauth_creds.json`.
- **Antigravity** — run `agy` once, Google-account sign-in (on SSH/headless it
  prints a URL + one-time code). Tokens live in the OS keyring.

## Automating sign-in (headless / CI)

The browser OAuth flows are interactive. The non-interactive options, best-first
per CLI:

- **Claude Code** — run `claude setup-token` once on a machine with a browser;
  set the printed long-lived token as `CLAUDE_CODE_OAUTH_TOKEN` on the headless
  machine. Or set `ANTHROPIC_API_KEY` (API billing instead of the subscription).
  Copying `~/.claude/.credentials.json` from a signed-in machine also works on
  Windows/Linux.
- **Codex** — `codex login --device-auth` (device-code flow), or pipe an API key
  into `codex login --with-api-key`. Copying `~/.codex/auth.json` to the target
  machine is officially documented.
- **Gemini** — set `GEMINI_API_KEY`, or a service account via
  `GOOGLE_APPLICATION_CREDENTIALS`; or reuse a cached `~/.gemini/oauth_creds.json`.
- **Antigravity** — no documented headless token path; the first sign-in per
  machine needs the interactive URL + code flow.

Seeding a credential file is the same mechanism the library's own clean-context
mode uses (it copies exactly these files into a per-run home — see
[architecture.md](architecture.md), context modes). Treat the files like
passwords.

## Why the library doesn't auto-install

An `EnsureInstalled()` that runs `npm install -g` from library code would mutate
the operator's machine, need elevation in some setups, and hide a supply-chain
decision inside a package restore. The library's contract is: *detect and
explain* (`InspectEnvironment`), *self-heal only what it created or what is
mechanically broken* (the npm-shim heal for a present-but-broken `claude`
install), and leave *installing and authenticating* to the operator or their
provisioning script — which the `CliSetupInfo` data is designed to feed.

Antigravity runs the official `agy` binary with the operator’s existing sign-in. Its stdin stream uses `{"event":"user","message":{"content":"..."}}`; closing stdin ends a one-turn run. This input mode activates headless processing without a positional `-p` prompt, so large prompts stay off argv. The runner does not read, copy, or inject credentials. The [vendor FAQ](https://antigravity.google/docs/faq/) says headless `agy -p` is intended for direct scripting and CI, and that wrapping `agy` in third-party coding agents or applications is not supported under consumer plans. This descriptor supports functional tests; routing policy belongs to the host.

The default Antigravity binary locations are `%LOCALAPPDATA%\agy\bin\agy.exe` on Windows and `~/.local/bin/agy` on Linux/macOS. The runner falls back to `agy` on PATH.
