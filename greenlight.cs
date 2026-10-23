#:package Npgsql@8.0.8
// Greenlight: is a ticket ready to go live? Builds a per-ticket manifest from git history across all local repos,
// writes it to out/<KEY>.md and optionally posts/updates it as a comment on the Jira ticket.
//
//   dotnet run greenlight.cs -- MM-1234            build the manifest (local only)
//   dotnet run greenlight.cs -- MM-1234 --fetch    git fetch all repos first
//   dotnet run greenlight.cs -- MM-1234 --post     also create/update the manifest comment on Jira
//   dotnet run greenlight.cs -- --ui               open the browser UI on http://localhost:5199
//   dotnet run greenlight.cs -- --selftest         check the core rules (no network, no changes)
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
if (flags.Contains("--selftest")) return SelfTest.Run(appDir);
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
              IReadOnlyDictionary<string, TagDetail> TagDetails)
{
    public JsonObject Setup { get; init; } = new() { ["services"] = new JsonArray(), ["menus"] = new JsonArray() };   // System Manager setup on QA
}

// Sub-tickets in Jira under this ticket, whatever the toggle says. Possible=false: sub-tasks can't have any, or Jira isn't connected.
record SubTickets(bool Possible, int Total, int Mine);

class Ledger
{
    public string AppDir { get; }
    public string KeyPattern { get; }
    public double DailyTargetHours { get; }
    public string ReminderAt { get; }   // "17:00": the page reminds you if today is short
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
        // a network share could hand git a hostile .git/config: local folders only
        if (path.StartsWith(@"\\")) throw new Exception("Use a folder on this computer, not a network share.");
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
        // a feature isn't finished until its time is logged: the handover says whether yours is
        if (Jira.Configured && await CurrentUser() is { } me && stations.FirstOrDefault(x => x.Id == "qa") is { } handover)
        {
            try
            {
                var keysToCheck = scope.Select(s => s.Key).Prepend(key).Distinct().ToList();
                var mine = (await Task.WhenAll(keysToCheck.Select(async k => (await Jira.Worklogs(k)).Where(w => (string?)w["author"]?["accountId"] == me.AccountId).Sum(w => (int?)w["timeSpentSeconds"] ?? 0)))).Sum();
                var hours = mine / 3600; var minutes = mine % 3600 / 60;
                handover.Items.Add(mine > 0
                    ? new("done", $"{(hours > 0 ? $"{hours}h " : "")}{(minutes > 0 ? $"{minutes}m " : "")}logged by you".Trim(), keysToCheck.Count > 1 ? $"Across {keysToCheck.Count} tickets." : null)
                    : new("check", "No time logged by you yet", "Log your time on the ticket before it goes to QA, so nothing is missing later."));
                emit(new JsonObject { ["type"] = "station", ["station"] = ReportJson.Station(handover) });
            }
            catch { /* worklogs unreadable: the handover just doesn't mention time */ }
        }

        Lap("steps");
        var doc = Manifest.Build(key, issue, scope, results, highlights, dbCfg, (int)cfg["jiraMaxFilesPerRepo"]!);
        QaPrep.AddToDoc(doc, stations);
        var setup = AccessScan.Setup(results, name => paths.GetValueOrDefault(name), Repos);
        setup["data"] = LookupScan.Find(results, name => paths.GetValueOrDefault(name), (string?)cfg["lookups"]?["connectionEnv"], dbCfg.Repo);
        AccessScan.AddToDoc(doc, setup);

        Directory.CreateDirectory(OutDir);
        var mdPath = Path.Combine(OutDir, $"{key}.md");
        var markdown = doc.Markdown();
        File.WriteAllText(mdPath, markdown);

        var report = new Report(key, DateTime.Now, issue, scope, ancestors, results, doc, Manifest.Warnings(key, results),
                                Manifest.Highlights(results, highlights, dbCfg), markdown, mdPath, stations, subs, tagDetails) { Setup = setup };
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
    // git by full path, found once on PATH: never a git.exe that happens to sit in the current folder
    public static readonly Lazy<string> GitExe = new(() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => { try { return Path.Combine(d.Trim('"'), "git.exe"); } catch { return ""; } })
            .FirstOrDefault(f => f != "" && Path.IsPathRooted(f) && File.Exists(f)) ?? "git");

    public static string Run(string repo, params string[] args) => RunWithInput(repo, null, args);

    public static string RunWithInput(string repo, string? input, params string[] args)
    {
        var psi = new ProcessStartInfo(GitExe.Value)
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

    // git's empty tree, to diff a file against "before it existed"
    public static string EmptyTree(string repo) => RunWithInput(repo, "", "hash-object", "-t", "tree", "--stdin").Trim();


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
record BranchInfo(string Name, int Unmerged)
{
    // where the branch stands against main and dev: commits still to merge in, and commits it is missing from them
    public List<BranchVs> Vs { get; init; } = [];
    // when its newest commit was written (unix seconds): a merge request merged before that didn't take it
    public long TipAt { get; init; }
}
record BranchVs(string Target, int Ahead, int Behind);
// Path: where the script lives in the repository (the release copy once it graduated), so the UI can open it
record DbItem(string Service, string Location, string Kind, string File, string Status, bool Warning, string? Path = null);
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
        var branchShas = new List<(string Name, HashSet<string> Unmerged, string? Tip)>();
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
            var tipRef = group.OrderBy(b => b.StartsWith("origin/") ? 0 : 1).First();
            var tip = ix.Refs.FirstOrDefault(x => x.Name == (tipRef.StartsWith("origin/") ? $"refs/remotes/{tipRef}" : $"refs/heads/{tipRef}")).Sha;
            branchShas.Add((group.Key + (pushed ? "" : " (local only, never pushed)"), unmerged, tip));
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

        // a squash merge lands a whole branch as one new commit, which matches none of its commits' patch ids:
        // when merging the branch into the default branch changes nothing, its commits are in there too
        foreach (var b in branchShas)
            if (b.Tip is not null && b.Unmerged.Any(s => !landed.Contains(s)) && MergeChangesNothing(repo, defaultRef, b.Tip))
                landed.UnionWith(b.Unmerged);

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
            .Select(b => new BranchInfo(b.Name, b.Unmerged.Count(s => !foreign.Contains(s) && !landed.Contains(s)))
            {
                Vs = Compare(repo, ix, b.Tip, defaultRef),
                TipAt = b.Tip is not null && ix.BySha.TryGetValue(b.Tip, out var tc) ? tc.At : 0,
            })
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


    // Merging the branch into the target, without touching the working copy (git merge-tree): the same tree as the
    // target means the target already holds everything the branch changes. A conflict means it doesn't.
    internal static bool MergeChangesNothing(string repo, string target, string tip)
    {
        var merged = Git.Lines(Git.Cached(repo, null, "merge-tree", "--write-tree", "--no-messages", target, tip)).FirstOrDefault();
        var tree = Git.Cached(repo, null, "rev-parse", $"{target}^{{tree}}").Trim();
        return merged is { Length: 40 } && merged == tree;
    }

    // A branch against main (or master) and dev (or develop), whichever exist, walked in the in-memory history:
    // Ahead = its commits not in the target (still to merge), Behind = the target's commits it doesn't have (to bring in).
    // A branch whose change the target already has counts as merged, whatever the commit ids: GitLab's rebase
    // merges land each commit again under a new id (matched by patch id), and squash merges land several commits
    // as one (no single patch id matches, so the branch is merged into the target in memory: if that changes
    // nothing, the target has it all).
    static List<BranchVs> Compare(string repo, RepoIndex ix, string? tip, string defaultRef)
    {
        var list = new List<BranchVs>();
        if (tip is null || !ix.BySha.ContainsKey(tip)) return list;
        var mine = ix.Reachable(tip);
        foreach (var name in new[] { "main", "master", "dev", "develop" })
        {
            var target = ix.Refs.FirstOrDefault(r => r.Name == $"refs/remotes/origin/{name}");
            if (target.Sha is null || list.Any(x => x.Target is "main" or "master" && name is "main" or "master" || x.Target is "dev" or "develop" && name is "dev" or "develop")) continue;
            var theirs = ix.Reachable(target.Sha);
            int Count(IEnumerable<string> shas) => shas.Count(s => ix.BySha.TryGetValue(s, out var c) && c.Parents.Length < 2);
            var ahead = Count(mine.Where(s => !theirs.Contains(s)));
            if (ahead > 0 && int.TryParse(Git.Cached(repo, null, "rev-list", "--count", "--cherry-pick", "--right-only", "--no-merges", $"{target.Sha}...{tip}").Trim(), out var notLanded))
                ahead = Math.Min(ahead, notLanded);
            if (ahead > 0 && MergeChangesNothing(repo, target.Sha, tip)) ahead = 0;
            list.Add(new BranchVs(name, ahead, Count(theirs.Where(s => !mine.Contains(s)))));
        }
        return list;
    }

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
                    onDefault ? $"in release tag {loc}" : $"in tag {loc}, not yet on {defaultRef}", !onDefault, path));
                continue;
            }
            if (head.Contains(path))
            {
                items.Add(new DbItem(svc, loc, kind, file, "STAGED in dev/ only, not in any release tag", true, path));
                continue;
            }
            // graduated? routines keep their file name; schema/seed blocks carry a "-- from dev/<Kind>/<file>" marker
            var byName = head.FirstOrDefault(h => h.StartsWith($"{cfg.Root}/{svc}/") && !h.StartsWith($"{cfg.Root}/{svc}/{cfg.Staging}/")
                                                  && h.EndsWith("/" + file));
            var tag = byName?.Split('/')[2];
            var where = byName;
            if (tag is null)
            {
                var hit = Git.Lines(Git.Run(repo, "grep", "-l", "-F", $"from {cfg.Staging}/{kind}/{file}", defaultRef, "--", $"{cfg.Root}/{svc}/"))
                             .FirstOrDefault();
                where = hit?.Split(':', 2).ElementAtOrDefault(1);
                tag = where?.Split('/').ElementAtOrDefault(2);
            }
            // not on the default branch: it still lives at its dev/ path on the ticket's branch
            items.Add(tag is not null
                ? new DbItem(svc, loc, kind, file, $"graduated to release tag {tag}", false, where)
                : new DbItem(svc, loc, kind, file, $"removed from dev/ and NOT found in any tag on {defaultRef}", true, path));
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

// ───────────────────────────── access: what QA needs set up in the System Manager ─────────────────────────────
// Scopes register themselves when a service starts (from [AuthorizeScope] endpoints), but granting them to roles,
// creating menus and their actions, and linking actions to scopes are done by hand in the System Manager on each
// environment. This works out, from the code, exactly what that is for a ticket.
static class AccessScan
{
    static readonly Regex ClassRx = new(@"class\s+(\w+?)Controller(?:V(\d+))?\b");
    static readonly Regex AreaRx = new(@"\[Area\(\s*""([^""]+)""\s*\)\]");
    static readonly Regex MethodRx = new(@"public\s+(?:async\s+)?[\w<>\[\],\s\.?]+?\s(\w+)\s*\(");
    static readonly Regex HttpRx = new(@"\[Http(Get|Post|Put|Delete|Patch)\b");
    static readonly Regex ActionNameRx = new(@"\[ActionName\(\s*""([^""]+)""");
    static readonly Regex ExplicitRx = new(@"\[AuthorizeScope\(\s*""([^""]*)""\s*,\s*""([^""]*)""");
    public static readonly Regex RouteRx = new(@"\bpath\s*:\s*[""'`](/[^""'`]*)[""'`]");
    public static readonly Regex StartupRx = new(@"GenerateScopesAsync\(\s*""([^""]+)""");

    // one protected endpoint: its scope, and how it is called
    public record Endpoint(string Scope, string Verb, string Route, string Action, string Controller);
    static readonly Regex HttpRouteRx = new(@"\[Http(Get|Post|Put|Delete|Patch)(?:\(\s*""([^""]*)"")?");
    static readonly Regex RouteAttrRx = new(@"\[Route\(\s*""([^""]*)""");

    public static HashSet<string> Scopes(string code) => Endpoints(code).Select(e => e.Scope).ToHashSet(StringComparer.OrdinalIgnoreCase);

    // The endpoints a controller protects, with each scope named the way the access check builds it at runtime
    // ([area:]controller[:vN]:action:method, lower case, without the service's Common:AppName prefix) and its full route.
    public static List<Endpoint> Endpoints(string code)
    {
        var list = new List<Endpoint>();
        if (string.IsNullOrEmpty(code) || ClassRx.Match(code) is not { Success: true } cls) return list;
        var controller = cls.Groups[1].Value;
        var ver = cls.Groups[2].Success ? $":v{cls.Groups[2].Value}" : "";
        var areaName = AreaRx.Match(code) is { Success: true } a && a.Index < cls.Index ? a.Groups[1].Value : "";
        var prefix = $"{(areaName == "" ? "" : areaName + ":")}{controller}{ver}";
        var classRoute = RouteAttrRx.Matches(code[..cls.Index]).LastOrDefault()?.Groups[1].Value ?? "";
        string Url(string method, string action) => "/" + string.Join("/", new[] { classRoute, method }.Where(x => x != "").Select(x => x.Trim('/')))
            .Replace("[area]", areaName.ToLowerInvariant()).Replace("[controller]", controller).Replace("[action]", action);
        for (var at = code.IndexOf("[AuthorizeScope", StringComparison.Ordinal); at >= 0; at = code.IndexOf("[AuthorizeScope", at + 1, StringComparison.Ordinal))
        {
            if (MethodRx.Match(code, at) is not { Success: true } m) break;
            var start = code.LastIndexOf('\n', at) + 1;
            while (start > 0)
            {
                var prevStart = code.LastIndexOf('\n', start - 2) + 1;
                var prev = code[prevStart..(start - 1)].Trim();
                if (prev.Length > 0 && !prev.StartsWith('[') && !prev.StartsWith("//")) break;
                start = prevStart;
            }
            var attrs = code[start..m.Index];
            var action = ActionNameRx.Match(attrs) is { Success: true } an ? an.Groups[1].Value : m.Groups[1].Value;
            var explicitScope = ExplicitRx.Match(attrs) is { Success: true } ex && ex.Groups[2].Value != "" ? ex.Groups[2].Value.ToLowerInvariant() : null;
            var methodRoute = RouteAttrRx.Match(attrs) is { Success: true } mr ? mr.Groups[1].Value : null;
            var https = HttpRouteRx.Matches(attrs).ToList();
            if (https.Count == 0) list.Add(new(explicitScope ?? $"{prefix}:{action}:any".ToLowerInvariant(), "ANY", Url(methodRoute ?? "", action), action, controller));
            foreach (var h in https)
            {
                var verb = h.Groups[1].Value;
                list.Add(new(explicitScope ?? $"{prefix}:{action}:{verb}".ToLowerInvariant(), verb.ToUpperInvariant(),
                             Url(h.Groups[2].Success ? h.Groups[2].Value : methodRoute ?? "", action), action, controller));
            }
        }
        return list;
    }

    // per controller file the ticket touched: the scopes it protected before the ticket's first change and after its last
    public static (SortedSet<string> Added, SortedSet<string> Removed) Changes(string repo, IReadOnlyList<Commit> own)
    {
        var (a, r) = EndpointChanges(repo, own);
        return (new(a.Select(e => e.Scope), StringComparer.OrdinalIgnoreCase), new(r.Select(e => e.Scope), StringComparer.OrdinalIgnoreCase));
    }
    // the same comparison, keeping every endpoint's details (for the System Manager setup table)
    public static (List<Endpoint> Added, List<Endpoint> Removed) EndpointChanges(string repo, IReadOnlyList<Commit> own)
    {
        var added = new List<Endpoint>(); var removed = new List<Endpoint>();
        foreach (var g in ControllerFiles(own))
        {
            var (before, after) = Versions(repo, g);
            var b = Endpoints(before); var a = Endpoints(after);
            var bs = b.Select(e => e.Scope).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var as_ = a.Select(e => e.Scope).ToHashSet(StringComparer.OrdinalIgnoreCase);
            added.AddRange(a.Where(e => !bs.Contains(e.Scope)));
            removed.AddRange(b.Where(e => !as_.Contains(e.Scope)));
        }
        return (added.DistinctBy(e => e.Scope).OrderBy(e => e.Scope).ToList(), removed.DistinctBy(e => e.Scope).OrderBy(e => e.Scope).ToList());
    }

    static IEnumerable<IGrouping<string, (Commit c, string Status, string Path)>> ControllerFiles(IReadOnlyList<Commit> own) =>
        own.SelectMany(c => c.Files.Select(f => (c, f.Status, f.Path)))
           .Where(x => x.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && x.Path.Contains("Controller", StringComparison.OrdinalIgnoreCase))
           .GroupBy(x => x.Path);

    static (string Before, string After) Versions(string repo, IGrouping<string, (Commit c, string Status, string Path)> g)
    {
        var first = g.First(); var last = g.Last();   // commits are oldest first
        return (first.Status == "A" ? "" : Git.Cached(repo, null, "show", $"{first.c.Sha}^:{g.Key}"),
                last.Status == "D" ? "" : Git.Cached(repo, null, "show", $"{last.c.Sha}:{g.Key}"));
    }

    // every protected endpoint of a repository at a revision (to match what a page calls, new or not)
    static readonly ConcurrentDictionary<string, List<Endpoint>> catalogs = new();
    public static List<Endpoint> Catalog(string repo, string rev)
    {
        var id = $"{repo}|{Git.Fingerprint(repo)}|{rev}";
        if (catalogs.TryGetValue(id, out var hit)) return hit;
        var files = Git.Lines(Git.Cached(repo, null, "grep", "-l", "-F", "[AuthorizeScope", rev, "--", "*.cs"))
                       .Select(l => l.StartsWith(rev + ":") ? l[(rev.Length + 1)..] : l).ToList();
        var list = files.AsParallel().WithDegreeOfParallelism(6).SelectMany(f => Endpoints(Git.Cached(repo, null, "show", $"{rev}:{f}"))).ToList();
        if (catalogs.Count > 200) catalogs.Clear();
        return catalogs[id] = list;
    }

    // ── front-end pages: the menu each new page needs, its actions, and the scopes those actions call ──
    public record PageCall(string Path, string? Verb);
    static readonly Regex ElementRx = new(@"element\s*:\s*<\s*(\w+)");
    static readonly Regex ActionUseRx = new(@"(?:hasAction\(\s*\w+\s*,\s*|actions\??\.includes\(\s*)[""'`]([^""'`]+)[""'`]");
    static readonly Regex ConstPathRx = new(@"const\s+(\w+)\s*=\s*[""'`](/[^""'`$]*)[""'`]");
    static readonly Regex TemplateCallRx = new(@"`\$\{(\w+)\}(/[^`]*)`");
    static readonly Regex LiteralCallRx = new(@"[""'`](/(?:[\w.-]+/)*api/[^""'`\s]*)[""'`]");
    static readonly Regex VerbRx = new(@"\.(get|post|put|delete|patch)\s*[<(]", RegexOptions.IgnoreCase);

    // what a front-end ticket's changed files declare: actions checked, and API paths called
    public static (List<string> Actions, List<PageCall> Calls) PageUse(IEnumerable<string> sources)
    {
        var actions = new List<string>(); var calls = new List<PageCall>();
        foreach (var src in sources)
        {
            foreach (Match m in ActionUseRx.Matches(src)) if (!actions.Contains(m.Groups[1].Value)) actions.Add(m.Groups[1].Value);
            var consts = ConstPathRx.Matches(src).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
            foreach (var line in src.Split('\n'))
            {
                var verb = VerbRx.Match(line) is { Success: true } v ? v.Groups[1].Value.ToUpperInvariant() : null;
                foreach (Match m in TemplateCallRx.Matches(line))
                    if (consts.TryGetValue(m.Groups[1].Value, out var basePath)) calls.Add(new(basePath + Regex.Replace(m.Groups[2].Value, @"\$\{[^}]*\}", "{}"), verb));
                foreach (Match m in LiteralCallRx.Matches(line)) if (!consts.ContainsValue(m.Groups[1].Value)) calls.Add(new(Regex.Replace(m.Groups[1].Value, @"\$\{[^}]*\}", "{}"), verb));
            }
        }
        return (actions, calls.DistinctBy(c => (c.Path.ToLowerInvariant(), c.Verb)).ToList());
    }

    // does a backend route serve a front-end call? The call may carry a gateway prefix (e.g. /core), parameters are wildcards
    static string[] Segs(string path) => path.Split('?')[0].Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries)
        .Select(s => s.StartsWith('{') || s.Contains("${") ? "*" : s.Replace("-", "").ToLowerInvariant()).ToArray();
    public static bool Serves(Endpoint e, PageCall c)
    {
        if (c.Verb is not null && e.Verb != "ANY" && !e.Verb.Equals(c.Verb, StringComparison.OrdinalIgnoreCase)) return false;
        var b = Segs(e.Route); var f = Segs(c.Path);
        if (b.Length == 0 || f.Length < b.Length) return false;
        // a route that is mostly placeholders would match anything: it must agree on at least two real words
        if (b.Count(x => x != "*" && x != "api" && !x.StartsWith("v")) < 2) return false;
        var off = f.Length - b.Length;
        for (var i = 0; i < b.Length; i++)
        {
            string bs = b[i], fs = f[off + i];
            if (bs == "*" || fs == "*" || bs == fs) continue;
            if (bs.StartsWith("v*") || (bs.StartsWith("v") && bs.Contains("apiversion"))) { if (Regex.IsMatch(fs, @"^v\d+$")) continue; }
            return false;
        }
        return true;
    }

    // which action a scope belongs to: reads go with VIEW; the rest go to the action whose name they share a word with,
    // then by kind (PUT/PATCH to Edit, DELETE to Delete, POST to Add/Create), and anything left to Manage
    public static Dictionary<string, List<Endpoint>> Link(List<string> actions, List<Endpoint> used, out List<Endpoint> unlinked)
    {
        var map = actions.ToDictionary(a => a, _ => new List<Endpoint>(), StringComparer.OrdinalIgnoreCase);
        unlinked = [];
        static IEnumerable<string> Words(string s) => Regex.Split(Regex.Replace(s, "([a-z])([A-Z])", "$1 $2"), @"[^A-Za-z]+").Where(w => w.Length > 2).Select(w => w.ToLowerInvariant());
        // the first of the names (in priority order) that the page has as an action
        string? Find(params string[] names) => names.Select(n => actions.FirstOrDefault(a => a.Equals(n, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(a => a is not null);
        foreach (var e in used)
        {
            string? target = null;
            if (e.Verb == "GET") target = Find("VIEW", "View");
            target ??= actions.Where(a => !a.Equals("VIEW", StringComparison.OrdinalIgnoreCase))
                              .FirstOrDefault(a => Words(a).Intersect(Words(e.Action + " " + e.Route)).Any());
            target ??= e.Verb switch
            {
                "PUT" or "PATCH" => Find("Edit", "Update", "Manage"),
                "DELETE" => Find("Delete", "Remove", "Manage"),
                "POST" => Find("Add", "Create", "Manage"),
                _ => null,
            };
            target ??= Find("Manage");
            if (target is null) unlinked.Add(e); else map[target].Add(e);
        }
        return map;
    }

    static JsonObject Ep(Endpoint e, string repo) => new() { ["scope"] = e.Scope, ["verb"] = e.Verb, ["route"] = e.Route, ["action"] = $"{e.Controller}.{e.Action}", ["repo"] = repo };
    static readonly Regex RouteFileRx = new(@"(?i)rout[^/]*\.(jsx?|tsx?)$");
    static readonly Regex PageFileRx = new(@"(?i)\.(jsx?|tsx?)$");

    // Everything to set up in the System Manager on QA for a ticket: per service the scopes it adds and removes;
    // per new page the menu, its actions, and the scopes to link to each action.
    public static JsonObject Setup(List<RepoResult> results, Func<string, string?> pathOf, IReadOnlyList<string> allRepos)
    {
        var services = new JsonArray();
        var pool = new List<(Endpoint E, string Repo, bool New)>();
        foreach (var r in results)
        {
            if (pathOf(r.Name) is not { } path) continue;
            var own = r.Commits.Where(c => !c.IsForeign).ToList();
            if (own.Count == 0) continue;
            var (added, removed) = EndpointChanges(path, own);
            pool.AddRange(added.Select(e => (e, r.Name, true)));
            if (added.Count + removed.Count == 0) continue;
            var tag = StartupRx.Match(Git.Cached(path, null, "grep", "-h", "-F", "GenerateScopesAsync(", own[^1].Sha, "--", "*.cs")) is { Success: true } t ? t.Groups[1].Value : null;
            services.Add(new JsonObject
            {
                ["repo"] = r.Name, ["tag"] = tag,
                ["added"] = new JsonArray(added.Select(e => (JsonNode)Ep(e, r.Name)).ToArray()),
                ["removed"] = new JsonArray(removed.Select(e => (JsonNode)Ep(e, r.Name)).ToArray()),
            });
        }

        var menus = new JsonArray();
        foreach (var r in results)
        {
            if (pathOf(r.Name) is not { } path) continue;
            var own = r.Commits.Where(c => !c.IsForeign).ToList();
            var touched = own.SelectMany(c => c.Files.Select(f => (c, f.Status, f.Path))).Where(x => PageFileRx.IsMatch(x.Path)).GroupBy(x => x.Path).ToList();
            if (touched.Count == 0) continue;
            // new routes: path entries added in routing files
            var patches = Git.Many(path, own.Select(c => c.Sha), "-p", "--unified=0", "--no-color", "--format=%x1e%H");
            var routes = new List<(string Uri, string File)>();
            foreach (var c in own)
            {
                string? file = null;
                foreach (var raw in patches.GetValueOrDefault(c.Sha, "").Split('\n'))
                {
                    var line = raw.TrimEnd('\r');
                    if (line.StartsWith("+++ ")) { file = line.StartsWith("+++ b/") ? line[6..] : null; continue; }
                    if (file is not null && line.StartsWith('+') && RouteFileRx.IsMatch(file) && RouteRx.Match(line) is { Success: true } m && !routes.Any(x => x.Uri == m.Groups[1].Value))
                        routes.Add((m.Groups[1].Value, file));
                }
            }
            if (routes.Count == 0) continue;
            // the ticket's page sources as they stand after its last change (tests and mocks left out)
            string Latest(IGrouping<string, (Commit c, string Status, string Path)> g) => g.Last().Status == "D" ? "" : Git.Cached(path, null, "show", $"{g.Last().c.Sha}:{g.Key}");
            // shared plumbing (API clients, configuration, helpers) is not the page: leave it out
            var sources = touched.Where(g => !Regex.IsMatch(g.Key, @"(?i)(test|mock|spec|/config/|/helpers?/|/utils?/|reusable)")).Select(Latest).ToList();
            var (pageActions, calls) = PageUse(sources);
            // match calls: this ticket's new endpoints first, then every protected endpoint in the repositories
            var matched = new List<(PageCall Call, Endpoint? E, string? Repo)>();
            List<(Endpoint E, string Repo, bool New)>? everything = null;
            foreach (var call in calls)
            {
                var hit = pool.FirstOrDefault(p => Serves(p.E, call));
                if (hit.E is null)
                {
                    everything ??= allRepos.AsParallel().WithDegreeOfParallelism(6)
                        .SelectMany(repo => { try { var ix = RepoIndex.For(repo); return Catalog(repo, ix.DefaultRef).Select(e => (e, Path.GetFileName(repo), false)); } catch { return []; } })
                        .ToList();
                    hit = everything.FirstOrDefault(p => Serves(p.E, call));
                }
                matched.Add((call, hit.E, hit.E is null ? null : hit.Repo));
            }
            var used = matched.Where(m => m.E is not null).Select(m => (m.E!, m.Repo!)).DistinctBy(x => x.Item1.Scope).ToList();
            var actions = new List<string> { "VIEW" };
            actions.AddRange(pageActions.Where(a => !a.Equals("VIEW", StringComparison.OrdinalIgnoreCase)));
            var link = Link(actions, used.Select(u => u.Item1).ToList(), out var unlinked);
            string RepoOf(Endpoint e) => used.First(u => u.Item1.Scope == e.Scope).Item2;
            foreach (var (uri, file) in routes)
            {
                var routeFile = Git.Cached(path, null, "show", $"{own[^1].Sha}:{file}");
                var at = routeFile.IndexOf($"\"{uri}\"", StringComparison.Ordinal);
                var page = at >= 0 && ElementRx.Match(routeFile, at) is { Success: true } em && em.Index - at < 400 ? em.Groups[1].Value : null;
                menus.Add(new JsonObject
                {
                    ["repo"] = r.Name, ["uri"] = uri.TrimStart('/'), ["page"] = page, ["routeFile"] = file,
                    ["actions"] = new JsonArray(actions.Select(a => (JsonNode)new JsonObject
                    {
                        ["name"] = a,
                        ["scopes"] = new JsonArray(link[a].Select(e => (JsonNode)Ep(e, RepoOf(e))).ToArray()),
                    }).ToArray()),
                    ["unlinked"] = new JsonArray(unlinked.Select(e => (JsonNode)Ep(e, RepoOf(e))).ToArray()),
                    ["unmatched"] = new JsonArray(matched.Where(m => m.E is null).Select(m => (JsonNode)$"{m.Call.Verb ?? "?"} {m.Call.Path}").ToArray()),
                    ["calls"] = new JsonArray(matched.Select(m => (JsonNode)new JsonObject { ["verb"] = m.Call.Verb, ["path"] = m.Call.Path, ["scope"] = m.E?.Scope, ["repo"] = m.Repo }).ToArray()),
                });
            }
        }
        return new JsonObject { ["services"] = services, ["menus"] = menus };
    }

    // the same, as tables in the handover (Jira comment and .md)
    public static void AddToDoc(Doc doc, JsonObject setup)
    {
        var services = setup["services"]!.AsArray(); var menus = setup["menus"]!.AsArray();
        // lookup rows to create by hand: those no script seeds and that are newer than the ticket
        var data = setup["data"]?["rows"]?.AsArray().Where(d => (string?)d!["seeded"] is null && (bool?)d["older"] != true).ToList() ?? [];
        if (services.Count == 0 && menus.Count == 0 && data.Count == 0) return;
        var blocks = new List<Block> { new Heading(3, "System Manager setup on QA") };
        var scopeRows = services.SelectMany(s => s!["added"]!.AsArray().Select(e => new List<Span>[]
            { [new((string)s["repo"]!)], [new((string)e!["scope"]!, Code: true)], [new($"{(string)e["verb"]!} {(string)e["route"]!}", Code: true)], [new("grant to QA roles")] })
            .Concat(s["removed"]!.AsArray().Select(e => new List<Span>[]
            { [new((string)s["repo"]!)], [new((string)e!["scope"]!, Code: true)], [new($"{(string)e["verb"]!} {(string)e["route"]!}", Code: true)], [new("removed")] }))).ToList();
        if (scopeRows.Count > 0)
        {
            blocks.Add(new Para([new("Scopes (prefix each with the service's AppName):")]));
            blocks.Add(new Table(["Service", "Scope", "Endpoint", "To do"], scopeRows));
        }
        foreach (var m in menus)
        {
            blocks.Add(new Para([new("Menu "), new((string)m!["uri"]!, Code: true), new($" ({(string?)m["page"] ?? "new page"}, {(string)m["repo"]!})")]));
            var rows = m["actions"]!.AsArray().Select(a => new List<Span>[]
            {
                [new((string)a!["name"]!, Code: true)],
                a["scopes"]!.AsArray().Count == 0 ? [new("-")] : a["scopes"]!.AsArray().Select(x => new Span((string)x!["scope"]! + " ", Code: true)).ToList(),
            }).ToList();
            if (m["unlinked"]!.AsArray().Count > 0)
                rows.Add([[new("(choose an action)")], m["unlinked"]!.AsArray().Select(x => new Span((string)x!["scope"]! + " ", Code: true)).ToList()]);
            blocks.Add(new Table(["Action", "Scopes to link"], rows));
        }
        if (data.Count > 0)
        {
            blocks.Add(new Para([new("Data the code needs (made by hand on dev, no script creates it):")]));
            blocks.Add(new Table(["What", "Tag", "Name", "On dev since"], data.Select(d => new List<Span>[]
                { [new($"{(string)d!["kind"]!} ({(string)d["schema"]!})")], [new((string)d["tag"]!, Code: true)], [new((string?)d["name"] ?? "-")], [new((string?)d["created"] ?? "-")] }).ToList()));
        }
        var at = doc.Blocks.FindIndex(b => b is Heading { Text: "QA preparation" });
        doc.Blocks.InsertRange(at < 0 ? Math.Min(2, doc.Blocks.Count) : Math.Min(doc.Blocks.Count, at + 1 + doc.Blocks.Skip(at + 1).TakeWhile(b => b is not Heading).Count()), blocks);
    }
}

// ───────────────────────────── System Manager data: lookup rows the ticket's code names by tag ─────────────────────────────

// Categories, types, features, criteria and the like live in tables with a `tag` column; code refers to a row by that tag
// (e.g. "IVR_MENU_BUILDER"). Rows made by hand in the System Manager on dev must be made again on QA, so: collect the
// UPPER_SNAKE literals the ticket's own commits add in code, look them up in every tag table of the dev database
// (a read-only session), and list each hit with what it is, whether a SQL script already seeds it, and whether it
// existed before the ticket's first commit (then QA most likely has it already).
static class LookupScan
{
    static readonly Regex CodeFile = new(@"(?i)\.(cs|jsx?|tsx?)$");
    static readonly Regex SkipFile = new(@"(?i)(test|mock|spec|\.d\.ts$|/migrations?/)");
    static readonly Regex TagLit = new(@"[""'`]([A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+|[A-Z][A-Z0-9]{4,})[""'`]");
    static readonly Regex SkipSchema = new(@"(^dump|_old$|_\d{8}$|history|^log$|^dba$|^pg_)");
    static readonly HashSet<string> Common = new(StringComparer.Ordinal) { "PATCH", "DELETE", "OPTIONS", "ERROR", "WARNING", "SUCCESS", "FAILED", "TRUE", "FALSE", "NULL" };

    public static JsonObject Find(List<RepoResult> results, Func<string, string?> pathOf, string? connEnv, string dbRepo)
    {
        var rows = new JsonArray();
        var res = new JsonObject { ["rows"] = rows };
        // tag → where the ticket first names it
        var named = new Dictionary<string, (string Repo, string File)>(StringComparer.Ordinal);
        foreach (var r in results)
        {
            if (pathOf(r.Name) is not { } path) continue;
            var own = r.Commits.Where(c => !c.IsForeign).ToList();
            if (own.Count == 0) continue;
            var patches = Git.Many(path, own.Select(c => c.Sha), "-p", "--unified=0", "--no-color", "--format=%x1e%H");
            foreach (var c in own)
            {
                string? file = null;
                foreach (var raw in patches.GetValueOrDefault(c.Sha, "").Split('\n'))
                {
                    var line = raw.TrimEnd('\r');
                    if (line.StartsWith("+++ ")) { file = line.StartsWith("+++ b/") && CodeFile.IsMatch(line) && !SkipFile.IsMatch(line) ? line[6..] : null; continue; }
                    if (file is null || !line.StartsWith('+')) continue;
                    foreach (Match m in TagLit.Matches(line))
                        if (!Common.Contains(m.Groups[1].Value)) named.TryAdd(m.Groups[1].Value, (r.Name, file));
                }
            }
        }
        if (named.Count == 0) return res;
        var cs = connEnv is null ? null : Environment.GetEnvironmentVariable(connEnv) ?? Environment.GetEnvironmentVariable(connEnv, EnvironmentVariableTarget.User);
        res["candidates"] = named.Count;
        if (string.IsNullOrWhiteSpace(cs)) { res["status"] = "no-connection"; res["env"] = connEnv; return res; }

        // the answer for this set of tags is remembered for a while, and an unreachable database too (off VPN every
        // ticket load would otherwise wait for the connect timeout)
        var tagSet = named.Keys.Order(StringComparer.Ordinal).ToArray();
        var memoKey = $"{Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(cs)))}|{string.Join(",", tagSet)}";
        if (!memo.TryGetValue(memoKey, out var known) || DateTime.UtcNow > known.Until)
        {
            known = Query(cs, tagSet);
            if (memo.Count > 200) memo.Clear();
            memo[memoKey] = known;
        }
        if (known.Error is not null) { res["status"] = "error"; res["error"] = known.Error; return res; }
        var hits = known.Hits!;
        if (hits.Count == 0) return res;
        return Describe(res, rows, hits, named, results, pathOf, dbRepo);
    }

    static readonly ConcurrentDictionary<string, (DateTime Until, List<(string Schema, string Table, string Tag, DateTime? Created, string? Name, long N)>? Hits, string? Error)> memo = new();
    // which tables have a tag column barely changes: read once per half hour per database
    static readonly ConcurrentDictionary<string, (DateTime Until, List<(string S, string T, bool Created, bool Deleted, string? NameSql)> Tables)> catalogs = new();

    static (DateTime Until, List<(string Schema, string Table, string Tag, DateTime? Created, string? Name, long N)>? Hits, string? Error) Query(string cs, string[] tagSet)
    {
        var hits = new List<(string Schema, string Table, string Tag, DateTime? Created, string? Name, long N)>();
        try
        {
            var b = new Npgsql.NpgsqlConnectionStringBuilder(cs) { Options = "-c default_transaction_read_only=on", ApplicationName = "greenlight", Timeout = 5, CommandTimeout = 30 };
            using var conn = new Npgsql.NpgsqlConnection(b.ConnectionString);
            conn.Open();
            static string Q(string id) => "\"" + id.Replace("\"", "\"\"") + "\"";
            var catKey = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(cs)));
            // every live table with a tag column: whether it has created_date / is_deleted, and where its display name lives
            var tables = catalogs.TryGetValue(catKey, out var cat) && DateTime.UtcNow < cat.Until ? cat.Tables : new List<(string S, string T, bool Created, bool Deleted, string? NameSql)>();
            if (tables.Count == 0)
            using (var cmd = new Npgsql.NpgsqlCommand("""
                SELECT c.table_schema, c.table_name,
                       EXISTS (SELECT 1 FROM information_schema.columns x WHERE x.table_schema = c.table_schema AND x.table_name = c.table_name AND x.column_name = 'created_date'),
                       EXISTS (SELECT 1 FROM information_schema.columns x WHERE x.table_schema = c.table_schema AND x.table_name = c.table_name AND x.column_name = 'is_deleted'),
                       EXISTS (SELECT 1 FROM information_schema.columns x WHERE x.table_schema = c.table_schema AND x.table_name = c.table_name AND x.column_name = 'name'),
                       (SELECT d.table_name::text FROM information_schema.columns d
                         WHERE d.table_schema = c.table_schema AND d.table_name IN (c.table_name || '_details', c.table_name || '_detail')
                           AND d.column_name = c.table_name || '_id'
                           AND EXISTS (SELECT 1 FROM information_schema.columns n WHERE n.table_schema = d.table_schema AND n.table_name = d.table_name AND n.column_name = 'name')
                         LIMIT 1)
                  FROM information_schema.columns c JOIN information_schema.tables t USING (table_schema, table_name)
                 WHERE c.column_name = 'tag' AND t.table_type = 'BASE TABLE'
                """, conn))
            using (var rd = cmd.ExecuteReader())
                while (rd.Read())
                {
                    var (s, t) = (rd.GetString(0), rd.GetString(1));
                    if (SkipSchema.IsMatch(s)) continue;
                    var nameSql = rd.GetBoolean(4) ? "x.name::text"
                        : rd.IsDBNull(5) ? null : $"(SELECT d.name::text FROM {Q(s)}.{Q(rd.GetString(5))} d WHERE d.{Q(t + "_id")} = x.id AND d.name IS NOT NULL LIMIT 1)";
                    tables.Add((s, t, rd.GetBoolean(2), rd.GetBoolean(3), nameSql));
                }
            catalogs[catKey] = (DateTime.UtcNow.AddMinutes(30), tables);
            if (tables.Count > 0)
            {
                // one statement over all of them; schema and table names come from the catalog and are passed as values
                var sql = string.Join("\nUNION ALL\n", tables.Select((t, i) =>
                    $"SELECT @s{i}, @t{i}, x.tag::text, {(t.Created ? "x.created_date::timestamp" : "NULL::timestamp")}, {t.NameSql ?? "NULL::text"} FROM {Q(t.S)}.{Q(t.T)} x WHERE x.tag::text = ANY(@tags){(t.Deleted ? " AND x.is_deleted IS NOT TRUE" : "")}"));
                using var cmd = new Npgsql.NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("tags", tagSet);
                for (var i = 0; i < tables.Count; i++) { cmd.Parameters.AddWithValue($"s{i}", tables[i].S); cmd.Parameters.AddWithValue($"t{i}", tables[i].T); }
                using var rd = cmd.ExecuteReader();
                var all = new List<(string S, string T, string Tag, DateTime? Created, string? Name)>();
                while (rd.Read()) all.Add((rd.GetString(0), rd.GetString(1), rd.GetString(2), rd.IsDBNull(3) ? null : rd.GetDateTime(3), rd.IsDBNull(4) ? null : rd.GetString(4)));
                // one row per tag and table: the same tag repeats once per tenant
                hits = all.GroupBy(x => (x.S, x.T, x.Tag))
                    .Select(g => (g.Key.S, g.Key.T, g.Key.Tag, g.Min(x => x.Created), g.Select(x => x.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)), (long)g.Count())).ToList();
            }
        }
        catch (Exception e) { return (DateTime.UtcNow.AddMinutes(2), null, e.Message); }
        return (DateTime.UtcNow.AddMinutes(10), hits, null);
    }

    static JsonObject Describe(JsonObject res, JsonArray rows, List<(string Schema, string Table, string Tag, DateTime? Created, string? Name, long N)> hits,
        Dictionary<string, (string Repo, string File)> named, List<RepoResult> results, Func<string, string?> pathOf, string dbRepo)
    {
        // tags a SQL script already inserts: in the migration repo's default branch, or on the ticket's own commits there
        var seeded = new Dictionary<string, string>(StringComparer.Ordinal);
        if (pathOf(dbRepo) is { } dbPath)
        {
            var refs = new List<string> { Git.DefaultRef(dbPath) };
            if (results.FirstOrDefault(r => r.Name.Equals(dbRepo, StringComparison.OrdinalIgnoreCase))?.Commits.LastOrDefault(c => !c.IsForeign) is { } last) refs.Add(last.Sha);
            var args = new List<string> { "grep", "-F", "-o" };
            foreach (var t in hits.Select(h => h.Tag).Distinct()) { args.Add("-e"); args.Add($"'{t}'"); }
            args.AddRange(refs); args.Add("--"); args.Add("*.sql");
            foreach (var line in Git.Lines(Git.Cached(dbPath, null, args.ToArray())))
            {
                // <ref>:<path>:'<TAG>'
                var q = line.LastIndexOf(":'", StringComparison.Ordinal); if (q < 0) continue;
                var file = line[..q];
                seeded.TryAdd(line[(q + 2)..].TrimEnd('\''), file[(file.IndexOf(':') + 1)..]);
            }
        }
        // rows created well before the ticket's first commit were not made for it: QA most likely has them
        var first = results.SelectMany(r => r.Commits.Where(c => !c.IsForeign)).Select(c => c.Date).DefaultIfEmpty("").Min()!;
        DateTime? since = DateTime.TryParse(first, out var f0) ? f0.AddDays(-2) : null;
        foreach (var h in hits.OrderBy(h => h.Schema).ThenBy(h => h.Table).ThenBy(h => h.Tag))
        {
            var (repo, file) = named[h.Tag];
            rows.Add(new JsonObject
            {
                ["schema"] = h.Schema, ["table"] = h.Table, ["kind"] = Kind(h.Table), ["tag"] = h.Tag, ["name"] = h.Name,
                ["created"] = h.Created?.ToString("yyyy-MM-dd"), ["tenants"] = h.N,
                ["repo"] = repo, ["file"] = file,
                ["seeded"] = seeded.GetValueOrDefault(h.Tag),
                ["older"] = since is not null && h.Created is not null && h.Created < since,
            });
        }
        return res;
    }

    // "bundle_category" → "Bundle category"
    public static string Kind(string table) => table.Length == 0 ? table : char.ToUpperInvariant(table[0]) + table[1..].Replace('_', ' ');
}

// ───────────────────────────── QA preparation: the stations a ticket passes on its way to QA ─────────────────────────────

// State: done (nothing left), todo (someone must act), check (a person should confirm), wait (blocked by an earlier
// station), skip (doesn't apply to this ticket).
record StationItem(string State, string Title, string? Detail = null, string? Repo = null,
                   string? ActionLabel = null, string? ActionUrl = null, List<ItemFile>? Files = null);
// a file an item is about: Local when it's in the working copy (opens in an editor), Web for GitLab otherwise
record ItemFile(string Path, bool Local, string? Web);
record Station(string Id, string Name, string State, string Headline, List<StationItem> Items);

static class QaPrep
{
    internal static readonly Regex ConfigFile = new(@"(?i)(appsettings[^/]*\.json$|(^|/)config\.[^/]*\.js$|(^|/)\.env(\.[^/]*)?$)");
    internal static readonly Regex PipelineFile = new(@"(?i)(\.gitlab-ci\.yml$|(^|/)dockerfile$|(^|/)downstream-env\.yml$)");
    internal static readonly Regex AccessFile = new(@"(?i)(permission|scope|role|polic|claim|menu|sidebar|navigation)");
    // A file named like one of those only counts when the lines added to it do access work: a name alone also catches
    // UI components such as ExportFormatMenu.tsx (a dropdown), which need nothing in the System Manager.
    // a new entry in a sidebar, navigation, menu or routes file (a page someone must reach) is access work without naming a role
    internal static readonly Regex NavFile = new(@"(?i)(sidebar|navigation|nav|menu|rout)[^/]*\.(jsx?|tsx?|json|ya?ml)$");
    internal static readonly Regex NavEntry = new(@"(?i)\b(path|to|href|url|uri|route|link)\s*[:=]\s*\{?\s*[""'`]/[\w\-/:.]*[""'`]");
    internal static readonly Regex AccessContent = new(@"(?i)(\[Authorize\b|\bAllowAnonymous\b|\bAddPolicy\b|\bpolicy\s*[:=]|\bRequire\w*(Scope|Role|Claim)|\b(has|check|is|can)\w*(Permission|Scope|Role|Action)s?\s*\(|\bpermissions?\s*[:=\[.]|\b(scopes?|roles?|claims?)\s*[:=\[]|\bactions\s*\??\.\s*includes\s*\(|\bmenu_?(uri|actions?)\b)");
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
        var code = Out(Code(repos, pathOf));
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

    // Lines tools add to commit messages to credit themselves; they don't belong in a merge request's description.
    internal static readonly Regex Attribution = new(@"(?im)^[ \t]*(co-authored-by:.*|🤖?[ \t]*generated with \[?claude code\]?.*)$\r?\n?");

    // What GitLab's new merge request form starts with: the message of the branch's one commit, or its commit subjects as a list,
    // without attribution lines. Left to itself, GitLab copies the commit message as it is.
    internal static string MrDescription(string? path, string branch, string into)
    {
        if (path is null) return "";
        var commits = Git.Records(Git.Cached(path, null, "log", "--no-merges", "--format=%x1e%s%x1f%b", $"origin/{into}..origin/{branch}"))
            .Select(f => (Subject: f.ElementAtOrDefault(0)?.Trim() ?? "", Body: f.ElementAtOrDefault(1) ?? "")).Where(c => c.Subject != "").ToList();
        var text = commits.Count == 1 ? commits[0].Body : string.Join("\n", commits.AsEnumerable().Reverse().Select(c => $"- {c.Subject}"));
        return Regex.Replace(Attribution.Replace(text, ""), @"\n{3,}", "\n\n").Trim();
    }

    static Station Code(List<RepoResult> repos, Func<string, string?> pathOf)
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
                string? NewMr(string into)
                {
                    if (r.WebUrl == "" || local) return null;
                    var desc = MrDescription(pathOf(r.Name), name, into);
                    return $"{r.WebUrl}/-/merge_requests/new?merge_request%5Bsource_branch%5D={Uri.EscapeDataString(name)}&merge_request%5Btarget_branch%5D={Uri.EscapeDataString(into)}"
                         + (desc == "" ? "" : $"&merge_request%5Bdescription%5D={Uri.EscapeDataString(desc[..Math.Min(desc.Length, 4000)])}");
                }
                string Commits(int n) => $"{n} commit{(n == 1 ? "" : "s")} waiting";
                if (local)
                {
                    items.Add(new("todo", $"Push and merge {name}", $"{Commits(b.Unmerged)}. The branch only exists on this computer.", r.Name));
                    continue;
                }
                // a service with a dev branch takes two merge requests from the ticket branch: into dev first, then into the default branch
                // (not when dev is the default branch itself: then there is one merge request, into it)
                var dev = b.Vs.FirstOrDefault(v => v.Target is "dev" or "develop" && v.Target != target);
                if (dev is null)
                {
                    var mr = NewMr(target);
                    items.Add(new("todo", $"Merge {name} into {target}", $"{Commits(b.Unmerged)}.", r.Name, mr is null ? null : "Open merge request", mr));
                    continue;
                }
                if (dev.Ahead == 0)
                {
                    var done = r.Merges.LastOrDefault(m => m.Target == dev.Target && m.MrUrl is not null && m.SourceBranch.EndsWith(name));
                    items.Add(new("done", $"Merged {name} into {dev.Target}", $"Step 1 of 2.{(done is null ? "" : $" Merge request !{done.MrUrl!.Split('/')[^1]}.")}",
                        r.Name, done is null ? null : $"Open !{done.MrUrl!.Split('/')[^1]}", done?.MrUrl));
                }
                else
                {
                    var mr = NewMr(dev.Target);
                    items.Add(new("todo", $"Merge {name} into {dev.Target}", $"Step 1 of 2. {Commits(dev.Ahead)}.", r.Name, mr is null ? null : "Open merge request", mr));
                }
                var main = NewMr(target);
                items.Add(new(dev.Ahead == 0 ? "todo" : "wait", $"Merge {name} into {target}",
                    dev.Ahead == 0 ? $"Step 2 of 2. {Commits(b.Unmerged)}." : $"Step 2 of 2, after the {dev.Target} merge request is merged. {Commits(b.Unmerged)}.",
                    r.Name, main is null ? null : "Open merge request", main));
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
        var open = items.Count(i => i.State is "todo" or "wait");
        var repoCount = items.Select(i => i.Repo).Distinct().Count();
        return new("code", "Code", state, items.Count == 0 ? "No code found for this ticket yet"
            : open > 0 ? $"{open} merge request{(open == 1 ? "" : "s")} to go" : $"Merged in {repoCount} repositor{(repoCount == 1 ? "y" : "ies")}", items);
    }

    // QA deploys version tags, so merged code only reaches QA once a tag contains every one of the ticket's commits.
    static Station Tag(List<RepoResult> repos, DbConfig db, Func<string, string?> pathOf, ConcurrentDictionary<string, TagDetail>? tagsOut)
    {
        var items = new List<StationItem>();
        foreach (var r in repos.Where(r => !r.Name.Equals(db.Repo, StringComparison.OrdinalIgnoreCase) && Own(r).Any()))
        {
            var path = pathOf(r.Name);
            var own = Own(r).ToList();
            if (own.Any(c => !c.InDefault) || path is null)
            {
                // tags are cut from the default branch: being merged into dev isn't enough yet, so say which merge it waits for
                var main = Target(r);
                var dev = r.Branches.SelectMany(b => b.Vs).Where(v => v.Target is "dev" or "develop").ToList();
                items.Add(new("wait", $"Waiting for the merge into {main}",
                    dev.Count > 0 && dev.All(v => v.Ahead == 0) ? $"Merged into {dev[0].Target} already. Tags are cut from {main}, so step 2 of the Code step (into {main}) has to be merged first."
                        : $"Tags are cut from {main}, so the code has to be merged there first.", r.Name));
                continue;
            }

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
            : state == "wait" ? "Waiting for the code to reach the default branch" : "Every repository is in a version tag", items);
    }

    static Station Database(List<RepoResult> repos, DbConfig db, Func<string, string?> pathOf)
    {
        var items = new List<StationItem>();
        var dbRepo = repos.FirstOrDefault(r => r.Name.Equals(db.Repo, StringComparison.OrdinalIgnoreCase));
        if (dbRepo is null || dbRepo.Db.Count == 0) return new("db", "Database", "skip", "No database scripts", items);
        var path = pathOf(dbRepo.Name);

        // the scripts themselves, so each item can open them: from the working copy, or on GitLab when they aren't checked out here.
        // Scripts still waiting for a release are on the ticket's branch; graduated ones are on the default branch.
        // with several ticket branches, the one that has the file (pushed ones only: GitLab can't show the others)
        var pushed = dbRepo.Branches.Where(b => !b.Name.EndsWith(" (local only, never pushed)")).Select(b => b.Name.StartsWith("origin/") ? b.Name[7..] : b.Name).Distinct().ToList();
        string? BranchWith(string file) => path is null ? pushed.FirstOrDefault()
            : pushed.FirstOrDefault(b => Git.Cached(path, null, "ls-tree", "--name-only", $"origin/{b}", "--", file).Trim() != "");
        List<ItemFile> Files(IEnumerable<DbItem> ds) => ds.Where(d => d.Path is not null).Select(d => d.Path!).Distinct().Select(p =>
        {
            // no branch has a script removed from dev/: no link, rather than one that 404s
            var at = ds.First(d => d.Path == p).Warning ? BranchWith(p) : Target(dbRepo);
            var web = dbRepo.WebUrl == "" || at is null ? null : $"{dbRepo.WebUrl}/-/blob/{at}/{string.Join("/", p.Split('/').Select(Uri.EscapeDataString))}";
            return new ItemFile(p, path is not null && LocalApps.SafeFile(path, p) is { } f && File.Exists(f), web);
        }).ToList();

        foreach (var g in dbRepo.Db.Where(d => d.Warning).GroupBy(d => (d.Service, d.Status)))
        {
            var files = string.Join(", ", g.Select(d => d.File));
            var staged = g.Key.Status.Contains("STAGED", StringComparison.OrdinalIgnoreCase);
            items.Add(new("todo",
                staged ? $"Move {g.Count()} script{(g.Count() == 1 ? "" : "s")} from dev into a release ({g.Key.Service})"
                       : $"Find {g.Count()} script{(g.Count() == 1 ? "" : "s")} missing from every release ({g.Key.Service})",
                staged ? $"{files}. QA only gets scripts that are inside a release folder." : files, dbRepo.Name, Files: Files(g)));
        }

        foreach (var g in dbRepo.Db.Where(d => !d.Warning).GroupBy(d => (d.Service, Release: ReleaseOf(d))).Where(g => g.Key.Release is not null))
        {
            var (svc, release) = (g.Key.Service, g.Key.Release!);
            items.Add(new("check", $"Run {svc} {release} on QA",
                "Build the QA migration for this release and run it on the QA database, if it isn't there yet.", dbRepo.Name, Files: Files(g)));

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
            var sqlAccess = new SortedSet<string>(StringComparer.Ordinal);
            var routes = new SortedSet<string>(StringComparer.Ordinal);
            var services = new SortedSet<string>(StringComparer.Ordinal);
            var accessFiles = new SortedSet<string>(StringComparer.Ordinal);
            var patches = Git.Many(path, Own(r).Select(c => c.Sha), "-p", "--unified=0", "--no-color", "--format=%x1e%H");
            foreach (var c in Own(r))
            {
                string? file = null;
                foreach (var raw in patches.GetValueOrDefault(c.Sha, "").Split('\n'))
                {
                    var line = raw.TrimEnd('\r');
                    if (line.StartsWith("+++ ")) { file = line.StartsWith("+++ b/") ? line[6..] : null; continue; }
                    if (file is null) continue;
                    // a removed permission check matters as much as an added one
                    if (line.StartsWith('-') && !line.StartsWith("---"))
                    {
                        if (AccessFile.IsMatch(file) && !ConfigFile.IsMatch(file) && AccessContent.IsMatch(line[1..])) accessFiles.Add(file);
                        continue;
                    }
                    if (!line.StartsWith('+')) continue;
                    var added = line[1..];
                    if (AccessFile.IsMatch(file) && !ConfigFile.IsMatch(file) && (AccessContent.IsMatch(added) || NavFile.IsMatch(file) && NavEntry.IsMatch(added))) accessFiles.Add(file);
                    if (ConfigFile.IsMatch(file))
                    {
                        var m = file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? JsonKey.Match(added)
                              : file.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ? JsKey.Match(added) : EnvKey.Match(added);
                        if (m.Success) (keys.TryGetValue(file, out var set) ? set : keys[file] = new(StringComparer.Ordinal)).Add(m.Groups[1].Value);
                    }
                    else if (file.EndsWith(".cs") && AccessScan.StartupRx.Match(added) is { Success: true } sv) services.Add(sv.Groups[1].Value);
                    else if (file.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) && AccessSql.IsMatch(added)) sqlAccess.Add(file);
                    else if (Regex.IsMatch(file, @"(?i)rout[^/]*\.(jsx?|tsx?)$") && AccessScan.RouteRx.Match(added) is { Success: true } rt) routes.Add(rt.Groups[1].Value);
                }
            }
            foreach (var (file, set) in keys)
                config.Add(new("check", $"{set.Count} new setting{(set.Count == 1 ? "" : "s")} in {Path.GetFileName(file)}",
                    string.Join(", ", set.Take(12)) + (set.Count > 12 ? $" and {set.Count - 12} more" : ""), r.Name));
            foreach (var f in r.Files.Where(f => f.Value != "D" && PipelineFile.IsMatch(f.Key)))
                config.Add(new("check", $"Pipeline changed: {Path.GetFileName(f.Key)}", f.Key.Contains('/') ? f.Key : null, r.Name));
            foreach (var tag in services)
                access.Add(new("check", $"New service {tag} registers its scopes",
                    "Its infrastructure.service row must exist on QA (a seed script) before it starts, or every route returns 403.", r.Name));
            var (newScopes, goneScopes) = AccessScan.Changes(path, Own(r).ToList());
            if (newScopes.Count > 0)
                access.Add(new("check", $"Grant {newScopes.Count} new scope{(newScopes.Count == 1 ? "" : "s")} on QA",
                    "They register when the service starts on QA. Grant them to the QA roles: each one is listed, ready to copy, under System Manager setup below.", r.Name));
            if (goneScopes.Count > 0)
                access.Add(new("done", $"{goneScopes.Count} scope{(goneScopes.Count == 1 ? "" : "s")} removed",
                    "They disappear when the service starts on QA; nothing to grant. Listed under System Manager setup below.", r.Name));
            if (routes.Count > 0)
                access.Add(new("check", routes.Count == 1 ? $"Create the menu {routes.First()} on QA" : $"Create {routes.Count} menus on QA",
                    "Add it with its actions, link each action to its scopes, and give it to the QA roles. Everything is under System Manager setup below.", r.Name));
            foreach (var f in sqlAccess)
                access.Add(new("check", "Grants roles or menus in SQL", f, r.Name));
            if (!r.Name.Equals(db.Repo, StringComparison.OrdinalIgnoreCase))
                foreach (var f in accessFiles.Where(f => r.Files.GetValueOrDefault(f) is { } st && st != "D"))
                    access.Add(new("check", $"Changed: {Path.GetFileName(f)}", f, r.Name));
        }
        return (config, access);
    }

    static Station Access(List<RepoResult> repos, DbConfig db, List<StationItem> fromCode)
    {
        var items = new List<StationItem>(fromCode);   // access-named files with access work in them come from ScanPatches
        return new("access", "Access", items.Count == 0 ? "skip" : "check",
            items.Count == 0 ? "No new scopes, menus or permission changes"
            : items.Any(i => i.State == "check" && Regex.IsMatch(i.Title, "^(Grant|Create|New service)")) ? "Set up access in the System Manager on QA"
            : "Make sure QA users can reach the feature", items);
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
        // When Greenlight itself was started from inside VS Code (its terminal, a task, an extension), it inherits
        // ELECTRON_RUN_AS_NODE=1 and VS Code's internal variables. Passed on, they make Code.exe run as plain Node
        // and quit instead of opening a window. Editors get a clean environment.
        foreach (var name in psi.Environment.Keys.Where(k => k.StartsWith("ELECTRON_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("VSCODE_", StringComparison.OrdinalIgnoreCase)).ToList())
            psi.Environment.Remove(name);
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
                var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                // Explorer splits its arguments on commas: a path with one would turn into extra arguments
                if (file is not null && File.Exists(file) && !file.Contains(',')) Launch(explorer, $"/select,{file}");
                else Launch(explorer, file is not null ? Path.GetDirectoryName(file)! : repo);
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
        var dir = Path.Combine(Path.GetTempPath(), "greenlight", "diff", Path.GetFileName(repo), first[..7] + "-" + last[..7]);
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

    internal static long[] Numbers(string tag)
    {
        var m = Version.Match(tag);
        return m.Success ? m.Groups["nums"].Value.Split('.').Select(n => long.TryParse(n, out var v) ? v : 0).ToArray() : [];
    }

    internal sealed class VersionOrder : IComparer<long[]>
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

record GitLabUser(long Id, string Username, string Name, string? AvatarUrl = null);

// Fetches a profile picture. The account's own client (with its token) is used only for its own host;
// anything else (Gravatar, a CDN) is fetched anonymously, so a token never leaves for another site.
static class Avatars
{
    static readonly HttpClient anon = new() { Timeout = TimeSpan.FromSeconds(8) };
    public static async Task<(byte[] Bytes, string Type)?> Fetch(string? url, string ownHost, HttpClient? own)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https") return null;
        var client = own is not null && u.Host.Equals(ownHost, StringComparison.OrdinalIgnoreCase) ? own : anon;
        try
        {
            using var res = await client.GetAsync(u);
            var type = res.Content.Headers.ContentType?.MediaType ?? "";
            if (!res.IsSuccessStatusCode || !type.StartsWith("image/") || type.Contains("svg")) return null;   // no SVG: it can carry script
            var bytes = await res.Content.ReadAsByteArrayAsync();
            return bytes.Length is > 0 and < 1_000_000 ? (bytes, type) : null;
        }
        catch { return null; }
    }
}

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
        return Me = new GitLabUser((long)u["id"]!, (string)u["username"]!, (string?)u["name"] ?? (string)u["username"]!, (string?)u["avatar_url"]);
    }

    public Task<(byte[] Bytes, string Type)?> Avatar() => Avatars.Fetch(Me?.AvatarUrl, new Uri(BaseUrl).Host, http);


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
    public Task<JsonArray> Events(long userId, DateOnly after, DateOnly before) =>
        All($"users/{userId}/events?after={after:yyyy-MM-dd}&before={before:yyyy-MM-dd}");
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
            ["state"] = (string?)mr["state"], ["mergedAt"] = (string?)mr["merged_at"], ["mergeStatus"] = (string?)mr["detailed_merge_status"] ?? (string?)mr["merge_status"],
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

// ───────────────────────────── getting to QA: deployments, actions, my day (standup and worklog suggestions) ─────────────────────────────

static class Work
{
    static Regex KeyRx(Ledger l) => new($@"(?<![A-Za-z0-9])(?:{l.KeyPattern})(?!\d)", RegexOptions.IgnoreCase);
    static readonly Regex MergeOf = new(@"^Merge (?:remote-tracking )?branch '([^']+)'");
    static async Task<T?> Try<T>(Func<Task<T>> f) where T : class { try { return await f(); } catch { return null; } }


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

    // ── actions ──
    static string ProjectOf(string repo) => Git.WebUrl(repo) is { Length: > 0 } w ? new Uri(w).AbsolutePath.Trim('/') : throw new Exception("This repository has no GitLab remote.");


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

// ───────────────────────────── self test: the rules that decide readiness, checked without network ─────────────────────────────

static class SelfTest
{
    public static int Run(string appDir)
    {
        var fails = 0; var n = 0;
        void Is<T>(string what, T got, T want) { n++; if (!EqualityComparer<T>.Default.Equals(got, want)) { fails++; Console.WriteLine($"FAIL {what}: got {got}, want {want}"); } }

        // next version tag
        Is("bump patch", Tags.Bump("5.0.10"), "5.0.11");
        Is("bump keeps prefix", Tags.Bump("v5.0.84"), "v5.0.85");
        Is("bump four parts", Tags.Bump("4.6.18.1.8"), "4.6.18.1.9");
        Is("bump suffix", Tags.Bump("2.1.9-rc"), "2.1.10-rc");
        Is("bump none", Tags.Bump(null), (string?)null);

        // newest release by version number, whatever the prefix or creation date
        var order = new[] { "5.0.9", "v5.0.10", "4.6.18.1.8", "5.0.10", "v5.0.2" }.OrderByDescending(Tags.Numbers, Tags.VersionOrder.Instance).ToList();
        Is("newest first", order[0] is "v5.0.10" or "5.0.10", true);
        Is("hotfix of old line is not newest", order.IndexOf("4.6.18.1.8") == order.Count - 1, true);

        // file paths from the page must stay inside the repository
        var repo = Path.Combine(Path.GetTempPath(), "greenlight-selftest-repo");
        Is("path inside", LocalApps.SafeFile(repo, "src/a.cs") is not null, true);
        Is("path parent", LocalApps.SafeFile(repo, "../x"), (string?)null);
        Is("path rooted", LocalApps.SafeFile(repo, @"C:\Windows\win.ini"), (string?)null);
        Is("path wildcard", LocalApps.SafeFile(repo, "src/*.cs"), (string?)null);
        Is("path stream", LocalApps.SafeFile(repo, "a.cs:secret"), (string?)null);

        // ticket keys in branch names and messages: whole keys only
        var rx = new Regex(@"(?<![A-Za-z0-9])(?:MM-[0-9]+)(?!\d)", RegexOptions.IgnoreCase);
        Is("key in branch", rx.Match("feature/MM-8725-dashboard").Value, "MM-8725");
        var one = new Regex(@"(?<![A-Za-z0-9])(?:MM-8725)(?!\d)", RegexOptions.IgnoreCase);   // how a check looks for one ticket
        Is("one key is not a prefix of another", one.IsMatch("MM-87250 other ticket"), false);
        Is("one key found", one.IsMatch("fix(MM-8725): dashboard"), true);
        Is("key inside word", rx.IsMatch("XMM-8725"), false);
        Is("key lower case", rx.Match("mm-12 fix").Value, "mm-12");

        // configuration keys that the Config step looks for
        Is("json key", QaPrep.JsonKey.Match("  \"Scheduler:Enabled\": true,").Groups[1].Value, "Scheduler:Enabled");
        Is("config file", QaPrep.ConfigFile.IsMatch("API/appsettings.Development.json"), true);
        Is("not config", QaPrep.ConfigFile.IsMatch("src/settings.ts"), false);

        // scope names, built the way the access check builds them at runtime
        const string controller = """
            [Area("Admin")]
            [Route("api/v1/[area]/[controller]")]
            public class ScheduledTasksController : MainController
            {
                [HttpGet("get-all")]
                [AuthorizeScope]
                public async Task<IActionResult> GetAll([FromQuery] GetAllRequest request) => await Run();

                [HttpPost("{id}/run")]
                [AuthorizeScope]
                public async Task<IActionResult> RunNow(long id) { return Ok(); }

                [HttpGet("public")]
                public IActionResult Open() => Ok();

                [AuthorizeScope("member:menu", "member:menu:get-all:get")]
                [HttpGet("menus")]
                public IActionResult Menus() => Ok();
            }
            """;
        var sc = AccessScan.Scopes(controller);
        Is("scope get", sc.Contains("admin:scheduledtasks:getall:get"), true);
        Is("scope post", sc.Contains("admin:scheduledtasks:runnow:post"), true);
        Is("unprotected endpoint has no scope", sc.Any(x => x.Contains(":open:")), false);
        Is("explicit scope name kept", sc.Contains("member:menu:get-all:get"), true);
        Is("scope count", sc.Count, 3);
        Is("versioned controller", AccessScan.Scopes("public class OrdersControllerV2 : C { [HttpGet] [AuthorizeScope] public IActionResult List() => Ok(); }").Single(), "orders:v2:list:get");
        Is("react route", AccessScan.RouteRx.Match("    path: \"/scheduled-tasks\",").Groups[1].Value, "/scheduled-tasks");
        Is("new service", AccessScan.StartupRx.Match("app.GenerateScopesAsync(\"APPOINTMENT\");").Groups[1].Value, "APPOINTMENT");
        // a merge request description starts from the commit message, without the lines tools add to credit themselves
        Is("co-author trailer dropped", QaPrep.Attribution.Replace("Needs the migration.\n\nCo-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>\n\nCloses MM-9306", ""), "Needs the migration.\n\n\nCloses MM-9306");
        Is("generated-with line dropped", QaPrep.Attribution.Replace("Summary\n🤖 Generated with [Claude Code](https://claude.com/claude-code)\n", ""), "Summary\n");
        Is("other text kept", QaPrep.Attribution.Replace("Co-authored the export with the data team.", ""), "Co-authored the export with the data team.");
        // an access-named file counts for what was added to it, not for its name
        Is("menu component markup is no access work", QaPrep.AccessContent.IsMatch("      <MenuItem onClick={() => onPick(\"xlsx\")}>Excel</MenuItem>"), false);
        Is("menu component state is no access work", QaPrep.AccessContent.IsMatch("  const [anchorEl, setAnchorEl] = useState<null | HTMLElement>(null);"), false);
        Is("action check", QaPrep.AccessContent.IsMatch("  const canExport = !actions || actions.includes(\"Export\");"), true);
        Is("authorize attribute", QaPrep.AccessContent.IsMatch("    [Authorize(Policy = \"Admin\")]"), true);
        Is("permission list", QaPrep.AccessContent.IsMatch("  permissions: [\"VIEW\", \"EXPORT\"],"), true);
        Is("roles in a sidebar entry", QaPrep.AccessContent.IsMatch("    roles: [\"Reseller\"],"), true);
        Is("sidebar entry without roles", QaPrep.NavFile.IsMatch("src/layout/Sidebar.tsx") && QaPrep.NavEntry.IsMatch("  { path: \"/ivr-builder\", label: \"IVR builder\" },"), true);
        Is("router link in a nav file", QaPrep.NavEntry.IsMatch("        <Link to=\"/inbox/analytics\">Analytics</Link>"), true);
        Is("dropdown menu item is no page", QaPrep.NavEntry.IsMatch("      <MenuItem onClick={() => onPick(\"xlsx\")}>Excel</MenuItem>"), false);

        Console.WriteLine(fails == 0 ? $"All {n} checks passed." : $"{fails} of {n} checks failed.");
        return fails == 0 ? 0 : 1;
    }
}

record JiraUser(string AccountId, string DisplayName, string Email, string? AvatarUrl = null);

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
        Me = new JiraUser((string)j["accountId"]!, (string?)j["displayName"] ?? Email, (string?)j["emailAddress"] ?? Email, (string?)j["avatarUrls"]?["48x48"]);
        return Me;
    }

    public Task<(byte[] Bytes, string Type)?> Avatar() => Avatars.Fetch(Me?.AvatarUrl, new Uri(baseUrl).Host, http);

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

    // one page of results; pass the returned token to get the next page (null when there are no more)
    public async Task<(List<IssueInfo> Items, string? Next)> SearchPage(string jql, int size, string? token)
    {
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
            ["files"] = i.Files is null ? null : new JsonArray(i.Files.Select(f => (JsonNode)new JsonObject { ["path"] = f.Path, ["local"] = f.Local, ["web"] = f.Web }).ToArray()),
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
            ["setup"] = rep.Setup.DeepClone(),
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
                ["branches"] = new JsonArray(r.Branches.Select(b => (JsonNode)new JsonObject
                {
                    ["name"] = b.Name, ["unmerged"] = b.Unmerged, ["tipAt"] = b.TipAt,
                    ["vs"] = new JsonArray(b.Vs.Select(v => (JsonNode)new JsonObject { ["target"] = v.Target, ["ahead"] = v.Ahead, ["behind"] = v.Behind }).ToArray()),
                }).ToArray()),
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
            // the Host header is the client's word: also require the connection itself to come from this computer
            if (!IPAddress.IsLoopback(req.RemoteEndPoint.Address)) { await Send(res, 403, Err("Forbidden")); return; }

            // no framing (clickjacking), no type sniffing, scripts and data only from this server
            res.Headers["X-Frame-Options"] = "DENY";
            res.Headers["X-Content-Type-Options"] = "nosniff";
            res.Headers["Referrer-Policy"] = "no-referrer";
            res.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'; object-src 'none'";

            var path = req.Url.AbsolutePath.TrimEnd('/');

            // every API call, reads included, must come from our own page: other sites can't add this header
            // without CORS, which we never allow (stops drive-by fetches, cache swaps and worklog pulls)
            if (path.StartsWith("/api/") && !Trusted(req)) { await Send(res, 403, Err("Missing X-Ledger header")); return; }
            if (req.ContentLength64 > 1_000_000) { await Send(res, 413, Err("Request too large")); return; }
            var key = req.QueryString["key"]?.Trim().ToUpperInvariant();

            switch (req.HttpMethod, path)
            {
                case ("GET", ""):
                    await SendText(res, 200, File.ReadAllText(Path.Combine(ledger.AppDir, "ui.html")), "text/html; charset=utf-8");
                    return;

                case ("GET", "/me/avatar"):
                {
                    // an image tag can't send X-Ledger, so this lives outside /api/; it only ever returns your own picture
                    var av = req.QueryString["of"] == "gitlab" ? await GitLabAvatar(ledger) : await MyAvatar(ledger);
                    if (av is null) { await Send(res, 404, Err("No avatar")); return; }
                    res.StatusCode = 200; res.ContentType = av.Value.Type; res.ContentLength64 = av.Value.Bytes.Length;
                    res.Headers["Cache-Control"] = "private, max-age=3600";
                    await res.OutputStream.WriteAsync(av.Value.Bytes);
                    res.Close();
                    return;
                }

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
                    // served from this computer, so re-reading is free; a week-long cache kept showing an old app.js after every edit
                    res.Headers["Cache-Control"] = "no-cache";
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
                        ["gitlabBaseUrl"] = ledger.GitLabBaseUrl,
                        ["gitlab"] = await ledger.GitLabUser() is { } gu ? new JsonObject { ["name"] = gu.Name, ["username"] = gu.Username, ["avatar"] = await GitLabAvatar(ledger) is not null } : null,
                        ["gitlabRejected"] = ledger.GitLab.Configured && ledger.GitLab.Me is null,
                        ["dailyTargetHours"] = ledger.DailyTargetHours,
                        ["avatar"] = await MyAvatar(ledger) is not null,
                        ["reminderAt"] = ledger.ReminderAt,
                        ["diskFree"] = Work.DiskFree(ledger.AppDir),
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

                case ("GET", "/api/jira/transitions"):
                    if (key is null || !ledger.IsValidKey(key)) { await Send(res, 400, Err("Invalid key")); return; }
                    await Send(res, 200, await ledger.Jira.Transitions(key));
                    return;

                case ("POST", "/api/jira/transition"):
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
                    await Send(res, 200, await Work.CreateTag(ledger, repoName, (string?)body["tag"] ?? ""));
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
                    var shas = body["shas"]?.AsArray().Select(n => (string?)n).Distinct().Take(200).Where(s => LocalApps.IsCommit(repo, s)).Select(s => s!).Distinct().ToList() ?? [];
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

    // your profile picture: Jira first, then GitLab; remembered for an hour per account
    static (string Who, DateTime At, (byte[] Bytes, string Type)? Image) avatar = ("", DateTime.MinValue, null);
    static async Task<(byte[] Bytes, string Type)?> MyAvatar(Ledger ledger)
    {
        var jira = await ledger.CurrentUser();
        var gl = await ledger.GitLabUser();
        var who = $"{jira?.AccountId}|{gl?.Username}";
        if (avatar.Who == who && DateTime.UtcNow - avatar.At < TimeSpan.FromHours(1)) return avatar.Image;
        var img = (jira is not null ? await ledger.Jira.Avatar() : null) ?? (gl is not null ? await ledger.GitLab.Avatar() : null);
        avatar = (who, DateTime.UtcNow, img);
        return img;
    }

    static (string Who, DateTime At, (byte[] Bytes, string Type)? Image) glAvatar = ("", DateTime.MinValue, null);
    static async Task<(byte[] Bytes, string Type)?> GitLabAvatar(Ledger ledger)
    {
        if (await ledger.GitLabUser() is not { } gl) return null;
        if (glAvatar.Who == gl.Username && DateTime.UtcNow - glAvatar.At < TimeSpan.FromHours(1)) return glAvatar.Image;
        var img = await ledger.GitLab.Avatar();
        glAvatar = (gl.Username, DateTime.UtcNow, img);
        return img;
    }

    static JsonArray Recent(Ledger ledger, int take = 30)
    {
        if (!Directory.Exists(ledger.OutDir)) return [];
        // re-read only when a result file was added or changed
        var files = Directory.GetFiles(ledger.OutDir, "*.json");
        var sig = $"{files.Length}|{(files.Length == 0 ? 0 : files.Max(f => File.GetLastWriteTimeUtc(f).Ticks))}";
        if (recent.Sig != sig)
            recent = (sig, files.Select(f => { try { return JsonNode.Parse(File.ReadAllText(f)); } catch { return null; } })
                .Where(n => n?["key"] is not null).OrderByDescending(n => (string?)n!["generated"]).Select(n => n!).ToList());
        return new JsonArray(recent.Items.Take(take).Select(n => n.DeepClone()).ToArray());
    }
    static (string Sig, List<JsonNode> Items) recent = ("", []);

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
