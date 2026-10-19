// feature-ledger: builds a per-ticket manifest from git history across all local repos,
// writes it to out/<KEY>.md and optionally posts/updates it as a comment on the Jira ticket.
//
//   dotnet run ledger.cs -- MM-1234            build the manifest (local only)
//   dotnet run ledger.cs -- MM-1234 --fetch    git fetch all repos first
//   dotnet run ledger.cs -- MM-1234 --post     also create/update the manifest comment on Jira
//
// Every item in the manifest comes from git with a deterministic rule:
//   1. commits whose message contains the key
//   2. commits brought in by a merge whose SOURCE branch contains the key (e.g. GitLab MR merges)
//   3. commits on branches containing the key that are not yet in the repo's default branch
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var flags = args.Where(a => a.StartsWith("--")).Select(a => a.ToLowerInvariant()).ToHashSet();
var key = args.FirstOrDefault(a => !a.StartsWith("--"))?.Trim().ToUpperInvariant();

var appDir = AppContext.GetData("EntryPointFileDirectoryPath") as string ?? Directory.GetCurrentDirectory();
var cfg = JsonNode.Parse(File.ReadAllText(Path.Combine(appDir, "ledger.json")))!;
var keyPattern = (string)cfg["keyPattern"]!;

if (key is null || !Regex.IsMatch(key, $"^(?:{keyPattern})$"))
{
    Console.Error.WriteLine($"Usage: dotnet run ledger.cs -- <KEY matching {keyPattern}> [--fetch] [--post]");
    return 2;
}

var keyRx = new Regex($@"(?<![A-Za-z0-9]){Regex.Escape(key)}(?!\d)", RegexOptions.IgnoreCase);
var anyKeyRx = new Regex($@"(?<![A-Za-z0-9])(?:{keyPattern})(?!\d)", RegexOptions.IgnoreCase);
var reposRoot = Path.GetFullPath(Path.Combine(appDir, (string)cfg["reposRoot"]!));
var exclude = cfg["excludeRepos"]!.AsArray().Select(n => (string)n!).ToHashSet(StringComparer.OrdinalIgnoreCase);
var repos = Directory.GetDirectories(reposRoot)
    .Where(d => Directory.Exists(Path.Combine(d, ".git")) && !exclude.Contains(Path.GetFileName(d)))
    .Where(d => !string.Equals(Path.GetFullPath(d).TrimEnd('\\'), appDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
    .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
    .ToList();

if (flags.Contains("--fetch"))
{
    Console.Error.WriteLine($"Fetching {repos.Count} repos...");
    Parallel.ForEach(repos, r => Git.Run(r, "fetch", "--all", "--prune", "--quiet"));
}

Console.Error.WriteLine($"Scanning {repos.Count} repos for {key}...");
var db = cfg["db"]!;
var dbCfg = new DbConfig((string)db["repo"]!, (string)db["root"]!, (string)db["stagingFolder"]!);
var found = new ConcurrentBag<RepoResult>();
Parallel.ForEach(repos, r => { var res = Scanner.Scan(r, key, keyRx, anyKeyRx, dbCfg); if (res is not null) found.Add(res); });
var results = found.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

var highlights = cfg["highlights"]!.AsObject()
    .Select(kv => (Name: kv.Key, Rx: new Regex((string)kv.Value!)))
    .ToList();

var jira = new Jira((string)cfg["jira"]!["baseUrl"]!, Env((string)cfg["jira"]!["emailEnv"]!), Env((string)cfg["jira"]!["tokenEnv"]!));
var issue = jira.Configured ? await jira.GetIssue(key) : null;

var doc = Manifest.Build(key, issue, results, highlights, dbCfg, (int)cfg["jiraMaxFilesPerRepo"]!);
var outDir = Path.Combine(appDir, "out");
Directory.CreateDirectory(outDir);
var mdPath = Path.Combine(outDir, $"{key}.md");
var markdown = doc.Markdown();
File.WriteAllText(mdPath, markdown);
Console.WriteLine(markdown);
Console.Error.WriteLine($"Written: {mdPath}");

if (flags.Contains("--post"))
{
    if (!jira.Configured)
    {
        Console.Error.WriteLine("Jira credentials missing: set the JIRA_EMAIL and JIRA_API_TOKEN user environment variables.");
        return 1;
    }
    var url = await jira.UpsertManifestComment(key, doc.JiraWiki());
    Console.Error.WriteLine($"Jira comment saved: {url}");
}
return 0;

static string? Env(string name) =>
    Environment.GetEnvironmentVariable(name) ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);

// ───────────────────────────── git ─────────────────────────────

static class Git
{
    public static string Run(string repo, params string[] args) => RunWithInput(repo, null, args);

    public static string RunWithInput(string repo, string? input, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(repo);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.quotepath=off");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var outTask = p.StandardOutput.ReadToEndAsync();
        if (input is not null) { p.StandardInput.Write(input); p.StandardInput.Close(); }
        var output = outTask.Result;
        p.WaitForExit();
        _ = err.Result;
        return p.ExitCode == 0 ? output : "";
    }

    // Stable id of a commit's change, identical for the same patch under any SHA or message.
    public static string PatchId(string repo, string sha) =>
        RunWithInput(repo, Run(repo, "diff-tree", "-p", "--no-color", sha), "patch-id", "--stable").Split(' ')[0].Trim();

    public static IEnumerable<string> Lines(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static IEnumerable<string[]> Records(string s) =>
        s.Split('\x1e', StringSplitOptions.RemoveEmptyEntries)
         .Select(r => r.Trim('\n', '\r', ' '))
         .Where(r => r.Length > 0)
         .Select(r => r.Split('\x1f'));

    public static string WebUrl(string repo)
    {
        var u = Run(repo, "remote", "get-url", "origin").Trim();
        if (u == "") return "";
        var ssh = Regex.Match(u, @"^git@([^:]+):(.+?)(\.git)?$");
        if (ssh.Success) return $"https://{ssh.Groups[1].Value}/{ssh.Groups[2].Value}";
        u = Regex.Replace(u, @"^(https?://)[^@/]+@", "$1");
        return Regex.Replace(u, @"\.git$", "");
    }
}

// ───────────────────────────── scanning ─────────────────────────────

record DbConfig(string Repo, string Root, string Staging);
record Commit(string Sha, string Short, string Author, string Date, string Subject, string Source, bool InDefault,
              string? OtherKey, List<(string Status, string Path)> Files)
{
    public string? Note { get; set; }   // e.g. carries this ticket's change under another ticket's message
}
record MergeInfo(string Sha, string Date, string SourceBranch, string? Target, string? MrUrl, bool InDefault);
record BranchInfo(string Name, int Unmerged);
record DbItem(string Service, string Location, string Kind, string File, string Status, bool Warning);
record RepoResult(string Name, string WebUrl, string DefaultRef, List<Commit> Commits, List<MergeInfo> Merges,
                  List<BranchInfo> Branches, SortedDictionary<string, string> Files, List<DbItem> Db);

static class Scanner
{
    static readonly Regex MergeSubject = new(@"^Merge (?:remote-tracking )?branch '([^']+)'(?: into '?([^'\s]+)'?)?");
    static readonly Regex MergeRequest = new(@"See merge request (\S+)!(\d+)");

    public static RepoResult? Scan(string repo, string key, Regex keyRx, Regex anyKeyRx, DbConfig dbCfg)
    {
        var defaultRef = Git.Run(repo, "symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD").Trim();
        if (defaultRef == "") defaultRef = "HEAD";
        var web = Git.WebUrl(repo);
        var sources = new Dictionary<string, string>();

        // 1. commits whose message mentions the key (branches and tags only: --all would include stash entries)
        foreach (var r in Git.Records(Git.Run(repo, "log", "--branches", "--remotes", "--tags", "--no-merges", "-i", "-F", $"--grep={key}", "--format=%H%x1f%B%x1e")))
            if (r.Length >= 2 && keyRx.IsMatch(r[1])) sources.TryAdd(r[0], "message");

        // 2. commits brought in by merges of a branch named after the key
        var merges = new List<MergeInfo>();
        foreach (var r in Git.Records(Git.Run(repo, "log", "--branches", "--remotes", "--tags", "--merges", "-i", "-F", $"--grep={key}", "--date=short",
                                              "--format=%H%x1f%P%x1f%ad%x1f%s%x1f%b%x1e")))
        {
            if (r.Length < 5) continue;
            var m = MergeSubject.Match(r[3]);
            if (!m.Success || !keyRx.IsMatch(m.Groups[1].Value)) continue;   // skips "Merge branch 'main' into MM-…"
            var parents = r[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parents.Length < 2) continue;
            foreach (var sha in Git.Lines(Git.Run(repo, "rev-list", "--no-merges", $"{parents[0]}..{parents[1]}")))
                sources.TryAdd(sha, "merge");
            var mr = MergeRequest.Match(r[4]);
            var mrUrl = mr.Success && web != "" ? $"{web}/-/merge_requests/{mr.Groups[2].Value}" : null;
            merges.Add(new MergeInfo(r[0], r[2], m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : null, mrUrl,
                                     IsAncestor(repo, r[0], defaultRef)));
        }

        // 3. branches named after the key: work not yet in the default branch.
        //    --cherry-pick drops commits whose patch already landed under another sha (rebased / squashed MRs).
        var refs = Git.Lines(Git.Run(repo, "for-each-ref", "--format=%(refname:short)", "refs/heads", "refs/remotes"))
                      .Where(b => !b.EndsWith("/HEAD") && keyRx.IsMatch(b)).ToList();
        var branches = new List<BranchInfo>();
        foreach (var group in refs.GroupBy(b => b.StartsWith("origin/") ? b["origin/".Length..] : b))
        {
            var unmerged = new HashSet<string>();
            foreach (var b in group)
                foreach (var sha in Git.Lines(Git.Run(repo, "rev-list", "--no-merges", "--cherry-pick", "--right-only", $"{defaultRef}...{b}")))
                {
                    unmerged.Add(sha);
                    sources.TryAdd(sha, "branch");
                }
            var pushed = group.Any(b => b.StartsWith("origin/"));
            branches.Add(new BranchInfo(group.Key + (pushed ? "" : " (local only, never pushed)"), unmerged.Count));
        }

        if (sources.Count == 0 && merges.Count == 0 && branches.Count == 0) return null;

        var inDefault = Git.Lines(Git.Run(repo, "rev-list", defaultRef)).ToHashSet();
        var commits = new List<Commit>();
        var copies = new List<string>();
        foreach (var (sha, source) in sources)
        {
            // a commit outside the default branch whose patch is already in it counts as merged
            var merged = inDefault.Contains(sha) ||
                         !Git.Lines(Git.Run(repo, "rev-list", "--no-merges", "--cherry-pick", "--right-only", $"{defaultRef}...{sha}")).Contains(sha);
            if (merged && !inDefault.Contains(sha)) { copies.Add(sha); continue; }   // copy of a patch already in the default branch
            var text = Git.Run(repo, "show", "--date=short", "--name-status", "-M", "--format=%x1e%h%x1f%an%x1f%ad%x1f%s", sha);
            var lines = text.Split('\n');
            var head = lines.FirstOrDefault(l => l.StartsWith('\x1e'))?.TrimStart('\x1e').Split('\x1f');
            if (head is null || head.Length < 4) continue;
            var files = new List<(string, string)>();
            foreach (var l in lines.Skip(1))
            {
                var parts = l.TrimEnd('\r').Split('\t');
                if (parts.Length < 2 || parts[0].Length == 0) continue;
                files.Add((parts[0][..1], parts[^1]));
            }
            var otherKey = keyRx.IsMatch(head[3]) ? null
                : anyKeyRx.Matches(head[3]).Select(m => m.Value.ToUpperInvariant()).FirstOrDefault(k => k != key);
            commits.Add(new Commit(sha, head[0], head[1], head[2], head[3], source, merged, otherKey, files));
        }
        commits = commits.OrderBy(c => c.Date).ThenBy(c => c.Short).ToList();

        // A skipped copy that names this ticket, whose merged twin has another message: say so on the twin,
        // otherwise this ticket's change would look like someone else's work.
        if (copies.Count > 0)
        {
            var twins = commits.Where(c => c.InDefault && !keyRx.IsMatch(c.Subject))
                               .GroupBy(c => Git.PatchId(repo, c.Sha)).Where(g => g.Key != "")
                               .ToDictionary(g => g.Key, g => g.First());
            foreach (var sha in copies)
            {
                var subject = Git.Run(repo, "log", "-1", "--format=%s", sha).Trim();
                if (!keyRx.IsMatch(subject) || !twins.TryGetValue(Git.PatchId(repo, sha), out var twin)) continue;
                twin.Note = $"contains this ticket's change {sha[..8]} \"{subject}\", merged under this commit's message";
            }
        }

        // aggregate file status across the feature: A wins over M, a later D marks it deleted
        var agg = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in commits)
            foreach (var (st, path) in c.Files)
                agg[path] = agg.TryGetValue(path, out var prev) && prev == "A" && st != "D" ? "A" : st == "R" ? "A" : st;

        var name = Path.GetFileName(repo);
        var dbItems = string.Equals(name, dbCfg.Repo, StringComparison.OrdinalIgnoreCase)
            ? AnalyzeDb(repo, defaultRef, agg, dbCfg)
            : [];

        return new RepoResult(name, web, defaultRef, commits, merges.OrderBy(m => m.Date).ToList(),
                              branches.OrderBy(b => b.Name).ToList(), agg, dbItems);
    }

    // nothing in target..sha means sha is already contained in target
    static bool IsAncestor(string repo, string sha, string target) =>
        Git.Run(repo, "rev-list", "-n", "1", $"{target}..{sha}").Trim() == "";

    // Locates every DB script the feature touched: still staged in dev/, already in a release tag, or lost.
    static List<DbItem> AnalyzeDb(string repo, string defaultRef, SortedDictionary<string, string> files, DbConfig cfg)
    {
        var head = Git.Lines(Git.Run(repo, "ls-tree", "-r", "--name-only", defaultRef)).ToHashSet(StringComparer.Ordinal);
        var items = new List<DbItem>();
        foreach (var (path, st) in files)
        {
            var p = path.Split('/');
            if (p.Length < 4 || p[0] != cfg.Root || !path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)) continue;
            string svc = p[1], loc = p[2], file = p[^1], kind = p.Length >= 5 ? p[3] : "";
            if (st == "D") continue;

            if (loc != cfg.Staging)
            {
                var onDefault = head.Contains(path);
                items.Add(new DbItem(svc, loc, kind, file,
                    onDefault ? $"in release tag {loc}" : $"in tag {loc}, not yet on {defaultRef}", !onDefault));
                continue;
            }
            if (head.Contains(path))
            {
                items.Add(new DbItem(svc, loc, kind, file, "STAGED in dev/ only, not in any release tag", true));
                continue;
            }
            // graduated? routines keep their file name; schema/seed blocks carry a "-- from dev/<Kind>/<file>" marker
            var byName = head.FirstOrDefault(h => h.StartsWith($"{cfg.Root}/{svc}/") && !h.StartsWith($"{cfg.Root}/{svc}/{cfg.Staging}/")
                                                  && h.EndsWith("/" + file));
            var tag = byName?.Split('/')[2];
            if (tag is null)
            {
                var hit = Git.Lines(Git.Run(repo, "grep", "-l", "-F", $"from {cfg.Staging}/{kind}/{file}", defaultRef, "--", $"{cfg.Root}/{svc}/"))
                             .FirstOrDefault();
                tag = hit?.Split(':', 2).ElementAtOrDefault(1)?.Split('/').ElementAtOrDefault(2);
            }
            items.Add(tag is not null
                ? new DbItem(svc, loc, kind, file, $"graduated to release tag {tag}", false)
                : new DbItem(svc, loc, kind, file, $"removed from dev/ and NOT found in any tag on {defaultRef}", true));
        }
        return items.OrderBy(i => i.Service).ThenBy(i => i.Location).ThenBy(i => i.Kind).ThenBy(i => i.File).ToList();
    }
}

// ───────────────────────────── manifest document ─────────────────────────────

record Span(string Text, string? Url = null, bool Code = false);

// A tiny format-neutral document so the same manifest renders to Markdown (file) and Jira wiki (comment).
abstract record Block;
record Heading(int Level, string Text) : Block;
record Para(List<Span> Spans) : Block;
record Bullets(List<List<Span>> Items) : Block;
record Table(string[] Headers, List<List<Span>[]> Rows) : Block;
record FullOnly(Block Inner) : Block;   // shown in the .md file, omitted from the Jira comment
record JiraOnly(Block Inner) : Block;   // shown in the Jira comment, omitted from the .md file

class Doc
{
    public const string Marker = "Feature manifest (feature-ledger)";
    public List<Block> Blocks { get; } = [];

    public string Markdown()
    {
        var sb = new StringBuilder();
        foreach (var b in Blocks) Render(b switch { FullOnly f => f.Inner, JiraOnly => null, _ => b });
        return sb.ToString();

        void Render(Block? b)
        {
            switch (b)
            {
                case Heading h: sb.Append('\n').Append(new string('#', h.Level)).Append(' ').AppendLine(Md(h.Text)); break;
                case Para p: sb.AppendLine(Spans(p.Spans)).AppendLine(); break;
                case Bullets l: foreach (var i in l.Items) sb.Append("- ").AppendLine(Spans(i)); sb.AppendLine(); break;
                case Table t:
                    sb.Append("| ").Append(string.Join(" | ", t.Headers.Select(Md))).AppendLine(" |");
                    sb.Append('|').Append(string.Concat(t.Headers.Select(_ => "---|"))).AppendLine();
                    foreach (var r in t.Rows) sb.Append("| ").Append(string.Join(" | ", r.Select(Spans))).AppendLine(" |");
                    sb.AppendLine();
                    break;
            }
        }
        static string Md(string s) => s.Replace("|", "\\|");
        static string Spans(List<Span> spans) => string.Concat(spans.Select(s =>
            s.Url is not null ? $"[{Md(s.Text)}]({s.Url})" : s.Code ? $"`{s.Text}`" : Md(s.Text)));
    }

    public string JiraWiki()
    {
        var sb = new StringBuilder();
        foreach (var b in Blocks) Render(b switch { FullOnly => null, JiraOnly j => j.Inner, _ => b });
        return sb.ToString();

        void Render(Block? b)
        {
            switch (b)
            {
                case Heading h: sb.Append($"h{h.Level + 1}. ").AppendLine(W(h.Text)); break;
                case Para p: sb.AppendLine(Spans(p.Spans)).AppendLine(); break;
                case Bullets l: foreach (var i in l.Items) sb.Append("* ").AppendLine(Spans(i)); sb.AppendLine(); break;
                case Table t:
                    sb.Append("||").Append(string.Join("||", t.Headers.Select(W))).AppendLine("||");
                    foreach (var r in t.Rows) sb.Append('|').Append(string.Join("|", r.Select(c => Cell(Spans(c))))).AppendLine("|");
                    sb.AppendLine();
                    break;
            }
        }

        static string Cell(string s) => s.Length == 0 ? " " : s;
        static string W(string s) => Regex.Replace(s, @"[|\[\]{}*_+^~-]", m => "\\" + m.Value);
        static string Spans(List<Span> spans) => string.Concat(spans.Select(s =>
            s.Url is not null ? $"[{W(s.Text)}|{s.Url}]" : s.Code ? "{{" + s.Text.Replace("}", "") + "}}" : W(s.Text)));
    }
}

record IssueInfo(string Summary, string Status, string Assignee, string FixVersions);

static class Manifest
{
    static List<Span> T(string s) => [new(s)];
    static List<Span> C(string s) => [new(s, Code: true)];
    static List<Span> L(string text, string? url) => [new(text, url)];

    public static Doc Build(string key, IssueInfo? issue, List<RepoResult> repos,
                            List<(string Name, Regex Rx)> highlights, DbConfig dbCfg, int maxFiles)
    {
        var d = new Doc();
        var b = d.Blocks;
        b.Add(new Heading(2, $"{Doc.Marker}: {key}{(issue is null ? "" : " - " + issue.Summary)}"));
        var meta = issue is null ? "" : $"Status: {issue.Status} · Assignee: {issue.Assignee} · Fix versions: {issue.FixVersions} · ";
        b.Add(new Para(T($"{meta}Generated {DateTime.Now:yyyy-MM-dd HH:mm} from git across all local repos. Re-run to refresh; this comment is replaced, not duplicated.")));

        if (repos.Count == 0)
        {
            b.Add(new Para(T($"No commits, merges or branches reference {key} in any repo.")));
            return d;
        }

        // attention: anything QA would trip over
        var warn = new List<List<Span>>();
        foreach (var r in repos)
        {
            foreach (var br in r.Branches.Where(x => x.Unmerged > 0))
                warn.Add([new($"{r.Name}: "), new(br.Name, Code: true), new($" has {br.Unmerged} commit(s) not merged into {r.DefaultRef}")]);
            var stray = r.Commits.Count(c => !c.InDefault && c.Source == "message");
            if (stray > 0 && r.Branches.All(x => x.Unmerged == 0))
                warn.Add(T($"{r.Name}: {stray} commit(s) mentioning {key} are not in {r.DefaultRef}"));
            foreach (var i in r.Db.Where(i => i.Warning))
                warn.Add([new($"DB {i.Service}: "), new($"{i.Location}/{i.Kind}/{i.File}", Code: true), new($" - {i.Status}")]);
        }
        b.Add(new Heading(3, "Needs attention before QA"));
        b.Add(warn.Count == 0 ? new Para(T("Nothing: all work is merged and every DB script is in a release tag.")) : new Bullets(warn));

        b.Add(new Heading(3, "Repositories"));
        b.Add(new Table(["Repo", "Merge requests", "Branches", "Commits", "In default branch"],
            repos.Select(r => new List<Span>[]
            {
                L(r.Name, r.WebUrl == "" ? null : r.WebUrl),
                r.Merges.Count == 0 ? T("-") : r.Merges.SelectMany((m, i) => (i > 0 ? T(", ") : [])
                    .Concat(m.MrUrl is null ? T($"merge {m.Sha[..8]} ({m.Date})") : L($"!{m.MrUrl.Split('/')[^1]} ({m.Date})", m.MrUrl))).ToList(),
                r.Branches.Count == 0 ? T("-") : T(string.Join(", ", r.Branches.Select(x => x.Name).Distinct())),
                T(r.Commits.Count.ToString()),
                T($"{r.Commits.Count(c => c.InDefault)}/{r.Commits.Count} ({r.DefaultRef})"),
            }).ToList()));

        var dbItems = repos.SelectMany(r => r.Db).ToList();
        if (dbItems.Count > 0)
        {
            b.Add(new Heading(3, "Database scripts"));
            b.Add(new Table(["Service", "Folder", "Kind", "File", "Status"],
                dbItems.Select(i => new List<Span>[] { T(i.Service), T(i.Location), T(i.Kind), C(i.File), T((i.Warning ? "⚠ " : "") + i.Status) }).ToList()));
        }

        var hl = highlights
            .Select(h => (h.Name, Hits: repos.SelectMany(r => r.Files
                .Where(f => f.Value != "D" && h.Rx.IsMatch(f.Key))
                .Where(f => !(r.Name.Equals(dbCfg.Repo, StringComparison.OrdinalIgnoreCase) && f.Key.StartsWith(dbCfg.Root + "/")))   // already in the DB table
                .Select(f => (r.Name, f.Key))).ToList()))
            .Where(h => h.Hits.Count > 0).ToList();
        if (hl.Count > 0)
        {
            b.Add(new Heading(3, "Highlights"));
            foreach (var (name, hits) in hl)
            {
                b.Add(new Para([new(name, Code: false)]));
                b.Add(new Bullets(hits.Select(h => new List<Span> { new($"{h.Item1}: "), new(h.Item2, Code: true) }).ToList()));
            }
        }

        b.Add(new Heading(3, "Changed files"));
        foreach (var r in repos.Where(r => r.Files.Count > 0))
        {
            b.Add(new Para(T($"{r.Name} ({r.Files.Count} files)")));
            var items = r.Files.Select(f => new List<Span> { new($"{f.Value} "), new(f.Key, Code: true) }).ToList();
            b.Add(new FullOnly(new Bullets(items)));
            b.Add(new JiraOnly(new Bullets(items.Count <= maxFiles ? items
                : [.. items.Take(maxFiles), T($"... and {items.Count - maxFiles} more (full list in out/{key}.md)")])));
        }

        b.Add(new FullOnly(new Heading(3, "Commits")));
        foreach (var r in repos)
        {
            b.Add(new FullOnly(new Para(T(r.Name))));
            b.Add(new FullOnly(new Bullets(r.Commits.Select(c => new List<Span>
            {
                r.WebUrl == "" ? new(c.Short, Code: true) : new(c.Short, $"{r.WebUrl}/-/commit/{c.Sha}"),
                new($" {c.Date} {c.Author} - {c.Subject}{(c.InDefault ? "" : "  (not in " + r.DefaultRef + ")")}" +
                    (c.Note is not null ? $"  ({c.Note})"
                     : c.OtherKey is null ? "" : $"  ({c.OtherKey} work that came in through this ticket's branch)")),
            }).ToList())));
        }
        return d;
    }
}

// ───────────────────────────── Jira ─────────────────────────────

class Jira
{
    readonly HttpClient? http;
    readonly string baseUrl;
    public bool Configured => http is not null;

    public Jira(string baseUrl, string? email, string? token)
    {
        this.baseUrl = baseUrl.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(token)) return;
        http = new HttpClient { BaseAddress = new Uri(this.baseUrl), Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{token}")));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<IssueInfo?> GetIssue(string key)
    {
        try
        {
            var res = await http!.GetAsync($"/rest/api/2/issue/{key}?fields=summary,status,assignee,fixVersions");
            if (!res.IsSuccessStatusCode) { Console.Error.WriteLine($"Jira: could not read {key} ({(int)res.StatusCode})."); return null; }
            var f = JsonNode.Parse(await res.Content.ReadAsStringAsync())!["fields"]!;
            var fix = f["fixVersions"]?.AsArray().Select(v => (string?)v?["name"]).Where(v => v is not null).ToList() ?? [];
            return new IssueInfo((string?)f["summary"] ?? "", (string?)f["status"]?["name"] ?? "",
                                 (string?)f["assignee"]?["displayName"] ?? "Unassigned", fix.Count == 0 ? "none" : string.Join(", ", fix));
        }
        catch (Exception e) { Console.Error.WriteLine($"Jira: {e.Message}"); return null; }
    }

    // Replaces the previous manifest comment if there is one, so the ticket always shows a single, current manifest.
    public async Task<string> UpsertManifestComment(string key, string wiki)
    {
        string? existing = null;
        for (var start = 0; existing is null; start += 100)
        {
            var page = JsonNode.Parse(await (await http!.GetAsync($"/rest/api/2/issue/{key}/comment?startAt={start}&maxResults=100")).EnsureSuccessStatusCode()
                                          .Content.ReadAsStringAsync())!;
            var comments = page["comments"]!.AsArray();
            existing = comments.FirstOrDefault(c => ((string?)c?["body"])?.Contains(Doc.Marker) == true)?["id"]?.ToString();
            if (start + comments.Count >= (int)page["total"]!) break;
        }
        var body = new StringContent(new JsonObject { ["body"] = wiki }.ToJsonString(), Encoding.UTF8, "application/json");
        var res = existing is null
            ? await http!.PostAsync($"/rest/api/2/issue/{key}/comment", body)
            : await http!.PutAsync($"/rest/api/2/issue/{key}/comment/{existing}", body);
        if (!res.IsSuccessStatusCode)
            throw new Exception($"Jira returned {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        var id = JsonNode.Parse(await res.Content.ReadAsStringAsync())!["id"]!.ToString();
        return $"{baseUrl}/browse/{key}?focusedCommentId={id}";
    }
}
