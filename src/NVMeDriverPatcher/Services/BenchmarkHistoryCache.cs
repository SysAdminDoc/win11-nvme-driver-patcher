using NVMeDriverPatcher.Models;

namespace NVMeDriverPatcher.Services;

internal sealed class BenchmarkHistoryCache
{
    private List<BenchmarkResult> _history = [];
    private string _path = string.Empty;
    private DateTime _lastWriteUtc = DateTime.MinValue;
    private long _length = -1;
    private bool _loaded;

    public List<BenchmarkResult> Get(string workingDir)
    {
        var path = GetHistoryPath(workingDir);
        var (lastWriteUtc, length) = ReadSignature(path);
        if (_loaded &&
            string.Equals(_path, path, StringComparison.OrdinalIgnoreCase) &&
            _lastWriteUtc == lastWriteUtc &&
            _length == length)
        {
            return _history;
        }

        _history = BenchmarkService.GetHistory(workingDir);
        _path = path;
        _lastWriteUtc = lastWriteUtc;
        _length = length;
        _loaded = true;
        return _history;
    }

    public void Invalidate() => _loaded = false;

    private static string GetHistoryPath(string workingDir)
    {
        try
        {
            return string.IsNullOrWhiteSpace(workingDir)
                ? string.Empty
                : Path.Combine(workingDir, "benchmark_results.json");
        }
        catch
        {
            return string.Empty;
        }
    }

    private static (DateTime LastWriteUtc, long Length) ReadSignature(string path)
    {
        if (string.IsNullOrEmpty(path))
            return (DateTime.MinValue, -1);

        try
        {
            var info = new FileInfo(path);
            return info.Exists
                ? (info.LastWriteTimeUtc, info.Length)
                : (DateTime.MinValue, -1);
        }
        catch
        {
            return (DateTime.MinValue, -1);
        }
    }
}
