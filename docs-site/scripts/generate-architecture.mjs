#!/usr/bin/env node
// Generate the docs site's architecture model from the real sources of truth.
//
//   inputs   infra/main.bicep, infra/modules/*.bicep   (module graph, regions, quota
//            tiers, model deployments, alias/allowlist map, fg-role tags, gateway paths)
//            src/*/*.csproj                            (ProjectReference graph)
//            src/FoundryGate.Functions/**/*.cs         ([TimerTrigger] schedules)
//            src/FoundryGate.Api/**/*.cs               (controller route prefixes)
//   outputs  docs-site/src/generated/architecture.json (the model the site renders)
//            docs-site/src/generated/architecture.mmd  (machine-readable Mermaid graph)
//
// Runs as `prebuild`, so `npm run build` cannot render a stale picture; CI regenerates and
// fails if `git status --porcelain docs-site/src/generated` is non-empty, so a change to
// infra/ or src/ that moves the diagram has to land with the regenerated file (#221).
//
// Deliberately dependency-free: a hand-rolled reader for the *subset* of Bicep the
// template actually uses (literal params, module declarations, output references). It is
// not a Bicep parser and does not try to be. Anything it cannot read literally comes back
// as an `{ expr }` marker rather than a guess, and the values the diagram is BUILT from
// are asserted to be real literals (see literalArray) so an unreadable one fails the
// build loudly instead of quietly publishing an empty picture.
import { readFileSync, readdirSync, writeFileSync, mkdirSync, existsSync } from 'node:fs';
import { join, dirname, relative, basename } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const docsSite = dirname(here);
const repoRoot = dirname(docsSite);
const outDir = join(docsSite, 'src', 'generated');

const read = (p) => readFileSync(join(repoRoot, p), 'utf8');
const rel = (p) => relative(repoRoot, p).split('\\').join('/');

// Code-unit ordering, never localeCompare: the output is a committed artifact compared
// byte-for-byte by CI, and ICU collation can differ between a dev box and the runner.
const byKey = (key) => (a, b) => (key(a) < key(b) ? -1 : key(a) > key(b) ? 1 : 0);

// ── A tolerant reader for Bicep literals ──────────────────────────────────────
// Handles: 'strings', numbers, true/false/null, [arrays], {objects} (unquoted or
// 'quoted' keys, newline- or comma-separated), // and /* */ comments. Anything else
// (ternaries, function calls, interpolation) comes back as { expr: '<source text>' }.
class BicepReader {
  constructor(text, pos = 0) {
    this.s = text;
    this.i = pos;
  }

  skip() {
    for (;;) {
      const c = this.s[this.i];
      if (c === undefined) return;
      if (c === ' ' || c === '\t' || c === '\r' || c === '\n') {
        this.i++;
      } else if (c === '/' && this.s[this.i + 1] === '/') {
        while (this.i < this.s.length && this.s[this.i] !== '\n') this.i++;
      } else if (c === '/' && this.s[this.i + 1] === '*') {
        const end = this.s.indexOf('*/', this.i + 2);
        this.i = end === -1 ? this.s.length : end + 2;
      } else {
        return;
      }
    }
  }

  // Reads a raw expression: everything up to the end of the logical value. Used as the
  // fallback so an unreadable value never derails the surrounding literal.
  readExpression() {
    const start = this.i;
    let depth = 0;
    while (this.i < this.s.length) {
      const c = this.s[this.i];
      if (c === "'") {
        this.readString();
        continue;
      }
      if (c === '(' || c === '[' || c === '{') depth++;
      if (c === ')' || c === ']' || c === '}') {
        if (depth === 0) break;
        depth--;
      }
      if (c === '\n' && depth === 0) break;
      this.i++;
    }
    return { expr: this.s.slice(start, this.i).trim() };
  }

  readString() {
    // Bicep single-quoted string; ''' multi-line strings are not used in any value we read.
    this.i++; // opening quote
    let out = '';
    let interpolated = false;
    while (this.i < this.s.length) {
      const c = this.s[this.i];
      if (c === '\\') {
        const n = this.s[this.i + 1];
        out += n === 'n' ? '\n' : n === 't' ? '\t' : n;
        this.i += 2;
        continue;
      }
      if (c === '$' && this.s[this.i + 1] === '{') {
        interpolated = true;
        const end = this.s.indexOf('}', this.i);
        out += this.s.slice(this.i, end + 1);
        this.i = end + 1;
        continue;
      }
      if (c === "'") {
        this.i++;
        return interpolated ? { expr: out } : out;
      }
      out += c;
      this.i++;
    }
    return out;
  }

  readValue() {
    this.skip();
    const c = this.s[this.i];
    if (c === undefined) return null;
    if (c === "'") {
      const v = this.readString();
      // A bare string can still be the head of an expression ('a' == b ? …); peek.
      const save = this.i;
      this.skip();
      const next = this.s[this.i];
      if (next && '?=!+<>'.includes(next)) {
        this.i = save;
        return this.readExpression();
      }
      this.i = save;
      return v;
    }
    if (c === '[') return this.readArray();
    if (c === '{') return this.readObject();
    if (/[-0-9]/.test(c)) {
      const m = /^-?\d+(\.\d+)?/.exec(this.s.slice(this.i));
      // A lone `-` (unary minus on an expression) is not a number literal.
      if (!m) return this.readExpression();
      const save = this.i;
      this.i += m[0].length;
      const after = this.s[this.i];
      if (after && /[a-zA-Z_(]/.test(after)) {
        this.i = save;
        return this.readExpression();
      }
      return Number(m[0]);
    }
    const word = /^(true|false|null)\b/.exec(this.s.slice(this.i));
    if (word) {
      const save = this.i;
      this.i += word[0].length;
      this.skip();
      if (this.s[this.i] === '?' || this.s[this.i] === '=') {
        this.i = save;
        return this.readExpression();
      }
      return word[0] === 'true' ? true : word[0] === 'false' ? false : null;
    }
    return this.readExpression();
  }

  readArray() {
    const open = this.i;
    this.i++; // [
    this.skip();
    // A comprehension — `[for x in y: …]` — is an expression, not a literal. Rewind to
    // the bracket itself (not to open+1, which lands on whitespace when the `for` sits on
    // the next line and would send readBracketedExpression running to EOF).
    if (/^for\b/.test(this.s.slice(this.i))) {
      this.i = open;
      return this.readBracketedExpression('[', ']');
    }
    const out = [];
    for (;;) {
      this.skip();
      if (this.s[this.i] === ']') {
        this.i++;
        return out;
      }
      if (this.s[this.i] === ',') {
        this.i++;
        continue;
      }
      if (this.i >= this.s.length) return out;
      out.push(this.readValue());
    }
  }

  readObject() {
    this.i++; // {
    const out = {};
    for (;;) {
      this.skip();
      if (this.s[this.i] === '}') {
        this.i++;
        return out;
      }
      if (this.s[this.i] === ',') {
        this.i++;
        continue;
      }
      if (this.i >= this.s.length) return out;
      let key;
      if (this.s[this.i] === "'") {
        key = this.readString();
        if (typeof key !== 'string') key = key.expr;
      } else {
        const m = /^[A-Za-z_][A-Za-z0-9_]*/.exec(this.s.slice(this.i));
        if (!m) {
          // Not a key — bail out of the object rather than looping forever.
          this.readExpression();
          continue;
        }
        key = m[0];
        this.i += m[0].length;
      }
      this.skip();
      if (this.s[this.i] === ':') this.i++;
      out[key] = this.readValue();
    }
  }

  readBracketedExpression(open, close) {
    const start = this.i;
    let depth = 0;
    while (this.i < this.s.length) {
      const c = this.s[this.i];
      if (c === "'") {
        this.readString();
        continue;
      }
      if (c === open) depth++;
      if (c === close) {
        depth--;
        this.i++;
        if (depth === 0) break;
        continue;
      }
      this.i++;
    }
    return { expr: this.s.slice(start, this.i).trim() };
  }
}

/**
 * Default value of `param <name> …= <literal>` in a Bicep file. The type is matched
 * loosely (`array`, `string[]`, a user-defined type name) so a type tidy-up in the
 * template cannot silently turn a param into "absent" — which would render an EMPTY
 * diagram that the CI freshness check would then bless as up to date.
 */
function bicepParam(text, name) {
  const declared = new RegExp(`^param\\s+${name}\\b`, 'm').test(text);
  const re = new RegExp(`^param\\s+${name}\\s+[A-Za-z_][A-Za-z0-9_.]*(\\[\\])?\\s*=\\s*`, 'm');
  const m = re.exec(text);
  if (!m) {
    if (declared) {
      throw new Error(
        `generate-architecture: param '${name}' is declared but its default could not be read. ` +
          `The diagram is derived from it — teach scripts/generate-architecture.mjs the new shape.`,
      );
    }
    return undefined;
  }
  return new BicepReader(text, m.index + m[0].length).readValue();
}

/**
 * Assert a param that the renderer treats as a list really came back as one. An
 * expression default (a ternary, a `union(...)`, a comprehension) reads as `{ expr }`,
 * and quietly mapping over that would crash the build with a TypeError naming nothing.
 */
function literalArray(value, name) {
  if (value === undefined) return [];
  if (!Array.isArray(value)) {
    throw new Error(
      `generate-architecture: param '${name}' is not an array literal ` +
        `(read as ${JSON.stringify(value).slice(0, 120)}). The diagram is derived from it — ` +
        `teach scripts/generate-architecture.mjs how to read the new shape.`,
    );
  }
  return value;
}

/** Value of `var <name> = <literal>`. */
function bicepVar(text, name) {
  const re = new RegExp(`^var\\s+${name}\\s*=\\s*`, 'm');
  const m = re.exec(text);
  if (!m) return undefined;
  return new BicepReader(text, m.index + m[0].length).readValue();
}

/** The text inside a module's `if ( … )`, counting nested parens. Null when there is none. */
function balancedCondition(tail) {
  const at = /\bif\s*\(/.exec(tail);
  if (!at) return null;
  let depth = 1;
  let i = at.index + at[0].length;
  const start = i;
  for (; i < tail.length && depth > 0; i++) {
    if (tail[i] === '(') depth++;
    else if (tail[i] === ')') depth--;
  }
  return depth === 0 ? tail.slice(start, i - 1).trim() : null;
}

/** Every `module <symbol> '<path>' = …` in a file, with its body text. */
function bicepModules(text, file) {
  const out = [];
  const re = /^module\s+([A-Za-z_][A-Za-z0-9_]*)\s+'([^']+)'\s*=/gm;
  let m;
  while ((m = re.exec(text)) !== null) {
    const [, symbol, path] = m;
    // Everything between `=` and the body's opening brace carries the `if (…)` /
    // `[for … :` decoration, which may sit on the following line.
    const braceAt = text.indexOf('{', m.index + m[0].length);
    const tail = text.slice(m.index + m[0].length, braceAt);
    const reader = new BicepReader(text, braceAt);
    reader.readBracketedExpression('{', '}');
    const body = text.slice(braceAt, reader.i);
    // `if (…)` with balanced parens: `if (!empty(x))` must not truncate to `!empty(x`.
    const condition = balancedCondition(tail);
    const loop = /\[\s*for\s+([^:]+):/.exec(tail);
    out.push({
      symbol,
      file,
      template: path,
      conditional: condition,
      forEach: loop ? loop[1].trim() : null,
      body,
    });
  }
  return out;
}

/** Which other module symbols a module body reads outputs from, plus explicit dependsOn. */
function moduleEdges(body, symbols, self) {
  const deps = new Set();
  for (const m of body.matchAll(/([A-Za-z_][A-Za-z0-9_]*)\s*(?:\[[^\]]*\])?\s*\.\??outputs\./g)) {
    if (symbols.has(m[1])) deps.add(m[1]);
  }
  const explicit = /dependsOn:\s*\[([^\]]*)\]/.exec(body);
  if (explicit) {
    for (const m of explicit[1].matchAll(/[A-Za-z_][A-Za-z0-9_]*/g)) {
      if (symbols.has(m[0])) deps.add(m[0]);
    }
  }
  // A module's own symbol shows up in `var` blocks its params interpolate; that is not
  // a dependency on itself.
  deps.delete(self);
  return [...deps].sort();
}

function fgRole(body) {
  const m = /'fg-role':\s*'([^']+)'/.exec(body);
  return m ? m[1] : null;
}

// ── infra/ ────────────────────────────────────────────────────────────────────
const mainPath = 'infra/main.bicep';
const main = read(mainPath);
const gatewayBicep = read('infra/modules/ai-gateway.bicep');

// Directories the generator cannot do without. A rename would otherwise surface as a raw
// ENOENT out of `prebuild`, which now also gates `npm run dev`.
function requireDir(...segments) {
  const dir = join(repoRoot, ...segments);
  if (!existsSync(dir)) {
    throw new Error(
      `generate-architecture: expected ${segments.join('/')} to exist. If it moved, update ` +
        'docs-site/scripts/generate-architecture.mjs; the architecture diagram is derived from it.',
    );
  }
  return dir;
}

const moduleFiles = readdirSync(requireDir('infra', 'modules'))
  .filter((f) => f.endsWith('.bicep'))
  .sort();

const sources = [mainPath, ...moduleFiles.map((f) => `infra/modules/${f}`)];

function collectModules(text, file) {
  const mods = bicepModules(text, file);
  const symbols = new Set(mods.map((m) => m.symbol));
  return mods.map((m) => ({
    symbol: m.symbol,
    file: m.file,
    template: m.template,
    component: basename(m.template, '.bicep'),
    role: fgRole(m.body),
    conditional: m.conditional,
    forEach: m.forEach,
    dependsOn: moduleEdges(m.body, symbols, m.symbol),
  }));
}

const rootModules = collectModules(main, mainPath);
const controlPlaneModules = collectModules(
  read('infra/modules/control-plane.bicep'),
  'infra/modules/control-plane.bicep',
);

const foundryRegions = literalArray(bicepParam(main, 'foundryRegions'), 'foundryRegions');
const pooled = literalArray(bicepParam(main, 'pooledModelDeployments'), 'pooledModelDeployments');
const primaryOnly = literalArray(
  bicepParam(main, 'primaryOnlyModelDeployments'),
  'primaryOnlyModelDeployments',
);
const quotaTiersRaw = literalArray(bicepParam(main, 'quotaTiers'), 'quotaTiers');
const aliasMapRaw = bicepParam(main, 'productModelAliases') ?? {};
const aliasMap = Array.isArray(aliasMapRaw) || aliasMapRaw.expr ? {} : aliasMapRaw;

if (!foundryRegions.length || !quotaTiersRaw.length) {
  throw new Error(
    'generate-architecture: read zero regions or zero quota tiers out of infra/main.bicep. ' +
      'That would publish an empty diagram — refusing to write the model.',
  );
}

const modelName = (d) => `${d.model}${d.version ? ` (${d.version})` : ''}`;

const regions = foundryRegions.map((region, i) => ({
  name: region,
  role: i === 0 ? 'primary' : 'pool-member',
  poolPriority: i === 0 ? 1 : 2,
  deployments: (i === 0 ? [...pooled, ...primaryOnly] : pooled).map((d) => ({
    name: d.name,
    format: d.format,
    model: modelName(d),
    sku: d.sku,
    capacity: d.capacity,
  })),
}));

const quotaTiers = quotaTiersRaw.map((tier) => ({
  name: tier.name,
  displayName: tier.displayName,
  description: tier.description ?? null,
  monthlyTokenQuota: tier.monthlyTokenQuota,
  tpm: tier.tpm,
  aliases: Object.entries(aliasMap[tier.name] ?? {})
    .map(([alias, v]) => ({ alias, deployment: v.deployment, pool: v.pool, provider: v.provider }))
    .sort(byKey((a) => a.alias)),
}));

// The paths and APIM api names come out of ai-gateway.bicep; `label`, `client` and
// `header` are editorial — which CLI a front door is for, and which header that CLI
// already sends — and live here so the two diagrams say the same thing about them.
// (Ground truth for the headers: CLAUDE.md §Architecture ground truths.)
const frontDoors = [
  {
    provider: 'anthropic',
    path: bicepVar(gatewayBicep, 'anthropicApiPath'),
    api: bicepVar(gatewayBicep, 'anthropicApiName'),
    label: 'Anthropic Messages API',
    client: 'Claude Code',
    header: 'x-api-key',
  },
  {
    provider: 'openai',
    path: bicepVar(gatewayBicep, 'openaiApiPath'),
    api: bicepVar(gatewayBicep, 'openaiApiName'),
    label: 'OpenAI Responses / Chat Completions',
    client: 'Codex CLI',
    header: 'api-key',
  },
];

// ── src/ ──────────────────────────────────────────────────────────────────────
const srcDir = join(repoRoot, 'src');
const projectDirs = readdirSync(srcDir, { withFileTypes: true })
  .filter((e) => e.isDirectory())
  .map((e) => e.name)
  .sort();

const projects = [];
for (const dir of projectDirs) {
  const csproj = join(srcDir, dir, `${dir}.csproj`);
  if (!existsSync(csproj)) continue;
  const text = readFileSync(csproj, 'utf8');
  const references = [...text.matchAll(/<ProjectReference\s+Include="[^"]*?([A-Za-z.]+)\.csproj"/g)]
    .map((m) => m[1])
    .filter((v, i, a) => a.indexOf(v) === i)
    .sort();
  projects.push({ name: dir, path: rel(csproj), references, isTest: /\.Tests\./.test(dir) });
  sources.push(rel(csproj));
}

/** Every .cs file under a directory. */
function csFiles(dir) {
  const out = [];
  const walk = (d) => {
    for (const e of readdirSync(d, { withFileTypes: true })) {
      const p = join(d, e.name);
      if (e.isDirectory()) {
        if (e.name === 'bin' || e.name === 'obj') continue;
        walk(p);
      } else if (e.name.endsWith('.cs')) {
        out.push(p);
      }
    }
  };
  walk(dir);
  return out.sort();
}

// Timer-triggered Functions and their real NCRONTAB schedules.
const CRON_PROSE = {
  '0 0 2 * * *': 'Daily at 02:00 UTC',
  '0 1 0 * * *': 'Daily at 00:01 UTC',
  '0 */15 * * * *': 'Every 15 minutes',
};

const functions = [];
for (const file of csFiles(requireDir('src', 'FoundryGate.Functions'))) {
  const text = readFileSync(file, 'utf8');
  const timer = /\[TimerTrigger\("([^"]+)"/.exec(text);
  if (!timer) continue;
  const name = /\[Function\((?:nameof\(([A-Za-z0-9_]+)\)|"([^"]+)")\)\]/.exec(text);
  functions.push({
    name: name ? (name[1] ?? name[2]) : basename(file, '.cs'),
    file: rel(file),
    cron: timer[1],
    schedule: CRON_PROSE[timer[1]] ?? `NCRONTAB ${timer[1]}`,
  });
  sources.push(rel(file));
}
functions.sort(byKey((f) => f.name));

// API controller route prefixes: the [Route] template declared on the base class,
// resolved per concrete controller by substituting [controller].
const routeTemplates = new Set();
const controllers = new Set();
for (const file of csFiles(requireDir('src', 'FoundryGate.Api'))) {
  const text = readFileSync(file, 'utf8');
  for (const m of text.matchAll(/\[Route\("([^"]+)"\)\]/g)) routeTemplates.add(m[1]);
  for (const m of text.matchAll(/\bclass\s+([A-Za-z0-9_]+)Controller\b/g)) {
    if (m[1] !== 'Api') controllers.add(m[1]);
  }
}
const apiRouteTemplates = [...routeTemplates].sort();
const apiRoutes = apiRouteTemplates
  .flatMap((t) =>
    t.includes('[controller]')
      ? [...controllers].map((c) => t.replace('[controller]', c.toLowerCase()))
      : [t],
  )
  .sort();

// ── model ─────────────────────────────────────────────────────────────────────
const model = {
  // Deliberately no timestamp or commit sha: this file is committed, and a value that
  // changed on every run would make the CI freshness check unfixable. Provenance is
  // stamped onto the page at build time instead (see ArchitectureDiagram.astro).
  note: 'GENERATED FILE — do not edit. Run docs-site/scripts/generate-architecture.mjs.',
  generator: 'docs-site/scripts/generate-architecture.mjs',
  sources: [...new Set(sources)].sort(),
  infra: {
    entrypoint: mainPath,
    scope: /^targetScope\s*=\s*'([^']+)'/m.exec(main)?.[1] ?? null,
    resourceGroup: /name:\s*'(rg-[^']+)'/.exec(main)?.[1] ?? null,
    modules: rootModules,
    controlPlaneModules,
    regions,
    quotaTiers,
    modelDeployments: { pooled, primaryOnly },
    frontDoors,
    pools: [
      {
        name: bicepVar(gatewayBicep, 'anthropicPoolName'),
        kind: 'pool',
        members: regions.map((r) => ({ region: r.name, priority: r.poolPriority })),
      },
      {
      // The OpenAI backend is named for the primary account in ai-gateway.bicep
      // (`foundry-openai-${foundryAccounts[0].name}`); the account name is only known at
      // deploy time, so the label carries the shape rather than a fake name.
      name: 'foundry-openai (primary account)',
      kind: 'single',
      members: [{ region: regions[0].name, priority: 1 }],
    },
    ],
  },
  code: { projects, functions, apiRouteTemplates, apiRoutes },
};

// ── Mermaid artifact (machine-readable twin of the rendered SVG) ──────────────
const id = (s) => s.replace(/[^A-Za-z0-9_]/g, '_');
const mermaid = [
  '%% GENERATED FILE — do not edit. Run docs-site/scripts/generate-architecture.mjs.',
  '%% Bicep module graph of infra/main.bicep: an edge means the target consumes the',
  '%% source module\'s outputs (or names it in dependsOn).',
  'graph TD',
];
for (const m of rootModules) {
  const label = m.forEach ? `${m.symbol}<br/>(per region)` : m.symbol;
  const shape = m.conditional ? `${id(m.symbol)}(["${label}"])` : `${id(m.symbol)}["${label}"]`;
  mermaid.push(`  ${shape}`);
}
for (const m of rootModules) {
  for (const dep of m.dependsOn) mermaid.push(`  ${id(dep)} --> ${id(m.symbol)}`);
}
mermaid.push('');
mermaid.push('%% Control plane sub-modules (infra/modules/control-plane.bicep)');
for (const m of controlPlaneModules) mermaid.push(`  cp_${id(m.symbol)}["${m.symbol}"]`);
for (const m of controlPlaneModules) {
  for (const dep of m.dependsOn) mermaid.push(`  cp_${id(dep)} --> cp_${id(m.symbol)}`);
}

mkdirSync(outDir, { recursive: true });
writeFileSync(join(outDir, 'architecture.json'), `${JSON.stringify(model, null, 2)}\n`, 'utf8');
writeFileSync(join(outDir, 'architecture.mmd'), `${mermaid.join('\n')}\n`, 'utf8');

console.log(
  `architecture: ${rootModules.length} root modules, ${regions.length} regions, ` +
    `${quotaTiers.length} tiers, ${projects.length} projects, ${functions.length} timers ` +
    `-> ${rel(join(outDir, 'architecture.json'))}`,
);
