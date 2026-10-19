// feature-ledger: builds a per-ticket manifest from git history across all local repos,
// writes it to out/<KEY>.md and optionally posts/updates it as a comment on the Jira ticket.
//
//   dotnet run ledger.cs -- MM-1234            build the manifest (local only)
//   dotnet run ledger.cs -- MM-1234 --fetch    git fetch all repos first
//   dotnet run ledger.cs -- MM-1234 --post     also create/update the manifest comment on Jira
//   dotnet run ledger.cs -- --ui               open the browser UI on http://localhost:5199
//
// Every item in the manifest comes from git with a deterministic rule:
//   1. commits whose message contains the key
//   2. commits brought in by a merge whose SOURCE branch contains the key (e.g. GitLab MR merges)
//   3. commits on branches containing the key that are not yet in the repo's default branch
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

var flags = args.Where(a => a.StartsWith("--")).Select(a => a.ToLowerInvariant()).ToHashSet();
var key = args.FirstOrDefault(a => !a.StartsWith("--"))?.Trim().ToUpperInvariant();

var appDir = AppContext.GetData("EntryPointFileDirectoryPath") as string ?? Directory.GetCurrentDirectory();
var ledger = new Ledger(appDir);

if (flags.Contains("--ui"))
{
    await UiServer.Run(ledger, open: !flags.Contains("--no-open"));
    return 0;
}

if (key is null || !ledger.IsValidKey(key))
{
    Console.Error.WriteLine($"Usage: dotnet run ledger.cs -- <KEY matching {ledger.KeyPattern}> [--fetch] [--post]");
    Console.Error.WriteLine("       dotnet run ledger.cs -- --ui");
    return 2;
}

var report = await ledger.Build(key, flags.Contains("--fetch"), msg => Console.Error.WriteLine(msg));
Console.WriteLine(report.Markdown);
Console.Error.WriteLine($"Written: {report.MarkdownPath}");

if (flags.Contains("--post"))
{
    if (!ledger.Jira.Configured)
    {
        Console.Error.WriteLine("Jira credentials missing: set the JIRA_EMAIL and JIRA_API_TOKEN user environment variables.");
        return 1;
    }
    var url = await ledger.Jira.UpsertManifestComment(key, report.Doc.JiraWiki());
    Console.Error.WriteLine($"Jira comment saved: {url}");
}
return 0;

// ───────────────────────────── ledger: one build, shared by the CLI and the UI ─────────────────────────────

// Scope: every child ticket included in the scan (an epic's stories and their sub-tasks, a story's sub-tasks).
// Ancestors: the parent chain above the ticket, for context only (not scanned).
record Report(string Key, DateTime Generated, IssueInfo? Issue, List<IssueInfo> Scope, List<IssueInfo> Ancestors,
              List<RepoResult> Repos, Doc Doc,
              List<List<Span>> Warnings, List<(string Name, List<(string Repo, string Path)> Hits)> Highlights,
              string Markdown, string MarkdownPath, List<Station> Stations);

class Ledger
{
    public string AppDir { get; }
    public string KeyPattern { get; }
    public string OutDir { get; }
    public Jira Jira { get; private set; } = null!;
    public string JiraBaseUrl { get; private set; }
    public string? CredentialSource { get; private set; }   // where the current Jira credentials came from
    public string ReposRoot { get; private set; } = "";
    public List<string> Repos { get; private set; } = [];
    readonly JsonNode cfg;
    readonly DbConfig dbCfg;
    readonly List<(string Name, Regex Rx)> highlights;
    readonly UserSettings settings = UserSettings.Load();

    public Ledger(string appDir)
    {
        AppDir = appDir;
        cfg = JsonNode.Parse(File.ReadAllText(Path.Combine(appDir, "ledger.json")))!;
        KeyPattern = (string)cfg["keyPattern"]!;
        OutDir = Path.Combine(appDir, "out");
        var db = cfg["db"]!;
        dbCfg = new DbConfig((string)db["repo"]!, (string)db["root"]!, (string)db["stagingFolder"]!);
        highlights = cfg["highlights"]!.AsObject().Select(kv => (Name: kv.Key, Rx: new Regex((string)kv.Value!))).ToList();
        JiraBaseUrl = (settings.JiraBaseUrl ?? (string)cfg["jira"]!["baseUrl"]!).TrimEnd('/');
        LoadRepos(settings.ReposRoot ?? Path.GetFullPath(Path.Combine(appDir, (string)cfg["reposRoot"]!)));
        LoadJira();
    }

    // ── per-user account: each person connects their own Jira account; the token lives in their Windows Credential Manager ──

    static string CredTarget(string baseUrl) => $"feature-ledger:{new Uri(baseUrl).Host}";

    void LoadJira()
    {
        if (CredStore.Read(CredTarget(JiraBaseUrl)) is { } c)
        {
            Jira = new Jira(JiraBaseUrl, c.User, c.Secret);
            CredentialSource = "Windows Credential Manager";
            return;
        }
        var email = Env((string)cfg["jira"]!["emailEnv"]!);
        var token = Env((string)cfg["jira"]!["tokenEnv"]!);
        Jira = new Jira(JiraBaseUrl, email, token);
        CredentialSource = Jira.Configured ? "environment variables" : null;
    }

    public async Task<JiraUser> Connect(string baseUrl, string email, string token)
    {
        baseUrl = baseUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) || u.Scheme != "https")
            throw new Exception("Jira site must be an https URL, e.g. https://yourcompany.atlassian.net");
        var candidate = new Jira(baseUrl, email, token);
        if (!candidate.Configured) throw new Exception("Enter your Atlassian email and API token.");
        var me = await candidate.Verify();   // nothing is saved unless Jira accepts the credentials
        CredStore.Write(CredTarget(baseUrl), email.Trim(), token.Trim());
        settings.JiraBaseUrl = baseUrl;
        settings.Save();
        JiraBaseUrl = baseUrl;
        Jira = candidate;
        CredentialSource = "Windows Credential Manager";
        return me;
    }

    public void Disconnect()
    {
        CredStore.Delete(CredTarget(JiraBaseUrl));
        LoadJira();   // falls back to environment variables if those are still set
    }

    public async Task<JiraUser?> CurrentUser()
    {
        if (!Jira.Configured) return null;
        try { return Jira.Me ?? await Jira.Verify(); }
        catch { return null; }
    }

    // ── per-user repos folder ──

    public void SetReposRoot(string path)
    {
        path = Path.GetFullPath(path.Trim());
        if (!Directory.Exists(path)) throw new Exception($"Folder not found: {path}");
        LoadRepos(path);
        if (Repos.Count == 0) throw new Exception($"No git repositories found in {path}");
        settings.ReposRoot = path;
        settings.Save();
    }

    void LoadRepos(string root)
    {
        var exclude = cfg["excludeRepos"]!.AsArray().Select(n => (string)n!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ReposRoot = root;
        Repos = !Directory.Exists(root) ? [] : Directory.GetDirectories(root)
            .Where(d => Directory.Exists(Path.Combine(d, ".git")) && !exclude.Contains(Path.GetFileName(d)))
            .Where(d => !string.Equals(Path.GetFullPath(d).TrimEnd('\\'), AppDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public bool IsValidKey(string key) => Regex.IsMatch(key, $"^(?:{KeyPattern})$");

    public async Task<Report> Build(string key, bool fetch, Action<string> progress, bool includeChildren = true, bool onlyMine = false)
    {
        IssueInfo? issue = null;
        var scope = new List<IssueInfo>();
        var ancestors = new List<IssueInfo>();
        if (Jira.Configured)
        {
            issue = await Jira.GetIssue(key);
            if (issue is not null && includeChildren)
            {
                progress($"Reading child tickets of {key} from Jira...");
                scope = await Jira.Descendants(key);
                // other people's sub-tickets are left out: their keys aren't scanned, so their commits don't count either
                if (onlyMine && await CurrentUser() is { } me)
                    scope = scope.Where(s => s.AssigneeId == me.AccountId).ToList();
            }
            for (var (pk, depth) = (issue?.ParentKey, 0); pk is not null && depth < 3; depth++)
            {
                if (await Jira.GetIssue(pk) is not { } p) break;
                ancestors.Insert(0, p);
                pk = p.ParentKey;
            }
        }

        if (fetch)
        {
            progress($"Fetching {Repos.Count} repos...");
            Parallel.ForEach(Repos, r => Git.Run(r, "fetch", "--all", "--prune", "--quiet"));
        }

        var keys = new HashSet<string>(scope.Select(s => s.Key).Prepend(key), StringComparer.OrdinalIgnoreCase);
        progress($"Scanning {Repos.Count} repos for {keys.Count} ticket(s)...");
        var alternation = string.Join("|", keys.OrderByDescending(k => k.Length).Select(Regex.Escape));
        var keyRx = new Regex($@"(?<![A-Za-z0-9])(?:{alternation})(?!\d)", RegexOptions.IgnoreCase);
        var anyKeyRx = new Regex($@"(?<![A-Za-z0-9])(?:{KeyPattern})(?!\d)", RegexOptions.IgnoreCase);
        var found = new ConcurrentBag<RepoResult>();
        Parallel.ForEach(Repos, r => { var res = Scanner.Scan(r, keys, keyRx, anyKeyRx, dbCfg); if (res is not null) found.Add(res); });
        var results = found.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

        progress("Working out what QA needs...");
        var paths = Repos.ToDictionary(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
        var stations = QaPrep.Compute(issue, results, dbCfg, name => paths.GetValueOrDefault(name));

        var doc = Manifest.Build(key, issue, scope, results, highlights, dbCfg, (int)cfg["jiraMaxFilesPerRepo"]!);
        QaPrep.AddToDoc(doc, stations);

        Directory.CreateDirectory(OutDir);
        var mdPath = Path.Combine(OutDir, $"{key}.md");
        var markdown = doc.Markdown();
        File.WriteAllText(mdPath, markdown);

        var report = new Report(key, DateTime.Now, issue, scope, ancestors, results, doc, Manifest.Warnings(key, results),
                                Manifest.Highlights(results, highlights, dbCfg), markdown, mdPath, stations);
        File.WriteAllText(Path.Combine(OutDir, $"{key}.json"), ReportJson.Summary(report).ToJsonString());
        return report;
    }

    static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
}

// Per-user settings, outside the repo: %APPDATA%\feature-ledger\settings.json
class UserSettings
{
    static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "feature-ledger", "settings.json");
    public string? JiraBaseUrl { get; set; }
    public string? ReposRoot { get; set; }

    public static UserSettings Load()
    {
        try
        {
            var j = JsonNode.Parse(File.ReadAllText(FilePath))!;
            return new UserSettings { JiraBaseUrl = (string?)j["jiraBaseUrl"], ReposRoot = (string?)j["reposRoot"] };
        }
        catch { return new UserSettings(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, new JsonObject { ["jiraBaseUrl"] = JiraBaseUrl, ["reposRoot"] = ReposRoot }.ToJsonString(new() { WriteIndented = true }));
    }
}

// Windows Credential Manager (per Windows user, encrypted by the OS). No token is ever written to a file.
static class CredStore
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct CREDENTIAL
    {
        public int Flags; public int Type; public string TargetName; public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize; public IntPtr CredentialBlob; public int Persist;
        public int AttributeCount; public IntPtr Attributes; public string? TargetAlias; public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredRead(string target, int type, int flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredWrite(ref CREDENTIAL credential, int flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CredDelete(string target, int type, int flags);
    [DllImport("advapi32.dll")]
    static extern void CredFree(IntPtr buffer);

    const int Generic = 1, PersistLocalMachine = 2;

    public static (string User, string Secret)? Read(string target)
    {
        if (!OperatingSystem.IsWindows() || !CredRead(target, Generic, 0, out var p)) return null;
        try
        {
            var c = Marshal.PtrToStructure<CREDENTIAL>(p);
            var secret = c.CredentialBlobSize == 0 ? "" : Marshal.PtrToStringUni(c.CredentialBlob, c.CredentialBlobSize / 2);
            return (c.UserName, secret);
        }
        finally { CredFree(p); }
    }

    public static void Write(string target, string user, string secret)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Saving credentials needs Windows Credential Manager.");
        var blob = Marshal.StringToCoTaskMemUni(secret);
        try
        {
            var c = new CREDENTIAL
            {
                Type = Generic, TargetName = target, UserName = user, Persist = PersistLocalMachine,
                CredentialBlob = blob, CredentialBlobSize = secret.Length * 2, Comment = "feature-ledger Jira API token",
            };
            if (!CredWrite(ref c, 0)) throw new Exception($"Could not save the credential (error {Marshal.GetLastWin32Error()}).");
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(blob); }
    }

    public static void Delete(string target)
    {
        if (OperatingSystem.IsWindows()) CredDelete(target, Generic, 0);
    }
}

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
    public string? Ticket { get; set; } // which key in scope this commit belongs to (story, sub-task, ...)
    public bool IsForeign => OtherKey is not null && Note is null;
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

    // keys: the ticket plus, for a story or epic, every child ticket in scope. keyRx matches any of them.
    public static RepoResult? Scan(string repo, IReadOnlySet<string> keys, Regex keyRx, Regex anyKeyRx, DbConfig dbCfg)
    {
        var defaultRef = Git.Run(repo, "symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD").Trim();
        if (defaultRef == "") defaultRef = "HEAD";
        var web = Git.WebUrl(repo);
        var sources = new Dictionary<string, string>();
        var via = new Dictionary<string, string>();   // sha -> key of the branch/merge that brought it in
        var greps = keys.Select(k => $"--grep={k}").ToArray();
        string? KeyIn(string text) => keyRx.Match(text) is { Success: true } m ? m.Value.ToUpperInvariant() : null;

        // 1. commits whose message mentions a key (branches and tags only: --all would include stash entries)
        foreach (var r in Git.Records(Git.Run(repo, ["log", "--branches", "--remotes", "--tags", "--no-merges", "-i", "-F", .. greps, "--format=%H%x1f%B%x1e"])))
            if (r.Length >= 2 && KeyIn(r[1]) is { } msgKey && sources.TryAdd(r[0], "message")) via[r[0]] = msgKey;

        // 2. commits brought in by merges of a branch named after a key
        var merges = new List<MergeInfo>();
        foreach (var r in Git.Records(Git.Run(repo, ["log", "--branches", "--remotes", "--tags", "--merges", "-i", "-F", .. greps, "--date=short",
                                              "--format=%H%x1f%P%x1f%ad%x1f%s%x1f%b%x1e"])))
        {
            if (r.Length < 5) continue;
            var m = MergeSubject.Match(r[3]);
            if (!m.Success || KeyIn(m.Groups[1].Value) is not { } mergeKey) continue;   // skips "Merge branch 'main' into MM-…"
            var parents = r[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parents.Length < 2) continue;
            foreach (var sha in Git.Lines(Git.Run(repo, "rev-list", "--no-merges", $"{parents[0]}..{parents[1]}")))
                if (sources.TryAdd(sha, "merge")) via[sha] = mergeKey;
            var mr = MergeRequest.Match(r[4]);
            var mrUrl = mr.Success && web != "" ? $"{web}/-/merge_requests/{mr.Groups[2].Value}" : null;
            merges.Add(new MergeInfo(r[0], r[2], m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : null, mrUrl,
                                     IsAncestor(repo, r[0], defaultRef)));
        }

        // 3. branches named after the key: work not yet in the default branch.
        //    --cherry-pick drops commits whose patch already landed under another sha (rebased / squashed MRs).
        var refs = Git.Lines(Git.Run(repo, "for-each-ref", "--format=%(refname:short)", "refs/heads", "refs/remotes"))
                      .Where(b => !b.EndsWith("/HEAD") && keyRx.IsMatch(b)).ToList();
        var branchShas = new List<(string Name, HashSet<string> Unmerged)>();
        foreach (var group in refs.GroupBy(b => b.StartsWith("origin/") ? b["origin/".Length..] : b))
        {
            var unmerged = new HashSet<string>();
            var branchKey = KeyIn(group.Key)!;
            foreach (var b in group)
                foreach (var sha in Git.Lines(Git.Run(repo, "rev-list", "--no-merges", "--cherry-pick", "--right-only", $"{defaultRef}...{b}")))
                {
                    unmerged.Add(sha);
                    if (sources.TryAdd(sha, "branch")) via[sha] = branchKey;
                }
            var pushed = group.Any(b => b.StartsWith("origin/"));
            branchShas.Add((group.Key + (pushed ? "" : " (local only, never pushed)"), unmerged));
        }

        if (sources.Count == 0 && merges.Count == 0 && branchShas.Count == 0) return null;

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
            var subjectKey = KeyIn(head[3]);
            var otherKey = subjectKey is not null ? null
                : anyKeyRx.Matches(head[3]).Select(m => m.Value.ToUpperInvariant()).FirstOrDefault(k => !keys.Contains(k));
            commits.Add(new Commit(sha, head[0], head[1], head[2], head[3], source, merged, otherKey, files)
            {
                Ticket = subjectKey ?? via.GetValueOrDefault(sha),
            });
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
                if (KeyIn(subject) is not { } copyKey || !twins.TryGetValue(Git.PatchId(repo, sha), out var twin)) continue;
                twin.Note = $"contains {copyKey}'s change {sha[..8]} \"{subject}\", merged under this commit's message";
                twin.Ticket = copyKey;
            }
        }

        // Another ticket's commit that only rode along on this ticket's branch is listed, but neither its files
        // nor its merge state count toward this ticket.
        var foreign = commits.Where(c => c.IsForeign).Select(c => c.Sha).ToHashSet();
        var branches = branchShas
            .Select(b => new BranchInfo(b.Name, b.Unmerged.Count(s => !foreign.Contains(s))))
            .ToList();

        // aggregate file status across the feature: A wins over M, a later D marks it deleted
        var agg = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in commits.Where(c => !c.IsForeign))
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

// Level is Jira's hierarchy level: 1 epic, 0 story/task/bug, -1 sub-task. StatusCategory: new | indeterminate | done.
record IssueInfo(string Key, string Summary, string Type, int Level, string Status, string StatusCategory,
                 string Assignee, string? AssigneeId, string? ParentKey, string? ParentSummary, string? ParentType,
                 string FixVersions, string Updated);

static class Manifest
{
    static List<Span> T(string s) => [new(s)];
    static List<Span> C(string s) => [new(s, Code: true)];
    static List<Span> L(string text, string? url) => [new(text, url)];

    // anything QA would trip over
    public static List<List<Span>> Warnings(string key, List<RepoResult> repos)
    {
        var warn = new List<List<Span>>();
        foreach (var r in repos)
        {
            foreach (var br in r.Branches.Where(x => x.Unmerged > 0))
                warn.Add([new($"{r.Name}: "), new(br.Name, Code: true), new($" has {br.Unmerged} commit(s) not merged into {r.DefaultRef}")]);
            var stray = r.Commits.Count(c => !c.InDefault && !c.IsForeign && c.Source == "message");
            if (stray > 0 && r.Branches.All(x => x.Unmerged == 0))
                warn.Add(T($"{r.Name}: {stray} commit(s) mentioning {key} are not in {r.DefaultRef}"));
            foreach (var i in r.Db.Where(i => i.Warning))
                warn.Add([new($"DB {i.Service}: "), new($"{i.Location}/{i.Kind}/{i.File}", Code: true), new($" - {i.Status}")]);
        }
        return warn;
    }

    public static List<(string Name, List<(string Repo, string Path)> Hits)> Highlights(
        List<RepoResult> repos, List<(string Name, Regex Rx)> highlights, DbConfig dbCfg) =>
        highlights
            .Select(h => (h.Name, Hits: repos.SelectMany(r => r.Files
                .Where(f => f.Value != "D" && h.Rx.IsMatch(f.Key))
                .Where(f => !(r.Name.Equals(dbCfg.Repo, StringComparison.OrdinalIgnoreCase) && f.Key.StartsWith(dbCfg.Root + "/")))   // already in the DB table
                .Select(f => (r.Name, f.Key))).ToList()))
            .Where(h => h.Hits.Count > 0).ToList();

    // Per ticket in scope: its own commits (merged/total) and the repos they are in.
    public static (int Total, int Merged, List<string> Repos) TicketActivity(string key, List<RepoResult> repos)
    {
        var mine = repos.SelectMany(r => r.Commits.Where(c => !c.IsForeign && string.Equals(c.Ticket, key, StringComparison.OrdinalIgnoreCase))
                                                  .Select(c => (r.Name, c))).ToList();
        return (mine.Count, mine.Count(x => x.c.InDefault), mine.Select(x => x.Name).Distinct().ToList());
    }

    public static Doc Build(string key, IssueInfo? issue, List<IssueInfo> scope, List<RepoResult> repos,
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

        var warn = Warnings(key, repos);
        b.Add(new Heading(3, "Needs attention before QA"));
        b.Add(warn.Count == 0 ? new Para(T("Nothing: all work is merged and every DB script is in a release tag.")) : new Bullets(warn));

        if (scope.Count > 0)
        {
            b.Add(new Heading(3, $"Tickets in scope ({scope.Count + 1})"));
            b.Add(new Table(["Ticket", "Type", "Summary", "Assignee", "Status", "Commits merged", "Repos"],
                (issue is null ? scope : scope.Prepend(issue)).Select(t =>
                {
                    var a = TicketActivity(t.Key, repos);
                    return new List<Span>[]
                    {
                        T((t.Level < 0 ? "↳ " : "") + t.Key), T(t.Type), T(t.Summary), T(t.Assignee), T(t.Status),
                        T(a.Total == 0 ? "-" : $"{a.Merged}/{a.Total}"), T(a.Repos.Count == 0 ? "-" : string.Join(", ", a.Repos)),
                    };
                }).ToList()));
        }

        b.Add(new Heading(3, "Repositories"));
        b.Add(new Table(["Repo", "Merge requests", "Branches", "Commits", "In default branch"],
            repos.Select(r => new List<Span>[]
            {
                L(r.Name, r.WebUrl == "" ? null : r.WebUrl),
                r.Merges.Count == 0 ? T("-") : r.Merges.SelectMany((m, i) => (i > 0 ? T(", ") : [])
                    .Concat(m.MrUrl is null ? T($"merge {m.Sha[..8]} ({m.Date})") : L($"!{m.MrUrl.Split('/')[^1]} ({m.Date})", m.MrUrl))).ToList(),
                r.Branches.Count == 0 ? T("-") : T(string.Join(", ", r.Branches.Select(x => x.Name).Distinct())),
                T(r.Commits.Count(c => !c.IsForeign).ToString()),
                T($"{r.Commits.Count(c => !c.IsForeign && c.InDefault)}/{r.Commits.Count(c => !c.IsForeign)} ({r.DefaultRef})"),
            }).ToList()));

        var dbItems = repos.SelectMany(r => r.Db).ToList();
        if (dbItems.Count > 0)
        {
            b.Add(new Heading(3, "Database scripts"));
            b.Add(new Table(["Service", "Folder", "Kind", "File", "Status"],
                dbItems.Select(i => new List<Span>[] { T(i.Service), T(i.Location), T(i.Kind), C(i.File), T((i.Warning ? "⚠ " : "") + i.Status) }).ToList()));
        }

        var hl = Highlights(repos, highlights, dbCfg);
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

        List<Span> CommitLine(RepoResult r, Commit c) =>
        [
            r.WebUrl == "" ? new(c.Short, Code: true) : new(c.Short, $"{r.WebUrl}/-/commit/{c.Sha}"),
            new($" {c.Date} {c.Author} - {(c.Ticket is null || c.Subject.Contains(c.Ticket, StringComparison.OrdinalIgnoreCase) ? "" : $"[{c.Ticket}] ")}" +
                $"{c.Subject}{(c.InDefault ? "" : "  (not in " + r.DefaultRef + ")")}" +
                (c.Note is not null ? $"  ({c.Note})" : "")),
        ];

        b.Add(new FullOnly(new Heading(3, "Commits")));
        foreach (var r in repos.Where(r => r.Commits.Any(c => !c.IsForeign)))
        {
            b.Add(new FullOnly(new Para(T(r.Name))));
            b.Add(new FullOnly(new Bullets(r.Commits.Where(c => !c.IsForeign).Select(c => CommitLine(r, c)).ToList())));
        }

        var foreign = repos.SelectMany(r => r.Commits.Where(c => c.IsForeign).Select(c => (r, c))).ToList();
        if (foreign.Count > 0)
        {
            b.Add(new Heading(3, "Other tickets' commits on this ticket's branches"));
            b.Add(new Para(T("Listed for reference only. Their files and merge state are not counted above.")));
            b.Add(new Bullets(foreign.Select(x => (List<Span>)[new($"{x.r.Name} {x.c.OtherKey}: "), .. CommitLine(x.r, x.c)]).ToList()));
        }
        return d;
    }
}

// ───────────────────────────── Jira ─────────────────────────────

// ───────────────────────────── QA preparation: the stations a ticket passes on its way to QA ─────────────────────────────

// State: done (nothing left), todo (someone must act), check (a person should confirm), wait (blocked by an earlier
// station), skip (doesn't apply to this ticket).
record StationItem(string State, string Title, string? Detail = null, string? Repo = null,
                   string? ActionLabel = null, string? ActionUrl = null);
record Station(string Id, string Name, string State, string Headline, List<StationItem> Items);

static class QaPrep
{
    static readonly Regex ConfigFile = new(@"(?i)(appsettings[^/]*\.json$|(^|/)config\.[^/]*\.js$|(^|/)\.env(\.[^/]*)?$)");
    static readonly Regex PipelineFile = new(@"(?i)(\.gitlab-ci\.yml$|(^|/)dockerfile$|(^|/)downstream-env\.yml$)");
    static readonly Regex AccessFile = new(@"(?i)(permission|scope|role|polic|claim|menu|sidebar|navigation)");
    static readonly Regex JsonKey = new(@"^\s*""([A-Za-z0-9_.:\-]+)""\s*:");
    static readonly Regex JsKey = new(@"^\s*([A-Za-z_$][A-Za-z0-9_$]*)\s*:");
    static readonly Regex EnvKey = new(@"^\s*([A-Z][A-Z0-9_]*)\s*=");
    static readonly Regex AccessSql = new(@"(?i)\bmember\.(menu\w*|scope_role|menu_action_scope|role\w*|service_scope)\b");

    public static List<Station> Compute(IssueInfo? issue, List<RepoResult> repos, DbConfig db, Func<string, string?> pathOf)
    {
        var code = Code(repos);
        var tag = Tag(repos, db, pathOf);
        var (cfgItems, accessFromCode) = ScanPatches(repos, db, pathOf);
        var database = Database(repos, db, pathOf);
        var access = Access(repos, db, accessFromCode);
        var config = new Station("config", "Config", cfgItems.Count == 0 ? "skip" : "check",
            cfgItems.Count == 0 ? "No settings changed" : "QA settings live outside git: add the new keys there", cfgItems);
        var handover = Handover(issue, [code, tag, database, access, config]);
        return [code, tag, database, access, config, handover];
    }

    static string Worst(IEnumerable<StationItem> items, string empty)
    {
        var s = items.Select(i => i.State).ToList();
        return s.Count == 0 ? empty : s.Contains("todo") ? "todo" : s.Contains("wait") ? "wait" : s.Contains("check") ? "check" : "done";
    }

    static string Target(RepoResult r) => r.DefaultRef.StartsWith("origin/") ? r.DefaultRef["origin/".Length..] : r.DefaultRef;
    static IEnumerable<Commit> Own(RepoResult r) => r.Commits.Where(c => !c.IsForeign);

    static Station Code(List<RepoResult> repos)
    {
        var items = new List<StationItem>();
        foreach (var r in repos.Where(r => Own(r).Any() || r.Branches.Any(b => b.Unmerged > 0)))
        {
            var target = Target(r);
            var waiting = r.Branches.Where(b => b.Unmerged > 0).ToList();
            foreach (var b in waiting)
            {
                var local = b.Name.EndsWith(" (local only, never pushed)");
                var name = local ? b.Name[..b.Name.IndexOf(" (local only")] : b.Name;
                var mr = r.WebUrl != "" && !local
                    ? $"{r.WebUrl}/-/merge_requests/new?merge_request%5Bsource_branch%5D={Uri.EscapeDataString(name)}&merge_request%5Btarget_branch%5D={Uri.EscapeDataString(target)}"
                    : null;
                items.Add(new("todo", local ? $"Push and merge {name}" : $"Merge {name} into {target}",
                    $"{b.Unmerged} commit{(b.Unmerged == 1 ? "" : "s")} waiting{(local ? ". The branch only exists on this computer" : "")}.",
                    r.Name, mr is null ? null : "Open merge request", mr));
            }
            var stray = Own(r).Count(c => !c.InDefault) - waiting.Sum(b => b.Unmerged);
            if (stray > 0 && waiting.Count == 0)
                items.Add(new("todo", $"{stray} commit{(stray == 1 ? "" : "s")} not in {target}", "They mention the ticket but no ticket branch holds them.", r.Name));
            if (waiting.Count == 0 && stray <= 0)
            {
                var mrs = r.Merges.Count(m => m.MrUrl is not null);
                items.Add(new("done", $"Merged into {target}", mrs > 0 ? $"{mrs} merge request{(mrs == 1 ? "" : "s")}" : null, r.Name));
            }
        }
        var state = Worst(items, "todo");
        var open = items.Count(i => i.State == "todo");
        return new("code", "Code", state, items.Count == 0 ? "No code found for this ticket yet"
            : open > 0 ? $"{open} thing{(open == 1 ? "" : "s")} to merge" : $"Merged in {items.Count} repositor{(items.Count == 1 ? "y" : "ies")}", items);
    }

    // QA deploys version tags, so merged code only reaches QA once a tag contains every one of the ticket's commits.
    static Station Tag(List<RepoResult> repos, DbConfig db, Func<string, string?> pathOf)
    {
        var items = new List<StationItem>();
        foreach (var r in repos.Where(r => !r.Name.Equals(db.Repo, StringComparison.OrdinalIgnoreCase) && Own(r).Any()))
        {
            var path = pathOf(r.Name);
            var own = Own(r).ToList();
            if (own.Any(c => !c.InDefault) || path is null) { items.Add(new("wait", "Waiting for the code to merge", null, r.Name)); continue; }

            List<string>? common = null;
            foreach (var c in own)
            {
                var tags = Git.Lines(Git.Run(path, "tag", "--contains", c.Sha, "--sort=v:refname")).ToList();
                common = common is null ? tags : common.Where(tags.Contains).ToList();
                if (common.Count == 0) break;
            }
            if (common is { Count: > 0 })
            {
                var latest = Git.Lines(Git.Run(path, "tag", "--sort=-v:refname")).FirstOrDefault();
                items.Add(new("done", $"In tag {common[0]}",
                    latest is not null && latest != common[0] ? $"Also in every later tag, up to {latest}." : "This is the latest tag.", r.Name));
            }
            else
            {
                var newTag = r.WebUrl == "" ? null : $"{r.WebUrl}/-/tags/new?ref={Uri.EscapeDataString(Target(r))}";
                var last = Git.Lines(Git.Run(path, "tag", "--sort=-v:refname")).FirstOrDefault();
                items.Add(new("todo", $"Cut a new version tag from {Target(r)}",
                    last is null ? "The merged code isn't in any tag yet." : $"The merged code isn't in any tag yet. Latest tag is {last}.",
                    r.Name, newTag is null ? null : "Create tag", newTag));
            }
        }
        var todo = items.Count(i => i.State == "todo");
        var state = Worst(items, "skip");
        return new("tag", "Tagged", state, items.Count == 0 ? "No service code to tag"
            : todo > 0 ? $"{todo} repositor{(todo == 1 ? "y needs" : "ies need")} a new tag"
            : state == "wait" ? "Waiting for code to merge" : "Every repository is in a version tag", items);
    }

    static Station Database(List<RepoResult> repos, DbConfig db, Func<string, string?> pathOf)
    {
        var items = new List<StationItem>();
        var dbRepo = repos.FirstOrDefault(r => r.Name.Equals(db.Repo, StringComparison.OrdinalIgnoreCase));
        if (dbRepo is null || dbRepo.Db.Count == 0) return new("db", "Database", "skip", "No database scripts", items);
        var path = pathOf(dbRepo.Name);

        foreach (var g in dbRepo.Db.Where(d => d.Warning).GroupBy(d => (d.Service, d.Status)))
        {
            var files = string.Join(", ", g.Select(d => d.File));
            var staged = g.Key.Status.Contains("STAGED", StringComparison.OrdinalIgnoreCase);
            items.Add(new("todo",
                staged ? $"Move {g.Count()} script{(g.Count() == 1 ? "" : "s")} from dev into a release ({g.Key.Service})"
                       : $"Find {g.Count()} script{(g.Count() == 1 ? "" : "s")} missing from every release ({g.Key.Service})",
                staged ? $"{files}. QA only gets scripts that are inside a release folder." : files, dbRepo.Name));
        }

        foreach (var g in dbRepo.Db.Where(d => !d.Warning).GroupBy(d => (d.Service, Release: ReleaseOf(d))).Where(g => g.Key.Release is not null))
        {
            var (svc, release) = (g.Key.Service, g.Key.Release!);
            items.Add(new("check", $"Run {svc} {release} on QA",
                "Build the QA migration for this release and run it on the QA database, if it isn't there yet.", dbRepo.Name));

            // a release whose dev seed has data but whose QA seed files are empty placeholders
            if (path is null) continue;
            var folder = $"{db.Root}/{svc}/{release}/DataSeeding/";
            var seedFiles = Git.Lines(Git.Run(path, "ls-tree", "--name-only", dbRepo.DefaultRef, folder)).ToList();
            int Content(string f) => Git.Run(path, "show", $"{dbRepo.DefaultRef}:{f}").Split('\n')
                .Count(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("--"));
            var dev = seedFiles.Where(f => Path.GetFileName(f).StartsWith("data-seeding-dev")).Sum(Content);
            var qaFiles = seedFiles.Where(f => Path.GetFileName(f).StartsWith("data-seeding-qa")).ToList();
            if (dev > 0 && qaFiles.Count > 0 && qaFiles.All(f => Content(f) == 0))
                items.Add(new("check", $"QA seed data is empty in {svc} {release}",
                    $"The dev seed has {dev} lines of data, but every QA seed file is only a header. Confirm QA doesn't need that data.", dbRepo.Name));
        }
        var todo = items.Count(i => i.State == "todo");
        var runs = items.Count(i => i.Title.StartsWith("Run "));
        return new("db", "Database", Worst(items, "skip"),
            todo > 0 ? $"{todo} script group{(todo == 1 ? "" : "s")} not in a release" : $"{runs} release{(runs == 1 ? "" : "s")} to run on QA", items);
    }

    static string? ReleaseOf(DbItem d)
    {
        if (!d.Location.Equals("dev", StringComparison.OrdinalIgnoreCase)) return d.Location;
        var m = Regex.Match(d.Status, @"release tag (\S+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    // Reads the added lines of the ticket's own commits once: new config keys, new protected endpoints, permission/menu SQL.
    static (List<StationItem> Config, List<StationItem> Access) ScanPatches(List<RepoResult> repos, DbConfig db, Func<string, string?> pathOf)
    {
        var config = new List<StationItem>();
        var access = new List<StationItem>();
        foreach (var r in repos)
        {
            var path = pathOf(r.Name);
            if (path is null) continue;
            var keys = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
            var scopes = 0;
            var sqlAccess = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var c in Own(r))
            {
                string? file = null;
                foreach (var raw in Git.Run(path, "show", "--format=", "--unified=0", "--no-color", c.Sha).Split('\n'))
                {
                    var line = raw.TrimEnd('\r');
                    if (line.StartsWith("+++ ")) { file = line.StartsWith("+++ b/") ? line[6..] : null; continue; }
                    if (file is null || !line.StartsWith('+')) continue;
                    var added = line[1..];
                    if (ConfigFile.IsMatch(file))
                    {
                        var m = file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? JsonKey.Match(added)
                              : file.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ? JsKey.Match(added) : EnvKey.Match(added);
                        if (m.Success) (keys.TryGetValue(file, out var set) ? set : keys[file] = new(StringComparer.Ordinal)).Add(m.Groups[1].Value);
                    }
                    else if (file.EndsWith(".cs") && added.Contains("[AuthorizeScope")) scopes++;
                    else if (file.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) && AccessSql.IsMatch(added)) sqlAccess.Add(file);
                }
            }
            foreach (var (file, set) in keys)
                config.Add(new("check", $"{set.Count} new setting{(set.Count == 1 ? "" : "s")} in {Path.GetFileName(file)}",
                    string.Join(", ", set.Take(12)) + (set.Count > 12 ? $" and {set.Count - 12} more" : ""), r.Name));
            foreach (var f in r.Files.Where(f => f.Value != "D" && PipelineFile.IsMatch(f.Key)))
                config.Add(new("check", $"Pipeline changed: {Path.GetFileName(f.Key)}", f.Key.Contains('/') ? f.Key : null, r.Name));
            if (scopes > 0)
                access.Add(new("check", $"{scopes} new protected endpoint{(scopes == 1 ? "" : "s")}",
                    "The scopes register themselves when the service starts on QA. Make sure the QA roles are granted them.", r.Name));
            foreach (var f in sqlAccess)
                access.Add(new("check", "Grants roles or menus in SQL", f, r.Name));
        }
        return (config, access);
    }

    static Station Access(List<RepoResult> repos, DbConfig db, List<StationItem> fromCode)
    {
        var items = new List<StationItem>(fromCode);
        foreach (var r in repos.Where(r => !r.Name.Equals(db.Repo, StringComparison.OrdinalIgnoreCase)))
            foreach (var f in r.Files.Where(f => f.Value != "D" && AccessFile.IsMatch(f.Key) && !ConfigFile.IsMatch(f.Key)))
                items.Add(new("check", $"Changed: {Path.GetFileName(f.Key)}", f.Key, r.Name));
        return new("access", "Access", items.Count == 0 ? "skip" : "check",
            items.Count == 0 ? "No permission or menu changes found" : "Make sure QA users can reach the feature", items);
    }

    static Station Handover(IssueInfo? issue, List<Station> before)
    {
        if (issue is null)
            return new("qa", "QA", "wait", "Connect Jira to track the handover", [new("wait", "Jira isn't connected")]);
        var inQa = issue.StatusCategory == "done" || Regex.IsMatch(issue.Status, @"(?i)\b(qa|test)");
        var blocked = before.Count(s => s.State is "todo" or "wait");
        // Jira already says QA while the code can't be there yet: the mistake this tool exists to catch
        if (inQa && blocked > 0)
            return new("qa", "QA", "todo", $"Jira says {issue.Status}, but QA can't have it yet",
                [new("todo", $"{blocked} station{(blocked == 1 ? " is" : "s are")} still open",
                     "Finish them before QA starts testing, or move the ticket back in Jira.")]);
        if (inQa) return new("qa", "QA", "done", $"Handed over: {issue.Status}", [new("done", $"Jira status is {issue.Status}")]);
        return blocked > 0
            ? new("qa", "QA", "wait", "Clear the stations before handing over",
                  [new("wait", $"{blocked} station{(blocked == 1 ? "" : "s")} still open", $"Jira status is {issue.Status}.")])
            : new("qa", "QA", "todo", "Ready to hand over",
                  [new("todo", "Move the ticket to Ready for QA and post the handover", $"Jira status is {issue.Status}.", null, "Post handover to Jira", "post:")]);
    }

    // The handover comment and the .md start with the station checklist.
    public static void AddToDoc(Doc doc, List<Station> stations)
    {
        var mark = new Dictionary<string, string> { ["done"] = "[x]", ["todo"] = "[ ]", ["check"] = "[?]", ["wait"] = "[ ]", ["skip"] = "[-]" };
        var blocks = new List<Block> { new Heading(3, "QA preparation") };
        foreach (var s in stations)
        {
            blocks.Add(new Para([new($"{mark[s.State]} {s.Name}: {s.Headline}")]));
            var items = s.Items.Where(i => i.State != "done" || s.State == "done").Select(i => new List<Span>
            {
                new($"{mark[i.State]} {(i.Repo is null ? "" : i.Repo + ": ")}{i.Title}{(i.Detail is null ? "" : " - " + i.Detail)}"),
            }).ToList();
            if (items.Count > 0 && s.State != "skip") blocks.Add(new Bullets(items));
        }
        doc.Blocks.InsertRange(Math.Min(2, doc.Blocks.Count), blocks);
    }
}

record JiraUser(string AccountId, string DisplayName, string Email);

class Jira
{
    const string Fields = "summary,status,issuetype,parent,assignee,fixVersions,updated";
    // Request paths below are relative ("rest/api/..."), so they work against both the site and the API gateway base.
    HttpClient? http;
    readonly string baseUrl;   // the Jira site, used for browse links
    readonly string token = "";
    public bool Configured => http is not null;
    public string Email { get; } = "";
    public JiraUser? Me { get; private set; }

    public Jira(string baseUrl, string? email, string? token)
    {
        this.baseUrl = baseUrl.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(token)) return;
        Email = email.Trim();
        this.token = token.Trim();
        http = Client(this.baseUrl + "/");
    }

    HttpClient Client(string apiBase)
    {
        var c = new HttpClient { BaseAddress = new Uri(apiBase), Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Email}:{token}")));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return c;
    }

    // Tokens created with "Create API token with scopes" only work through the Atlassian API gateway,
    // https://api.atlassian.com/ex/jira/<cloudId>/, not through the site URL.
    async Task<HttpClient?> GatewayClient()
    {
        try
        {
            using var anon = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var cloudId = (string?)JsonNode.Parse(await anon.GetStringAsync($"{baseUrl}/_edge/tenant_info"))?["cloudId"];
            return cloudId is null ? null : Client($"https://api.atlassian.com/ex/jira/{cloudId}/");
        }
        catch { return null; }
    }

    // Confirms the credentials and remembers whose account this is (comments are only ever replaced if they are ours).
    public async Task<JiraUser> Verify()
    {
        if (!Email.Contains('@')) throw new Exception($"\"{Email}\" isn't an email address.");
        if (token.Length < 100)
            throw new Exception($"That token is only {token.Length} characters; Atlassian API tokens are about 190. " +
                                "Make sure you pasted the API token and not your password (browsers sometimes autofill this box).");
        var res = await http!.GetAsync("rest/api/3/myself");
        if (res.StatusCode == HttpStatusCode.Unauthorized && await GatewayClient() is { } gateway)
        {
            var viaGateway = await gateway.GetAsync("rest/api/3/myself");
            if (viaGateway.IsSuccessStatusCode) { http = gateway; res = viaGateway; }
            else if (viaGateway.StatusCode == HttpStatusCode.Forbidden)
                throw new Exception("This is a scoped API token that is missing scopes. Create a new one with read:jira-user, read:jira-work and write:jira-work, or use a classic token (\"Create API token\").");
        }
        if (res.StatusCode == HttpStatusCode.Unauthorized)
        {
            var reason = res.Headers.TryGetValues("X-Seraph-LoginReason", out var v) ? string.Join(",", v) : "";
            if (reason.Contains("AUTHENTICATION_DENIED"))
                throw new Exception("Atlassian is asking for a CAPTCHA after failed attempts. Sign out of Jira in your browser, sign in again, then retry here.");
            var said = (await res.Content.ReadAsStringAsync()).Trim();
            if (said.Length > 200) said = said[..200];
            throw new Exception($"Jira didn't accept {Email} with this token{(said.Length > 0 ? $" (Jira says: {said})" : "")}. " +
                                "Use the email you sign in to Atlassian with (it can differ from the one Jira shows on your profile), " +
                                "and a token created while signed in as that account.");
        }
        if (res.StatusCode == HttpStatusCode.Forbidden)
            throw new Exception("Jira accepted the token but refused access. Your account may not have access to this site.");
        if (!res.IsSuccessStatusCode) throw new Exception($"Jira returned {(int)res.StatusCode}.");
        var j = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        Me = new JiraUser((string)j["accountId"]!, (string?)j["displayName"] ?? Email, (string?)j["emailAddress"] ?? Email);
        return Me;
    }

    public async Task<List<IssueInfo>> Search(string jql, int max = 200)
    {
        var all = new List<IssueInfo>();
        string? next = null;
        do
        {
            var body = new JsonObject
            {
                ["jql"] = jql,
                ["fields"] = new JsonArray(Fields.Split(',').Select(f => (JsonNode)f).ToArray()),
                ["maxResults"] = Math.Min(100, max - all.Count),
            };
            if (next is not null) body["nextPageToken"] = next;
            var res = await http!.PostAsync("rest/api/3/search/jql", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
            if (!res.IsSuccessStatusCode) throw new Exception($"Jira search returned {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
            var page = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
            all.AddRange(page["issues"]!.AsArray().Select(i => Parse(i!)));
            next = (bool?)page["isLast"] == true ? null : (string?)page["nextPageToken"];
        } while (next is not null && all.Count < max);
        return all;
    }

    public async Task<IssueInfo?> GetIssue(string key)
    {
        try
        {
            var res = await http!.GetAsync($"rest/api/3/issue/{key}?fields={Fields}");
            if (!res.IsSuccessStatusCode) { Console.Error.WriteLine($"Jira: could not read {key} ({(int)res.StatusCode})."); return null; }
            return Parse(JsonNode.Parse(await res.Content.ReadAsStringAsync())!);
        }
        catch (Exception e) { Console.Error.WriteLine($"Jira: {e.Message}"); return null; }
    }

    // Every ticket below this one: an epic's stories/tasks/bugs and their sub-tasks, a story's sub-tasks.
    public async Task<List<IssueInfo>> Descendants(string key)
    {
        var found = new List<IssueInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { key };
        var level = new List<string> { key };
        for (var depth = 0; depth < 3 && level.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var chunk in level.Chunk(50))
                foreach (var i in await Search($"parent in ({string.Join(",", chunk)}) ORDER BY key", 500))
                    if (seen.Add(i.Key)) { found.Add(i); next.Add(i.Key); }
            level = next;
        }
        return found;
    }

    // Tickets assigned to me (any status) whose key matches exactly or whose summary contains the words.
    public Task<List<IssueInfo>> SearchMine(string text, bool exactKey)
    {
        var q = text.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var match = exactKey ? $"key = \"{q}\"" : $"summary ~ \"{q}*\"";
        return Search($"assignee = currentUser() AND {match} ORDER BY updated DESC", 25);
    }

    public Task<List<IssueInfo>> MyOpenIssues() =>
        Search("assignee = currentUser() AND statusCategory != Done ORDER BY updated DESC", 200);

    static IssueInfo Parse(JsonNode i)
    {
        var f = i["fields"]!;
        var fix = f["fixVersions"]?.AsArray().Select(v => (string?)v?["name"]).Where(v => v is not null).ToList() ?? [];
        var p = f["parent"];
        return new IssueInfo(
            Key: (string)i["key"]!,
            Summary: (string?)f["summary"] ?? "",
            Type: (string?)f["issuetype"]?["name"] ?? "",
            Level: (int?)f["issuetype"]?["hierarchyLevel"] ?? 0,
            Status: (string?)f["status"]?["name"] ?? "",
            StatusCategory: (string?)f["status"]?["statusCategory"]?["key"] ?? "",
            Assignee: (string?)f["assignee"]?["displayName"] ?? "Unassigned",
            AssigneeId: (string?)f["assignee"]?["accountId"],
            ParentKey: (string?)p?["key"],
            ParentSummary: (string?)p?["fields"]?["summary"],
            ParentType: (string?)p?["fields"]?["issuetype"]?["name"],
            FixVersions: fix.Count == 0 ? "none" : string.Join(", ", fix),
            Updated: ((string?)f["updated"])?[..Math.Min(10, ((string?)f["updated"])!.Length)] ?? "");
    }

    // Replaces our previous manifest comment if there is one, so the ticket always shows a single, current manifest.
    // Only comments written by the connected account are replaced; someone else's manifest comment is left alone.
    public async Task<string> UpsertManifestComment(string key, string wiki)
    {
        var me = Me ?? await Verify();
        string? existing = null;
        for (var start = 0; existing is null; start += 100)
        {
            var page = JsonNode.Parse(await (await http!.GetAsync($"rest/api/2/issue/{key}/comment?startAt={start}&maxResults=100")).EnsureSuccessStatusCode()
                                          .Content.ReadAsStringAsync())!;
            var comments = page["comments"]!.AsArray();
            existing = comments.FirstOrDefault(c => ((string?)c?["body"])?.Contains(Doc.Marker) == true
                                                     && (string?)c?["author"]?["accountId"] == me.AccountId)?["id"]?.ToString();
            if (start + comments.Count >= (int)page["total"]!) break;
        }
        var body = new StringContent(new JsonObject { ["body"] = wiki }.ToJsonString(), Encoding.UTF8, "application/json");
        var res = existing is null
            ? await http!.PostAsync($"rest/api/2/issue/{key}/comment", body)
            : await http!.PutAsync($"rest/api/2/issue/{key}/comment/{existing}", body);
        if (!res.IsSuccessStatusCode)
            throw new Exception($"Jira returned {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        var id = JsonNode.Parse(await res.Content.ReadAsStringAsync())!["id"]!.ToString();
        return $"{baseUrl}/browse/{key}?focusedCommentId={id}";
    }
}

// ───────────────────────────── UI: JSON for the page + a localhost-only server ─────────────────────────────

static class ReportJson
{
    static JsonArray Spans(List<Span> spans) =>
        new(spans.Select(s => (JsonNode)new JsonObject { ["t"] = s.Text, ["code"] = s.Code, ["url"] = s.Url }).ToArray());

    static string? CommitUrl(RepoResult r, Commit c) => r.WebUrl == "" ? null : $"{r.WebUrl}/-/commit/{c.Sha}";

    static JsonObject CommitJson(RepoResult r, Commit c) => new()
    {
        ["sha"] = c.Sha, ["short"] = c.Short, ["date"] = c.Date, ["author"] = c.Author, ["subject"] = c.Subject,
        ["inDefault"] = c.InDefault, ["note"] = c.Note, ["url"] = CommitUrl(r, c), ["ticket"] = c.Ticket,
    };

    public static JsonObject Issue(IssueInfo i) => new()
    {
        ["key"] = i.Key, ["summary"] = i.Summary, ["type"] = i.Type, ["level"] = i.Level,
        ["status"] = i.Status, ["statusCategory"] = i.StatusCategory, ["assignee"] = i.Assignee, ["assigneeId"] = i.AssigneeId,
        ["parentKey"] = i.ParentKey, ["parentSummary"] = i.ParentSummary, ["parentType"] = i.ParentType,
        ["fixVersions"] = i.FixVersions, ["updated"] = i.Updated,
    };

    public static JsonObject Summary(Report rep) => new()
    {
        ["key"] = rep.Key,
        ["generated"] = rep.Generated.ToString("yyyy-MM-dd HH:mm"),
        ["summary"] = rep.Issue?.Summary,
        ["status"] = rep.Issue?.Status,
        ["type"] = rep.Issue?.Type,
        ["children"] = rep.Scope.Count,
        ["warnings"] = rep.Stations.Sum(s => s.Items.Count(i => i.State == "todo")),
        ["repos"] = rep.Repos.Count,
        ["stations"] = new JsonArray(rep.Stations.Select(s => (JsonNode)new JsonObject { ["id"] = s.Id, ["state"] = s.State }).ToArray()),
    };

    static JsonArray StationsJson(List<Station> stations) => new(stations.Select(s => (JsonNode)new JsonObject
    {
        ["id"] = s.Id, ["name"] = s.Name, ["state"] = s.State, ["headline"] = s.Headline,
        ["items"] = new JsonArray(s.Items.Select(i => (JsonNode)new JsonObject
        {
            ["state"] = i.State, ["title"] = i.Title, ["detail"] = i.Detail, ["repo"] = i.Repo,
            ["actionLabel"] = i.ActionLabel, ["actionUrl"] = i.ActionUrl,
        }).ToArray()),
    }).ToArray());

    public static JsonObject Full(Report rep, string jiraBase)
    {
        var own = rep.Repos.SelectMany(r => r.Commits.Where(c => !c.IsForeign)).ToList();
        return new JsonObject
        {
            ["key"] = rep.Key,
            ["generated"] = rep.Generated.ToString("yyyy-MM-dd HH:mm"),
            ["jiraUrl"] = $"{jiraBase}/browse/{rep.Key}",
            ["issue"] = rep.Issue is null ? null : Issue(rep.Issue),
            ["ancestors"] = new JsonArray(rep.Ancestors.Select(a => (JsonNode)Issue(a)).ToArray()),
            ["stations"] = StationsJson(rep.Stations),
            ["scope"] = new JsonArray((rep.Issue is null ? rep.Scope : rep.Scope.Prepend(rep.Issue)).Select(t =>
            {
                var j = Issue(t);
                var a = Manifest.TicketActivity(t.Key, rep.Repos);
                j["commits"] = a.Total;
                j["merged"] = a.Merged;
                j["repos"] = new JsonArray(a.Repos.Select(x => (JsonNode)x).ToArray());
                return (JsonNode)j;
            }).ToArray()),
            ["warnings"] = new JsonArray(rep.Warnings.Select(w => (JsonNode)Spans(w)).ToArray()),
            ["stats"] = new JsonObject
            {
                ["repos"] = rep.Repos.Count,
                ["mergeRequests"] = rep.Repos.Sum(r => r.Merges.Count),
                ["commits"] = own.Count,
                ["merged"] = own.Count(c => c.InDefault),
                ["files"] = rep.Repos.Sum(r => r.Files.Count),
                ["dbScripts"] = rep.Repos.Sum(r => r.Db.Count),
            },
            ["repos"] = new JsonArray(rep.Repos.Select(r => (JsonNode)new JsonObject
            {
                ["name"] = r.Name,
                ["webUrl"] = r.WebUrl == "" ? null : r.WebUrl,
                ["defaultRef"] = r.DefaultRef,
                ["merges"] = new JsonArray(r.Merges.Select(m => (JsonNode)new JsonObject
                {
                    ["label"] = m.MrUrl is null ? $"merge {m.Sha[..8]}" : $"!{m.MrUrl.Split('/')[^1]}",
                    ["url"] = m.MrUrl, ["date"] = m.Date, ["inDefault"] = m.InDefault,
                }).ToArray()),
                ["branches"] = new JsonArray(r.Branches.Select(b => (JsonNode)new JsonObject { ["name"] = b.Name, ["unmerged"] = b.Unmerged }).ToArray()),
                ["commits"] = new JsonArray(r.Commits.Where(c => !c.IsForeign).Select(c => (JsonNode)CommitJson(r, c)).ToArray()),
                ["files"] = new JsonArray(r.Files.Select(f => (JsonNode)new JsonObject { ["status"] = f.Value, ["path"] = f.Key }).ToArray()),
            }).ToArray()),
            ["db"] = new JsonArray(rep.Repos.SelectMany(r => r.Db).Select(i => (JsonNode)new JsonObject
            {
                ["service"] = i.Service, ["location"] = i.Location, ["kind"] = i.Kind,
                ["file"] = i.File, ["status"] = i.Status, ["warning"] = i.Warning,
            }).ToArray()),
            ["highlights"] = new JsonArray(rep.Highlights.Select(h => (JsonNode)new JsonObject
            {
                ["name"] = h.Name,
                ["hits"] = new JsonArray(h.Hits.Select(x => (JsonNode)new JsonObject { ["repo"] = x.Repo, ["path"] = x.Path }).ToArray()),
            }).ToArray()),
            ["foreign"] = new JsonArray(rep.Repos.SelectMany(r => r.Commits.Where(c => c.IsForeign).Select(c =>
            {
                var j = CommitJson(r, c);
                j["repo"] = r.Name;
                j["otherKey"] = c.OtherKey;
                return (JsonNode)j;
            })).ToArray()),
        };
    }
}

static class UiServer
{
    static readonly SemaphoreSlim buildLock = new(1, 1);
    static readonly ConcurrentDictionary<string, Report> cache = new(StringComparer.OrdinalIgnoreCase);

    public static async Task Run(Ledger ledger, bool open)
    {
        HttpListener? listener = null;
        var port = 0;
        for (var p = 5199; p < 5210 && listener is null; p++)
        {
            var l = new HttpListener();
            l.Prefixes.Add($"http://localhost:{p}/");
            try { l.Start(); listener = l; port = p; }
            catch (HttpListenerException) { l.Close(); }
        }
        if (listener is null) { Console.Error.WriteLine("No free port between 5199 and 5209."); return; }

        var url = $"http://localhost:{port}/";
        Console.WriteLine($"feature-ledger UI running at {url}  (Ctrl+C to stop)");
        if (open) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }

        while (listener.IsListening)
        {
            var ctx = await listener.GetContextAsync();
            _ = Task.Run(() => Handle(ledger, ctx));
        }
    }

    static async Task Handle(Ledger ledger, HttpListenerContext ctx)
    {
        var req = ctx.Request;
        var res = ctx.Response;
        try
        {
            // only this machine, and only pages served by this server (blocks DNS rebinding and cross-site posts)
            if (req.Url!.Host is not ("localhost" or "127.0.0.1")) { await Send(res, 403, Err("Forbidden host")); return; }

            var path = req.Url.AbsolutePath.TrimEnd('/');
            var key = req.QueryString["key"]?.Trim().ToUpperInvariant();

            switch (req.HttpMethod, path)
            {
                case ("GET", ""):
                    await SendText(res, 200, File.ReadAllText(Path.Combine(ledger.AppDir, "ui.html")), "text/html; charset=utf-8");
                    return;

                case ("GET", var p) when p.StartsWith("/assets/"):
                {
                    // only plain file names from the assets folder: no sub-paths, no "..", only known types
                    var name = p["/assets/".Length..];
                    var types = new Dictionary<string, string> { [".ttf"] = "font/ttf", [".woff2"] = "font/woff2", [".svg"] = "image/svg+xml" };
                    var file = Path.Combine(ledger.AppDir, "assets", name);
                    if (!Regex.IsMatch(name, @"^[A-Za-z0-9_\-]+\.[a-z0-9]+$") || !types.TryGetValue(Path.GetExtension(name), out var type) || !File.Exists(file))
                    { await Send(res, 404, Err("Not found")); return; }
                    var bytes = await File.ReadAllBytesAsync(file);
                    res.StatusCode = 200; res.ContentType = type; res.ContentLength64 = bytes.Length;
                    res.Headers["Cache-Control"] = "max-age=604800";
                    await res.OutputStream.WriteAsync(bytes);
                    res.Close();
                    return;
                }

                case ("GET", "/api/status"):
                {
                    var me = await ledger.CurrentUser();
                    await Send(res, 200, new JsonObject
                    {
                        ["jiraConfigured"] = me is not null,
                        ["jiraBaseUrl"] = ledger.JiraBaseUrl,
                        ["account"] = me is null ? null : new JsonObject
                        {
                            ["name"] = me.DisplayName, ["email"] = me.Email, ["accountId"] = me.AccountId,
                            ["source"] = ledger.CredentialSource,
                        },
                        ["credentialsRejected"] = ledger.Jira.Configured && me is null,
                        ["keyPattern"] = ledger.KeyPattern,
                        ["reposRoot"] = ledger.ReposRoot,
                        ["repoCount"] = ledger.Repos.Count,
                        ["recent"] = Recent(ledger),
                    });
                    return;
                }

                case ("POST", "/api/account"):
                {
                    if (!Trusted(req)) { await Send(res, 403, Err("Forbidden")); return; }
                    var body = await ReadJson(req);
                    var me = await ledger.Connect((string?)body["baseUrl"] ?? ledger.JiraBaseUrl, (string?)body["email"] ?? "", (string?)body["token"] ?? "");
                    await Send(res, 200, new JsonObject { ["name"] = me.DisplayName, ["email"] = me.Email });
                    return;
                }

                case ("POST", "/api/account/disconnect"):
                    if (!Trusted(req)) { await Send(res, 403, Err("Forbidden")); return; }
                    ledger.Disconnect();
                    await Send(res, 200, new JsonObject { ["ok"] = true, ["stillConfigured"] = ledger.Jira.Configured });
                    return;

                case ("POST", "/api/settings"):
                {
                    if (!Trusted(req)) { await Send(res, 403, Err("Forbidden")); return; }
                    var body = await ReadJson(req);
                    if ((string?)body["reposRoot"] is { Length: > 0 } root) ledger.SetReposRoot(root);
                    await Send(res, 200, new JsonObject { ["reposRoot"] = ledger.ReposRoot, ["repoCount"] = ledger.Repos.Count });
                    return;
                }

                case ("GET", "/api/my-tickets"):
                {
                    if (await ledger.CurrentUser() is null) { await Send(res, 401, Err("Connect your Jira account first")); return; }
                    var issues = await ledger.Jira.MyOpenIssues();
                    var built = Recent(ledger, 1000).Select(n => n!).ToDictionary(n => (string)n["key"]!, n => n, StringComparer.OrdinalIgnoreCase);
                    await Send(res, 200, new JsonArray(issues.Select(i =>
                    {
                        var j = ReportJson.Issue(i);
                        if (built.TryGetValue(i.Key, out var b)) j["lastBuild"] = b.DeepClone();
                        return (JsonNode)j;
                    }).ToArray()));
                    return;
                }

                case ("GET", "/api/search"):
                {
                    if (await ledger.CurrentUser() is null) { await Send(res, 401, Err("Connect your Jira account first")); return; }
                    var q = (req.QueryString["q"] ?? "").Trim();
                    if (q.Length < 2) { await Send(res, 200, new JsonArray()); return; }
                    var exact = ledger.IsValidKey(q.ToUpperInvariant());
                    var hits = await ledger.Jira.SearchMine(exact ? q.ToUpperInvariant() : q, exact);
                    await Send(res, 200, new JsonArray(hits.Select(i => (JsonNode)ReportJson.Issue(i)).ToArray()));
                    return;
                }

                case ("GET", "/api/manifest"):
                {
                    if (key is null || !ledger.IsValidKey(key)) { await Send(res, 400, Err($"Enter a key matching {ledger.KeyPattern}")); return; }
                    var fetch = req.QueryString["fetch"] == "1";
                    var children = req.QueryString["children"] != "0";
                    var onlyMine = req.QueryString["mine"] != "0";
                    await buildLock.WaitAsync();
                    try
                    {
                        var rep = await ledger.Build(key, fetch, msg => Console.WriteLine($"[{key}] {msg}"), children, onlyMine);
                        cache[key] = rep;
                        await Send(res, 200, ReportJson.Full(rep, ledger.JiraBaseUrl));
                    }
                    finally { buildLock.Release(); }
                    return;
                }

                case ("GET", "/api/markdown"):
                {
                    var file = key is null || !ledger.IsValidKey(key) ? null : Path.Combine(ledger.OutDir, $"{key}.md");
                    if (file is null || !File.Exists(file)) { await Send(res, 404, Err("Build the manifest first")); return; }
                    if (req.QueryString["download"] == "1") res.AddHeader("Content-Disposition", $"attachment; filename=\"{key}.md\"");
                    await SendText(res, 200, File.ReadAllText(file), "text/markdown; charset=utf-8");
                    return;
                }

                case ("POST", "/api/post"):
                {
                    if (!Trusted(req)) { await Send(res, 403, Err("Missing X-Ledger header")); return; }
                    if (key is null || !ledger.IsValidKey(key)) { await Send(res, 400, Err("Invalid key")); return; }
                    if (await ledger.CurrentUser() is null) { await Send(res, 400, Err("Connect your Jira account first (Account button, top right).")); return; }
                    if (!cache.TryGetValue(key, out var rep)) { await Send(res, 400, Err("Build the manifest first")); return; }
                    var commentUrl = await ledger.Jira.UpsertManifestComment(key, rep.Doc.JiraWiki());
                    await Send(res, 200, new JsonObject { ["url"] = commentUrl });
                    return;
                }

                default:
                    await Send(res, 404, Err("Not found"));
                    return;
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            try { await Send(res, 500, Err(e.Message)); } catch { }
        }
    }

    static JsonArray Recent(Ledger ledger, int take = 30)
    {
        if (!Directory.Exists(ledger.OutDir)) return [];
        return new JsonArray(Directory.GetFiles(ledger.OutDir, "*.json")
            .Select(f => { try { return JsonNode.Parse(File.ReadAllText(f)); } catch { return null; } })
            .Where(n => n?["key"] is not null)
            .OrderByDescending(n => (string?)n!["generated"])
            .Take(take)
            .Select(n => n!)
            .ToArray());
    }

    // State-changing calls must come from our own page: a custom header can't be sent cross-site without CORS, which we never allow.
    static bool Trusted(HttpListenerRequest req) => req.Headers["X-Ledger"] == "1";

    static async Task<JsonNode> ReadJson(HttpListenerRequest req)
    {
        using var reader = new StreamReader(req.InputStream, Encoding.UTF8);
        var text = await reader.ReadToEndAsync();
        return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text) ?? new JsonObject();
    }

    static JsonObject Err(string message) => new() { ["error"] = message };

    static Task Send(HttpListenerResponse res, int status, JsonNode body) =>
        SendText(res, status, body.ToJsonString(), "application/json; charset=utf-8");

    static async Task SendText(HttpListenerResponse res, int status, string body, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        res.StatusCode = status;
        res.ContentType = contentType;
        res.Headers["Cache-Control"] = "no-store";
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }
}
