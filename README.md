# feature-ledger

Answers "what did ticket MM-1234 touch, and is all of it ready for QA?" from git alone, across every repo.
No manual notes and no third-party tools: just Git, the Jira REST API and VS Code.

## How it works

1. **Git hooks guarantee the key.** `hooks/commit-msg` rejects commits without a Jira key. On a branch named
   `MM-1234-...` it prefixes the key for you, so following the branch convention is all it takes.
   Commits that really have no ticket must say `[no-ticket]`.
2. **The manifest is computed, not written.** `ledger.cs` scans all repos under `reposRoot` and collects, per ticket:
   - commits whose message contains the key
   - commits brought in by merges whose *source* branch contains the key (GitLab MR merges, with MR links)
   - commits on branches named after the key that are not yet in the default branch (patch-equivalent rebased copies are ignored)
3. **DB scripts are traced through `db-migration-scripts`.** Every touched `DBMigrations/<Service>/<folder>/...` file is
   reported as *in release tag X*, *graduated from dev/ to tag X* (routine file name or the `-- from dev/...` marker),
   *still staged in dev/*, or *removed from dev/ and not found in any tag*.
4. **Jira gets one living comment.** `--post` creates the manifest comment on the ticket, or replaces the previous one.

## Usage

```powershell
dotnet run ledger.cs -- --ui               # browser UI on http://localhost:5199 (Ctrl+C to stop)
dotnet run ledger.cs -- MM-1234            # build out/MM-1234.md
dotnet run ledger.cs -- MM-1234 --fetch    # git fetch every repo first
dotnet run ledger.cs -- MM-1234 --post     # also create/update the comment on the Jira ticket
.\install-hooks.ps1                        # enable the commit-msg hook in every repo (local git config)
.\install-hooks.ps1 -Uninstall
```

In VS Code: *Terminal → Run Task → Ledger: ...*

The UI (`ui.html`, served by `ledger.cs`) listens on localhost only. It builds the same manifest as the CLI,
shows readiness for QA, merge requests, DB scripts, changed files and commits, and posts to Jira with one click.
Deep link: `http://localhost:5199/#MM-1234`.

## Setup

- .NET 10 SDK and Git.
- For `--post` and for showing the ticket's summary and status: create a Jira API token at
  <https://id.atlassian.com/manage-profile/security/api-tokens>, then set the user environment variables
  `JIRA_EMAIL` and `JIRA_API_TOKEN`.

## Configuration (`ledger.json`)

| Key | Meaning |
|---|---|
| `reposRoot` | folder containing all repos (relative to this folder) |
| `keyPattern` | Jira key regex, also used by the hook |
| `db` | migration repo name, its root folder and its staging folder |
| `highlights` | path regexes that get their own section (permissions/scopes/menus, config, pipeline) |
| `jiraMaxFilesPerRepo` | file list cap in the Jira comment (the .md file is always complete) |
