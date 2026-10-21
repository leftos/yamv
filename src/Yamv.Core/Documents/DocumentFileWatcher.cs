using Yamv.Core.Diagnostics;
using Yamv.Core.Theming;

namespace Yamv.Core.Documents;

/// <summary>
/// Watches the folders of the open documents and raises <see cref="Changed"/> once the events that name an open document have
/// been quiet for 150 ms, or at most 1 s after the first of them, so the subscriber can re-read those documents.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Changed"/> is raised on a thread-pool thread with the full paths of the open documents that may have changed. A
/// subscriber posts its work to its UI thread and never waits on it there, since <see cref="Dispose"/> waits for a
/// <see cref="Changed"/> already running. A subscriber that throws is logged and the watcher keeps working.
/// </para>
/// <para>
/// Each folder of an open document is watched once, however many of its documents are open. Everything done on a folder (probing
/// it, starting and disposing its watch, reading its documents' stamps) happens in that folder's pass, on a thread-pool thread, and
/// at most one pass runs per folder at a time: a folder on a hung share holds one thread and delays no other folder.
/// <see cref="SetPaths"/>, the 2 s tick and the watches' own events only ask for a pass, so none of them waits on a folder; a
/// request made while the folder's pass runs makes that pass run once more.
/// </para>
/// <para>
/// Every 2 s each folder is probed: a folder that does not exist is watched once it does, and a folder deleted, recreated or
/// replaced is watched again, as is one whose watch reports an error. A folder appearing or being replaced reports every open
/// document in it, as does a watch that reports an error once it counts as working, and one that starts working again. A watch
/// counts as working once a tick finds it at least 2 s past a start that raised no error.
/// </para>
/// <para>
/// A folder whose watch keeps failing is polled instead: every 2 s the length and last write time of each of its open documents
/// are compared with those of the previous poll, and only the documents whose stamp changed are reported. A watch fails when it
/// reports an error before it counts as working, or when it cannot be started at all. Nothing here opens a document.
/// </para>
/// <para>
/// A document opened in a polled folder, and every open document of a folder that starts being polled, gets its first stamp on a
/// pass asked for at that moment; a change after that read is reported. A change before it is not: the window is one thread-pool
/// dispatch, plus the rest of that folder's pass when one is already running, and, outside the watcher, the time between the
/// subscriber's own read of the document and its call to <see cref="SetPaths"/>.
/// </para>
/// </remarks>
public sealed class DocumentFileWatcher : IDisposable
{
    private static readonly TimeSpan TrailingDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinimumDelay = TimeSpan.FromMilliseconds(1);

    private readonly Lock gate = new();
    private readonly Lock runGate = new();
    private readonly TimeProvider time;
    private readonly IAppLog log;
    private readonly Seams io;
    private readonly ITimer tickTimer;

    // Guarded by gate: the folders and everything about them, the passes in flight, the sources of removed folders waiting for
    // their folder's pass to dispose them, and the state of the pending report.
    private readonly Dictionary<string, FolderWatch> folders;
    private readonly HashSet<string> passing;
    private readonly Dictionary<string, List<IFolderEvents>> retired;
    private HashSet<string> pending;
    private ITimer? reportTimer;
    private DateTimeOffset batchStart;
    private bool batchPending;
    private bool disposed;

    // 1 while a tick runs; a tick that finds it set returns at once.
    private int ticking;

    /// <summary>Starts a watcher that follows no document until <see cref="SetPaths"/> says which.</summary>
    /// <param name="timeProvider">The clock for the debounce and the folder checks; the app passes <see cref="TimeProvider.System"/>.</param>
    /// <param name="log">Where a folder that cannot be watched is reported.</param>
    public DocumentFileWatcher(TimeProvider timeProvider, IAppLog log)
        : this(timeProvider, log, new Seams(path => new FileSystemFolderEvents(path), new FileSystemFolderProbe(), FileStamps.Read, PostToPool)) { }

    internal DocumentFileWatcher(TimeProvider timeProvider, IAppLog log, Seams io)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(io);
        this.time = timeProvider;
        this.log = log;
        this.io = io;
        folders = new Dictionary<string, FolderWatch>(AppPaths.PathComparer);
        passing = new HashSet<string>(AppPaths.PathComparer);
        retired = new Dictionary<string, List<IFolderEvents>>(AppPaths.PathComparer);
        pending = new HashSet<string>(AppPaths.PathComparer);

        // Last: every field a tick reads is set before the first tick can run.
        tickTimer = timeProvider.CreateTimer(OnTick, null, TickInterval, TickInterval);
    }

    /// <summary>Raised on a thread-pool thread with the open documents that may have changed, once per batch.</summary>
    public event EventHandler<DocumentsChangedEventArgs>? Changed;

    /// <summary>What a folder's next pass is asked to do; requests made before the pass takes them merge.</summary>
    [Flags]
    private enum PassRequests
    {
        None = 0,

        /// <summary>Probe the folder, poll it when it is polled, re-arm its watch when needed and judge the watch's health.</summary>
        Check = 1,

        /// <summary>Poll the folder when it is polled, which records the first stamp of every document not polled yet.</summary>
        Baseline = 2,
    }

    /// <summary>
    /// Replaces the open documents this watcher follows, watching each folder they are in once. It does no work on a folder itself:
    /// it only asks for the passes that probe and watch a new folder, stamp a document new to a polled folder, and dispose the
    /// watch of a folder no longer needed, so a slow share never blocks the caller.
    /// </summary>
    /// <param name="documentPaths">
    /// The full paths of the open documents, compared as the platform compares file paths. Each is made canonical first.
    /// </param>
    /// <exception cref="ArgumentException">A path is not a full path, or has no folder of its own.</exception>
    public void SetPaths(IEnumerable<string> documentPaths)
    {
        ArgumentNullException.ThrowIfNull(documentPaths);
        Dictionary<string, HashSet<string>> wanted = ByFolder(documentPaths);
        List<string> due = [];
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            DropUnwanted(wanted, due);
            Merge(wanted, due);
            KeepOnlyPending(AllPaths(wanted));
        }

        PostPasses(due);
    }

    /// <summary>
    /// Stops watching every folder and waits for a <see cref="Changed"/> already running; nothing is raised after it returns. It
    /// does not wait for a folder's pass: a pass still running disposes any watch it starts and reports nothing.
    /// </summary>
    public void Dispose()
    {
        List<IFolderEvents> stale = [];
        ITimer? report;
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            batchPending = false;
            foreach (FolderWatch folder in folders.Values)
            {
                if (folder.Source is { } source)
                {
                    SetSource(folder, null, null);
                    stale.Add(source);
                }
            }

            foreach (List<IFolderEvents> sources in retired.Values)
            {
                stale.AddRange(sources);
            }

            folders.Clear();
            retired.Clear();
            report = reportTimer;
        }

        tickTimer.Dispose();
        report?.Dispose();
        foreach (IFolderEvents source in stale)
        {
            source.Dispose();
        }

        lock (runGate)
        {
            // Wait for a Changed already running; nothing is raised after this.
        }
    }

    private static void PostToPool(Action pass) => ThreadPool.UnsafeQueueUserWorkItem(static run => run(), pass, preferLocal: false);

    /// <summary>Groups the given documents by their folder, each group holding the canonical paths.</summary>
    private static Dictionary<string, HashSet<string>> ByFolder(IEnumerable<string> documentPaths)
    {
        var wanted = new Dictionary<string, HashSet<string>>(AppPaths.PathComparer);
        foreach (string path in documentPaths)
        {
            if (!Path.IsPathFullyQualified(path))
            {
                throw new ArgumentException($"The document path {path} is not a full path.", nameof(documentPaths));
            }

            string full = Path.GetFullPath(path);
            string folder =
                Path.GetDirectoryName(full) ?? throw new ArgumentException($"The document path {path} has no folder.", nameof(documentPaths));
            if (!wanted.TryGetValue(folder, out HashSet<string>? open))
            {
                open = new HashSet<string>(AppPaths.PathComparer);
                wanted.Add(folder, open);
            }

            open.Add(full);
        }

        return wanted;
    }

    /// <summary>Every document path in the groups, in one set.</summary>
    private static HashSet<string> AllPaths(Dictionary<string, HashSet<string>> wanted)
    {
        var all = new HashSet<string>(AppPaths.PathComparer);
        foreach (HashSet<string> open in wanted.Values)
        {
            all.UnionWith(open);
        }

        return all;
    }

    private static bool IsArmFailure(Exception exception) => exception is ArgumentException or IOException or UnauthorizedAccessException;

    private static List<string> Matched(FolderWatch folder, FolderEvent folderEvent)
    {
        List<string> changed = [];
        AddIfOpen(folder, folderEvent.Name, changed);
        AddIfOpen(folder, folderEvent.OldName, changed);
        return changed;
    }

    private static void AddIfOpen(FolderWatch folder, string? name, List<string> changed)
    {
        if (name is null)
        {
            return;
        }

        if (folder.Open.TryGetValue(Path.Combine(folder.Folder, name), out string? open))
        {
            changed.Add(open);
        }
    }

    private static bool NeedsArm(FolderWatch folder, string? identity) =>
        folder.Source is null || folder.Identity != identity || folder.ReArmRequested;

    private static bool Replacing(FolderWatch folder, string? identity) => folder.Source is not null && folder.Identity != identity;

    /// <summary>Marks the folder as polled, returning whether it was not polled before.</summary>
    private static bool JustPolling(FolderWatch folder)
    {
        if (folder.Unwatchable)
        {
            return false;
        }

        folder.Unwatchable = true;
        return true;
    }

    /// <summary>Gives the folder a new source, or none; a new source is not healthy and has not started yet.</summary>
    /// <remarks>Callers hold <see cref="gate"/>.</remarks>
    private static void SetSource(FolderWatch folder, IFolderEvents? source, string? identity)
    {
        folder.Source = source;
        folder.Identity = identity;
        folder.Healthy = false;
        folder.StartedAt = null;
    }

    private bool IsCurrent(FolderWatch folder) => folders.TryGetValue(folder.Folder, out FolderWatch? current) && ReferenceEquals(current, folder);

    /// <summary>
    /// Asks for a pass on <paramref name="path"/>, adding the path to <paramref name="due"/> when no pass of it is in flight, so
    /// the caller posts it once it has released <see cref="gate"/>; a pass in flight takes the request on its next loop.
    /// </summary>
    /// <remarks>Callers hold <see cref="gate"/>.</remarks>
    private void Request(string path, FolderWatch? folder, PassRequests what, List<string> due)
    {
        folder?.Requests |= what;

        if (passing.Add(path))
        {
            due.Add(path);
        }
    }

    /// <summary>Posts a pass for each path; callers never hold <see cref="gate"/>.</summary>
    private void PostPasses(List<string> due)
    {
        foreach (string path in due)
        {
            io.Post(() => RunPath(path));
        }
    }

    /// <summary>Records that a source started, returning whether the start ends a blind period that reported documents.</summary>
    /// <remarks>Callers hold <see cref="gate"/>.</remarks>
    private bool NoteStarted(FolderWatch folder)
    {
        bool errored = folder.ReArmRequested;
        bool wasFailing = folder.WatcherFailing;
        folder.WatcherFailing = wasFailing && errored;
        if (!errored)
        {
            folder.StartedAt = time.GetUtcNow();
        }

        return !errored && !folder.Unwatchable && wasFailing;
    }

    /// <summary>Whether the folder's source started without an error at least a full tick ago.</summary>
    /// <remarks>Callers hold <see cref="gate"/>.</remarks>
    private bool HasWorkedATick(FolderWatch folder) => folder.StartedAt is { } started && time.GetUtcNow() - started >= TickInterval;

    /// <summary>Takes the folders no longer needed out of the watch set, leaving each source for its folder's pass to dispose.</summary>
    private void DropUnwanted(Dictionary<string, HashSet<string>> wanted, List<string> due)
    {
        List<string> removed = [.. folders.Keys.Where(folder => !wanted.ContainsKey(folder))];
        foreach (string folder in removed)
        {
            FolderWatch watch = folders[folder];
            folders.Remove(folder);
            if (watch.Source is not { } source)
            {
                continue;
            }

            SetSource(watch, null, null);
            if (!retired.TryGetValue(folder, out List<IFolderEvents>? sources))
            {
                sources = [];
                retired.Add(folder, sources);
            }

            sources.Add(source);
            Request(folder, null, PassRequests.None, due);
        }
    }

    /// <summary>
    /// Gives each kept folder its new document set, asking a polled folder that gained a document for its first stamp, and adds a
    /// watch for each folder seen for the first time, asking for its check.
    /// </summary>
    private void Merge(Dictionary<string, HashSet<string>> wanted, List<string> due)
    {
        foreach (KeyValuePair<string, HashSet<string>> pair in wanted)
        {
            if (folders.TryGetValue(pair.Key, out FolderWatch? watch))
            {
                bool gained = watch.Unwatchable && !watch.Open.IsSupersetOf(pair.Value);
                watch.Open = pair.Value;
                if (gained)
                {
                    Request(pair.Key, watch, PassRequests.Baseline, due);
                }

                continue;
            }

            var created = new FolderWatch(pair.Key) { Open = pair.Value };
            folders.Add(pair.Key, created);
            Request(pair.Key, created, PassRequests.Check, due);
        }
    }

    /// <summary>Drops from the pending report every document that is no longer open, and the report itself when none is left.</summary>
    private void KeepOnlyPending(HashSet<string> open)
    {
        if (!batchPending)
        {
            return;
        }

        pending.IntersectWith(open);
        if (pending.Count > 0)
        {
            return;
        }

        batchPending = false;
        reportTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Asks every folder for a check; it does no work on a folder itself.</summary>
    private void OnTick(object? state)
    {
        if (Interlocked.Exchange(ref ticking, 1) == 1)
        {
            return;
        }

        try
        {
            List<string> due = [];
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                foreach (FolderWatch folder in folders.Values)
                {
                    Request(folder.Folder, folder, PassRequests.Check, due);
                }
            }

            PostPasses(due);
        }
        finally
        {
            Volatile.Write(ref ticking, 0);
        }
    }

    /// <summary>
    /// The pass of one folder path, the only code that touches that folder on disk. It loops while requests keep arriving and
    /// leaves the path free for the next request once none is left, or when it fails.
    /// </summary>
    private void RunPath(string path)
    {
        FolderWatch? folder = null;
        bool released = false;
        try
        {
            while (TakeWork(path) is { } work)
            {
                folder = work.Folder;
                RunPass(work);
            }

            released = true;
        }
        catch (Exception exception)
        {
            OnPassFailed(path, folder, exception);
        }
        finally
        {
            if (!released)
            {
                ReleaseAfterFailure(path);
            }
        }
    }

    /// <summary>Takes what the path's pass must do next, or frees the path and returns null when there is nothing left.</summary>
    private PassWork? TakeWork(string path)
    {
        lock (gate)
        {
            retired.Remove(path, out List<IFolderEvents>? stale);
            PassRequests requests = PassRequests.None;
            if (folders.TryGetValue(path, out FolderWatch? folder))
            {
                requests = folder.Requests;
                folder.Requests = PassRequests.None;
            }

            if (disposed || (stale is null && requests == PassRequests.None))
            {
                passing.Remove(path);
                return null;
            }

            return new PassWork(folder, requests, stale);
        }
    }

    private void RunPass(PassWork work)
    {
        foreach (IFolderEvents source in work.Stale ?? [])
        {
            source.Dispose();
        }

        if (work.Folder is not { } folder)
        {
            return;
        }

        if ((work.Requests & PassRequests.Check) != 0)
        {
            ProbeFolder(folder);
            NoteTickWorked(folder);
        }
        else if ((work.Requests & PassRequests.Baseline) != 0)
        {
            Poll(folder);
        }
    }

    /// <summary>Frees the path of a pass that failed, posting it again when a request arrived while it ran.</summary>
    private void ReleaseAfterFailure(string path)
    {
        bool again;
        lock (gate)
        {
            passing.Remove(path);
            bool asked = retired.ContainsKey(path) || (folders.TryGetValue(path, out FolderWatch? folder) && folder.Requests != PassRequests.None);
            again = !disposed && asked && passing.Add(path);
        }

        if (again)
        {
            io.Post(() => RunPath(path));
        }
    }

    private void NoteTickWorked(FolderWatch folder)
    {
        bool recovered;
        lock (gate)
        {
            recovered = folder.TickFailing;
            folder.TickFailing = false;
        }

        if (recovered)
        {
            log.LogInfo($"Checking the folder {folder.Folder} works again.");
        }
    }

    /// <summary>Logs a failed pass once rather than on every tick.</summary>
    private void OnPassFailed(string path, FolderWatch? folder, Exception exception)
    {
        bool firstFailure = true;
        if (folder is not null)
        {
            lock (gate)
            {
                firstFailure = !folder.TickFailing;
                folder.TickFailing = true;
            }
        }

        if (firstFailure)
        {
            log.LogError($"Cannot check the folder {path}; retrying every 2 s.", exception);
        }
    }

    private void ProbeFolder(FolderWatch folder)
    {
        FolderState state = io.Probe.Probe(folder.Folder);
        if (!state.Exists)
        {
            OnFolderMissing(folder);
            return;
        }

        IFolderEvents? before;
        lock (gate)
        {
            before = folder.Source;
        }

        Poll(folder);
        ReArm(folder, state.Identity);
        FinishTick(folder, before);
    }

    /// <summary>
    /// Marks a source healthy once it has worked for a full tick since it started, and ends polling when its folder was polled.
    /// </summary>
    private void FinishTick(FolderWatch folder, IFolderEvents? before)
    {
        lock (gate)
        {
            if (disposed || !IsCurrent(folder))
            {
                return;
            }

            if (folder.Source is null || !ReferenceEquals(folder.Source, before) || !HasWorkedATick(folder))
            {
                return;
            }

            folder.Healthy = true;
            if (!folder.Unwatchable)
            {
                return;
            }

            folder.Unwatchable = false;
            folder.Polled = new Dictionary<string, FileStamp?>(AppPaths.PathComparer);
            ScheduleReport(folder.Open);
        }

        log.LogInfo($"Watching the folder {folder.Folder} again.");
    }

    private void OnFolderMissing(FolderWatch folder)
    {
        IFolderEvents? stale;
        bool logMissing;
        lock (gate)
        {
            if (disposed || !IsCurrent(folder))
            {
                return;
            }

            folder.MissingBefore = true;
            folder.ReArmRequested = false;
            folder.Unwatchable = false;
            folder.Polled = new Dictionary<string, FileStamp?>(AppPaths.PathComparer);
            stale = folder.Source;
            SetSource(folder, null, null);
            logMissing = !folder.MissingLogged;
            folder.MissingLogged = true;
        }

        stale?.Dispose();
        if (logMissing)
        {
            log.LogInfo($"The folder {folder.Folder} does not exist; it is watched once it does.");
        }
    }

    /// <summary>
    /// Leaves a present folder watched as it is, or replaces its source with a new one, reporting every open document in it when
    /// the folder has appeared or been replaced.
    /// </summary>
    private void ReArm(FolderWatch folder, string? identity)
    {
        IFolderEvents? stale = null;
        bool arm;
        bool changed;
        lock (gate)
        {
            if (disposed || !IsCurrent(folder))
            {
                return;
            }

            folder.MissingLogged = false;
            changed = folder.MissingBefore;
            folder.MissingBefore = false;
            arm = NeedsArm(folder, identity);
            if (arm)
            {
                changed |= Replacing(folder, identity);
                stale = folder.Source;
                SetSource(folder, null, null);
                folder.ReArmRequested = false;
            }

            if (changed)
            {
                ScheduleReport(folder.Open);
            }
        }

        stale?.Dispose();
        if (arm)
        {
            Arm(folder, identity, changed);
        }
    }

    private void Arm(FolderWatch folder, string? identity, bool changed)
    {
        IFolderEvents next;
        try
        {
            next = io.Arm(folder.Folder);
        }
        catch (Exception exception) when (IsArmFailure(exception))
        {
            OnArmFailed(folder, exception, changed);
            return;
        }

        next.Raised += folderEvent => OnRaised(folder, next, folderEvent);
        bool kept;
        lock (gate)
        {
            // Only this folder's pass arms it, and only while it has no source; the check stays as a guard.
            kept = !disposed && IsCurrent(folder) && folder.Source is null;
            if (kept)
            {
                SetSource(folder, next, identity);
            }
        }

        if (!kept)
        {
            next.Dispose();
            return;
        }

        Start(folder, next, changed);
    }

    private void Start(FolderWatch folder, IFolderEvents next, bool changed)
    {
        try
        {
            next.Start();
        }
        catch (ObjectDisposedException) when (IsGone(folder, next))
        {
            return;
        }
        catch (Exception exception) when (IsArmFailure(exception))
        {
            Clear(folder, next);
            next.Dispose();
            OnArmFailed(folder, exception, changed);
            return;
        }

        OnStarted(folder, next);
    }

    /// <summary>Records that a source started, reporting every open document when that ends a blind period.</summary>
    private void OnStarted(FolderWatch folder, IFolderEvents next)
    {
        bool recovered;
        lock (gate)
        {
            if (disposed || !ReferenceEquals(folder.Source, next))
            {
                return;
            }

            recovered = NoteStarted(folder);
            if (recovered)
            {
                ScheduleReport(folder.Open);
            }
        }

        if (recovered)
        {
            log.LogInfo($"Watching the folder {folder.Folder} again.");
        }
    }

    /// <summary>Whether this source was disposed or replaced while it was starting.</summary>
    private bool IsGone(FolderWatch folder, IFolderEvents next)
    {
        lock (gate)
        {
            return disposed || !ReferenceEquals(folder.Source, next);
        }
    }

    private void Clear(FolderWatch folder, IFolderEvents next)
    {
        lock (gate)
        {
            if (!ReferenceEquals(folder.Source, next))
            {
                return;
            }

            SetSource(folder, null, null);
        }
    }

    /// <summary>Polls a folder whose watch cannot be started; its pass then runs once more to record the first stamps.</summary>
    private void OnArmFailed(FolderWatch folder, Exception exception, bool changed)
    {
        bool switched;
        List<string> due = [];
        lock (gate)
        {
            if (disposed || !IsCurrent(folder))
            {
                return;
            }

            switched = JustPolling(folder);
            if (changed)
            {
                ScheduleReport(folder.Open);
            }

            if (switched)
            {
                Request(folder.Folder, folder, PassRequests.Baseline, due);
            }
        }

        PostPasses(due);
        if (switched)
        {
            LogPolling(folder, exception);
        }
    }

    private void LogPolling(FolderWatch folder, Exception? exception) =>
        log.LogWarning($"The folder {folder.Folder} cannot report changes; checking its documents every 2 s.", exception);

    /// <summary>Reports the open documents whose stamp changed since the previous poll; a document's first poll only records it.</summary>
    private void Poll(FolderWatch folder)
    {
        string[] open;
        Dictionary<string, FileStamp?> before;
        lock (gate)
        {
            if (!IsPolling(folder))
            {
                return;
            }

            open = [.. folder.Open];
            before = folder.Polled;
        }

        Dictionary<string, FileStamp?> after = ReadStamps(open);
        List<string> changed = [];
        foreach (string path in open)
        {
            if (before.TryGetValue(path, out FileStamp? previous) && !Equals(previous, after[path]))
            {
                changed.Add(path);
            }
        }

        lock (gate)
        {
            if (!IsPolling(folder))
            {
                return;
            }

            changed.RemoveAll(path => !folder.Open.Contains(path));
            folder.Polled = after;
            if (changed.Count > 0)
            {
                ScheduleReport(changed);
            }
        }
    }

    private Dictionary<string, FileStamp?> ReadStamps(string[] open)
    {
        var stamps = new Dictionary<string, FileStamp?>(AppPaths.PathComparer);
        foreach (string path in open)
        {
            stamps[path] = io.Stamp(path);
        }

        return stamps;
    }

    /// <summary>Whether this folder is watched by polling rather than by a source that reports changes.</summary>
    private bool IsPolling(FolderWatch folder) => !disposed && IsCurrent(folder) && folder.Unwatchable;

    private void OnRaised(FolderWatch folder, IFolderEvents source, FolderEvent folderEvent)
    {
        try
        {
            if (folderEvent.Kind == FolderEventKind.Error)
            {
                OnSourceError(folder, source, folderEvent.Error);
                return;
            }

            OnEntryEvent(folder, source, folderEvent);
        }
        catch (Exception exception)
        {
            log.LogError($"A handler of a change in the folder {folder.Folder} failed.", exception);
        }
    }

    private void OnEntryEvent(FolderWatch folder, IFolderEvents source, FolderEvent folderEvent)
    {
        lock (gate)
        {
            if (disposed || !ReferenceEquals(folder.Source, source))
            {
                return;
            }

            List<string> changed = Matched(folder, folderEvent);
            if (changed.Count > 0)
            {
                ScheduleReport(changed);
            }
        }
    }

    /// <summary>
    /// An error means the source may have missed events. A watch that has worked for a full tick reports every open document in
    /// its folder and is armed again on the next tick; one that has not, or is already polled, is polled instead, and reports and
    /// logs nothing more until a watch works again. It reads nothing on the event's thread: the first stamps are its pass's job.
    /// </summary>
    private void OnSourceError(FolderWatch folder, IFolderEvents source, Exception? error)
    {
        bool switched = false;
        bool logged = false;
        List<string> due = [];
        lock (gate)
        {
            if (disposed || !ReferenceEquals(folder.Source, source))
            {
                return;
            }

            folder.ReArmRequested = true;
            if (folder.Healthy && !folder.Unwatchable)
            {
                logged = !folder.WatcherFailing;
                folder.WatcherFailing = true;
                ScheduleReport(folder.Open);
            }
            else if (JustPolling(folder))
            {
                switched = true;
                Request(folder.Folder, folder, PassRequests.Baseline, due);
            }
        }

        PostPasses(due);
        if (switched)
        {
            LogPolling(folder, error);
        }
        else if (logged)
        {
            log.LogWarning($"The document watcher of {folder.Folder} failed; re-arming.", error);
        }
    }

    /// <summary>Adds the paths to the pending batch and moves its deadline to 150 ms off now, at most 1 s after the batch began.</summary>
    /// <remarks>Callers hold <see cref="gate"/> and have checked that the watcher is not disposed.</remarks>
    private void ScheduleReport(IEnumerable<string> paths)
    {
        if (disposed)
        {
            return;
        }

        pending.UnionWith(paths);
        if (!batchPending)
        {
            batchPending = true;
            batchStart = time.GetUtcNow();
        }

        reportTimer ??= time.CreateTimer(OnReportDue, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        reportTimer.Change(WaitLeft(), Timeout.InfiniteTimeSpan);
    }

    private TimeSpan WaitLeft()
    {
        DateTimeOffset now = time.GetUtcNow();
        DateTimeOffset trailing = now + TrailingDelay;
        DateTimeOffset capped = batchStart + MaxWait;
        TimeSpan wait = (trailing < capped ? trailing : capped) - now;
        return wait > TimeSpan.Zero ? wait : MinimumDelay;
    }

    private void OnReportDue(object? state)
    {
        lock (runGate)
        {
            HashSet<string> report;
            lock (gate)
            {
                if (disposed || !batchPending)
                {
                    return;
                }

                batchPending = false;
                report = pending;
                pending = new HashSet<string>(AppPaths.PathComparer);
            }

            RaiseChanged(report);
        }
    }

    private void RaiseChanged(HashSet<string> paths)
    {
        EventHandler<DocumentsChangedEventArgs>? handler = Changed;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler(this, new DocumentsChangedEventArgs(paths));
        }
        catch (Exception exception)
        {
            log.LogError("A subscriber to the document watcher failed.", exception);
        }
    }

    /// <summary>The watcher's ways into the file system and the thread pool, replaced in tests.</summary>
    /// <param name="Arm">Builds the event source of a folder.</param>
    /// <param name="Probe">Reports whether a folder exists and which folder it is.</param>
    /// <param name="Stamp">Reads a document's length and last write time, or null when it is missing.</param>
    /// <param name="Post">Runs a folder's pass on another thread.</param>
    internal sealed record Seams(Func<string, IFolderEvents> Arm, IFolderProbe Probe, Func<string, FileStamp?> Stamp, Action<Action> Post);

    /// <summary>What one loop of a folder's pass does: the removed sources to dispose and the requests on the current watch.</summary>
    private readonly record struct PassWork(FolderWatch? Folder, PassRequests Requests, List<IFolderEvents>? Stale);

    /// <summary>One watched folder and the open documents it holds; every settable member is guarded by the watcher's gate.</summary>
    private sealed class FolderWatch(string folder)
    {
        public string Folder { get; } = folder;

        /// <summary>The open documents in this folder, canonicalised by <see cref="SetPaths"/>.</summary>
        public HashSet<string> Open { get; set; } = new(AppPaths.PathComparer);

        /// <summary>Set only through <see cref="SetSource"/>, which also resets its health.</summary>
        public IFolderEvents? Source { get; set; }

        public string? Identity { get; set; }

        /// <summary>What the folder's next pass is asked to do.</summary>
        public PassRequests Requests { get; set; }

        /// <summary>When the current source's <c>Start</c> returned without raising an error; null until then.</summary>
        public DateTimeOffset? StartedAt { get; set; }

        public bool ReArmRequested { get; set; }

        public bool MissingBefore { get; set; }

        public bool MissingLogged { get; set; }

        public bool WatcherFailing { get; set; }

        public bool TickFailing { get; set; }

        /// <summary>Whether the current source has worked for a full tick since it started without reporting an error.</summary>
        public bool Healthy { get; set; }

        /// <summary>Whether the watch keeps failing, so the documents are polled instead.</summary>
        public bool Unwatchable { get; set; }

        /// <summary>The stamps read at the previous poll; read and written only by this folder's pass.</summary>
        public Dictionary<string, FileStamp?> Polled { get; set; } = new(AppPaths.PathComparer);
    }
}
