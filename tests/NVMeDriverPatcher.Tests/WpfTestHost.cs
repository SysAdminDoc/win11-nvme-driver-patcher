using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows.Threading;

namespace NVMeDriverPatcher.Tests;

/// <summary>
/// Single STA thread + single <see cref="System.Windows.Application"/> shared by every WPF test
/// in the assembly.
///
/// WPF permits exactly one Application per AppDomain, and binds it to the thread that created it.
/// Each WPF test class spinning up its own STA thread and its own <c>new App()</c> works in
/// isolation but throws "Cannot create more than one System.Windows.Application instance in the
/// same AppDomain" as soon as a second such class exists in the same run — and takes the test
/// host down with it. Route all WPF work through <see cref="Run"/> instead.
/// </summary>
internal static class WpfTestHost
{
    private static readonly object Gate = new();
    private static Dispatcher? _dispatcher;
    private static System.Windows.Application? _app;

    private static Dispatcher Dispatcher
    {
        get
        {
            lock (Gate)
            {
                if (_dispatcher is not null) return _dispatcher;

                var ready = new ManualResetEventSlim();
                ExceptionDispatchInfo? startupError = null;

                var thread = new Thread(() =>
                {
                    try
                    {
                        var app = new App { ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown };
                        app.InitializeComponent();
                        _app = app;
                        _dispatcher = Dispatcher.CurrentDispatcher;
                    }
                    catch (Exception ex)
                    {
                        startupError = ExceptionDispatchInfo.Capture(ex);
                    }
                    finally
                    {
                        ready.Set();
                    }

                    if (startupError is null)
                    {
                        Dispatcher.Run();
                        // Release WPF's process-wide state; a live Application otherwise keeps
                        // the test host from exiting after the last test has passed.
                        try { _app?.Shutdown(); } catch { }
                    }
                })
                {
                    IsBackground = true,     // never keeps the test host alive
                    Name = "WpfTestHost"
                };

                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();

                if (!ready.Wait(TimeSpan.FromSeconds(60)))
                    throw new TimeoutException("WPF test host did not start.");
                startupError?.Throw();

                return _dispatcher ?? throw new InvalidOperationException("WPF test host has no dispatcher.");
            }
        }
    }

    /// <summary>
    /// Tears the host down after the last WPF test. A live WPF Application/Dispatcher left
    /// running keeps the test host from exiting, which shows up as "Test Run Aborted / host
    /// process exited unexpectedly" plus a hang dump *after* every test has already passed.
    /// Driven by <see cref="WpfCollection"/> so it happens deterministically.
    /// </summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            _dispatcher?.InvokeShutdown();
            _dispatcher = null;
            _app = null;
        }
    }

    /// <summary>Runs <paramref name="action"/> on the shared STA/Application thread, rethrowing
    /// any exception on the caller's thread with its original stack.</summary>
    public static void Run(Action action)
    {
        ExceptionDispatchInfo? error = null;

        var operation = Dispatcher.InvokeAsync(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ExceptionDispatchInfo.Capture(ex); }
        });

        if (!operation.Task.Wait(TimeSpan.FromSeconds(120)))
            throw new TimeoutException("WPF test host operation timed out.");

        error?.Throw();
    }
}

/// <summary>Shuts the shared WPF host down once the last test in the collection has run.</summary>
public sealed class WpfHostFixture : IDisposable
{
    public void Dispose() => WpfTestHost.Shutdown();
}

/// <summary>
/// Every WPF-touching test class must join this collection: it serializes them (they share one
/// Application) and guarantees the host thread is shut down at the end of the run.
/// </summary>
[CollectionDefinition(Name)]
public sealed class WpfCollection : ICollectionFixture<WpfHostFixture>
{
    public const string Name = "wpf";
}
