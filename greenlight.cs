// Greenlight: is a ticket ready to go live? Builds a per-ticket manifest from git history across all local repos,
// writes it to out/<KEY>.md and optionally posts/updates it as a comment on the Jira ticket.
//
//   dotnet run greenlight.cs -- MM-1234            build the manifest (local only)
//   dotnet run greenlight.cs -- MM-1234 --fetch    git fetch all repos first
//   dotnet run greenlight.cs -- MM-1234 --post     also create/update the manifest comment on Jira
//   dotnet run greenlight.cs -- --ui               open the browser UI on http://localhost:5199
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
    Console.Error.WriteLine($"Usage: dotnet run greenlight.cs -- <KEY matching {ledger.KeyPattern}> [--fetch] [--post]");
    Console.Error.WriteLine("       dotnet run greenlight.cs -- --ui");
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
              string Markdown, string MarkdownPath, List<Station> Stations, SubTickets Subs,
              IReadOnlyDictionary<string, TagDetail> TagDetails);

// Sub-tickets in Jira under this ticket, whatever the toggle says. Possible=false: sub-tasks can't have any, or Jira isn't connected.
record SubTickets(bool Possible, int Total, int Mine);

class Ledger
{
    public string AppDir { get; }
    public string KeyPattern { get; }
    public double DailyTargetHours { get; }
    public string ReminderAt { get; }   // "17:00": the page reminds you if today is short
    public string? TeamJql { get; }
    public HashSet<int> WorkDays { get; }   // 0 = Sunday ... 6 = Saturday
    public string OutDir { get; }
    public Jira Jira { get; private set; } = null!;
    public GitLab GitLab { get; private set; } = null!;
    public string GitLabBaseUrl { get; private set; } = "https://gitlab.com";
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
        cfg = JsonNode.Parse(File.ReadAllText(Path.Combine(appDir, "greenlight.json")))!;
        KeyPattern = (string)cfg["keyPattern"]!;
        DailyTargetHours = (double?)cfg["time"]?["dailyTargetHours"] ?? 5;
        ReminderAt = (string?)cfg["time"]?["reminderAt"] ?? "17:00";
        TeamJql = (string?)cfg["team"]?["jql"];
        WorkDays = cfg["time"]?["workDays"]?.AsArray().Select(n => (int)n!).ToHashSet() ?? [1, 2, 3, 4, 5];
        OutDir = Path.Combine(appDir, "out");
        var db = cfg["db"]!;
        dbCfg = new DbConfig((string)db["repo"]!, (string)db["root"]!, (string)db["stagingFolder"]!);
        highlights = cfg["highlights"]!.AsObject().Select(kv => (Name: kv.Key, Rx: new Regex((string)kv.Value!))).ToList();
        JiraBaseUrl = (settings.JiraBaseUrl ?? (string)cfg["jira"]!["baseUrl"]!).TrimEnd('/');
        LoadRepos(settings.ReposRoot ?? Path.GetFullPath(Path.Combine(appDir, (string)cfg["reposRoot"]!)));
        LoadJira();
        GitLabBaseUrl = (settings.GitLabBaseUrl ?? GuessGitLab() ?? GitLabBaseUrl).TrimEnd('/');
        LoadGitLab();
    }

    // ── GitLab account (code review): a personal access token, also kept in Windows Credential Manager ──

    static string GitLabTarget(string baseUrl) => Migrated($"greenlight-gitlab:{new Uri(baseUrl).Host}", $"feature-ledger-gitlab:{new Uri(baseUrl).Host}");

    // the GitLab host the repositories are cloned from
    string? GuessGitLab() => Repos.Select(r => Git.WebUrl(r)).Where(u => u.StartsWith("https://"))
        .Select(u => new Uri(u)).Select(u => $"{u.Scheme}://{u.Host}").FirstOrDefault();

    void LoadGitLab() => GitLab = new GitLab(GitLabBaseUrl, CredStore.Read(GitLabTarget(GitLabBaseUrl))?.Secret);

    public async Task<GitLabUser> ConnectGitLab(string baseUrl, string token)
    {
        baseUrl = baseUrl.Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) || u.Scheme != "https")
            throw new Exception("GitLab address must be an https URL, e.g. https://gitlab.com");
        var candidate = new GitLab(baseUrl, token.Trim());
        if (!candidate.Configured) throw new Exception("Paste your GitLab personal access token.");
        var me = await candidate.Verify();   // saved only if GitLab accepts it
        CredStore.Write(GitLabTarget(baseUrl), me.Username, token.Trim());
        settings.GitLabBaseUrl = baseUrl;
        settings.Save();
        GitLabBaseUrl = baseUrl;
        GitLab = candidate;
        return me;
    }

    public void DisconnectGitLab() { CredStore.Delete(GitLabTarget(GitLabBaseUrl)); LoadGitLab(); }

    public async Task<GitLabUser?> GitLabUser()
    {
        if (!GitLab.Configured) return null;
        try { return GitLab.Me ?? await GitLab.Verify(); } catch { return null; }
    }

    public string? RepoForProject(string? pathWithNamespace) =>
        pathWithNamespace is null ? null : RepoPath(pathWithNamespace.Split('/')[^1]);

    // ── per-user account: each person connects their own Jira account; the token lives in their Windows Credential Manager ──

    static string CredTarget(string baseUrl) => Migrated($"greenlight:{new Uri(baseUrl).Host}", $"feature-ledger:{new Uri(baseUrl).Host}");

    // the tool used to be called feature-ledger: a token saved under the old name moves to the new one, once
    static string Migrated(string target, string old)
    {
        if (CredStore.Read(target) is null && CredStore.Read(old) is { } c) { CredStore.Write(target, c.User, c.Secret); CredStore.Delete(old); }
        return target;
    }

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
        RepoIndex.Warm(Repos);
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
        myEmails = null;
    }

    // Only repositories the ledger knows are ever opened or read on request from the page.
    public string? RepoPath(string? name) =>
        name is null ? null : Repos.FirstOrDefault(r => string.Equals(Path.GetFileName(r), name, StringComparison.OrdinalIgnoreCase));

    // Emails that count as "me" for commit authorship: the git identities configured in the repos, plus the Jira account.
    List<string>? myEmails;
    public List<string> MyEmails()
    {
        if (myEmails is not null) return myEmails;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // each repository's own identity, read from its config file (no process); then the global one, once
        foreach (var e in Repos.AsParallel().Select(r =>
                 {
                     try
                     {
                         var cfgFile = Path.Combine(r, ".git", "config");
                         if (!File.Exists(cfgFile)) return "";
                         var m = Regex.Match(File.ReadAllText(cfgFile), @"(?ms)^\[user\][^\[]*?^\s*email\s*=\s*(\S+)");
                         return m.Success ? m.Groups[1].Value : "";
                     }
                     catch { return ""; }
                 }))
            if (e != "") set.Add(e);
        if (Repos.Count > 0 && Git.Run(Repos[0], "config", "--global", "user.email").Trim() is { Length: > 0 } g) set.Add(g);
        if (Jira.Me?.Email is { Length: > 0 } je) set.Add(je);
        return myEmails = set.ToList();
    }

    public bool IsValidKey(string key) => Regex.IsMatch(key, $"^(?:{KeyPattern})$");

    public async Task<Report> Build(string key, bool fetch, Action<string> progress, bool includeChildren = true, bool onlyMine = false, Action<JsonObject>? emit = null)
    {
        emit ??= _ => { };
        // GREENLIGHT_TIMING=1 prints how long each phase took
        var clock = Stopwatch.StartNew();
        var timing = Environment.GetEnvironmentVariable("GREENLIGHT_TIMING") == "1";
        void Lap(string what) { if (timing) Console.Error.WriteLine($"[timing] {key} {what}: {clock.ElapsedMilliseconds} ms"); clock.Restart(); }
        IssueInfo? issue = null;
        var scope = new List<IssueInfo>();
        var ancestors = new List<IssueInfo>();
        var subs = new SubTickets(false, 0, 0);
        if (Jira.Configured)
        {
            issue = await Jira.GetIssue(key);
            // the parent chain is read while the child tickets are being read
            var chain = Task.Run(async () =>
            {
                var up = new List<IssueInfo>();
                for (var (pk, depth) = (issue?.ParentKey, 0); pk is not null && depth < 3; depth++)
                {
                    if (await Jira.GetIssue(pk) is not { } p) break;
                    up.Insert(0, p);
                    pk = p.ParentKey;
                }
                return up;
            });
            // sub-tasks can't have children; for anything else the counts are always looked up, so the UI can
            // say "no sub-tickets" or "none are yours" even while they're switched off
            if (issue is not null && issue.Level >= 0)
            {
                progress($"Reading child tickets of {key} from Jira...");
                var all = await Jira.Descendants(key);
                var meId = (await CurrentUser())?.AccountId;
                var mine = all.Where(s => s.AssigneeId == meId).ToList();
                subs = new SubTickets(true, all.Count, mine.Count);
                // other people's sub-tickets are left out: their keys aren't scanned, so their commits don't count either
                if (includeChildren) scope = onlyMine && meId is not null ? mine : all;
            }
            ancestors.AddRange(await chain);
        }
        // the ticket itself is known now: the page can show it while git is still being read
        emit(new JsonObject
        {
            ["type"] = "issue", ["key"] = key, ["issue"] = issue is null ? null : ReportJson.Issue(issue),
            ["ancestors"] = new JsonArray(ancestors.Select(a => (JsonNode)ReportJson.Issue(a)).ToArray()),
            ["subTickets"] = new JsonObject { ["possible"] = subs.Possible, ["total"] = subs.Total, ["mine"] = subs.Mine, ["included"] = scope.Count },
            ["jiraUrl"] = $"{JiraBaseUrl}/browse/{key}",
        });
        void Step(string phase, int done) => emit(new JsonObject { ["type"] = "progress", ["phase"] = phase, ["done"] = done, ["total"] = Repos.Count });

        if (fetch)
        {
            progress($"Fetching {Repos.Count} repos...");
            var fetched = 0;
            Step("fetch", 0);
            Parallel.ForEach(Repos, new ParallelOptions { MaxDegreeOfParallelism = 10 }, r => { Git.Run(r, "fetch", "--all", "--prune", "--quiet"); Git.Forget(r); Step("fetch", Interlocked.Increment(ref fetched)); });
        }

        var keys = new HashSet<string>(scope.Select(s => s.Key).Prepend(key), StringComparer.OrdinalIgnoreCase);
        Lap("jira + fetch");
        progress($"Scanning {Repos.Count} repos for {keys.Count} ticket(s)...");
        var alternation = string.Join("|", keys.OrderByDescending(k => k.Length).Select(Regex.Escape));
        var keyRx = new Regex($@"(?<![A-Za-z0-9])(?:{alternation})(?!\d)", RegexOptions.IgnoreCase);
        var anyKeyRx = new Regex($@"(?<![A-Za-z0-9])(?:{KeyPattern})(?!\d)", RegexOptions.IgnoreCase);
        var found = new ConcurrentBag<RepoResult>();
        var scanned = 0;
        Step("scan", 0);
        Parallel.ForEach(Repos, r => { var res = Scanner.Scan(r, keys, keyRx, anyKeyRx, dbCfg); if (res is not null) found.Add(res); Step("scan", Interlocked.Increment(ref scanned)); });
        var results = found.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();

        Lap("scan");
        progress("Working out what QA needs...");
        var paths = Repos.ToDictionary(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
        var tagDetails = new ConcurrentDictionary<string, TagDetail>(StringComparer.OrdinalIgnoreCase);
        var stations = QaPrep.Compute(issue, results, dbCfg, name => paths.GetValueOrDefault(name), tagDetails,
            s => emit(new JsonObject { ["type"] = "station", ["station"] = ReportJson.Station(s) }));

        Lap("steps");
        var doc = Manifest.Build(key, issue, scope, results, highlights, dbCfg, (int)cfg["jiraMaxFilesPerRepo"]!);
        QaPrep.AddToDoc(doc, stations);

        Directory.CreateDirectory(OutDir);
        var mdPath = Path.Combine(OutDir, $"{key}.md");
        var markdown = doc.Markdown();
        File.WriteAllText(mdPath, markdown);

        var report = new Report(key, DateTime.Now, issue, scope, ancestors, results, doc, Manifest.Warnings(key, results),
                                Manifest.Highlights(results, highlights, dbCfg), markdown, mdPath, stations, subs, tagDetails);
        File.WriteAllText(Path.Combine(OutDir, $"{key}.json"), ReportJson.Summary(report).ToJsonString());
        Lap("manifest + save");
        return report;
    }

    static string? Env(string name) =>
        Environment.GetEnvironmentVariable(name) ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
}

// Per-user settings, outside the repo: %APPDATA%\greenlight\settings.json
class UserSettings
{
    static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "greenlight", "settings.json");
    static readonly string OldPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "feature-ledger", "settings.json");
    public string? JiraBaseUrl { get; set; }
    public string? ReposRoot { get; set; }
    public string? GitLabBaseUrl { get; set; }

    public static UserSettings Load()
    {
        try
        {
            var j = JsonNode.Parse(File.ReadAllText(File.Exists(FilePath) ? FilePath : OldPath))!;   // settings saved before the rename still count
            return new UserSettings { JiraBaseUrl = (string?)j["jiraBaseUrl"], ReposRoot = (string?)j["reposRoot"], GitLabBaseUrl = (string?)j["gitlabBaseUrl"] };
        }
        catch { return new UserSettings(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, new JsonObject { ["jiraBaseUrl"] = JiraBaseUrl, ["reposRoot"] = ReposRoot, ["gitlabBaseUrl"] = GitLabBaseUrl }.ToJsonString(new() { WriteIndented = true }));
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
                CredentialBlob = blob, CredentialBlobSize = secret.Length * 2, Comment = "Greenlight token",
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
    // git's empty tree, to diff a file against "before it existed"
    public static string EmptyTree(string repo) => RunWithInput(repo, "", "hash-object", "-t", "tree", "--stdin").Trim();

    public static string PatchId(string repo, string sha) =>
        RunWithInput(repo, Run(repo, "diff-tree", "-p", "--no-color", sha), "patch-id", "--stable").Split(' ')[0].Trim();

    // One git process for many commits: `git log --no-walk` with the shas on stdin, split back per commit.
    // The format must start with %x1e%H so each record begins with its sha.
    public static Dictionary<string, string> Many(string repo, IEnumerable<string> shas, params string[] args)
    {
        var list = shas.Distinct().ToList();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (list.Count == 0) return map;
        var text = Cached(repo, string.Join("\n", list) + "\n", ["log", "--no-walk=unsorted", "--stdin", .. args]);
        foreach (var rec in text.Split('\x1e', StringSplitOptions.RemoveEmptyEntries))
        {
            var nl = rec.IndexOfAny(['\n', '\x1f']);
            var sha = (nl < 0 ? rec : rec[..nl]).Trim();
            if (sha.Length >= 40) map[sha[..40]] = rec;
        }
        return map;
    }

    // patch id per commit (same change under any sha), for many commits in two processes
    public static Dictionary<string, string> PatchIds(string repo, IEnumerable<string> shas)
    {
        var list = shas.Distinct().ToList();
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (list.Count == 0) return map;
        var patches = Cached(repo, string.Join("\n", list) + "\n", "log", "--no-walk=unsorted", "--stdin", "-p", "--no-color", "--format=commit %H");
        foreach (var l in Lines(Cached(repo, patches, "patch-id", "--stable")))
            if (l.Split(' ') is [var pid, var sha]) map[sha] = pid;
        return map;
    }

    // changes whenever any branch, remote branch or tag moves; cheap enough to ask every check
    // Every branch, remote branch and tag with the commit it points at, and a fingerprint of them all.
    // Remembered until a file watcher sees the repository's refs change (or a minute passes, as a safety net),
    // so an unchanged repository costs no git process at all.
    public record RefSet(string Print, List<(string Sha, string Name)> Refs);
    static readonly ConcurrentDictionary<string, (DateTime At, RefSet Value)> refSets = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, FileSystemWatcher> watchers = new(StringComparer.OrdinalIgnoreCase);

    public static RefSet Refs(string repo)
    {
        Watch(repo);
        if (refSets.TryGetValue(repo, out var p) && DateTime.UtcNow - p.At < TimeSpan.FromSeconds(watchers.ContainsKey(repo) ? 60 : 5)) return p.Value;
        var text = Run(repo, "for-each-ref", "--format=%(objectname)%09%(refname)", "refs/heads", "refs/remotes", "refs/tags");
        var refs = Lines(text).Select(l => l.Split('\t')).Where(x => x.Length == 2).Select(x => (x[0], x[1])).ToList();
        var v = new RefSet(Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(text))), refs);
        refSets[repo] = (DateTime.UtcNow, v);
        return v;
    }
    public static string Fingerprint(string repo) => Refs(repo).Print;
    public static void Forget(string repo) => refSets.TryRemove(repo, out _);

    static void Watch(string repo)
    {
        var dir = Path.Combine(repo, ".git");
        if (watchers.ContainsKey(repo) || !Directory.Exists(dir)) return;   // a worktree's .git is a file: fall back to the 5 s memo
        try
        {
            var w = new FileSystemWatcher(dir) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.DirectoryName };
            void Changed(object? _, FileSystemEventArgs e)
            {
                var rel = e.FullPath[dir.Length..].TrimStart('\\', '/');
                if (rel.StartsWith("refs") || rel.StartsWith("packed-refs") || rel.StartsWith("HEAD") || rel.StartsWith("FETCH_HEAD")) Forget(repo);
            }
            w.Changed += Changed; w.Created += Changed; w.Deleted += Changed; w.Renamed += (s, e) => Changed(s, e);
            w.Error += (_, _) => { Forget(repo); };
            w.EnableRaisingEvents = true;
            if (!watchers.TryAdd(repo, w)) w.Dispose();
        }
        catch { /* no watcher: the short memo still applies */ }
    }

    // A read whose answer depends only on the repository's objects and refs: remembered while they don't change.
    static readonly ConcurrentDictionary<string, string> reads = new();
    public static string Cached(string repo, string? input, params string[] args)
    {
        var id = $"{repo}\x1f{Fingerprint(repo)}\x1f{string.Join('\x1f', args)}\x1f{(input is null ? "" : Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(input))))}";
        if (reads.TryGetValue(id, out var hit)) return hit;
        var v = RunWithInput(repo, input, args);
        if (reads.Count > 3000) reads.Clear();
        return reads[id] = v;
    }

    // default branch and remote address, read from the .git folder when possible (no process)
    public static string DefaultRef(string repo)
    {
        try
        {
            var f = Path.Combine(repo, ".git", "refs", "remotes", "origin", "HEAD");
            if (File.Exists(f) && File.ReadAllText(f).Trim() is var t && t.StartsWith("ref: refs/remotes/")) return t["ref: refs/remotes/".Length..];
        }
        catch { }
        var r = Run(repo, "symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD").Trim();
        return r == "" ? "HEAD" : r;
    }

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

// ───────────────────────────── commit index: every repository's history, in memory ─────────────────────────────

record IndexedCommit(string Sha, string[] Parents, string Date, string Subject, string Body, string Email = "", long At = 0)
{
    public string Message => Body.Length == 0 ? Subject : Subject + "\n" + Body;
}

// One `git log` over all branches, remote branches and tags, kept until those refs move. Ticket checks search it
// in memory instead of walking history with git for every ticket, every repository, every time.
sealed class RepoIndex
{
    public required string Print { get; init; }
    public required string DefaultRef { get; init; }
    public required string Web { get; init; }
    public required List<(string Sha, string Name)> Refs { get; init; }
    public required List<IndexedCommit> Commits { get; init; }
    public required Dictionary<string, IndexedCommit> BySha { get; init; }

    // commits reachable from a tip, walked in memory
    public HashSet<string> Reachable(string tip, HashSet<string>? stopAt = null)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var todo = new Stack<string>();
        todo.Push(tip);
        while (todo.Count > 0)
        {
            var s = todo.Pop();
            if (stopAt?.Contains(s) == true || !seen.Add(s)) continue;
            if (BySha.TryGetValue(s, out var c)) foreach (var p in c.Parents) todo.Push(p);
        }
        return seen;
    }

    HashSet<string>? onDefault;
    public HashSet<string> OnDefault(string repo) => onDefault ??=
        Refs.FirstOrDefault(r => r.Name == $"refs/remotes/{DefaultRef}" || r.Name == $"refs/heads/{DefaultRef}") is { Sha.Length: > 0 } tip
            ? Reachable(tip.Sha)
            : Git.Lines(Git.Run(repo, "rev-list", DefaultRef)).ToHashSet(StringComparer.Ordinal);

    static readonly ConcurrentDictionary<string, RepoIndex> all = new(StringComparer.OrdinalIgnoreCase);

    public static RepoIndex For(string repo)
    {
        var refs = Git.Refs(repo);
        if (all.TryGetValue(repo, out var ix) && ix.Print == refs.Print) return ix;
        var commits = new List<IndexedCommit>();
        foreach (var r in Git.Records(Git.Run(repo, "log", "--branches", "--remotes", "--tags", "--date=short", "--format=%x1e%H%x1f%P%x1f%ad%x1f%ae%x1f%at%x1f%s%x1f%b")))
            if (r.Length >= 7)
                commits.Add(new IndexedCommit(r[0], r[1].Split(' ', StringSplitOptions.RemoveEmptyEntries), r[2], r[5], r[6].Trim(), r[3].Trim(), long.TryParse(r[4], out var at) ? at : 0));
        var bySha = new Dictionary<string, IndexedCommit>(commits.Count, StringComparer.Ordinal);
        foreach (var c in commits) bySha.TryAdd(c.Sha, c);
        ix = new RepoIndex { Print = refs.Print, DefaultRef = Git.DefaultRef(repo), Web = Git.WebUrl(repo), Refs = refs.Refs, Commits = commits, BySha = bySha };
        all[repo] = ix;
        return ix;
    }

    // built for every repository when Greenlight starts, so the first check is as quick as the rest
    public static void Warm(IEnumerable<string> repos) =>
        Task.Run(() => Parallel.ForEach(repos, new ParallelOptions { MaxDegreeOfParallelism = 12 }, r => { try { For(r); } catch { } }));
}

// ───────────────────────────── scanning ─────────────────────────────

record DbConfig(string Repo, string Root, string Staging);
record Commit(string Sha, string Short, string Author, string Date, string Subject, string Source, bool InDefault,
              string? OtherKey, List<(string Status, string Path)> Files)
{
    public string? Note { get; set; }   // e.g. carries this ticket's change under another ticket's message
    public string? Ticket { get; set; } // which key in scope this commit belongs to (story, sub-task, ...)
    public string AuthorEmail { get; set; } = "";
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
    // A repository whose branches and tags haven't moved gives the same answer for the same keys: reuse it.
    static readonly ConcurrentDictionary<string, RepoResult?> cache = new();

    public static RepoResult? Scan(string repo, IReadOnlySet<string> keys, Regex keyRx, Regex anyKeyRx, DbConfig dbCfg)
    {
        var id = $"{repo}|{string.Join(",", keys.Order(StringComparer.OrdinalIgnoreCase))}|{Git.Fingerprint(repo)}";
        if (cache.TryGetValue(id, out var hit)) return hit;
        var res = ScanFresh(repo, keys, keyRx, anyKeyRx, dbCfg);
        if (cache.Count > 4000) cache.Clear();
        cache[id] = res;
        return res;
    }

    static RepoResult? ScanFresh(string repo, IReadOnlySet<string> keys, Regex keyRx, Regex anyKeyRx, DbConfig dbCfg)
    {
        var ix = RepoIndex.For(repo);
        var defaultRef = ix.DefaultRef;
        var web = ix.Web;
        // everything on the default branch, merges included, walked in memory and only when something matched
        var inDefaultL = new Lazy<HashSet<string>>(() => ix.OnDefault(repo));
        var sources = new Dictionary<string, string>();
        var via = new Dictionary<string, string>();   // sha -> key of the branch/merge that brought it in
        string? KeyIn(string text) => keyRx.Match(text) is { Success: true } m ? m.Value.ToUpperInvariant() : null;
        bool Mentions(string text) { foreach (var k in keys) if (text.Contains(k, StringComparison.OrdinalIgnoreCase)) return true; return false; }

        // 1. commits whose message mentions a key (branches, remote branches and tags; never stash entries)
        // 2. commits brought in by merges of a branch named after a key
        var merges = new List<MergeInfo>();
        foreach (var c in ix.Commits)
        {
            var msg = c.Message;
            if (!Mentions(msg)) continue;
            if (c.Parents.Length < 2)
            {
                if (KeyIn(msg) is { } msgKey && sources.TryAdd(c.Sha, "message")) via[c.Sha] = msgKey;
                continue;
            }
            var m = MergeSubject.Match(c.Subject);
            if (!m.Success || KeyIn(m.Groups[1].Value) is not { } mergeKey) continue;   // skips "Merge branch 'main' into MM-…"
            // what the merge brought in: reachable from the merged branch but not from the first parent
            var before = ix.Reachable(c.Parents[0]);
            foreach (var sha in ix.Reachable(c.Parents[1], before))
                if (ix.BySha.TryGetValue(sha, out var bc) && bc.Parents.Length < 2 && sources.TryAdd(sha, "merge")) via[sha] = mergeKey;
            var mr = MergeRequest.Match(c.Body);
            var mrUrl = mr.Success && web != "" ? $"{web}/-/merge_requests/{mr.Groups[2].Value}" : null;
            merges.Add(new MergeInfo(c.Sha, c.Date, m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : null, mrUrl,
                                     inDefaultL.Value.Contains(c.Sha)));
        }

        // 3. branches named after the key: work not yet in the default branch.
        //    --cherry-pick drops commits whose patch already landed under another sha (rebased / squashed MRs).
        var refs = ix.Refs.Select(r => r.Name).Where(n => n.StartsWith("refs/heads/") || n.StartsWith("refs/remotes/"))
                      .Select(n => n.StartsWith("refs/heads/") ? n["refs/heads/".Length..] : n["refs/remotes/".Length..])
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

        var inDefault = inDefaultL.Value;
        // every matched commit in one git process: header fields, then its files
        var shown = Git.Many(repo, sources.Keys, "--date=short", "--name-status", "-M", "--format=%x1e%H%x1f%h%x1f%an%x1f%ad%x1f%s%x1f%ae%x1f%at");

        // A commit outside the default branch whose patch is already in it counts as merged (rebased or squashed
        // MRs). Same rule as `rev-list --cherry-pick`, done once for all commits: compare patch ids against what
        // landed on the default branch since the oldest of them was written.
        var outside = sources.Keys.Where(s => !inDefault.Contains(s)).ToList();
        var landed = new HashSet<string>(StringComparer.Ordinal);
        if (outside.Count > 0)
        {
            var oldest = outside.Select(s => shown.TryGetValue(s, out var t) && long.TryParse(t.Split('\n')[0].Split('\x1f').ElementAtOrDefault(6)?.Trim(), out var at) ? at : 0)
                                .DefaultIfEmpty(0).Min();
            // the same change touches the same files, so only main-branch commits touching them can be twins
            var touched = outside.SelectMany(s => shown.TryGetValue(s, out var t) ? t.Split('\n').Skip(1).Select(l => l.TrimEnd('\r').Split('\t')).Where(x => x.Length >= 2 && x[0].Length > 0).Select(x => x[^1]) : [])
                                 .Distinct(StringComparer.Ordinal).ToList();
            string[] since = ["rev-list", "--no-merges", $"--since={Math.Max(0, oldest - 86400)}", defaultRef];
            var upstream = Git.Lines(Git.Cached(repo, null, touched.Count > 0 && touched.Sum(t => t.Length + 3) < 20000 ? [.. since, "--", .. touched] : since)).ToList();
            if (upstream.Count > 0)
            {
                var ids = Git.PatchIds(repo, outside.Concat(upstream));
                var onDefault = upstream.Select(s => ids.GetValueOrDefault(s)).Where(p => p is not null).ToHashSet(StringComparer.Ordinal);
                foreach (var s in outside) if (ids.TryGetValue(s, out var pid) && onDefault.Contains(pid)) landed.Add(s);
            }
        }

        var commits = new List<Commit>();
        var copies = new List<string>();
        foreach (var (sha, source) in sources)
        {
            var merged = inDefault.Contains(sha) || landed.Contains(sha);
            if (merged && !inDefault.Contains(sha)) { copies.Add(sha); continue; }   // copy of a patch already in the default branch
            if (!shown.TryGetValue(sha, out var text)) continue;
            var lines = text.Split('\n');
            var head = lines[0].Split('\x1f').Skip(1).ToArray();   // drop the full sha
            if (head.Length < 4) continue;
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
                AuthorEmail = head.Length > 4 ? head[4].Trim() : "",
            });
        }
        commits = commits.OrderBy(c => c.Date).ThenBy(c => c.Short).ToList();

        // A skipped copy that names this ticket, whose merged twin has another message: say so on the twin,
        // otherwise this ticket's change would look like someone else's work.
        if (copies.Count > 0)
        {
            var candidates = commits.Where(c => c.InDefault && !keyRx.IsMatch(c.Subject)).ToList();
            var ids = Git.PatchIds(repo, candidates.Select(c => c.Sha).Concat(copies));
            var twins = candidates.Where(c => ids.ContainsKey(c.Sha)).GroupBy(c => ids[c.Sha])
                                  .ToDictionary(g => g.Key, g => g.First());
            foreach (var sha in copies)
            {
                var subject = shown.TryGetValue(sha, out var t) ? t.Split('\n')[0].Split('\x1f').ElementAtOrDefault(4) ?? "" : "";
                if (KeyIn(subject) is not { } copyKey || !ids.TryGetValue(sha, out var pid) || !twins.TryGetValue(pid, out var twin)) continue;
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
    public const string Marker = "Greenlight handover";
    public const string OldMarker = "Feature manifest (feature-ledger)";   // comments posted before the rename
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
                 string FixVersions, string Updated)
{
    public string Priority { get; init; } = "";
    public List<string> Labels { get; init; } = [];
    public List<string> Components { get; init; } = [];
    public string? Sprint { get; init; }        // the current sprint, else the latest one the ticket was in
    public string? SprintState { get; init; }   // active, future, closed
    public string? Due { get; init; }
}

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
    internal static readonly Regex ConfigFile = new(@"(?i)(appsettings[^/]*\.json$|(^|/)config\.[^/]*\.js$|(^|/)\.env(\.[^/]*)?$)");
    internal static readonly Regex PipelineFile = new(@"(?i)(\.gitlab-ci\.yml$|(^|/)dockerfile$|(^|/)downstream-env\.yml$)");
    internal static readonly Regex AccessFile = new(@"(?i)(permission|scope|role|polic|claim|menu|sidebar|navigation)");
    internal static readonly Regex JsonKey = new(@"^\s*""([A-Za-z0-9_.:\-]+)""\s*:");
    internal static readonly Regex JsKey = new(@"^\s*([A-Za-z_$][A-Za-z0-9_$]*)\s*:");
    internal static readonly Regex EnvKey = new(@"^\s*([A-Z][A-Z0-9_]*)\s*=");
    internal static readonly Regex AccessSql = new(@"(?i)\bmember\.(menu\w*|scope_role|menu_action_scope|role\w*|service_scope)\b");

    public static List<Station> Compute(IssueInfo? issue, List<RepoResult> repos, DbConfig db, Func<string, string?> pathOf,
                                        ConcurrentDictionary<string, TagDetail>? tagsOut = null, Action<Station>? onStation = null)
    {
        Station Out(Station s) { onStation?.Invoke(s); return s; }
        // the slow reads (each repository's patches and tags) run for all repositories at once; the steps below
        // then find them in the cache instead of waiting on one repository after another
        Parallel.ForEach(repos, new ParallelOptions { MaxDegreeOfParallelism = 12 }, r =>
        {
            if (pathOf(r.Name) is not { } path) return;
            var own = Own(r).ToList();
            if (own.Count == 0) return;
            Git.Many(path, own.Select(c => c.Sha), "-p", "--unified=0", "--no-color", "--format=%x1e%H");
            if (!r.Name.Equals(db.Repo, StringComparison.OrdinalIgnoreCase) && own.All(c => c.InDefault)) Tags.For(path, own.Select(c => c.Sha), r.WebUrl);
        });
        var code = Out(Code(repos));
        var tag = Out(Tag(repos, db, pathOf, tagsOut));
        var (cfgItems, accessFromCode) = ScanPatches(repos, db, pathOf);
        var database = Out(Database(repos, db, pathOf));
        var access = Out(Access(repos, db, accessFromCode));
        var config = Out(new Station("config", "Config", cfgItems.Count == 0 ? "skip" : "check",
            cfgItems.Count == 0 ? "No settings changed" : "QA settings live outside git: add the new keys there", cfgItems));
        var handover = Out(Handover(issue, [code, tag, database, access, config]));
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
    static Station Tag(List<RepoResult> repos, DbConfig db, Func<string, string?> pathOf, ConcurrentDictionary<string, TagDetail>? tagsOut)
    {
        var items = new List<StationItem>();
        foreach (var r in repos.Where(r => !r.Name.Equals(db.Repo, StringComparison.OrdinalIgnoreCase) && Own(r).Any()))
        {
            var path = pathOf(r.Name);
            var own = Own(r).ToList();
            if (own.Any(c => !c.InDefault) || path is null) { items.Add(new("wait", "Waiting for the code to merge", null, r.Name)); continue; }

            var t = Tags.For(path, own.Select(c => c.Sha), r.WebUrl);
            tagsOut?.TryAdd(r.Name, t);
            if (t.Containing.Count > 0)
            {
                var first = t.Containing[0];
                items.Add(new("done", $"In tag {first.Name}",
                    $"Tagged {first.Date}." + (t.Latest?.Name is { } l && l != first.Name ? $" Also in {t.Containing.Count - 1} later tag{(t.Containing.Count == 2 ? "" : "s")}, up to {l}." : " This is the latest tag."),
                    r.Name, "View tag", first.Url));
            }
            else
            {
                var newTag = r.WebUrl == "" ? null : $"{r.WebUrl}/-/tags/new?ref={Uri.EscapeDataString(Target(r))}" + (t.Next is null ? "" : $"&tag_name={Uri.EscapeDataString(t.Next)}");
                items.Add(new("todo", t.Next is null ? $"Cut a new version tag from {Target(r)}" : $"Cut tag {t.Next} from {Target(r)}",
                    t.Latest is null ? "The merged code isn't in any tag yet." : $"The merged code isn't in any tag yet. Latest tag is {t.Latest.Name}, from {t.Latest.Date}.",
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
            var seedFiles = Git.Lines(Git.Cached(path, null, "ls-tree", "--name-only", dbRepo.DefaultRef, folder)).ToList();
            int Content(string f) => Git.Cached(path, null, "show", $"{dbRepo.DefaultRef}:{f}").Split('\n')
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
            var patches = Git.Many(path, Own(r).Select(c => c.Sha), "-p", "--unified=0", "--no-color", "--format=%x1e%H");
            foreach (var c in Own(r))
            {
                string? file = null;
                foreach (var raw in patches.GetValueOrDefault(c.Sha, "").Split('\n'))
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

// ───────────────────────────── opening repositories and files on this computer ─────────────────────────────

static class LocalApps
{
    static readonly Regex Sha = new(@"^[0-9a-f]{7,40}$");

    // A path inside a repository: relative, no drive, no "..", no wildcards.
    public static string? SafeFile(string repo, string? rel)
    {
        if (string.IsNullOrWhiteSpace(rel) || Path.IsPathRooted(rel) || rel.Contains("..") || rel.IndexOfAny(['*', '?', '"', '<', '>', '|', ':']) >= 0) return null;
        var full = Path.GetFullPath(Path.Combine(repo, rel));
        return full.StartsWith(Path.GetFullPath(repo).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    public static bool IsCommit(string repo, string? sha) =>
        sha is not null && Sha.IsMatch(sha) && Git.Run(repo, "cat-file", "-t", sha).Trim() == "commit";

    // The solution Visual Studio should open: a .sln in the repository root, or one level down.
    public static string? Solution(string? repo)
    {
        if (repo is null || !Directory.Exists(repo)) return null;
        var sln = Directory.GetFiles(repo, "*.sln").FirstOrDefault()
               ?? Directory.GetDirectories(repo).Where(d => !Path.GetFileName(d).StartsWith('.'))
                           .SelectMany(d => Directory.GetFiles(d, "*.sln")).FirstOrDefault();
        return sln is null ? null : Path.GetRelativePath(repo, sln).Replace('\\', '/');
    }

    static string? codeExe, devenvExe;

    // VS Code's own executable understands the same arguments as the "code" command (-g, --diff), without a shell.
    static string? VsCode()
    {
        if (codeExe is not null) return codeExe;
        var candidates = new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe"),
        }.ToList();
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            if (File.Exists(Path.Combine(dir, "code.cmd"))) candidates.Add(Path.GetFullPath(Path.Combine(dir, "..", "Code.exe")));
        return codeExe = candidates.FirstOrDefault(File.Exists);
    }

    static string? VisualStudio()
    {
        if (devenvExe is not null) return devenvExe;
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere)) return null;
        var psi = new ProcessStartInfo(vswhere) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-latest", "-prerelease", "-property", "productPath" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var path = p.StandardOutput.ReadToEnd().Trim().Split('\n').FirstOrDefault()?.Trim();
        p.WaitForExit();
        return devenvExe = !string.IsNullOrEmpty(path) && File.Exists(path) ? path : null;
    }

    static void Launch(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }

    // app: code | vs | folder. file: optional path inside the repo.
    public static void Open(string repo, string app, string? file, int line)
    {
        switch (app)
        {
            case "code":
                var code = VsCode() ?? throw new Exception("VS Code isn't installed on this computer.");
                if (file is null) Launch(code, repo); else Launch(code, repo, "-g", $"{file}:{Math.Max(1, line)}");
                break;
            case "vs":
                var vs = VisualStudio() ?? throw new Exception("Visual Studio isn't installed on this computer.");
                if (file is not null) Launch(vs, "/edit", file);
                else if (Solution(repo) is { } sln) Launch(vs, Path.Combine(repo, sln));
                else Launch(vs, repo);
                break;
            case "folder":
                if (file is not null && File.Exists(file)) Launch("explorer.exe", $"/select,{file}");
                else Launch("explorer.exe", file is not null ? Path.GetDirectoryName(file)! : repo);
                break;
            default: throw new Exception("Unknown app.");
        }
    }

    // Oldest first by commit time (several commits on one day must not come out in any order).
    static List<string> Chronological(string repo, IEnumerable<string> shas) =>
        shas.Select(s => (s, t: long.TryParse(Git.Run(repo, "show", "-s", "--format=%ct", s).Trim(), out var t) ? t : 0))
            .OrderBy(x => x.t).Select(x => x.s).ToList();

    // The file before a commit; empty when the commit created it.
    static string Before(string repo, string sha, string rel) =>
        Git.Run(repo, "rev-parse", "--verify", "--quiet", $"{sha}^") is { Length: > 0 } ? $"{sha}^" : Git.EmptyTree(repo);

    // What each commit changed in one file (oldest first), plus the combined change from before the first to after the last.
    public static JsonObject Diff(string repo, string rel, IEnumerable<string> shas)
    {
        var ordered = Chronological(repo, shas);
        var commits = new JsonArray(ordered.Select(sha =>
        {
            var head = Git.Run(repo, "show", "-s", "--date=short", "--format=%h%x1f%an%x1f%ad%x1f%s", sha).Trim().Split('\x1f');
            var patch = Git.Run(repo, "show", "--format=", "--no-color", "-M", "--unified=3", sha, "--", rel);
            return (JsonNode)new JsonObject
            {
                ["sha"] = sha, ["short"] = head.ElementAtOrDefault(0), ["author"] = head.ElementAtOrDefault(1),
                ["date"] = head.ElementAtOrDefault(2), ["subject"] = head.ElementAtOrDefault(3), ["patch"] = patch,
            };
        }).ToArray());
        var net = Git.Run(repo, "diff", "--no-color", "-M", "--unified=3", Before(repo, ordered[0], rel), ordered[^1], "--", rel);
        return new JsonObject { ["commits"] = commits, ["net"] = net };
    }

    // Opens the file as it was before the first of these commits next to how it is after the last, in VS Code's diff view.
    public static void OpenDiff(string repo, string rel, List<string> shas)
    {
        var code = VsCode() ?? throw new Exception("VS Code isn't installed on this computer.");
        var ordered = Chronological(repo, shas);
        var first = ordered[0]; var last = ordered[^1];
        var dir = Path.Combine(Path.GetTempPath(), "greenlight", "diff", Path.GetFileName(repo), first[..8] + "-" + last[..8]);
        Directory.CreateDirectory(dir);
        var name = Path.GetFileName(rel);
        var before = Path.Combine(dir, "before " + name);
        var after = Path.Combine(dir, "after " + name);
        File.WriteAllText(before, Git.Run(repo, "show", $"{first}^:{rel}"));
        File.WriteAllText(after, Git.Run(repo, "show", $"{last}:{rel}"));
        Launch(code, "--diff", before, after);
    }
}

// ───────────────────────────── version tags (QA deploys tags) ─────────────────────────────

record TagRef(string Name, string Date, string? Url);
record TagDetail(TagRef? Latest, List<TagRef> Containing, string? Next);

static class Tags
{
    static readonly Regex Version = new(@"^(?<pre>[^\d]*)(?<nums>\d+(?:\.\d+)*)(?<post>.*)$");

    // newest version first, compared by its numbers whatever the prefix ("v5.0.84" and "5.0.10" sort together);
    // a hotfix tag on an old line (4.6.18.1.8 made today) doesn't count as the latest release
    static List<TagRef> List(string repo, string web, params string[] filter) =>
        Git.Lines(Git.Run(repo, ["for-each-ref", "refs/tags", "--format=%(refname:short)%09%(creatordate:short)", .. filter]))
            .Select(l => l.Split('\t')).Where(p => p.Length == 2)
            .Select(p => new TagRef(p[0], p[1], web == "" ? null : $"{web}/-/tags/{Uri.EscapeDataString(p[0])}"))
            .OrderByDescending(t => Numbers(t.Name), VersionOrder.Instance).ThenByDescending(t => t.Date, StringComparer.Ordinal).ToList();

    static long[] Numbers(string tag)
    {
        var m = Version.Match(tag);
        return m.Success ? m.Groups["nums"].Value.Split('.').Select(n => long.TryParse(n, out var v) ? v : 0).ToArray() : [];
    }

    sealed class VersionOrder : IComparer<long[]>
    {
        public static readonly VersionOrder Instance = new();
        public int Compare(long[]? a, long[]? b)
        {
            a ??= []; b ??= [];
            for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                var c = (i < a.Length ? a[i] : -1).CompareTo(i < b.Length ? b[i] : -1);
                if (c != 0) return c;
            }
            return 0;
        }
    }

    // Tags that hold every one of the commits (oldest first), the repository's newest tag, and the version that would come next.
    static readonly ConcurrentDictionary<string, TagDetail> cache = new();

    public static TagDetail For(string repo, IEnumerable<string> shas, string web)
    {
        var list0 = shas.Distinct().Order(StringComparer.Ordinal).ToList();
        var id = $"{repo}|{Git.Fingerprint(repo)}|{string.Join(",", list0)}";
        if (cache.TryGetValue(id, out var hit)) return hit;
        if (cache.Count > 4000) cache.Clear();
        return cache[id] = Compute(repo, list0, web);
    }

    static TagDetail Compute(string repo, List<string> shas, string web)
    {
        var all = List(repo, web);
        var latest = all.FirstOrDefault();
        HashSet<string>? common = null;
        // a tag holding the newest commits of each line of work holds their ancestors too: only those need asking
        var list = shas.Distinct().ToList();
        if (list.Count > 1) list = Git.Lines(Git.Run(repo, ["merge-base", "--independent", .. list])).ToList();
        foreach (var sha in list)
        {
            var with = List(repo, web, "--contains", sha).Select(t => t.Name).ToHashSet();
            common = common is null ? with : common.Where(with.Contains).ToHashSet();
            if (common.Count == 0) break;
        }
        var containing = all.Where(t => common?.Contains(t.Name) == true).Reverse().ToList();
        return new TagDetail(latest, containing, containing.Count == 0 ? Bump(latest?.Name) : null);
    }

    // 2.0.41 -> 2.0.42, v5.0.84 -> v5.0.85
    public static string? Bump(string? tag)
    {
        if (tag is null) return null;
        var m = Version.Match(tag);
        if (!m.Success) return null;
        var nums = m.Groups["nums"].Value.Split('.');
        nums[^1] = (long.Parse(nums[^1]) + 1).ToString();
        return m.Groups["pre"].Value + string.Join('.', nums) + m.Groups["post"].Value;
    }

    public static JsonObject Json(TagDetail t) => new()
    {
        ["latest"] = t.Latest is null ? null : Ref(t.Latest),
        ["containing"] = new JsonArray(t.Containing.Select(r => (JsonNode)Ref(r)).ToArray()),
        ["next"] = t.Next,
    };
    static JsonObject Ref(TagRef r) => new() { ["name"] = r.Name, ["date"] = r.Date, ["url"] = r.Url };
}

// ───────────────────────────── GitLab: merge requests you review, and the reviews on yours ─────────────────────────────

record GitLabUser(long Id, string Username, string Name);

class GitLab
{
    readonly HttpClient? http;
    public string BaseUrl { get; }
    public bool Configured => http is not null;
    public GitLabUser? Me { get; private set; }

    public GitLab(string baseUrl, string? token)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(token)) return;
        http = new HttpClient { BaseAddress = new Uri(BaseUrl + "/api/v4/"), Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Add("PRIVATE-TOKEN", token.Trim());
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    static async Task<JsonNode> Read(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        if (res.IsSuccessStatusCode) return JsonNode.Parse(text.Length == 0 ? "{}" : text)!;
        var why = (string?)(JsonNode.Parse(text.StartsWith('{') ? text : "{}")?["message"] as JsonValue) ?? text;
        throw new Exception(res.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "GitLab rejected the token. Create a new personal access token.",
            HttpStatusCode.Forbidden => "Your GitLab token isn't allowed to do that. It needs the api scope.",
            HttpStatusCode.NotFound => "GitLab couldn't find that merge request or project.",
            _ => $"GitLab returned {(int)res.StatusCode}: {why}",
        });
    }

    Task<JsonNode> Get(string path) => http!.GetAsync(path).ContinueWith(t => Read(t.Result)).Unwrap();
    Task<JsonNode> Post(string path, JsonObject? body = null) =>
        http!.PostAsync(path, new StringContent((body ?? new JsonObject()).ToJsonString(), Encoding.UTF8, "application/json")).ContinueWith(t => Read(t.Result)).Unwrap();

    async Task<JsonArray> All(string path)
    {
        var all = new JsonArray();
        for (var page = 1; page <= 50; page++)   // up to 5,000 items; past that GitLab's own UI is the better tool
        {
            var sep = path.Contains('?') ? '&' : '?';
            var res = await http!.GetAsync($"{path}{sep}per_page=100&page={page}");
            var arr = (await Read(res)).AsArray();
            foreach (var n in arr.ToList()) { arr.Remove(n); all.Add(n); }
            if (!res.Headers.TryGetValues("X-Next-Page", out var next) || string.IsNullOrEmpty(next.FirstOrDefault())) break;
        }
        return all;
    }

    public async Task<GitLabUser> Verify()
    {
        var u = await Get("user");
        return Me = new GitLabUser((long)u["id"]!, (string)u["username"]!, (string?)u["name"] ?? (string)u["username"]!);
    }

    static string P(string project) => Uri.EscapeDataString(project);
    string Mr(string project, long iid) => $"projects/{P(project)}/merge_requests/{iid}";

    public Task<JsonArray> ReviewRequests(string username) =>
        All($"merge_requests?scope=all&state=opened&reviewer_username={Uri.EscapeDataString(username)}&order_by=updated_at");
    public Task<JsonNode> MergeRequest(string project, long iid) => Get(Mr(project, iid));
    public Task<JsonNode> Approvals(string project, long iid) => Get($"{Mr(project, iid)}/approvals");
    public async Task<JsonArray> Reviewers(string project, long iid) { try { return (await Get($"{Mr(project, iid)}/reviewers")).AsArray(); } catch { return []; } }
    public Task<JsonArray> Commits(string project, long iid) => All($"{Mr(project, iid)}/commits");
    public Task<JsonArray> Discussions(string project, long iid) => All($"{Mr(project, iid)}/discussions");
    public async Task<JsonArray> Diffs(string project, long iid)
    {
        try { return await All($"{Mr(project, iid)}/diffs"); }
        catch { return (await Get($"{Mr(project, iid)}/changes"))["changes"]!.AsArray(); }   // GitLab before 15.7
    }
    public Task<JsonArray> ForBranch(string project, string branch) =>
        All($"projects/{P(project)}/merge_requests?state=all&source_branch={Uri.EscapeDataString(branch)}");
    // environments and what was last deployed to each (QA, staging, production, ...)
    public Task<JsonArray> Environments(string project) => All($"projects/{P(project)}/environments?states=available");
    public Task<JsonNode> Environment(string project, long id) => Get($"projects/{P(project)}/environments/{id}");
    public Task<JsonArray> MyOpenMergeRequests(string username) =>
        All($"merge_requests?scope=all&state=opened&author_username={Uri.EscapeDataString(username)}&order_by=updated_at");
    public async Task<JsonNode?> LatestPipeline(string project, long iid)
    {
        try { return (await Get($"{Mr(project, iid)}/pipelines?per_page=1")).AsArray().FirstOrDefault(); } catch { return null; }
    }
    public Task<JsonArray> Events(long userId, DateOnly after, DateOnly before) =>
        All($"users/{userId}/events?after={after:yyyy-MM-dd}&before={before:yyyy-MM-dd}");
    public Task<JsonNode> CreateMergeRequest(string project, string source, string target, string title) =>
        Post($"projects/{P(project)}/merge_requests", new JsonObject { ["source_branch"] = source, ["target_branch"] = target, ["title"] = title, ["remove_source_branch"] = true });
    public Task<JsonNode> CreateTag(string project, string name, string @ref, string message) =>
        Post($"projects/{P(project)}/repository/tags", new JsonObject { ["tag_name"] = name, ["ref"] = @ref, ["message"] = message });

    public Task<JsonNode> Approve(string project, long iid) => Post($"{Mr(project, iid)}/approve");
    public Task<JsonNode> Unapprove(string project, long iid) => Post($"{Mr(project, iid)}/unapprove");
    public Task<JsonNode> Comment(string project, long iid, string body) => Post($"{Mr(project, iid)}/notes", new JsonObject { ["body"] = body });
}

// ───────────────────────────── code review: what a reviewer needs, built from GitLab and local git ─────────────────────────────

static class Review
{
    // "https://gitlab.com/group/sub/project/-/merge_requests/12" -> "group/sub/project"
    public static string Project(JsonNode mr)
    {
        var url = (string)mr["web_url"]!;
        var cut = url.IndexOf("/-/", StringComparison.Ordinal);
        return new Uri(cut > 0 ? url[..cut] : url).AbsolutePath.Trim('/');
    }

    static string? Key(Ledger l, params string?[] texts)
    {
        var rx = new Regex($@"(?<![A-Za-z0-9])(?:{l.KeyPattern})(?!\d)", RegexOptions.IgnoreCase);
        return texts.Select(t => t is null ? null : rx.Match(t)).FirstOrDefault(m => m?.Success == true)?.Value.ToUpperInvariant();
    }

    static JsonObject Person(JsonNode? u) => new() { ["name"] = (string?)u?["name"], ["username"] = (string?)u?["username"], ["avatar"] = (string?)u?["avatar_url"] };

    public static JsonObject Summary(Ledger l, JsonNode mr, JsonNode? approvals, string? me)
    {
        var approvedBy = approvals?["approved_by"]?.AsArray().Select(a => (string?)a?["user"]?["username"]).ToList() ?? [];
        return new JsonObject
        {
            ["project"] = Project(mr), ["repo"] = Project(mr).Split('/')[^1], ["iid"] = (long)mr["iid"]!,
            ["title"] = (string?)mr["title"], ["url"] = (string?)mr["web_url"],
            ["source"] = (string?)mr["source_branch"], ["target"] = (string?)mr["target_branch"],
            ["author"] = Person(mr["author"]), ["draft"] = (bool?)mr["draft"] ?? false,
            ["state"] = (string?)mr["state"], ["mergeStatus"] = (string?)mr["detailed_merge_status"] ?? (string?)mr["merge_status"],
            ["conflicts"] = (bool?)mr["has_conflicts"] ?? false, ["updated"] = (string?)mr["updated_at"],
            ["comments"] = (int?)mr["user_notes_count"] ?? 0,
            ["key"] = Key(l, (string?)mr["source_branch"], (string?)mr["title"]),
            ["approvedByMe"] = me is not null && approvedBy.Contains(me),
            ["approvals"] = approvals is null ? null : new JsonObject { ["left"] = (int?)approvals["approvals_left"] ?? 0, ["by"] = new JsonArray(approvedBy.Select(a => (JsonNode?)a).ToArray()) },
        };
    }

    // the merge requests waiting for my review
    public static async Task<JsonArray> Inbox(Ledger l)
    {
        var me = await l.GitLabUser() ?? throw new Exception("Connect your GitLab account first.");
        var mrs = await l.GitLab.ReviewRequests(me.Username);
        var gate = new SemaphoreSlim(6);
        var rows = await Task.WhenAll(mrs.Select(async mr =>
        {
            await gate.WaitAsync();
            try { return Summary(l, mr!, await Try(() => l.GitLab.Approvals(Project(mr!), (long)mr!["iid"]!)), me.Username); }
            finally { gate.Release(); }
        }));
        // the Jira ticket behind each one, in one search
        var keys = rows.Select(r => (string?)r["key"]).Where(k => k is not null).Distinct().ToList();
        if (keys.Count > 0 && await l.CurrentUser() is not null)
        {
            var issues = (await Try(() => l.Jira.Search($"key in ({string.Join(",", keys)})", keys.Count)))?.ToDictionary(i => i.Key) ?? [];
            foreach (var r in rows) if ((string?)r["key"] is { } k && issues.TryGetValue(k, out var i)) r["issue"] = ReportJson.Issue(i);
        }
        return new JsonArray(rows.Select(r => (JsonNode)r).ToArray());
    }

    static async Task<T?> Try<T>(Func<Task<T>> f) where T : class { try { return await f(); } catch { return null; } }

    public static JsonArray Discussions(JsonArray raw) => new(raw.Select(d =>
    {
        var notes = d!["notes"]!.AsArray().Where(n => (bool?)n?["system"] != true).Select(n => (JsonNode)new JsonObject
        {
            ["id"] = (long)n!["id"]!, ["author"] = Person(n["author"]), ["body"] = (string?)n["body"], ["created"] = (string?)n["created_at"],
            ["resolvable"] = (bool?)n["resolvable"] ?? false, ["resolved"] = (bool?)n["resolved"] ?? false,
            ["path"] = (string?)n["position"]?["new_path"] ?? (string?)n["position"]?["old_path"],
            ["newLine"] = (int?)n["position"]?["new_line"], ["oldLine"] = (int?)n["position"]?["old_line"],
        }).ToArray();
        if (notes.Length == 0) return null;
        var resolvable = notes.Where(n => (bool)n!["resolvable"]!).ToList();
        return (JsonNode)new JsonObject
        {
            ["id"] = (string?)d["id"], ["notes"] = new JsonArray(notes),
            ["resolvable"] = resolvable.Count > 0, ["resolved"] = resolvable.Count > 0 && resolvable.All(n => (bool)n!["resolved"]!),
        };
    }).Where(d => d is not null).ToArray());

    public static JsonArray Reviewers(JsonArray reviewers, JsonNode mr) => new((reviewers.Count > 0
        ? reviewers.Select(r => (JsonNode)new JsonObject { ["user"] = Person(r!["user"]), ["state"] = (string?)r["state"] ?? "unreviewed" })
        : (mr["reviewers"]?.AsArray() ?? []).Select(u => (JsonNode)new JsonObject { ["user"] = Person(u), ["state"] = "unreviewed" })).ToArray());

    // everything for one merge request
    public static async Task<JsonObject> Full(Ledger l, string project, long iid)
    {
        var me = await l.GitLabUser() ?? throw new Exception("Connect your GitLab account first.");
        var gl = l.GitLab;
        var mrT = gl.MergeRequest(project, iid);
        var apT = Try(() => gl.Approvals(project, iid));
        var rvT = gl.Reviewers(project, iid);
        var cmT = gl.Commits(project, iid);
        var dfT = gl.Diffs(project, iid);
        var dsT = gl.Discussions(project, iid);
        await Task.WhenAll(mrT, apT, rvT, cmT, dfT, dsT);
        var mr = mrT.Result;
        var sum = Summary(l, mr, apT.Result, me.Username);
        var key = (string?)sum["key"];

        var commits = new JsonArray(cmT.Result.Select(c => (JsonNode)new JsonObject
        {
            ["sha"] = (string?)c!["id"], ["short"] = (string?)c["short_id"], ["subject"] = (string?)c["title"],
            ["author"] = (string?)c["author_name"], ["email"] = (string?)c["author_email"], ["date"] = ((string?)c["created_at"])?[..10],
            ["url"] = (string?)c["web_url"], ["hasKey"] = Key(l, (string?)c["title"], (string?)c["message"]) is not null,
        }).ToArray());

        var files = new JsonArray(dfT.Result.Select(f =>
        {
            var diff = (string?)f!["diff"] ?? "";
            var lines = diff.Split('\n');
            return (JsonNode)new JsonObject
            {
                ["old"] = (string?)f["old_path"], ["path"] = (string?)f["new_path"],
                ["status"] = (bool?)f["new_file"] == true ? "A" : (bool?)f["deleted_file"] == true ? "D" : (bool?)f["renamed_file"] == true ? "R" : "M",
                ["adds"] = lines.Count(x => x.StartsWith('+')), ["dels"] = lines.Count(x => x.StartsWith('-')), ["diff"] = diff,
            };
        }).ToArray());

        var discussions = Discussions(dsT.Result);
        var repo = l.RepoForProject(project);
        TagDetail? tags = null;
        if (repo is not null)
        {
            var shas = commits.Select(c => (string?)c!["sha"]).Where(s => s is not null && LocalApps.IsCommit(repo, s)).Select(s => s!).ToList();
            tags = shas.Count > 0 ? Tags.For(repo, shas, Git.WebUrl(repo)) : Tags.For(repo, [], Git.WebUrl(repo)) with { Containing = [] };
        }
        var issue = key is not null && await l.CurrentUser() is not null ? await l.Jira.GetIssue(key) : null;

        sum["description"] = (string?)mr["description"];
        sum["pipeline"] = mr["head_pipeline"] is JsonNode p ? new JsonObject { ["status"] = (string?)p["status"], ["url"] = (string?)p["web_url"] } : null;
        sum["reviewers"] = Reviewers(rvT.Result, mr);
        sum["commits"] = commits;
        sum["files"] = files;
        sum["discussions"] = discussions;
        sum["checklist"] = Checklist(l, mr, sum, commits, files, discussions, repo);
        sum["tags"] = tags is null ? null : Tags.Json(tags);
        sum["local"] = repo is not null;
        sum["sln"] = LocalApps.Solution(repo);
        sum["issue"] = issue is null ? null : ReportJson.Issue(issue);
        sum["me"] = me.Username;
        sum["jiraUrl"] = key is null ? null : $"{l.JiraBaseUrl}/browse/{key}";
        return sum;
    }

    static JsonObject Item(string state, string title, string? detail = null, string? url = null) =>
        new() { ["state"] = state, ["title"] = title, ["detail"] = detail, ["url"] = url };

    static JsonArray Checklist(Ledger l, JsonNode mr, JsonObject sum, JsonArray commits, JsonArray files, JsonArray discussions, string? repo)
    {
        var c = new JsonArray();
        void Add(JsonObject i) => c.Add(i);

        if ((bool?)sum["draft"] == true) Add(Item("todo", "Still a draft", "The author hasn't marked it ready for review."));
        Add(((string?)sum["key"]) is { } key ? Item("done", $"Linked to {key}") : Item("todo", "No Jira ticket key", "Neither the branch nor the title contains a ticket key."));
        var noKey = commits.Where(x => (bool?)x!["hasKey"] == false).Select(x => (string?)x!["subject"]).ToList();
        if (noKey.Count > 0) Add(Item("check", $"{noKey.Count} commit{(noKey.Count == 1 ? " has" : "s have")} no ticket key", string.Join(" · ", noKey.Take(4))));

        if ((bool?)sum["conflicts"] == true) Add(Item("todo", "Has merge conflicts", "The author needs to resolve them before it can merge."));
        switch ((string?)sum["mergeStatus"])
        {
            case "mergeable": Add(Item("done", "Ready to merge")); break;
            case "need_rebase": Add(Item("todo", "Needs a rebase", $"The branch is behind {(string?)sum["target"]}.")); break;
            case "discussions_not_resolved": Add(Item("todo", "Unresolved discussions block the merge")); break;
            case "ci_still_running": Add(Item("wait", "Pipeline still running")); break;
            case "ci_must_pass": Add(Item("todo", "The pipeline must pass first")); break;
            case "not_approved": Add(Item("check", "Waiting for approvals", $"{(int?)sum["approvals"]?["left"] ?? 0} more needed.")); break;
            case "draft_status": case "conflict": case null: break;
            case var other: Add(Item("check", $"Merge status: {other.Replace('_', ' ')}")); break;
        }

        var pipe = mr["head_pipeline"];
        Add((string?)pipe?["status"] switch
        {
            "success" => Item("done", "Pipeline passed", null, (string?)pipe!["web_url"]),
            "failed" => Item("todo", "Pipeline failed", "Look at the failing job before approving.", (string?)pipe!["web_url"]),
            "running" or "pending" or "created" => Item("wait", "Pipeline is running", null, (string?)pipe!["web_url"]),
            null => Item("check", "No pipeline ran for the latest commit"),
            var s => Item("check", $"Pipeline: {s}", null, (string?)pipe!["web_url"]),
        });

        if (repo is not null)
        {
            var def = Git.Run(repo, "symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD").Trim().Replace("origin/", "");
            if (def != "" && def != (string?)sum["target"]) Add(Item("check", $"Targets {(string?)sum["target"]}, not {def}", $"{def} is the branch this service is built from."));
        }

        var unresolved = discussions.Count(d => (bool)d!["resolvable"]! && !(bool)d["resolved"]!);
        if (unresolved > 0) Add(Item("todo", $"{unresolved} unresolved discussion{(unresolved == 1 ? "" : "s")}"));

        // what the change itself carries to QA
        int adds = 0, dels = 0, scopes = 0;
        var cfgKeys = new SortedSet<string>(StringComparer.Ordinal);
        var stagedSql = new List<string>(); var releaseSql = new SortedSet<string>(); var grants = new List<string>(); var pipeFiles = new List<string>();
        var codeChanged = false; var testsChanged = false;
        foreach (var f in files)
        {
            var path = (string)f!["path"]!; var diff = (string?)f["diff"] ?? "";
            adds += (int)f["adds"]!; dels += (int)f["dels"]!;
            if (Regex.IsMatch(path, @"(?i)(test|spec)")) testsChanged = true;
            else if (Regex.IsMatch(path, @"(?i)\.(cs|ts|tsx|js|jsx)$")) codeChanged = true;
            var parts = path.Split('/');
            if (path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) && parts.Length >= 3 && parts[0] == "DBMigrations")
            {
                if (parts[2] == "dev") stagedSql.Add(path); else releaseSql.Add($"{parts[1]} {parts[2]}");
            }
            if (QaPrep.PipelineFile.IsMatch(path)) pipeFiles.Add(path);
            foreach (var line in diff.Split('\n').Where(x => x.StartsWith('+')).Select(x => x[1..]))
            {
                if (QaPrep.ConfigFile.IsMatch(path))
                {
                    var m = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? QaPrep.JsonKey.Match(line) : path.EndsWith(".js") ? QaPrep.JsKey.Match(line) : QaPrep.EnvKey.Match(line);
                    if (m.Success) cfgKeys.Add(m.Groups[1].Value);
                }
                if (path.EndsWith(".cs") && line.Contains("[AuthorizeScope")) scopes++;
                if (path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) && QaPrep.AccessSql.IsMatch(line)) grants.Add(path);
            }
        }
        if (stagedSql.Count > 0) Add(Item("todo", $"{stagedSql.Count} database script{(stagedSql.Count == 1 ? " is" : "s are")} still in dev/", "Move them into a release folder, or QA won't get them."));
        if (releaseSql.Count > 0) Add(Item("check", "Database release to run on QA", string.Join(", ", releaseSql)));
        if (grants.Count > 0) Add(Item("check", "Grants roles or menus in SQL", string.Join(", ", grants.Distinct())));
        if (scopes > 0) Add(Item("check", $"{scopes} new protected endpoint{(scopes == 1 ? "" : "s")}", "QA roles need the new scopes."));
        if (cfgKeys.Count > 0) Add(Item("check", $"{cfgKeys.Count} new setting{(cfgKeys.Count == 1 ? "" : "s")}", string.Join(", ", cfgKeys.Take(12)) + " · QA settings live outside git."));
        if (pipeFiles.Count > 0) Add(Item("check", "Pipeline files changed", string.Join(", ", pipeFiles)));
        if (codeChanged && !testsChanged) Add(Item("check", "No test files changed"));
        if (adds + dels > 600) Add(Item("check", $"Large change: +{adds} −{dels} lines", "Consider asking for it to be split."));
        return c;
    }

    // for the developer: every merge request of a ticket, with its reviewers, approvals and comments
    public static async Task<JsonArray> ForTicket(Ledger l, Report rep)
    {
        if (await l.GitLabUser() is null) throw new Exception("Connect your GitLab account first.");
        var found = new Dictionary<string, (string Project, long Iid)>();
        foreach (var r in rep.Repos.Where(r => r.WebUrl != ""))
        {
            var project = new Uri(r.WebUrl).AbsolutePath.Trim('/');
            foreach (var m in r.Merges.Where(m => m.MrUrl is not null))
                found.TryAdd($"{project}!{m.MrUrl!.Split('/')[^1]}", (project, long.Parse(m.MrUrl.Split('/')[^1])));
            foreach (var b in r.Branches.Select(b => b.Name.Replace(" (local only, never pushed)", "")).Select(b => b.StartsWith("origin/") ? b[7..] : b).Distinct().Take(6))
                foreach (var mr in (await Try(() => l.GitLab.ForBranch(project, b))) ?? [])
                    found.TryAdd($"{project}!{(long)mr!["iid"]!}", (project, (long)mr["iid"]!));
        }
        var gate = new SemaphoreSlim(4);
        var list = await Task.WhenAll(found.Values.Take(20).Select(async x =>
        {
            await gate.WaitAsync();
            try
            {
                var mr = await l.GitLab.MergeRequest(x.Project, x.Iid);
                var s = Summary(l, mr, await Try(() => l.GitLab.Approvals(x.Project, x.Iid)), l.GitLab.Me?.Username);
                s["reviewers"] = Reviewers(await l.GitLab.Reviewers(x.Project, x.Iid), mr);
                s["discussions"] = Discussions(await l.GitLab.Discussions(x.Project, x.Iid));
                return (JsonNode)s;
            }
            catch { return null; }
            finally { gate.Release(); }
        }));
        return new JsonArray(list.Where(x => x is not null).OrderByDescending(x => (string?)x!["updated"]).ToArray());
    }
}

// ───────────────────────────── time: what's logged on a ticket, and my days against the daily target ─────────────────────────────

static class TimeSheet
{
    static DateTimeOffset StartedAt(JsonNode w)
    {
        var s = (string?)w["started"] ?? "";   // 2026-10-19T09:00:00.000+0200
        if (s.Length > 5 && (s[^5] == '+' || s[^5] == '-')) s = s[..^2] + ":" + s[^2..];
        return DateTimeOffset.TryParse(s, out var d) ? d : DateTimeOffset.MinValue;
    }

    static string Text(JsonNode? adf) => adf is null ? "" : adf is JsonValue v ? (string?)v ?? ""
        : string.Join(" ", (adf["content"]?.AsArray() ?? []).Select(n => (string?)n?["text"] ?? Text(n))).Trim();

    public static async Task<JsonObject> Ticket(Ledger l, string key)
    {
        var me = await l.CurrentUser() ?? throw new Exception("Connect your Jira account first.");
        var tt = await l.Jira.TimeTracking(key);
        var logs = await l.Jira.Worklogs(key);
        var entries = logs.Select(w => new
        {
            Id = (string?)w["id"], Who = (string?)w["author"]?["displayName"], Mine = (string?)w["author"]?["accountId"] == me.AccountId,
            At = StartedAt(w), Seconds = (int?)w["timeSpentSeconds"] ?? 0, Comment = Text(w["comment"]),
        }).OrderByDescending(e => e.At).ToList();
        return new JsonObject
        {
            ["estimate"] = (int?)tt["originalEstimateSeconds"], ["remaining"] = (int?)tt["remainingEstimateSeconds"],
            ["spent"] = entries.Sum(e => e.Seconds), ["mine"] = entries.Where(e => e.Mine).Sum(e => e.Seconds),
            ["people"] = new JsonArray(entries.GroupBy(e => e.Who).Select(g => (JsonNode)new JsonObject { ["name"] = g.Key, ["seconds"] = g.Sum(e => e.Seconds) }).ToArray()),
            ["entries"] = new JsonArray(entries.Select(e => (JsonNode)new JsonObject
            {
                ["id"] = e.Id, ["who"] = e.Who, ["mine"] = e.Mine, ["date"] = e.At.ToString("yyyy-MM-dd"), ["time"] = e.At.ToString("HH:mm"),
                ["seconds"] = e.Seconds, ["comment"] = e.Comment,
            }).ToArray()),
        };
    }

    // my worklogs from the start of the week, per day, against the daily target
    public static async Task<JsonObject> Week(Ledger l, DateOnly weekStart)
    {
        var me = await l.CurrentUser() ?? throw new Exception("Connect your Jira account first.");
        var from = new DateTimeOffset(weekStart.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local.GetUtcOffset(weekStart.ToDateTime(TimeOnly.MinValue)));
        var issues = await l.Jira.IssuesILoggedOn(weekStart);
        var gate = new SemaphoreSlim(6);
        var mine = (await Task.WhenAll(issues.Select(async i =>
        {
            await gate.WaitAsync();
            try { return (await l.Jira.Worklogs(i.Key, from)).Where(w => (string?)w["author"]?["accountId"] == me.AccountId).Select(w => (Issue: i, At: StartedAt(w), Seconds: (int?)w["timeSpentSeconds"] ?? 0)).ToList(); }
            finally { gate.Release(); }
        }))).SelectMany(x => x).Where(x => x.At >= from).ToList();

        var target = (int)Math.Round(l.DailyTargetHours * 3600);
        var days = Enumerable.Range(0, 7).Select(d => weekStart.AddDays(d)).Select(d =>
        {
            var logs = mine.Where(m => DateOnly.FromDateTime(m.At.ToLocalTime().DateTime) == d).ToList();
            var work = l.WorkDays.Contains((int)d.DayOfWeek);
            return (JsonNode)new JsonObject
            {
                ["date"] = d.ToString("yyyy-MM-dd"), ["weekday"] = d.DayOfWeek.ToString()[..3], ["workday"] = work,
                ["target"] = work ? target : 0, ["seconds"] = logs.Sum(x => x.Seconds),
                ["tickets"] = new JsonArray(logs.GroupBy(x => x.Issue.Key).Select(g => (JsonNode)new JsonObject
                {
                    ["key"] = g.Key, ["summary"] = g.First().Issue.Summary, ["seconds"] = g.Sum(x => x.Seconds),
                }).ToArray()),
            };
        }).ToArray();
        return new JsonObject { ["target"] = target, ["days"] = new JsonArray(days), ["today"] = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd") };
    }
}

// ───────────────────────────── day-to-day: deployments, sprints, team, standup, suggestions, notifications ─────────────────────────────

static class Work
{
    static Regex KeyRx(Ledger l) => new($@"(?<![A-Za-z0-9])(?:{l.KeyPattern})(?!\d)", RegexOptions.IgnoreCase);
    static readonly Regex MergeOf = new(@"^Merge (?:remote-tracking )?branch '([^']+)'");
    static async Task<T?> Try<T>(Func<Task<T>> f) where T : class { try { return await f(); } catch { return null; } }
    static string Jql(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // the project prefix of the key pattern, e.g. "MM" from "MM-[0-9]+"
    public static string? Project(Ledger l) => Regex.Match(l.KeyPattern, @"^([A-Z][A-Z0-9_]+)-") is { Success: true } m ? m.Groups[1].Value : null;

    // ── where is it deployed? ──
    public static async Task<JsonArray> Deployments(Ledger l, Report rep)
    {
        if (await l.GitLabUser() is null) throw new Exception("Connect your GitLab account to see deployments.");
        var gate = new SemaphoreSlim(4);
        var rows = await Task.WhenAll(rep.Repos.Where(r => r.WebUrl != "" && r.Commits.Any(c => !c.IsForeign)).Select(async r =>
        {
            await gate.WaitAsync();
            try
            {
                var project = new Uri(r.WebUrl).AbsolutePath.Trim('/');
                var path = l.RepoPath(r.Name);
                var own = r.Commits.Where(c => !c.IsForeign).Select(c => c.Sha).ToList();
                var envs = await Try(() => l.GitLab.Environments(project)) ?? [];
                var list = new JsonArray();
                foreach (var e in envs.Take(15))
                {
                    var full = await Try(() => l.GitLab.Environment(project, (long)e!["id"]!));
                    var d = full?["last_deployment"];
                    if (d is null) continue;
                    var sha = (string?)d["sha"];
                    bool? has = null;
                    if (sha is not null && path is not null)
                    {
                        var ix = RepoIndex.For(path);
                        if (ix.BySha.ContainsKey(sha)) { var reach = ix.Reachable(sha); has = own.All(reach.Contains); }
                    }
                    list.Add(new JsonObject
                    {
                        ["name"] = (string?)e["name"], ["tier"] = (string?)e["tier"], ["url"] = (string?)e["external_url"],
                        ["ref"] = (string?)d["ref"], ["sha"] = sha?[..Math.Min(8, sha.Length)], ["status"] = (string?)d["status"] ?? (string?)d["deployable"]?["status"],
                        ["date"] = ((string?)d["created_at"] ?? "")[..Math.Min(10, ((string?)d["created_at"] ?? "").Length)],
                        ["web"] = $"{r.WebUrl}/-/environments/{(long)e["id"]!}", ["contains"] = has is null ? null : JsonValue.Create(has.Value),
                    });
                }
                return (JsonNode)new JsonObject { ["repo"] = r.Name, ["environments"] = list };
            }
            finally { gate.Release(); }
        }));
        return new JsonArray(rows.Where(x => x?["environments"]?.AsArray().Count > 0).ToArray());
    }

    // ── sprints and the tickets in one ──
    public static async Task<JsonArray> Sprints(Ledger l)
    {
        var p = Project(l);
        var issues = await l.Jira.Search($"{(p is null ? "" : $"project = {p} AND ")}assignee = currentUser() AND (sprint in openSprints() OR sprint in futureSprints() OR sprint in closedSprints()) ORDER BY updated DESC", 300);
        var groups = issues.Where(i => i.Sprint is not null).GroupBy(i => (i.Sprint!, i.SprintState ?? "")).ToList();
        int Rank(string s) => s == "active" ? 0 : s == "future" ? 1 : 2;
        return new JsonArray(groups.OrderBy(g => Rank(g.Key.Item2)).ThenByDescending(g => g.Key.Item1, StringComparer.OrdinalIgnoreCase)
            .Where((g, i) => g.Key.Item2 != "closed" || i < 12)
            .Select(g => (JsonNode)new JsonObject { ["name"] = g.Key.Item1, ["state"] = g.Key.Item2, ["mine"] = g.Count() }).ToArray());
    }

    public static async Task<(List<IssueInfo> Items, string? Next)> SprintIssues(Ledger l, string sprint, bool onlyMine, string? next)
    {
        var p = Project(l);
        return await l.Jira.SearchPage($"{(p is null ? "" : $"project = {p} AND ")}sprint = \"{Jql(sprint)}\"{(onlyMine ? " AND assignee = currentUser()" : "")} ORDER BY rank", 50, next);
    }

    public static async Task<(List<IssueInfo> Items, string? Next)> Team(Ledger l, string? jql, string? next)
    {
        var p = Project(l);
        jql ??= $"{(p is null ? "" : $"project = {p} AND ")}sprint in openSprints() AND statusCategory != Done ORDER BY assignee, rank";
        return await l.Jira.SearchPage(jql, 50, next);
    }

    // ── my commits on a day, from the index (author time, local day) ──
    public static List<(string Repo, IndexedCommit C, string? Key)> MyCommits(Ledger l, DateOnly day)
    {
        var mine = l.MyEmails().Select(e => e.ToLowerInvariant()).ToHashSet();
        var rx = KeyRx(l);
        var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local.GetUtcOffset(day.ToDateTime(TimeOnly.MinValue))).ToUnixTimeSeconds();
        var list = new List<(string, IndexedCommit, string?)>();
        foreach (var r in l.Repos)
        {
            RepoIndex ix;
            try { ix = RepoIndex.For(r); } catch { continue; }
            var seen = new HashSet<(string, long)>();   // a rebased copy has the same subject and author time
            foreach (var c in ix.Commits)
                if (c.At >= from && c.At < from + 86400 && c.Parents.Length < 2 && mine.Contains(c.Email.ToLowerInvariant()) && seen.Add((c.Subject, c.At)))
                    list.Add((Path.GetFileName(r), c, rx.Match(c.Message) is { Success: true } m ? m.Value.ToUpperInvariant() : null));
        }
        return list.OrderBy(x => x.Item2.At).ToList();
    }

    static DateOnly Monday(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    // ── suggested worklogs: share what's left of the day's target by where you committed ──
    public static async Task<JsonObject> Suggest(Ledger l, DateOnly day)
    {
        var commits = MyCommits(l, day).Where(x => x.Key is not null).ToList();
        var week = await TimeSheet.Week(l, Monday(day));
        var d = week["days"]!.AsArray().First(x => (string?)x!["date"] == day.ToString("yyyy-MM-dd"))!;
        var logged = (int)d["seconds"]!;
        var target = (int)d["target"]!;
        var loggedOn = d["tickets"]!.AsArray().ToDictionary(t => (string)t!["key"]!, t => (int)t!["seconds"]!);
        var left = Math.Max(0, target - logged);
        var groups = commits.GroupBy(x => x.Key!).Select(g => (Key: g.Key, Count: g.Count(), First: g.Min(x => x.C.At), Last: g.Max(x => x.C.At),
                                                               Repos: g.Select(x => x.Repo).Distinct().ToList(), Subjects: g.Select(x => x.C.Subject).Take(4).ToList())).ToList();
        var total = groups.Sum(g => g.Count);
        var issues = groups.Count == 0 ? [] : (await Try(() => l.Jira.Search($"key in ({string.Join(",", groups.Select(g => g.Key))})", groups.Count)))?.ToDictionary(i => i.Key) ?? [];
        var rows = groups.OrderByDescending(g => g.Count).Select(g =>
        {
            var share = total == 0 ? 0 : (int)(Math.Round((double)left * g.Count / total / 900) * 900);   // 15-minute steps
            return (JsonNode)new JsonObject
            {
                ["key"] = g.Key, ["summary"] = issues.GetValueOrDefault(g.Key)?.Summary, ["commits"] = g.Count,
                ["from"] = DateTimeOffset.FromUnixTimeSeconds(g.First).ToLocalTime().ToString("HH:mm"), ["to"] = DateTimeOffset.FromUnixTimeSeconds(g.Last).ToLocalTime().ToString("HH:mm"),
                ["repos"] = new JsonArray(g.Repos.Select(x => (JsonNode)x).ToArray()), ["subjects"] = new JsonArray(g.Subjects.Select(x => (JsonNode)x).ToArray()),
                ["logged"] = loggedOn.GetValueOrDefault(g.Key), ["suggested"] = share,
            };
        }).ToArray();
        return new JsonObject { ["date"] = day.ToString("yyyy-MM-dd"), ["target"] = target, ["logged"] = logged, ["left"] = left, ["tickets"] = new JsonArray(rows) };
    }

    // ── standup: what I did on a day ──
    public static async Task<JsonObject> Standup(Ledger l, DateOnly day)
    {
        var commits = MyCommits(l, day);
        var keys = commits.Select(x => x.Key).Where(k => k is not null).Select(k => k!).Distinct().ToList();
        // merges that landed that day for those tickets
        var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeZoneInfo.Local.GetUtcOffset(day.ToDateTime(TimeOnly.MinValue))).ToUnixTimeSeconds();
        var merges = new List<(string Repo, string Branch, string? Key)>();
        var rx = KeyRx(l);
        foreach (var r in l.Repos)
        {
            RepoIndex ix;
            try { ix = RepoIndex.For(r); } catch { continue; }
            foreach (var c in ix.Commits.Where(c => c.Parents.Length >= 2 && c.At >= from && c.At < from + 86400))
                if (MergeOf.Match(c.Subject) is { Success: true } m && rx.Match(m.Groups[1].Value) is { Success: true } k)
                    merges.Add((Path.GetFileName(r), m.Groups[1].Value, k.Value.ToUpperInvariant()));
        }
        var mergeKeys = merges.Select(m => m.Key!).Distinct().ToList();
        var week = await Try(() => TimeSheet.Week(l, Monday(day)));
        var logs = week?["days"]?.AsArray().FirstOrDefault(x => (string?)x!["date"] == day.ToString("yyyy-MM-dd"))?["tickets"]?.AsArray()
                       .Select(t => (Key: (string)t!["key"]!, Summary: (string?)t["summary"], Seconds: (int)t["seconds"]!)).ToList() ?? [];
        var all = keys.Concat(logs.Select(x => x.Key)).Distinct().ToList();
        var issues = all.Count == 0 ? [] : (await Try(() => l.Jira.Search($"key in ({string.Join(",", all)})", all.Count)))?.ToDictionary(i => i.Key) ?? [];

        var reviews = new JsonArray();
        if (await l.GitLabUser() is { } gu)
            foreach (var e in await Try(() => l.GitLab.Events(gu.Id, day.AddDays(-1), day.AddDays(1))) ?? [])
            {
                var action = (string?)e!["action_name"] ?? "";
                var type = (string?)e["target_type"] ?? "";
                if (!(type is "MergeRequest" or "DiffNote" or "DiscussionNote" or "Note") || action is "opened" or "pushed to" or "pushed new") continue;
                if (((string?)e["created_at"] ?? "").Length < 10 || DateTimeOffset.Parse((string)e["created_at"]!).ToLocalTime().Date != day.ToDateTime(TimeOnly.MinValue).Date) continue;
                reviews.Add(new JsonObject { ["action"] = action, ["title"] = (string?)e["target_title"], ["note"] = ((string?)e["note"]?["body"])?[..Math.Min(140, ((string?)e["note"]?["body"])!.Length)] });
            }

        var work = all.Select(k => (JsonNode)new JsonObject
        {
            ["key"] = k, ["summary"] = issues.GetValueOrDefault(k)?.Summary ?? logs.FirstOrDefault(x => x.Key == k).Summary,
            ["status"] = issues.GetValueOrDefault(k)?.Status,
            ["commits"] = new JsonArray(commits.Where(x => x.Key == k).Select(x => (JsonNode)new JsonObject { ["repo"] = x.Repo, ["subject"] = x.C.Subject, ["time"] = DateTimeOffset.FromUnixTimeSeconds(x.C.At).ToLocalTime().ToString("HH:mm") }).ToArray()),
            ["merged"] = new JsonArray(merges.Where(m => m.Key == k).Select(m => (JsonNode)$"{m.Repo}: {m.Branch}").Distinct().ToArray()),
            ["logged"] = logs.Where(x => x.Key == k).Sum(x => x.Seconds),
        }).ToArray();
        var other = commits.Where(x => x.Key is null).Select(x => (JsonNode)new JsonObject { ["repo"] = x.Repo, ["subject"] = x.C.Subject }).ToArray();
        return new JsonObject { ["date"] = day.ToString("yyyy-MM-dd"), ["work"] = new JsonArray(work), ["otherCommits"] = new JsonArray(other), ["reviews"] = reviews };
    }

    // the last working day before today
    public static DateOnly PreviousWorkday(Ledger l)
    {
        var d = DateOnly.FromDateTime(DateTime.Now).AddDays(-1);
        for (var i = 0; i < 7 && !l.WorkDays.Contains((int)d.DayOfWeek); i++) d = d.AddDays(-1);
        return d;
    }

    // ── what changed for me in GitLab (the page compares it with what it saw last time) ──
    public static async Task<JsonObject> Notifications(Ledger l)
    {
        var me = await l.GitLabUser() ?? throw new Exception("Connect your GitLab account first.");
        var toReview = await Try(() => l.GitLab.ReviewRequests(me.Username)) ?? [];
        var mine = await Try(() => l.GitLab.MyOpenMergeRequests(me.Username)) ?? [];
        var gate = new SemaphoreSlim(6);
        var rows = await Task.WhenAll(mine.Take(30).Select(async mr =>
        {
            await gate.WaitAsync();
            try
            {
                var project = Review.Project(mr!); var iid = (long)mr!["iid"]!;
                var pipe = await l.GitLab.LatestPipeline(project, iid);
                var rv = await l.GitLab.Reviewers(project, iid);
                return (JsonNode)new JsonObject
                {
                    ["id"] = $"{project}!{iid}", ["project"] = project, ["iid"] = iid, ["title"] = (string?)mr["title"], ["url"] = (string?)mr["web_url"],
                    ["notes"] = (int?)mr["user_notes_count"] ?? 0, ["pipeline"] = (string?)pipe?["status"],
                    ["changesRequested"] = new JsonArray(rv.Where(r => (string?)r!["state"] == "requested_changes").Select(r => (JsonNode)((string?)r!["user"]?["name"] ?? "")).ToArray()),
                    ["approved"] = new JsonArray(rv.Where(r => (string?)r!["state"] == "approved").Select(r => (JsonNode)((string?)r!["user"]?["name"] ?? "")).ToArray()),
                };
            }
            finally { gate.Release(); }
        }));
        return new JsonObject
        {
            ["reviews"] = new JsonArray(toReview.Select(mr => (JsonNode)new JsonObject { ["id"] = $"{Review.Project(mr!)}!{(long)mr!["iid"]!}", ["title"] = (string?)mr["title"], ["author"] = (string?)mr["author"]?["name"], ["project"] = Review.Project(mr), ["iid"] = (long)mr["iid"]! }).ToArray()),
            ["mine"] = new JsonArray(rows),
        };
    }

    // ── actions ──
    static string ProjectOf(string repo) => Git.WebUrl(repo) is { Length: > 0 } w ? new Uri(w).AbsolutePath.Trim('/') : throw new Exception("This repository has no GitLab remote.");

    public static async Task<JsonObject> CreateMergeRequest(Ledger l, string repoName, string branch)
    {
        var repo = l.RepoPath(repoName) ?? throw new Exception("Unknown repository.");
        var ix = RepoIndex.For(repo);
        if (!ix.Refs.Any(r => r.Name == $"refs/remotes/origin/{branch}"))
            throw new Exception(ix.Refs.Any(r => r.Name == $"refs/heads/{branch}") ? "Push the branch to GitLab first." : "That branch doesn't exist.");
        var target = ix.DefaultRef.StartsWith("origin/") ? ix.DefaultRef[7..] : ix.DefaultRef;
        var key = KeyRx(l).Match(branch) is { Success: true } m ? m.Value.ToUpperInvariant() : null;
        var summary = key is not null && await l.CurrentUser() is not null ? (await l.Jira.GetIssue(key))?.Summary : null;
        var mr = await l.GitLab.CreateMergeRequest(ProjectOf(repo), branch, target, summary is null ? branch : $"{key} {summary}");
        return new JsonObject { ["url"] = (string?)mr["web_url"], ["iid"] = (long?)mr["iid"] };
    }

    public static async Task<JsonObject> CreateTag(Ledger l, string repoName, string tag)
    {
        var repo = l.RepoPath(repoName) ?? throw new Exception("Unknown repository.");
        if (!Regex.IsMatch(tag, @"^[A-Za-z0-9][A-Za-z0-9._\-]{0,99}$") || tag.Contains("..")) throw new Exception("Use letters, digits, dots and dashes for the tag name.");
        var ix = RepoIndex.For(repo);
        if (ix.Refs.Any(r => r.Name == $"refs/tags/{tag}")) throw new Exception($"Tag {tag} already exists.");
        var target = ix.DefaultRef.StartsWith("origin/") ? ix.DefaultRef[7..] : ix.DefaultRef;
        var t = await l.GitLab.CreateTag(ProjectOf(repo), tag, target, $"Release {tag}");
        _ = Task.Run(() => { Git.Run(repo, "fetch", "--tags", "--quiet"); Git.Forget(repo); });   // so the next check sees it
        return new JsonObject { ["url"] = $"{Git.WebUrl(repo)}/-/tags/{Uri.EscapeDataString(tag)}", ["name"] = (string?)t["name"] };
    }

    // ── housekeeping: temporary diff files, and how much disk is left ──
    public static void Clean()
    {
        foreach (var name in new[] { "greenlight", "feature-ledger" })
        {
            var dir = Path.Combine(Path.GetTempPath(), name);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                try { if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-1)) File.Delete(f); } catch { }
            foreach (var d in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
        }
    }

    public static long DiskFree(string path) { try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace; } catch { return -1; } }
}

record JiraUser(string AccountId, string DisplayName, string Email);

class Jira
{
    const string BaseFields = "summary,status,issuetype,parent,assignee,fixVersions,updated,priority,labels,components,duedate";
    // the sprint field is a custom field whose id differs per Jira site; looked up once
    static string? sprintField;
    static bool fieldsKnown;
    string Fields => sprintField is null ? BaseFields : $"{BaseFields},{sprintField}";
    async Task KnowFields()
    {
        if (fieldsKnown || http is null) return;
        try
        {
            var res = await http.GetAsync("rest/api/3/field");
            if (res.IsSuccessStatusCode)
                sprintField = JsonNode.Parse(await res.Content.ReadAsStringAsync())!.AsArray()
                    .FirstOrDefault(f => (string?)f?["schema"]?["custom"] == "com.pyxis.greenhopper.jira:gh-sprint")?["id"]?.ToString();
        }
        catch { }
        fieldsKnown = true;
    }
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
        await KnowFields();
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

    // one page of results; pass the returned token to get the next page (null when there are no more)
    public async Task<(List<IssueInfo> Items, string? Next)> SearchPage(string jql, int size, string? token)
    {
        await KnowFields();
        var body = new JsonObject
        {
            ["jql"] = jql,
            ["fields"] = new JsonArray(Fields.Split(',').Select(f => (JsonNode)f).ToArray()),
            ["maxResults"] = Math.Clamp(size, 1, 100),
        };
        if (!string.IsNullOrEmpty(token)) body["nextPageToken"] = token;
        var res = await http!.PostAsync("rest/api/3/search/jql", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        if (!res.IsSuccessStatusCode) throw new Exception($"Jira search returned {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
        var page = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        return (page["issues"]!.AsArray().Select(i => Parse(i!)).ToList(), (bool?)page["isLast"] == true ? null : (string?)page["nextPageToken"]);
    }

    public async Task<IssueInfo?> GetIssue(string key)
    {
        try
        {
            await KnowFields();
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
    public Task<(List<IssueInfo> Items, string? Next)> SearchMine(string text, bool exactKey, int size, string? token)
    {
        var q = text.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var match = exactKey ? $"key = \"{q}\"" : $"summary ~ \"{q}*\"";
        return SearchPage($"assignee = currentUser() AND {match} ORDER BY updated DESC", size, token);
    }

    public Task<List<IssueInfo>> MyOpenIssues() =>
        Search("assignee = currentUser() AND statusCategory != Done ORDER BY updated DESC", 200);

    static IssueInfo Parse(JsonNode i)
    {
        var f = i["fields"]!;
        var fix = f["fixVersions"]?.AsArray().Select(v => (string?)v?["name"]).Where(v => v is not null).ToList() ?? [];
        var p = f["parent"];
        var sprint = CurrentSprint(f);
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
            Updated: ((string?)f["updated"])?[..Math.Min(10, ((string?)f["updated"])!.Length)] ?? "")
        {
            Priority = (string?)f["priority"]?["name"] ?? "",
            Labels = f["labels"]?.AsArray().Select(l => (string?)l ?? "").Where(l => l != "").ToList() ?? [],
            Components = f["components"]?.AsArray().Select(c => (string?)c?["name"] ?? "").Where(c => c != "").ToList() ?? [],
            Due = (string?)f["duedate"],
            Sprint = sprint?.Name, SprintState = sprint?.State,
        };
    }

    static (string Name, string State)? CurrentSprint(JsonNode f)
    {
        if (sprintField is null || f[sprintField] is not JsonArray arr || arr.Count == 0) return null;
        var all = arr.Select(s => (Name: (string?)s?["name"] ?? "", State: (string?)s?["state"] ?? "", End: (string?)s?["endDate"] ?? "")).Where(s => s.Name != "").ToList();
        var pick = all.FirstOrDefault(s => s.State == "active");
        if (pick.Name is null or "") pick = all.Where(s => s.State == "future").FirstOrDefault();
        if (pick.Name is null or "") pick = all.OrderByDescending(s => s.End, StringComparer.Ordinal).FirstOrDefault();
        return pick.Name is null or "" ? null : (pick.Name, pick.State);
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
            existing = comments.FirstOrDefault(c => (((string?)c?["body"])?.Contains(Doc.Marker) == true || ((string?)c?["body"])?.Contains(Doc.OldMarker) == true)
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

    // ── workflow: move a ticket along (e.g. to Ready for QA) ──
    public async Task<JsonArray> Transitions(string key)
    {
        var res = await http!.GetAsync($"rest/api/3/issue/{key}/transitions");
        if (!res.IsSuccessStatusCode) throw new Exception($"Jira returned {(int)res.StatusCode} reading the ticket's transitions.");
        return new JsonArray(JsonNode.Parse(await res.Content.ReadAsStringAsync())!["transitions"]!.AsArray()
            .Select(t => (JsonNode)new JsonObject { ["id"] = (string?)t!["id"], ["name"] = (string?)t["name"], ["to"] = (string?)t["to"]?["name"], ["category"] = (string?)t["to"]?["statusCategory"]?["key"] }).ToArray());
    }
    public async Task Transition(string key, string id)
    {
        var res = await http!.PostAsync($"rest/api/3/issue/{key}/transitions", new StringContent(new JsonObject { ["transition"] = new JsonObject { ["id"] = id } }.ToJsonString(), Encoding.UTF8, "application/json"));
        if (!res.IsSuccessStatusCode) throw new Exception($"Jira didn't move the ticket ({(int)res.StatusCode}): {await res.Content.ReadAsStringAsync()}");
    }

    // ── time tracking (Jira worklogs) ──

    public async Task<JsonNode> TimeTracking(string key)
    {
        var res = await http!.GetAsync($"rest/api/3/issue/{key}?fields=timetracking");
        if (!res.IsSuccessStatusCode) throw new Exception($"Jira returned {(int)res.StatusCode} reading time tracking.");
        return JsonNode.Parse(await res.Content.ReadAsStringAsync())!["fields"]?["timetracking"] ?? new JsonObject();
    }

    // worklogs on one issue, optionally only those started on or after a moment
    public async Task<List<JsonNode>> Worklogs(string key, DateTimeOffset? since = null)
    {
        var all = new List<JsonNode>();
        for (var start = 0; ; start += 100)
        {
            var q = $"rest/api/3/issue/{key}/worklog?startAt={start}&maxResults=100" + (since is { } s ? $"&startedAfter={s.ToUnixTimeMilliseconds()}" : "");
            var res = await http!.GetAsync(q);
            if (!res.IsSuccessStatusCode) throw new Exception($"Jira returned {(int)res.StatusCode} reading worklogs.");
            var page = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
            var logs = page["worklogs"]!.AsArray();
            all.AddRange(logs.Select(w => w!));
            if (start + logs.Count >= ((int?)page["total"] ?? 0) || logs.Count == 0) break;
        }
        return all;
    }

    // issues where I logged time since a date
    public Task<List<IssueInfo>> IssuesILoggedOn(DateOnly since) =>
        Search($"worklogAuthor = currentUser() AND worklogDate >= \"{since:yyyy-MM-dd}\" ORDER BY updated DESC", 200);

    public async Task<JsonNode> AddWorklog(string key, DateTimeOffset started, int seconds, string? comment)
    {
        var body = new JsonObject
        {
            ["started"] = started.ToString("yyyy-MM-dd'T'HH:mm:ss.fff") + started.ToString("zzz").Replace(":", ""),
            ["timeSpentSeconds"] = seconds,
        };
        if (!string.IsNullOrWhiteSpace(comment))
            body["comment"] = new JsonObject
            {
                ["type"] = "doc", ["version"] = 1,
                ["content"] = new JsonArray(new JsonObject { ["type"] = "paragraph", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = comment.Trim() }) }),
            };
        var res = await http!.PostAsync($"rest/api/3/issue/{key}/worklog", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        if (!res.IsSuccessStatusCode) throw new Exception($"Jira didn't accept the worklog ({(int)res.StatusCode}): {await res.Content.ReadAsStringAsync()}");
        return JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
    }

    // a plain comment (wiki markup), e.g. a code review result
    public async Task<string> AddComment(string key, string wiki)
    {
        var res = await http!.PostAsync($"rest/api/2/issue/{key}/comment", new StringContent(new JsonObject { ["body"] = wiki }.ToJsonString(), Encoding.UTF8, "application/json"));
        if (!res.IsSuccessStatusCode) throw new Exception($"Jira returned {(int)res.StatusCode}: {await res.Content.ReadAsStringAsync()}");
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
        ["inDefault"] = c.InDefault, ["note"] = c.Note, ["url"] = CommitUrl(r, c), ["ticket"] = c.Ticket, ["email"] = c.AuthorEmail,
        ["files"] = new JsonArray(c.Files.Select(f => (JsonNode)new JsonObject { ["s"] = f.Status, ["p"] = f.Path }).ToArray()),
    };

    public static JsonObject Issue(IssueInfo i) => new()
    {
        ["key"] = i.Key, ["summary"] = i.Summary, ["type"] = i.Type, ["level"] = i.Level,
        ["priority"] = i.Priority, ["labels"] = new JsonArray(i.Labels.Select(l => (JsonNode)l).ToArray()),
        ["components"] = new JsonArray(i.Components.Select(c => (JsonNode)c).ToArray()),
        ["sprint"] = i.Sprint, ["sprintState"] = i.SprintState, ["due"] = i.Due,
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

    public static JsonObject Station(Station s) => (JsonObject)StationsJson([s])[0]!.DeepClone();

    static JsonArray StationsJson(List<Station> stations) => new(stations.Select(s => (JsonNode)new JsonObject
    {
        ["id"] = s.Id, ["name"] = s.Name, ["state"] = s.State, ["headline"] = s.Headline,
        ["items"] = new JsonArray(s.Items.Select(i => (JsonNode)new JsonObject
        {
            ["state"] = i.State, ["title"] = i.Title, ["detail"] = i.Detail, ["repo"] = i.Repo,
            ["actionLabel"] = i.ActionLabel, ["actionUrl"] = i.ActionUrl,
        }).ToArray()),
    }).ToArray());

    public static JsonObject Full(Report rep, Ledger ledger)
    {
        var own = rep.Repos.SelectMany(r => r.Commits.Where(c => !c.IsForeign)).ToList();
        return new JsonObject
        {
            ["key"] = rep.Key,
            ["generated"] = rep.Generated.ToString("yyyy-MM-dd HH:mm"),
            ["jiraUrl"] = $"{ledger.JiraBaseUrl}/browse/{rep.Key}",
            ["myEmails"] = new JsonArray(ledger.MyEmails().Select(e => (JsonNode)e).ToArray()),
            ["issue"] = rep.Issue is null ? null : Issue(rep.Issue),
            ["ancestors"] = new JsonArray(rep.Ancestors.Select(a => (JsonNode)Issue(a)).ToArray()),
            ["stations"] = StationsJson(rep.Stations),
            ["subTickets"] = new JsonObject { ["possible"] = rep.Subs.Possible, ["total"] = rep.Subs.Total, ["mine"] = rep.Subs.Mine, ["included"] = rep.Scope.Count },
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
                ["sln"] = LocalApps.Solution(ledger.RepoPath(r.Name)),
                ["tags"] = rep.TagDetails.TryGetValue(r.Name, out var td) ? Tags.Json(td) : null,
                ["merges"] = new JsonArray(r.Merges.Select(m => (JsonNode)new JsonObject
                {
                    ["label"] = m.MrUrl is null ? $"merge {m.Sha[..8]}" : $"!{m.MrUrl.Split('/')[^1]}",
                    ["url"] = m.MrUrl, ["date"] = m.Date, ["inDefault"] = m.InDefault, ["branch"] = m.SourceBranch,
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
        RepoIndex.Warm(ledger.Repos);   // history of every repository, read once in the background
        _ = Task.Run(Work.Clean);         // temporary diff files older than a day
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
        Console.WriteLine($"Greenlight running at {url}  (Ctrl+C to stop)");
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
                    var types = new Dictionary<string, string> { [".ttf"] = "font/ttf", [".woff2"] = "font/woff2", [".svg"] = "image/svg+xml", [".js"] = "text/javascript; charset=utf-8", [".css"] = "text/css; charset=utf-8", [".jpg"] = "image/jpeg", [".png"] = "image/png", [".webp"] = "image/webp" };
                    var file = Path.Combine(ledger.AppDir, "assets", name);
                    if (!Regex.IsMatch(name, @"^[A-Za-z0-9_\-]+(\.[A-Za-z0-9_\-]+)*\.[a-z0-9]+$") || name.Contains("..") || !types.TryGetValue(Path.GetExtension(name), out var type) || !File.Exists(file))
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
                        ["gitlabBaseUrl"] = ledger.GitLabBaseUrl,
                        ["gitlab"] = await ledger.GitLabUser() is { } gu ? new JsonObject { ["name"] = gu.Name, ["username"] = gu.Username } : null,
                        ["gitlabRejected"] = ledger.GitLab.Configured && ledger.GitLab.Me is null,
                        ["dailyTargetHours"] = ledger.DailyTargetHours,
                        ["reminderAt"] = ledger.ReminderAt,
                        ["diskFree"] = Work.DiskFree(ledger.AppDir),
                        ["project"] = Work.Project(ledger),
                    });
                    return;
                }

                case ("POST", "/api/gitlab/account"):
                {
                    if (!Trusted(req)) { await Send(res, 403, Err("Forbidden")); return; }
                    var body = await ReadJson(req);
                    var gu = await ledger.ConnectGitLab((string?)body["baseUrl"] ?? ledger.GitLabBaseUrl, (string?)body["token"] ?? "");
                    await Send(res, 200, new JsonObject { ["name"] = gu.Name, ["username"] = gu.Username });
                    return;
                }

                case ("POST", "/api/gitlab/disconnect"):
                    if (!Trusted(req)) { await Send(res, 403, Err("Forbidden")); return; }
                    ledger.DisconnectGitLab();
                    await Send(res, 200, new JsonObject { ["ok"] = true });
                    return;

                case ("GET", "/api/reviews"):
                    await Send(res, 200, await Review.Inbox(ledger));
                    return;

                case ("GET", "/api/review"):
                {
                    var project = req.QueryString["project"] ?? "";
                    if (!long.TryParse(req.QueryString["iid"], out var iid) || !Regex.IsMatch(project, @"^[\w.\-]+(/[\w.\-]+)+$")) { await Send(res, 400, Err("Unknown merge request.")); return; }
                    await Send(res, 200, await Review.Full(ledger, project, iid));
                    return;
                }

                case ("POST", "/api/review/approve"):
                case ("POST", "/api/review/unapprove"):
                case ("POST", "/api/review/comment"):
                {
                    if (!Trusted(req)) { await Send(res, 403, Err("Forbidden")); return; }
                    var body = await ReadJson(req);
                    var project = (string?)body["project"] ?? ""; var iid = (long?)body["iid"] ?? 0;
                    if (iid <= 0 || !Regex.IsMatch(project, @"^[\w.\-]+(/[\w.\-]+)+$")) { await Send(res, 400, Err("Unknown merge request.")); return; }
                    var gu = await ledger.GitLabUser() ?? throw new Exception("Connect your GitLab account first.");
                    var text = ((string?)body["body"] ?? "").Trim();
                    var ticket = (string?)body["key"];
                    var mrUrl = $"{ledger.GitLabBaseUrl}/{project}/-/merge_requests/{iid}";
                    string? jiraUrl = null;
                    if (path == "/api/review/approve") await ledger.GitLab.Approve(project, iid);
                    else if (path == "/api/review/unapprove") await ledger.GitLab.Unapprove(project, iid);
                    else
                    {
                        if (text.Length == 0) { await Send(res, 400, Err("Write what needs to change.")); return; }
                        var changes = (bool?)body["requestChanges"] == true;
                        await ledger.GitLab.Comment(project, iid, (changes ? "**Changes requested**\n\n" : "") + text);
                    }
                    // the review result also lands on the Jira ticket, so the developer sees it there
                    if ((bool?)body["jira"] == true && ticket is not null && ledger.IsValidKey(ticket) && await ledger.CurrentUser() is not null && path != "/api/review/unapprove")
                    {
                        var verdict = path == "/api/review/approve" ? "approved" : (bool?)body["requestChanges"] == true ? "requested changes on" : "commented on";
                        jiraUrl = await ledger.Jira.AddComment(ticket, $"Code review: {gu.Name} {verdict} [!{iid}|{mrUrl}]." + (text.Length > 0 ? $"\n\n{text}" : ""));
                    }
                    await Send(res, 200, new JsonObject { ["ok"] = true, ["jiraUrl"] = jiraUrl });
                    return;
                }

                case ("GET", "/api/ticket-reviews"):
                {
                    if (key is null || !cache.TryGetValue(key, out var rep)) { await Send(res, 400, Err("Check the ticket first.")); return; }
                    await Send(res, 200, await Review.ForTicket(ledger, rep));
                    return;
                }

                case ("GET", "/api/deployments"):
                {
                    if (key is null || !cache.TryGetValue(key, out var rep)) { await Send(res, 400, Err("Check the ticket first.")); return; }
                    await Send(res, 200, await Work.Deployments(ledger, rep));
                    return;
                }

                case ("GET", "/api/sprints"):
                    if (await ledger.CurrentUser() is null) { await Send(res, 401, Err("Connect your Jira account first")); return; }
                    await Send(res, 200, await Work.Sprints(ledger));
                    return;

                case ("GET", "/api/sprint"):
                case ("GET", "/api/team"):
                {
                    if (await ledger.CurrentUser() is null) { await Send(res, 401, Err("Connect your Jira account first")); return; }
                    var built = Recent(ledger, 1000).Select(n => n!).ToDictionary(n => (string)n["key"]!, n => n, StringComparer.OrdinalIgnoreCase);
                    var (items, next) = path == "/api/sprint"
                        ? await Work.SprintIssues(ledger, req.QueryString["name"] ?? "", req.QueryString["who"] != "all", req.QueryString["next"])
                        : await Work.Team(ledger, ledger.TeamJql, req.QueryString["next"]);
                    await Send(res, 200, new JsonObject
                    {
                        ["items"] = new JsonArray(items.Select(i => { var j = ReportJson.Issue(i); if (built.TryGetValue(i.Key, out var b)) j["lastBuild"] = b.DeepClone(); return (JsonNode)j; }).ToArray()),
                        ["next"] = next,
                    });
                    return;
                }

                case ("GET", "/api/standup"):
                {
                    if (await ledger.CurrentUser() is null) { await Send(res, 401, Err("Connect your Jira account first")); return; }
                    var day = DateOnly.TryParse(req.QueryString["date"], out var d0) ? d0 : Work.PreviousWorkday(ledger);
                    await Send(res, 200, await Work.Standup(ledger, day));
                    return;
                }

                case ("GET", "/api/time/suggest"):
                {
                    if (await ledger.CurrentUser() is null) { await Send(res, 401, Err("Connect your Jira account first")); return; }
                    var day = DateOnly.TryParse(req.QueryString["date"], out var d0) ? d0 : DateOnly.FromDateTime(DateTime.Now);
                    await Send(res, 200, await Work.Suggest(ledger, day));
                    return;
                }

                case ("GET", "/api/notifications"):
                    await Send(res, 200, await Work.Notifications(ledger));
                    return;

                case ("GET", "/api/jira/transitions"):
                    if (key is null || !ledger.IsValidKey(key)) { await Send(res, 400, Err("Invalid key")); return; }
                    await Send(res, 200, await ledger.Jira.Transitions(key));
                    return;

                case ("POST", "/api/jira/transition"):
                case ("POST", "/api/gitlab/create-mr"):
                case ("POST", "/api/gitlab/create-tag"):
                {
                    if (!Trusted(req)) { await Send(res, 403, Err("Missing X-Ledger header")); return; }
                    var body = await ReadJson(req);
                    if (path == "/api/jira/transition")
                    {
                        var k = ((string?)body["key"] ?? "").ToUpperInvariant(); var id = (string?)body["id"] ?? "";
                        if (!ledger.IsValidKey(k) || !Regex.IsMatch(id, @"^\d{1,9}$")) { await Send(res, 400, Err("Invalid request")); return; }
                        await ledger.Jira.Transition(k, id);
                        await Send(res, 200, new JsonObject { ["ok"] = true });
                        return;
                    }
                    if (await ledger.GitLabUser() is null) { await Send(res, 400, Err("Connect your GitLab account first.")); return; }
                    var repoName = (string?)body["repo"] ?? "";
                    await Send(res, 200, path == "/api/gitlab/create-mr"
                        ? await Work.CreateMergeRequest(ledger, repoName, (string?)body["branch"] ?? "")
                        : await Work.CreateTag(ledger, repoName, (string?)body["tag"] ?? ""));
                    return;
                }

                case ("GET", "/api/time"):
                    if (key is null || !ledger.IsValidKey(key)) { await Send(res, 400, Err("Invalid key")); return; }
                    await Send(res, 200, await TimeSheet.Ticket(ledger, key));
                    return;

                case ("GET", "/api/time/week"):
                {
                    var today = DateOnly.FromDateTime(DateTime.Now);
                    var start = DateOnly.TryParse(req.QueryString["from"], out var f) ? f : today.AddDays(-(((int)today.DayOfWeek + 6) % 7));   // Monday
                    await Send(res, 200, await TimeSheet.Week(ledger, start));
                    return;
                }

                case ("POST", "/api/time/log"):
                {
                    if (!Trusted(req)) { await Send(res, 403, Err("Forbidden")); return; }
                    var body = await ReadJson(req);
                    var k = ((string?)body["key"] ?? "").ToUpperInvariant();
                    var minutes = (int?)body["minutes"] ?? 0;
                    if (!ledger.IsValidKey(k)) { await Send(res, 400, Err("Invalid key")); return; }
                    if (minutes < 1 || minutes > 24 * 60) { await Send(res, 400, Err("Enter between 1 minute and 24 hours.")); return; }
                    if (!DateOnly.TryParse((string?)body["date"], out var d)) d = DateOnly.FromDateTime(DateTime.Now);
                    var at = d == DateOnly.FromDateTime(DateTime.Now) ? DateTimeOffset.Now.AddMinutes(-minutes) : new DateTimeOffset(d.ToDateTime(new TimeOnly(9, 0)), TimeZoneInfo.Local.GetUtcOffset(d.ToDateTime(new TimeOnly(9, 0))));
                    if (await ledger.CurrentUser() is null) { await Send(res, 400, Err("Connect your Jira account first.")); return; }
                    await ledger.Jira.AddWorklog(k, at, minutes * 60, (string?)body["comment"]);
                    await Send(res, 200, new JsonObject { ["ok"] = true });
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
                    if (q.Length < 2) { await Send(res, 200, new JsonObject { ["items"] = new JsonArray(), ["next"] = null }); return; }
                    var exact = ledger.IsValidKey(q.ToUpperInvariant());
                    var (hits, next) = await ledger.Jira.SearchMine(exact ? q.ToUpperInvariant() : q, exact, 20, req.QueryString["next"]);
                    await Send(res, 200, new JsonObject { ["items"] = new JsonArray(hits.Select(i => (JsonNode)ReportJson.Issue(i)).ToArray()), ["next"] = next });
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
                        await Send(res, 200, ReportJson.Full(rep, ledger));
                    }
                    finally { buildLock.Release(); }
                    return;
                }

                // the same check, sent in parts as they finish (one JSON object per line), so the page fills in as it goes
                case ("GET", "/api/manifest-stream"):
                {
                    if (key is null || !ledger.IsValidKey(key)) { await Send(res, 400, Err($"Enter a key matching {ledger.KeyPattern}")); return; }
                    res.StatusCode = 200;
                    res.ContentType = "application/x-ndjson; charset=utf-8";
                    res.Headers["Cache-Control"] = "no-store";
                    res.SendChunked = true;
                    var gate = new object();
                    var lastProgress = new Dictionary<string, int>();
                    void Write(JsonObject o)
                    {
                        // repository progress can arrive many times a second; pass on only real changes
                        if ((string?)o["type"] == "progress")
                        {
                            var ph = (string)o["phase"]!; var dn = (int)o["done"]!;
                            lock (gate) { if (lastProgress.TryGetValue(ph, out var prev) && prev >= dn) return; lastProgress[ph] = dn; }
                        }
                        var bytes = Encoding.UTF8.GetBytes(o.ToJsonString() + "\n");
                        lock (gate) { try { res.OutputStream.Write(bytes); res.OutputStream.Flush(); } catch { /* the page went away */ } }
                    }
                    await buildLock.WaitAsync();
                    try
                    {
                        var rep = await ledger.Build(key, req.QueryString["fetch"] == "1", msg => Console.WriteLine($"[{key}] {msg}"),
                                                     req.QueryString["children"] != "0", req.QueryString["mine"] != "0", Write);
                        cache[key] = rep;
                        Write(new JsonObject { ["type"] = "done", ["report"] = ReportJson.Full(rep, ledger) });
                    }
                    catch (Exception e) { Write(new JsonObject { ["type"] = "error", ["message"] = e.Message }); }
                    finally { buildLock.Release(); try { res.Close(); } catch { } }
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

                case ("POST", "/api/open"):
                case ("POST", "/api/diff"):
                case ("POST", "/api/open-diff"):
                {
                    if (!Trusted(req)) { await Send(res, 403, Err("Missing X-Ledger header")); return; }
                    var body = await ReadJson(req);
                    var repo = ledger.RepoPath((string?)body["repo"]);
                    if (repo is null) { await Send(res, 400, Err("Unknown repository.")); return; }
                    var rel = (string?)body["path"];
                    string? file = null;
                    if (rel is not null && (file = LocalApps.SafeFile(repo, rel)) is null) { await Send(res, 400, Err("Invalid file path.")); return; }
                    if (path == "/api/open")
                    {
                        LocalApps.Open(repo, (string?)body["app"] ?? "", file, (int?)body["line"] ?? 1);
                        await Send(res, 200, new JsonObject { ["ok"] = true });
                        return;
                    }
                    var shas = body["shas"]?.AsArray().Select(n => (string?)n).Where(s => LocalApps.IsCommit(repo, s)).Select(s => s!).Distinct().ToList() ?? [];
                    if (rel is null || shas.Count == 0) { await Send(res, 400, Err("Nothing to compare.")); return; }
                    if (path == "/api/diff") { await Send(res, 200, LocalApps.Diff(repo, rel, shas)); return; }
                    LocalApps.OpenDiff(repo, rel, shas);
                    await Send(res, 200, new JsonObject { ["ok"] = true });
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
