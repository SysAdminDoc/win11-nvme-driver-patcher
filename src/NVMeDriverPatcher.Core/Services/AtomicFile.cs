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
