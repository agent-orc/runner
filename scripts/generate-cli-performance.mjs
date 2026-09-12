#!/usr/bin/env node

import { readFile, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = resolve(fileURLToPath(new URL("..", import.meta.url)));
const dataPath = resolve(repositoryRoot, "website/data/cli-performance-observations.json");
const pagePath = resolve(repositoryRoot, "website/index.html");
const generatedAt = "2026-09-12T08:00:00Z";
const measuredAt = "2026-06-26T11:39:13Z";
const scenarioIds = [
  "simple-question.001",
  "repo-question.002",
  "small-fix.003",
  "complex-task.004"
];

const models = [
  { id: "claude-sonnet-5", label: "Claude Sonnet 5", cli: "Claude", status: "current", availabilityObservedAt: "2026-09-12" },
  { id: "claude-opus-5", label: "Claude Opus 5", cli: "Claude", status: "current", availabilityObservedAt: "2026-09-12" },
  { id: "claude-fable-5-1", label: "Claude Fable 5.1", cli: "Claude", status: "current", availabilityObservedAt: "2026-09-12" },
  { id: "gpt-5.6-sol", label: "GPT 5.6 Sol", cli: "Codex", status: "current", availabilityObservedAt: "2026-09-12" },
  { id: "gpt-5.6-terra", label: "GPT 5.6 Terra", cli: "Codex", status: "current", availabilityObservedAt: "2026-09-12" },
  { id: "gpt-5.6-luna", label: "GPT 5.6 Luna", cli: "Codex", status: "current", availabilityObservedAt: "2026-09-12" },
  { id: "gpt-6-astra", label: "GPT 6 Astra", cli: "Codex", status: "current", availabilityObservedAt: "2026-09-12" },
  { id: "claude-haiku-4-5", label: "Claude Haiku 4.5", cli: "Claude", status: "obsolete", lastMeasuredAt: "2026-06-26" },
  { id: "claude-sonnet-4-6", label: "Claude Sonnet 4.6", cli: "Claude", status: "obsolete", lastMeasuredAt: "2026-06-26" },
  { id: "claude-opus-4-8", label: "Claude Opus 4.8", cli: "Claude", status: "obsolete", lastMeasuredAt: "2026-06-26" },
  { id: "gpt-5.5", label: "GPT 5.5", cli: "Codex", status: "obsolete", lastMeasuredAt: "2026-06-26" },
  { id: "gpt-5.4-mini", label: "GPT 5.4 Mini", cli: "Codex", status: "obsolete", lastMeasuredAt: null, notes: "No CLI performance measurement is retained in this dataset." }
];

const historicalModelIds = new Map([
  ["Claude Haiku 4.5", "claude-haiku-4-5"],
  ["Claude Sonnet 4.6", "claude-sonnet-4-6"],
  ["Claude Opus 4.8", "claude-opus-4-8"],
  ["GPT 5.5", "gpt-5.5"]
]);

const targetVersions = { Claude: "2.1.269", Codex: "0.153.4" };
const reviewVersions = { Claude: "2.1.202", Codex: "0.144.1" };

function percentile(values, fraction) {
  const sorted = values.slice().sort((a, b) => a - b);
  return sorted[Math.max(0, Math.ceil(sorted.length * fraction) - 1)];
}

function median(values) {
  const sorted = values.slice().sort((a, b) => a - b);
  const middle = Math.floor(sorted.length / 2);
  return sorted.length % 2 ? sorted[middle] : Math.round((sorted[middle - 1] + sorted[middle]) / 2);
}

function groupRuns(runs) {
  const groups = new Map();
  const requiredFields = ["measuredAt", "scenario", "cli", "cliVersion", "modelId", "model", "thinking", "contextBucket", "wallClockMs", "firstCliFrameMs"];
  for (const [index, run] of runs.entries()) {
    const missing = requiredFields.filter(field => run[field] === undefined || run[field] === null || run[field] === "");
    if (missing.length) throw new Error(`Raw run ${index + 1} is missing: ${missing.join(", ")}`);
    const model = models.find(item => item.id === run.modelId);
    if (!model || model.status !== "current") throw new Error(`Raw run ${index + 1} does not use a current catalog model: ${run.modelId}`);
    if (model.cli !== run.cli) throw new Error(`Raw run ${index + 1} has CLI ${run.cli}, expected ${model.cli}`);
    if (run.cliVersion !== targetVersions[run.cli]) {
      throw new Error(`Raw run ${index + 1} used ${run.cli} ${run.cliVersion}; ${targetVersions[run.cli]} is required`);
    }
    if (!scenarioIds.includes(run.scenario)) throw new Error(`Raw run ${index + 1} has unknown scenario ${run.scenario}`);
    const key = [run.scenario, run.cli, run.modelId, run.thinking, run.contextBucket].join("|");
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(run);
  }

  return Array.from(groups.entries()).map(([aggregateKey, group]) => {
    const first = group[0];
    const numericMedian = field => median(group.map(run => Number(run[field] || 0)));
    const passed = group.filter(run => run.passed).length;
    return {
      recordType: "aggregate",
      state: "measured",
      lifecycle: "current",
      measuredAt: group.map(run => run.measuredAt).sort().at(-1),
      aggregateKey,
      scenario: first.scenario,
      cli: first.cli,
      cliVersion: first.cliVersion,
      modelId: first.modelId,
      model: first.model,
      thinking: first.thinking,
      contextBucket: first.contextBucket,
      contextTokens: numericMedian("contextTokens"),
      inputTokens: numericMedian("inputTokens"),
      cachedInputTokens: numericMedian("cachedInputTokens"),
      cacheCreationInputTokens: numericMedian("cacheCreationInputTokens"),
      outputTokens: numericMedian("outputTokens"),
      reasoningTokens: numericMedian("reasoningTokens"),
      totalTokensUsed: numericMedian("totalTokensUsed"),
      runs: group.length,
      medianMs: median(group.map(run => Number(run.wallClockMs))),
      p90Ms: percentile(group.map(run => Number(run.wallClockMs)), 0.9),
      firstCliFrameMs: median(group.map(run => Number(run.firstCliFrameMs))),
      firstOutputMs: median(group.map(run => Number(run.firstCliFrameMs))),
      successPct: Math.round((passed / group.length) * 1000) / 10,
      notes: `${passed}/${group.length} scenario runs passed; aggregated from imported raw run records.`
    };
  });
}

async function readRuns(path) {
  const text = await readFile(resolve(process.cwd(), path), "utf8");
  return text.split(/\r?\n/).filter(Boolean).map((line, index) => {
    try {
      return JSON.parse(line);
    } catch (error) {
      throw new Error(`Invalid JSON on ${path}:${index + 1}: ${error.message}`);
    }
  });
}

function pendingMeasurements(observations) {
  return models.filter(model => model.status === "current").map(model => {
    const measuredScenarios = new Set(observations.filter(item => item.modelId === model.id).map(item => item.scenario));
    return {
    state: "pending",
    recordedAt: "2026-09-12",
    cli: model.cli,
    targetCliVersion: targetVersions[model.cli],
    observedCliVersion: reviewVersions[model.cli],
    modelId: model.id,
    model: model.label,
    thinking: "pending",
    scenarios: scenarioIds.filter(scenario => !measuredScenarios.has(scenario)),
    reason: model.cli === "Claude"
      ? "Pending on a logged-in host with Claude Code 2.1.269; this review host has 2.1.202 and no Claude login."
      : model.id === "gpt-6-astra"
        ? "Pending on Codex 0.153.4; this review host has 0.144.1 and does not offer gpt-6-astra."
        : "Pending on Codex 0.153.4; this review host has 0.144.1, so its timings would not be comparable."
    };
  }).filter(item => item.scenarios.length);
}

function enrichObservation(observation) {
  const modelId = observation.modelId || historicalModelIds.get(observation.model);
  const model = models.find(item => item.id === modelId);
  if (!model) throw new Error(`No lifecycle metadata for model ${observation.model}`);
  return {
    recordType: "aggregate",
    ...observation,
    lifecycle: model.status,
    measuredAt: observation.measuredAt || measuredAt,
    cliVersion: observation.cliVersion || (model.status === "current"
      ? targetVersions[observation.cli]
      : (observation.cli === "Claude" ? "2.1.186" : "0.142.0")),
    modelId,
    aggregateKey: observation.aggregateKey || [observation.scenario, observation.cli, modelId, observation.thinking, observation.contextBucket].join("|")
  };
}

function validate(payload) {
  const catalog = new Map(payload.models.map(model => [model.id, model]));
  if (!payload.scenarios.every(scenario => Array.isArray(scenario.sourceTests) && scenario.sourceTests.length)) {
    throw new Error("Every scenario must retain at least one source-test reference.");
  }
  for (const observation of payload.observations) {
    const model = catalog.get(observation.modelId);
    if (!model) throw new Error(`Observation references unknown model ${observation.modelId}`);
    if (!observation.measuredAt) throw new Error(`Observation ${observation.aggregateKey} has no measurement date`);
    if (!observation.cliVersion) throw new Error(`Observation ${observation.aggregateKey} has no CLI version`);
    if (model.status === "obsolete" && !model.lastMeasuredAt) {
      throw new Error(`Measured obsolete model ${model.id} has no last measurement date`);
    }
  }
  for (const model of payload.models.filter(item => item.status === "obsolete")) {
    const dates = payload.observations.filter(item => item.modelId === model.id)
      .map(item => item.measuredAt.slice(0, 10)).sort();
    if (dates.length && model.lastMeasuredAt !== dates.at(-1)) {
      throw new Error(`Obsolete model ${model.id} must carry its latest measurement date`);
    }
  }
  const queued = new Set(payload.pendingMeasurements.map(item => item.modelId));
  for (const model of payload.models.filter(item => item.status === "current")) {
    if (!queued.has(model.id) && !payload.observations.some(item => item.modelId === model.id)) {
      throw new Error(`Current model ${model.id} is neither measured nor pending`);
    }
  }
}

const args = process.argv.slice(2);
const checkOnly = args.includes("--check");
const runArgIndex = args.indexOf("--runs");
const importedRuns = runArgIndex >= 0 ? await readRuns(args[runArgIndex + 1]) : [];
const existing = JSON.parse(await readFile(dataPath, "utf8"));
const preserved = (existing.observations || []).map(enrichObservation);
const importedAggregates = groupRuns(importedRuns);
const importedKeys = new Set(importedAggregates.map(item => item.aggregateKey));
const observations = preserved.filter(item => !importedKeys.has(item.aggregateKey)).concat(importedAggregates);

const payload = {
  ...existing,
  schemaVersion: 3,
  state: importedRuns.length ? "measured-with-pending" : "historical-measurements-with-pending-current-runs",
  generatedAt,
  sourceTruth: "Aggregate rows come from real CLI executions. Retired-model history is retained. Current-model runs that could not be made with the required CLI versions are explicit pending records and never synthetic timing rows.",
  provenance: {
    dataAsOf: "2026-09-12",
    generatedBy: "scripts/generate-cli-performance.mjs",
    generationCommand: "node scripts/generate-cli-performance.mjs",
    rowMeasurementDateField: "observations[].measuredAt",
    aggregation: "One aggregate per scenario + CLI + model + thinking + context bucket. Numeric token fields and firstCliFrameMs are medians; medianMs is the wall-clock median; p90Ms uses nearest-rank P90; successPct includes every imported run.",
    inputs: [
      "Preserved aggregate observations measured on 2026-06-26",
      "Optional raw JSONL passed with --runs",
      "Operator model-availability snapshot dated 2026-09-12"
    ],
    measurementEnvironments: [
      {
        id: "windows-local-2026-06-26",
        status: "measured",
        measuredAt,
        cliVersions: { Claude: "2.1.186", Codex: "0.142.0", Gemini: "0.41.2" }
      },
      {
        id: "agent-runner-01-2026-09-12",
        status: "pending-incompatible-host",
        observedAt: "2026-09-12",
        cliVersions: reviewVersions,
        authentication: { Claude: "not logged in", Codex: "logged in" },
        requiredCliVersions: targetVersions
      }
    ]
  },
  cliVersions: [
    { cli: "Claude", version: "2.1.186", measuredAt: "2026-06-26", status: "historical-measurement" },
    { cli: "Codex", version: "0.142.0", measuredAt: "2026-06-26", status: "historical-measurement" },
    { cli: "Claude", version: "2.1.269", observedAt: "2026-09-12", status: "required-for-pending-runs" },
    { cli: "Codex", version: "0.153.4", observedAt: "2026-09-12", status: "required-for-pending-runs" }
  ],
  models,
  observations,
  pendingMeasurements: pendingMeasurements(observations)
};

validate(payload);
const json = `${JSON.stringify(payload, null, 2)}\n`;
const page = await readFile(pagePath, "utf8");
const marker = /(<script type="application\/json" id="cli-performance-data"[^>]*>\n)[\s\S]*?(\n<\/script>)/;
if (!marker.test(page)) throw new Error("Could not find embedded CLI performance fallback payload.");
const updatedPage = page.replace(marker, `$1${json.trimEnd()}$2`);

if (checkOnly) {
  if (await readFile(dataPath, "utf8") !== json || page !== updatedPage) {
    throw new Error("CLI performance data is stale. Run node scripts/generate-cli-performance.mjs");
  }
  process.stdout.write(`Checked ${observations.length} aggregate rows and ${payload.pendingMeasurements.length} pending model queues.\n`);
} else {
  await writeFile(dataPath, json);
  await writeFile(pagePath, updatedPage);
  process.stdout.write(`Generated ${dataPath} with ${observations.length} aggregate rows and ${payload.pendingMeasurements.length} pending model queues.\n`);
}
