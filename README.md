# nuget-audit

**The single enforcement gate for NuGet supply chain security.**

The `Authors` field in every NuGet package is free text. Anyone can publish a package
claiming `Authors = "Microsoft"` — and neither Visual Studio nor the .NET CLI will warn
you. `nuget-audit` uses nuget.org's **prefix reservation and verified-owner** signal,
not self-reported metadata, to evaluate whether a publisher is who they claim to be.

It is the enforcement layer for the zero-trust NuGet workflow — tying together what five
separate tools cannot enforce alone:

| What you might already have | What it leaves uncovered |
|-----------------------------|--------------------------|
| `dotnet list package --vulnerable` | Publisher identity — CVEs only |
| Package Source Mapping | Trust evaluation — feed isolation only |
| NuGet lock files + `RestoreLockedMode` | Trust evaluation — graph pinning only |
| Dependabot | Verified-owner signal; can auto-merge within the attack detection window |
| NuGet package signing | Requires per-publisher key enrollment; no detection-window awareness |

`nuget-audit audit --check` in CI is the gate — and it warns when any of these controls
are not configured. But the enforcement spans the full workflow: `init` wires up the
infrastructure, the advisory system verifies every control is in place, and the
`preview-*` commands close the gap between a package change and the next audit.

---

## Contents

- [What It Looks Like](#what-it-looks-like)
- [The Detection Window](#the-detection-window)
- [Trust Model](#trust-model)
- [Quick Start](#quick-start)
- [Requirements](#requirements)
- [TrustConfig.json](#trustconfigjson)
- [Commands](#commands)
- [Workflows](#workflows)
- [Output Fields](#output-fields)
- [Known Limitations](#known-limitations)
- [Security Review Best Practices](#security-review-best-practices)
- [Related Tools](#related-tools)
- [Extended Documentation](#extended-documentation)

---

## What It Looks Like

A flagged audit (trust status rows are color-coded in the terminal):

```
Filtering to show only packages needing review... Found 2 package(s) needing review.

Type    PackageId            Version  Owners          Verified  Trust           Depr  Vuln
------  -------------------  -------  --------------  --------  --------------  ----  ----
Direct  Contoso.Http         3.1.0    contoso-oss     No        Untrusted       No    No
Direct  SomeLib              2.0.0    verified-owner  Yes       VersionChanged  No    No
```

A clean project:

```
Filtering to show only packages needing review... Found 0 package(s) needing review.

No packages match the current filter criteria.
```

A clean run exits with code 0. The CI gate is one line:

```bash
nuget-audit audit --check --path .
```

---

## The Detection Window

Most NuGet supply chain attacks are detected within days of a compromised version being
published — by the community, security researchers, or automated scanning. This creates a
defensible window: **a version in the ecosystem for weeks without incident is meaningfully
safer than one published yesterday.**

`nuget-audit` flags any package version published within `recentDaysThreshold` days
(default: 14) regardless of trust status. A verified `Microsoft.*` package published two
days ago carries more risk than one published six months ago — account compromise, insider
threat, and build pipeline compromise do not respect the verified flag.

**This is also why Dependabot requires extra caution.** It can open and auto-merge an
update PR for a newly published version within hours — well inside the detection window,
with no pre-restore review step. The recommended mitigation pattern (disable auto-merge,
minimum PR age, `--check` as a required CI status) is covered in the extended documentation
at [jzuras.github.io/nuget-audit](https://jzuras.github.io/nuget-audit).

---

## Trust Model

Each package is evaluated against a layered trust model:

| Condition | Trust Status | Action needed? |
|-----------|-------------|----------------|
| 404 on nuget.org — private or local feed | `PrivateFeed` | No |
| `verified=true` + owner in trusted owners | `Verified` | No |
| `verified=true` + owner **not** in trusted owners | `VerifiedUnknownOwner` | Yes |
| `verified=false` + exact ID+version in trusted packages | `TrustedPackage` | No |
| `verified=false` + ID in trusted packages, version changed | `VersionChanged` | Yes — urgent |
| `verified=false` + not in trusted packages | `Untrusted` | Yes |

Default audit mode shows only the three statuses that require action. A clean project
produces no output.

**What is prefix reservation?** Package owners apply to nuget.org to reserve a prefix
(e.g., `Microsoft.*`). nuget.org verifies ownership before granting it — this cannot be
self-reported by an attacker, unlike the free-text `Authors` field. No other developer-
facing .NET tooling surfaces this signal in an actionable way.

**Why are private feed packages trusted?** Packages that return 404 on nuget.org are
coming from a feed you explicitly configured — an internal registry, Azure Artifacts,
Telerik, etc. Dependency confusion attacks work the opposite way: an attacker publishes
to a *public* feed hoping to shadow a private one. A package that only exists on your
private feed cannot be shadowed that way.

**Why is version pinning for unverified packages the right default?** The `trustedPackages`
list applies to unverified publishers — exactly the packages that most warrant per-version
scrutiny. The cost is proportional to the risk. Verified publishers in `trustedOwners`
require no per-version action.

---

## Quick Start

```bash
# Install
dotnet tool install -g nuget-audit

# Update an existing installation
dotnet tool update -g nuget-audit

# Create TrustConfig.json and Directory.Build.targets in your solution directory
nuget-audit init --path .

# Audit your solution
nuget-audit audit --path MySolution.slnx

# Show the full workflow walkthrough
nuget-audit guide
```

---

## Requirements

- **.NET 8 SDK or later** (the projects you audit can target any framework)
- Internet access to query the NuGet API
- Runs on **Windows**, **macOS**, and **Linux**

---

## TrustConfig.json

`TrustConfig.json` is the only file you edit to configure trust. It lives at the solution
root and should be checked into source control. Run `nuget-audit init --path <dir>` to
create it along with `Directory.Build.targets` (the VS pre-build enforcement target) with defaults:

```json
{
  "trustedOwners": [
    "Microsoft",
    "dotnetfoundation",
    "aspnet"
  ],
  "trustedPackages": [
    { "id": "EXAMPLE.PACKAGE", "version": "1.0.0" }
  ],
  "recentDaysThreshold": 14
}
```

**`trustedOwners`** — nuget.org account names whose prefix-verified packages are trusted
at any version. Use the exact account name shown on the package's nuget.org page under
"Owners" — not the display name. Add your organization's nuget.org account names here for
packages you have broad confidence in across the entire catalog.

**`trustedPackages`** — packages you have manually reviewed, pinned to a specific version.
A version change re-flags the package for review. Use this for unverified publishers and
for verified publishers where you want explicit review of each update.

**`recentDaysThreshold`** — versions published within this many days are flagged as higher
supply chain risk regardless of trust status. Default: 14. A *Recently published* section
appears at the bottom of audit output listing any packages within the window.

Edit directly, or use the CLI:

```bash
nuget-audit trust-owner <owner> --path .
nuget-audit trust-package <id> <version> --path .
```

**When to use `trustedOwners` vs `trustedPackages`:** Add an owner to `trustedOwners` for
publishers you trust across their entire catalog — well-known organizations whose packages
you use broadly. Use `trustedPackages` for everything else, including verified packages
where you want to review each version change explicitly. This is the more conservative,
zero-trust default.

---

## Commands

Running `nuget-audit` with no arguments prints help. Use `nuget-audit <command> --help` for command-specific options.

### `nuget-audit audit`

Run a NuGet package security audit.

```bash
nuget-audit audit --path <path> [options]
```

| Option | Default | Description |
|--------|---------|-------------|
| `--path` | `.` | Path to `.csproj`, `.sln`, `.slnx`, or directory |
| `--all` | — | Show all packages regardless of trust status |
| `--verbose` | — | Disable per-owner transitive condensing; show all recently-published entries |
| `--package-list` | — | Output packages as copy-paste entries for TrustConfig.json |
| `--include-existing` | — | With `--package-list`: show all packages grouped by trust status |
| `--check` | — | Counts only, exit code 1 if any package issues or security advisories — for CI/CD and VS pre-build |
| `--format` | `table` | Output format: `table`, `csv`, or `json` |
| `--output` | — | Write output to file; requires `--format csv` or `--format json` |
| `--trust-config` | CWD | Path to TrustConfig.json |

When `--path` is a directory, auto-discovers the solution or project file:
`.slnx` → `.sln` → `.csproj`. Multiple files of the same type produce an error.

`--check` output:

```
Packages needing trust review: 2
Deprecated packages:           0
Packages with vulnerabilities: 1

Security advisory: No packages.lock.json found — lock file enforcement is missing.

Run nuget-audit audit for full details.
```

A clean run exits with code 0: all counts at zero and no security advisories.
Exit code 1 is returned for any package issues or any of the following advisory conditions:
missing `packages.lock.json`, `RestoreLockedMode` not set, no `Directory.Build.targets`
with a `nuget-audit` invocation, or Package Source Mapping not configured.

---

### When to Use Which Preview Mode

| Situation | Command |
|-----------|---------|
| Before the first restore (newly cloned) | `preview-restore` |
| After editing package references | `preview-restore` |
| Adding a new package | `preview-update <id>` |
| Updating an existing package | `preview-update <id> --version <ver>` |
| Reviewing what is currently restored | `audit` |

### `nuget-audit preview-update`

Preview transitive graph changes before adding or updating a package. Runs an exact
`dotnet restore` into a temp directory to resolve the graph — nothing lands in your
real NuGet cache.

```bash
nuget-audit preview-update <package-id> [--version <ver>] --path <path>
```

| Option | Default | Description |
|--------|---------|-------------|
| `--version` | latest | Target version |
| `--path` | `.` | Path to solution, project, or directory |
| `--trust-config` | CWD | Path to TrustConfig.json |
| `--fast` | off | Use approximate BFS resolver instead of `dotnet restore` (faster, less accurate) |

Output shows ADDED, CHANGED, and REMOVED sections relative to the current graph. Each new
or changed package is evaluated against the trust model.

**Private-feed packages:** `--version` is required (latest cannot be auto-resolved from a
private feed). The command falls back to the approximate BFS resolver with a warning —
exact restore is not supported for private feeds.

**Supply chain transition warning:** If the currently installed version of a package came
exclusively from a private feed and the target version is now on nuget.org, `preview-update`
emits a warning before the normal output. A red alert (`⛔`) fires when the publisher has no
nuget.org prefix reservation or is not in your trusted owners list — indicating a potential
dependency confusion attack. A yellow notice (`⚠`) fires when the publisher is verified and
trusted, indicating a likely legitimate vendor move to public distribution that is still worth
confirming before applying.

**Package Source Mapping conflict:** If your NuGet.config has Package Source Mapping and the
target version is available on nuget.org but PSM restricts that package to a private feed,
`preview-update` emits a specific, actionable error explaining which feed is blocking the
search and how to add a nuget.org mapping to resolve it.

**Stale lock file:** When `RestoreLockedMode=true` is in effect and the lock file is out
of date, `preview-update` falls back to reading `packages.lock.json` directly so you can
preview the change before running `dotnet restore --force-evaluate`.

### `nuget-audit preview-restore`

Resolve the full transitive graph by running `dotnet restore` into a temp directory.
Use before the first restore on a newly cloned project, or any time you have edited
package references and want to see the full graph before running the real restore.
Package files land in a temp directory that is deleted after the preview; the real
`obj/` directory is never touched.

```bash
nuget-audit preview-restore --path <path> [--framework <tfm>]
```

| Option | Default | Description |
|--------|---------|-------------|
| `--path` | `.` | Path to solution, project, or directory |
| `--framework` | auto | TFM for dependency resolution (only used with `--fast`) |
| `--trust-config` | CWD | Path to TrustConfig.json |
| `--fast` | off | Use approximate BFS resolver instead of `dotnet restore` (faster, less accurate) |

**Important:** Clone with the git CLI only — Visual Studio restores automatically on
solution open, closing the pre-restore review window.

### `nuget-audit guide`

Print a concise workflow walkthrough in the terminal — new-project setup and ongoing-use steps in order. Useful when you don't have a browser handy.

```bash
nuget-audit guide
```

### Other Commands

```bash
nuget-audit init --path <dir> [--force]          # Create TrustConfig.json + Directory.Build.targets
nuget-audit trust-owner <owner> --path .         # Add owner to trusted list
nuget-audit trust-package <id> <ver> --path .    # Pin a package/version
nuget-audit explain <topic>                      # In-depth explanation of a security concept (lock-files, psm, exec-content)
```

---

## Workflows

Run `nuget-audit guide` for the full interactive walkthrough. Extended workflow
documentation — including the zero-trust team workflow, solo-dev safe-pull workflow,
Dependabot integration pattern, and policy enforcement guide — is at
[jzuras.github.io/nuget-audit](https://jzuras.github.io/nuget-audit).

### New Project Setup

```bash
# 1. Create the project (SDK templates restore automatically — that is fine)
dotnet new blazor

# 2. Create TrustConfig.json and audit the restored graph
nuget-audit init --path .
nuget-audit audit --path .

# 3. For each flagged package: review and add to TrustConfig.json, then re-run to confirm
nuget-audit trust-owner <owner> --path .         # for verified publishers you trust broadly
nuget-audit trust-package <id> <ver> --path .    # for everything else
nuget-audit audit --path .

# 4. Lock the graph — add both properties to Directory.Build.props, then run --force-evaluate:
#    <PropertyGroup>
#      <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>  <!-- generates lock files -->
#      <RestoreLockedMode>true</RestoreLockedMode>                      <!-- enforces them -->
#    </PropertyGroup>
dotnet restore --force-evaluate

# 5. Commit everything
git add packages.lock.json Directory.Build.props Directory.Build.targets TrustConfig.json
git add .gitignore                               # add .nuget-audit-ok to .gitignore first
git commit -m "Lock NuGet graph and add audit enforcement"
```

The tool warns — and `--check` exits 1 — if it detects a lock file without
`RestoreLockedMode=true`, or if `RestoreLockedMode=true` is set but no
`Directory.Build.targets` with a `nuget-audit` invocation was found. Setup is
self-checking.

### Adding or Updating a Package

With `RestoreLockedMode=true` in place, `dotnet restore` fails if the lock file is out of
date, requiring a deliberate `dotnet restore --force-evaluate`. That deliberate act is the
entry point for this workflow.

```bash
nuget-audit preview-update SomePackage --version 2.0.0 --path .
# Review output — stop and investigate if anything is unexpected
# Edit .csproj or Directory.Packages.props by hand
# Do NOT use VS NuGet Package Manager — it restores immediately, bypassing preview
dotnet restore --force-evaluate
nuget-audit audit --path .
# Update TrustConfig.json for any newly flagged packages, re-run to confirm clean
git add packages.lock.json && git commit -m "Update SomePackage to 2.0.0"
```

### Cloning an Existing Project

**Repo has a lock file:** Clone and open normally — restore is constrained to exactly the
locked versions. Run `nuget-audit audit` after cloning to verify the current state before
building.

**Repo has no lock file:** Clone with the git CLI. Do not open in VS yet.

```bash
nuget-audit preview-restore --path .   # Review before anything downloads
# Proceed ONLY if the preview looks safe
dotnet restore
nuget-audit audit --path .
# Add to TrustConfig.json, re-run to confirm clean, then lock the graph (see above)
```

### CI/CD Integration

Two gates together enforce the full workflow:

| Gate | What it catches |
|------|----------------|
| `dotnet restore` with `RestoreLockedMode=true` | Package reference edited without updating the lock file |
| `nuget-audit audit --check` | Packages needing review, deprecated packages, known vulnerabilities, and setup advisory conditions |

```bash
dotnet restore                          # fails if lock file is out of date
nuget-audit audit --check --path .      # fails if any packages need review
```

### Visual Studio Pre-Build Enforcement

`nuget-audit init` creates `Directory.Build.targets` at the solution root, which runs
`--check` automatically before each build whenever the lock file changes. The tool warns
if `RestoreLockedMode=true` is set but this target is not present — run `nuget-audit init`
to create it.

Add `.nuget-audit-ok` to `.gitignore` — it is machine-local state created by the target.
CI does not use the sentinel; it always audits unconditionally.

---

## Output Fields

| Field | Description |
|-------|-------------|
| **Type** | `Direct` or `Trans` (transitive) |
| **PackageId** | Package name |
| **Version** | Resolved version |
| **Owners** | nuget.org account name(s) |
| **Verified** | `Yes` = nuget.org prefix reserved; `No` = unverified; `N/A` = private feed |
| **Trust** | Trust status — see Trust Model above |
| **Depr** | Deprecated? |
| **Vuln** | Has known vulnerabilities? |
| **Exec** | Executable content in local NuGet cache: `MSBld` (MSBuild `.props`/`.targets`), `Alyzr` (Roslyn analyzer), `Tools` (executables). `-` = in cache, confirmed clean. `?` = not in cache, could not be inspected. Column omitted in preview mode. |

CSV and JSON output include additional fields not shown in the console table: `Authors`, `LicenseExpression`, `LicenseUrl`, `Published`, and `ProjectUrl`. See the [User Guide](https://jzuras.github.io/nuget-audit/guide) for the full field reference.

---

## Known Limitations

This tool audits **package metadata**. It cannot inspect package code or runtime behavior.

| Threat | Covered? | Notes |
|--------|----------|-------|
| Unknown package from unknown publisher | ✅ Yes | — |
| Package impersonating a trusted author name | ✅ Yes | `verified` + trusted owners catches this |
| Unverified package at an unexpected version | ✅ Yes | `trustedPackages` exact ID+version match |
| Known CVE in any package | ✅ Yes | `Vuln` column |
| Packages introduced before any audit | ⚠️ Partial | Use `preview-restore` before the first restore or after editing package references; VS and some templates restore automatically |
| Changed transitive pulled in by restore | ⚠️ Partial | Use `preview-update` before `--force-evaluate`; lock files prevent silent changes |
| Private-feed packages in preview-update | ⚠️ Partial | Exact restore is not supported for private feeds; falls back to BFS (approximate) with a warning. If a private-feed package now appears on nuget.org, a supply chain transition warning is emitted (red for unverified publisher, yellow for verified). |
| Compromised account publishing a bad new version | ⚠️ Partial | Recently published section flags versions within `recentDaysThreshold` days |
| Ownership transfer followed by malicious release | ⚠️ Partial | Covered by recency check when the malicious version is published within the threshold window. The unmitigated residual is a version that has aged past the threshold and has not yet been flagged as malicious by the community — the two conditions an attacker must simultaneously achieve. Pair with NuGet package signing (`<trustedSigners>`) for ownership-transfer detection independent of the recency window — effective only if the original publisher was using author signatures, which many do not. |
| Executable content in packages | ⚠️ Post-restore only | `Exec` column flags presence; not available in preview mode — run the full audit immediately after restore, before the next build |
| Initial version already malicious | ❌ No | Out of scope for any automated tool — mitigate by checking download counts, project activity, and community reputation before adding any new dependency |

**What `dotnet restore` can execute:** Restore does not run arbitrary package code, but
can run MSBuild targets hooked into the `Restore` target by packages already in the
evaluated graph. A newly downloaded package cannot affect the same restore that fetched it
— but its targets can run on the next restore or build. This is why the recommended
workflow is: preview → restore → audit immediately, before the next build.

---

## Security Review Best Practices

When reviewing flagged packages:

- ✅ Check `Owners` — is this a nuget.org account you recognize?
- ✅ Check the package's nuget.org page `ProjectUrl` — does it point to a legitimate source?
- ✅ Review GitHub stars, download counts, last update
- ✅ Search for security advisories or CVEs
- ✅ For transitive packages, use `dotnet nuget why` to trace the dependency chain
- ✅ Consider whether you can upgrade or replace the direct dependency that pulls it in

---

## Related Tools

Pair `nuget-audit` with:

- **NuGet lock files** (`packages.lock.json` + `RestorePackagesWithLockFile=true` to generate, `RestoreLockedMode=true` to enforce) — pins the resolved graph; `dotnet restore` cannot silently pull in different versions
- **Package Source Mapping** — locks packages to specific feeds, preventing dependency confusion attacks
- **Central Package Management** — centralizes version declarations in `Directory.Packages.props`
- **NuGet package signing** (`<trustedSigners>`) — cryptographic publisher verification; signatures break if a new owner signs with a different key, providing ownership-transfer detection beyond the recency window
- **`dotnet list package --vulnerable`** — severity levels and advisory URLs for known CVEs
- **`dotnet nuget why`** — dependency chains for transitive packages

---

## Extended Documentation

The full zero-trust workflow, solo-dev safe-pull workflow, Dependabot integration pattern,
and policy enforcement guide are at
**[jzuras.github.io/nuget-audit](https://jzuras.github.io/nuget-audit)**.

---

Copyright (©) 2026 James Zuras. Licensed under the MIT License.
