# nuget-audit

**The single enforcement gate for NuGet supply chain security.**

`nuget-audit` is a .NET global tool that audits every package in your solution against
nuget.org's cryptographically backed publisher-verification signal — not the free-text
`Authors` field that anyone can spoof. It is the CI gate, the VS pre-build check, and
the pre-restore preview layer for a zero-trust NuGet workflow.

- **Website & full docs:** [jzuras.github.io/nuget-audit](https://jzuras.github.io/nuget-audit)
- **NuGet.org package:** [nuget.org/packages/nuget-audit](https://www.nuget.org/packages/nuget-audit)

---

## Install and run

```bash
dotnet tool install -g nuget-audit   # requires .NET 8 SDK or later
nuget-audit init --path .            # create TrustConfig.json + Directory.Build.targets
nuget-audit audit --path .           # run the audit
nuget-audit guide                    # full workflow walkthrough in the terminal
```

CI gate (add after `dotnet restore`):

```bash
nuget-audit audit --check --path .   # exits 1 if any package needs review
```

---

## What it checks

| Signal | Covered |
|--------|---------|
| Publisher identity (prefix reservation, not self-reported `Authors`) | Yes |
| Recently published versions (configurable window, default 14 days) | Yes |
| Known CVEs and deprecated packages | Yes |
| Executable content in packages (MSBuild targets, analyzers, tools) | Yes |
| Lock file, `RestoreLockedMode`, Package Source Mapping configured | Yes — advisory |

Full trust model, command reference, and workflow guides are on the
[website](https://jzuras.github.io/nuget-audit).

---

## Build from source

No build required to use the tool — install directly from NuGet.org as shown above.
This repository is published for transparency.

**Requirements:** .NET 8 SDK or later.

```bash
git clone https://github.com/jzuras/nuget-audit.git
cd nuget-audit
dotnet build nuget-audit.slnx
dotnet test --project tests/NugetAudit.Core.Tests/NugetAudit.Core.Tests.csproj
dotnet test --project tests/NugetAudit.Cli.Tests/NugetAudit.Cli.Tests.csproj
```

### Project structure

```
src/
  NugetAudit.Core/      — business logic, models, interfaces
  NugetAudit.Cli/       — dotnet global tool entry point (PackAsTool=true)
tests/
  NugetAudit.Core.Tests/
  NugetAudit.Cli.Tests/
```

---

## License

MIT — Copyright (c) 2026 James Zuras
