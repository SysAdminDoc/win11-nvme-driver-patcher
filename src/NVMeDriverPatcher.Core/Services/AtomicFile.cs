using System.IO;
using System.Text;

namespace NVMeDriverPatcher.Services;

/// <summary>
/// Publishes a file in one step: the content goes to a staging file only this call knows about,
/// gets flushed to disk, then moves over the target. Four processes share the working directory
/// (the elevated GUI, the CLI, the tray and the SYSTEM scheduled task), so a staging name made
/// from the target alone (<c>path + ".tmp"</c>) collides: the second writer's FileMode.Create
/// truncates the first one's half-written file, and whichever Move runs second finds its staging
/// file already gone and throws, usually into an empty catch.
/// </summary>
internal static class AtomicFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>A staging name no other process or call can pick.</summary>
    internal static string StagingPath(string path) => $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";

    private static readonly System.Text.RegularExpressions.Regex RxStagingName = new(
        @"\.\d+\.[0-9a-f]{32}\.tmp$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>True for a name <see cref="StagingPath"/> produced. The SafeBoot journal stages
    /// under the same naming.</summary>
    internal static bool IsStagingName(string fileName) => RxStagingName.IsMatch(fileName);

    /// <summary>
    /// Staging files under <paramref name="directory"/> and its subdirectories that an earlier run
    /// left behind: a process killed between the write and the rename. Only this naming counts,
    /// and only files whose last write is older than <paramref name="olderThan"/>, so a write in
    /// flight in another process is never listed. A directory that can't be read is skipped, and
    /// junctions aren't followed.
    /// </summary>
    internal static IReadOnlyList<string> StaleStagingFiles(string directory, TimeSpan olderThan)
    {
        var stale = new List<string>();
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return stale;
        var cutoff = DateTime.UtcNow - olderThan;
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            try
            {
                foreach (var path in Directory.EnumerateFiles(current, "*.tmp", SearchOption.TopDirectoryOnly))
                {
                    if (!IsStagingName(Path.GetFileName(path))) continue;
                    if (File.GetLastWriteTimeUtc(path) > cutoff) continue;
                    stale.Add(path);
                }
                foreach (var sub in Directory.EnumerateDirectories(current))
                {
                    if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                    pending.Push(sub);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable directory: skipped, as the summary says.
            }
        }
        return stale;
    }

    /// <summary>Deletes what <see cref="StaleStagingFiles"/> lists. Returns how many went; a file
    /// that can't be deleted is logged and left.</summary>
    internal static int SweepStale(string directory, TimeSpan olderThan, Action<string>? log = null)
    {
        int removed = 0;
        foreach (var path in StaleStagingFiles(directory, olderThan))
        {
            try
            {
                File.Delete(path);
                removed++;
                log?.Invoke($"[INFO] Removed a staging file an earlier run left behind: {path}");
            }
            catch (Exception ex)
            {
                log?.Invoke($"[WARN] Couldn't remove the staging file {path}: {ex.Message}");
            }
        }
        return removed;
    }

    /// <summary>
    /// Writes <paramref name="content"/> and publishes it at <paramref name="path"/>. The staging
    /// file is removed on any failure, and the failure is rethrown for the caller to log or swallow
    /// as it did before. The directory must already exist.
    /// </summary>
    public static void WriteAllText(string path, string content, Encoding? encoding = null)
    {
        var staging = StagingPath(path);
        try
        {
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, encoding ?? Utf8NoBom))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            Publish(staging, path);
        }
        catch
        {
            try { File.Delete(staging); } catch { }
            throw;
        }
    }

    /// <summary>Like <see cref="File.WriteAllLines(string, IEnumerable{string})"/>, published in one step.</summary>
    public static void WriteAllLines(string path, IEnumerable<string> lines, Encoding? encoding = null)
    {
        var text = new StringBuilder();
        foreach (var line in lines) text.AppendLine(line);
        WriteAllText(path, text.ToString(), encoding);
    }

    /// <summary>
    /// The rename fails for an instant when a reader has the target open without FileShare.Delete
    /// (File.ReadAllText does that) or when another writer's rename lands at the same moment. Both
    /// clear in milliseconds, but under load a busy file can stay contended for a while, so keep
    /// retrying for up to two seconds before giving up.
    /// </summary>
    private static void Publish(string staging, string path)
    {
        var deadline = Environment.TickCount64 + 2000;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(staging, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (Environment.TickCount64 < deadline && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(Math.Min(10 * attempt, 100));
            }
        }
    }
}
