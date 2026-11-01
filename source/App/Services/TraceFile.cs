using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Iris.Core;

namespace Iris.App.Services;

internal sealed class TraceFile : ITraceSink, IDisposable
{
    public const long MaxBytes = 300L * 1024 * 1024;
    private const int MaxQueued = 200_000;

    private readonly string _folder;
    private readonly Action<string> _log;
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _gate = new();
    private Thread? _thread;
    private volatile bool _open;
    private volatile bool _stopping;
    private int _queued;
    private long _dropped;

    private StreamWriter? _writer;
    private long _bytes;
    private bool _capped;

    public TraceFile(string folder, Action<string> log)
    {
        _folder = folder;
        _log = log;
    }

    public bool IsOpen => _open;

    public bool Enabled => _open;

    public string? FileName { get; private set; }

    public void Write(string line)
    {
        if (!_open)
            return;
        if (Interlocked.Increment(ref _queued) > MaxQueued)
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref _dropped);
            return;
        }

        _queue.Enqueue(line);
    }

    public bool Open(DateTime localNow, string header)
    {
        lock (_gate)
        {
            if (_open || _stopping)
                return _open;
            try
            {
                Directory.CreateDirectory(_folder);
                var name = "trace-" + localNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".jsonl";
                var path = Path.Combine(_folder, name);
                for (var n = 2; File.Exists(path); n++)
                    path = Path.Combine(_folder, Path.GetFileNameWithoutExtension(name) + "-" + n + ".jsonl");
                _writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 16), new UTF8Encoding(false), 1 << 16);
                FileName = Path.GetFileName(path);
                _bytes = 0;
                _capped = false;
                Interlocked.Exchange(ref _dropped, 0);
                _queue.Enqueue(header);
                Interlocked.Increment(ref _queued);
                _open = true;
                if (_thread is null)
                {
                    _thread = new Thread(Run) { Name = "IRIS trace", IsBackground = true, Priority = ThreadPriority.BelowNormal };
                    _thread.Start();
                }

                _wake.Set();
                _log("Developer trace started: " + FileName);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log("Developer trace could not be started: " + ex.Message);
                _writer = null;
                return false;
            }
        }
    }

    public void Close(string? trailer = null)
    {
        lock (_gate)
        {
            if (!_open)
                return;
            if (trailer is not null)
            {
                _queue.Enqueue(trailer);
                Interlocked.Increment(ref _queued);
            }

            _open = false;
            Drain();
            try
            {
                _writer?.Dispose();
            }
            catch (IOException)
            {
            }

            _writer = null;
            _log("Developer trace ended: " + FileName + " (" + (_bytes / 1024) + " KB" + (Interlocked.Read(ref _dropped) > 0 ? ", " + Interlocked.Read(ref _dropped) + " lines dropped" : string.Empty) + ")");
        }
    }

    public void Dispose()
    {
        Close();
        _stopping = true;
        _wake.Set();
        _thread?.Join(TimeSpan.FromSeconds(2));
    }

    private void Run()
    {
        while (!_stopping)
        {
            _wake.WaitOne(100);
            lock (_gate)
            {
                if (_open)
                    Drain();
            }
        }
    }

    private void Drain()
    {
        var writer = _writer;
        while (_queue.TryDequeue(out var line))
        {
            Interlocked.Decrement(ref _queued);
            if (writer is null || _capped)
                continue;
            try
            {
                writer.Write(line);
                writer.Write('\n');
                _bytes += line.Length + 1;
                if (_bytes > MaxBytes)
                {
                    _capped = true;
                    writer.Write("{\"ev\":\"cap\",\"bytes\":" + _bytes.ToString(CultureInfo.InvariantCulture) + "}\n");
                    _log("Developer trace reached " + (MaxBytes / (1024 * 1024)) + " MB and stops writing until the next overlay session: " + FileName);
                }
            }
            catch (IOException ex)
            {
                _capped = true;
                _log("Developer trace stopped (write failed: " + ex.Message + ")");
            }
        }

        try
        {
            writer?.Flush();
        }
        catch (IOException)
        {
        }
    }
}
