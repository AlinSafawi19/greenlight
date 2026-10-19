# Greenlight

Answers "what did ticket MM-1234 touch, and is all of it ready for QA?" from git alone, across every repo.
No manual notes and no third-party tools: just Git, the Jira REST API and VS Code.

## How it works

1. **Git hooks guarantee the key.** `hooks/commit-msg` rejects commits without a Jira key. On a branch named
   `MM-1234-...` it prefixes the key for you, so following the branch convention is all it takes.
   Commits that really have no ticket must say `[no-ticket]`.
2. **The manifest is computed, not written.** `greenlight.cs` scans all repos under `reposRoot` and collects, per ticket:
   - commits whose message contains the key
   - commits brought in by merges whose *source* branch contains the key (GitLab MR merges, with MR links)
   - commits on branches named after the key that are not yet in the default branch (patch-equivalent rebased copies are ignored)
3. **DB scripts are traced through `db-migration-scripts`.** Every touched `DBMigrations/<Service>/<folder>/...` file is
   reported as *in release tag X*, *graduated from dev/ to tag X* (routine file name or the `-- from dev/...` marker),
   *still staged in dev/*, or *removed from dev/ and not found in any tag*.
4. **Jira gets one living comment.** `--post` creates the manifest comment on the ticket, or replaces your previous one
   (someone else's manifest comment is never touched).

## Tickets, stories and epics

A ticket's scope is the ticket **plus everything under it in Jira** (the `parent` link, up to three levels):

| You build | Scanned keys | Typical use |
|---|---|---|
| Bug / Task / Enhancement without children | that key | standalone work |
| Story | the story + its sub-tasks and QA sub-tasks, whoever they're assigned to | QA tests the story |
| Epic | the epic + its stories/tasks/bugs + their sub-tasks | release-level view |

Every commit is attributed to exactly one ticket: the key in its message, otherwise the key of the branch or MR
that brought it in. The **Tickets** tab shows each ticket's assignee, Jira status and its own commits (merged/total),
so a story with five people on it shows who still has unmerged work. Readiness (*Needs attention*) rolls up across all
of them; Jira tickets that are not done are listed separately. Parents (story, epic) are shown as breadcrumbs.
Untick *Child tickets* to scan a single key only.

## Usage

```powershell
dotnet run greenlight.cs -- --ui               # browser UI on http://localhost:5199 (Ctrl+C to stop)
dotnet run greenlight.cs -- MM-1234            # build out/MM-1234.md
dotnet run greenlight.cs -- MM-1234 --fetch    # git fetch every repo first
dotnet run greenlight.cs -- MM-1234 --post     # also create/update the comment on the Jira ticket
.\install-hooks.ps1                        # enable the commit-msg hook in every repo (local git config)
.\install-hooks.ps1 -Uninstall
```

In VS Code: *Terminal → Run Task → Greenlight: ...*

The UI (`ui.html`, served by `greenlight.cs`) listens on localhost only. It builds the same manifest as the CLI,
shows readiness for QA, merge requests, DB scripts, changed files and commits, and posts to Jira with one click.
The sidebar always lists **your open tickets** (assigned to you, status not Done), grouped under their story or epic;
click the group header to build the whole story. Deep link: `http://localhost:5199/#MM-1234`.

## Setup (each person, once)

1. .NET 10 SDK and Git.
2. `dotnet run greenlight.cs -- --ui`, then **Connect Jira** (top right): your Jira site, your Atlassian email and an
   API token created at <https://id.atlassian.com/manage-profile/security/api-tokens> while logged in as yourself.
   The token is checked against Jira and stored in **your Windows Credential Manager** (entry `greenlight:<site>`),
   never in this folder. *Disconnect* removes it.
3. In the same dialog, set your **repositories folder** if your clones aren't next to this folder.
   Per-user settings live in `%APPDATA%\greenlight\settings.json`.

Everything you do in Jira (reading your tickets, posting comments) runs as your own account.
The environment variables `JIRA_EMAIL` / `JIRA_API_TOKEN` still work as a fallback for the CLI.

## Configuration (`greenlight.json`)

| Key | Meaning |
|---|---|
| `reposRoot` | folder containing all repos (relative to this folder) |
| `keyPattern` | Jira key regex, also used by the hook |
| `db` | migration repo name, its root folder and its staging folder |
| `highlights` | path regexes that get their own section (permissions/scopes/menus, config, pipeline) |
| `jiraMaxFilesPerRepo` | file list cap in the Jira comment (the .md file is always complete) |

## What's in the app

Greenlight has one job: you and the reviewer know exactly what a feature needs to reach QA, and every feature leaves a clean, lasting record.

- **My tickets**: your tickets, grouped by story (the header checks the whole story with your sub-tickets) or by readiness. A ticket shows the six steps to QA (code, tagged, database, access, config, handover) with what to do next, the evidence behind each step (repositories, files, commits, side-by-side changes, open in VS Code or Visual Studio), where each service is deployed, and the handover you post to Jira. Where Greenlight can do the fix itself, it offers it: create the merge request, cut the tag, move the ticket in Jira.
- **Reviews** (GitLab): merge requests waiting for your review, with a grouped checklist, the changes, discussions and tags; approve, request changes or comment. On your own ticket, the code review section shows each reviewer's state and comments.
- **Worklogs**: a feature isn't done until its time is logged. The Handover step says whether you logged time on the ticket; the Worklogs page shows your week against the daily target, suggests entries from your commits, and reminds you at the end of the day.

Light or dark, from the sidebar. Ctrl K or / finds one of your tickets.

## More settings (`greenlight.json`)

- `time.dailyTargetHours`, `time.workDays` (0 = Sunday), `time.reminderAt` ("17:00").
## Checks

`dotnet run greenlight.cs -- --selftest` checks the core rules (tags, versions, ticket keys, path safety, config keys) without network.
`GREENLIGHT_TIMING=1` prints how long each phase of a check takes.