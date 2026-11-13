# Snyk scanning

Snyk is run by hand (`snyk test --all-projects`, `snyk code test`) and is not part of CI.
The first full scan (2026-11-13, `main`) found no dependency vulnerabilities and 13 low
("note") Snyk Code findings. Their handling:

| Finding | Handling |
| --- | --- |
| `csharp/PT` in `Program.cs` (`--data-dir` reaches `Directory.CreateDirectory`) | Fixed. `DataDirectory.Resolve` rejects an override with a `..` segment or a NUL character; `Program` prints the error and exits with code 2. Absolute and plain relative paths still work. |
| `python/PT` in `.github/pages/render_downloads.py` | Fixed. `checked_file` refuses `..` segments and anything that is not an existing regular file before the script opens it. |
| `csharp/NoHardcodedCredentials` in `SavedFilterStore.cs` and `GoogleFactory.cs` | False positives, confirmed by the maintainer: the constants are a settings key (`filters.tasks`) and credential field names (`clientSecret`), not secret values. Not changed in code; recorded below. |
| `csharp/NoHardcodedCredentials/test` in seven test projects | False positives: made-up fixture values. `tests/**` is excluded from Snyk Code in `.snyk`. |

## The `.snyk` file

`.snyk` excludes `tests/**` and `.claude/worktrees/**` (other checkouts that sit inside the
tree and would be scanned again) from Snyk Code. It does not ignore anything in `src/`:
the Snyk CLI cannot ignore a Snyk Code finding from a policy file. If the two
`NoHardcodedCredentials` findings in `src/` show up again, ignore them in the Snyk web UI
with the reason above.

## Known limit of the path-traversal fixes

Snyk Code does not recognise `DataDirectory.IsAcceptable` or `checked_file` as sanitizers, so
a re-scan after the fix still reports `csharp/PT` (`Program.cs`) and `python/PT`
(`render_downloads.py`) as low-severity notes. The checks do work (they are unit tested);
the flow is still a command-line path going to a file API, which is intended for both tools.
Treat those notes as accepted risk and ignore them in the Snyk web UI if they are noisy.
A re-scan also lists a third `NoHardcodedCredentials` hit at `GoogleFactory.cs:100`. It is
the lookup of the build-time OAuth client id and secret from assembly metadata (see
[oauth-client-configuration.md](oauth-client-configuration.md)); the values come from release
secrets, not from source. It needs the same web-UI ignore as the other two.
