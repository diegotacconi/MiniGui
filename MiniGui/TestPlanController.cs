using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using OpenTap;

namespace MiniGui
{
    internal enum MiniGuiState
    {
        Idle,       // No test plan loaded.
        Loading,    // A test plan is being loaded.
        LoadFailed, // The last load attempt failed; any previously loaded plan is kept.
        Ready,      // A test plan is loaded and can be run.
        Running,    // The test plan is executing.
        Stopping    // Stop was requested; waiting for the run to end.
    }

    internal sealed class TestPlanController : IDisposable
    {
        private readonly object _gate = new object();
        private readonly MiniGuiLogListener _logListener;
        private TestPlan _plan;
        private CancellationTokenSource _runCancellation;
        private MiniGuiResultListener _resultListener;
        private bool _isRunning;
        private bool _disposed;
        public event Action<MiniGuiState> StateChanged;

        public MiniGuiState State { get; private set; } = MiniGuiState.Idle;
        public string LoadedPath { get; private set; }
        public Verdict? CurrentVerdict { get; private set; }

        // Set while State is LoadFailed.
        public string FailedLoadPath { get; private set; }
        public Exception LoadError { get; private set; }

        public bool IsRunning
        {
            get
            {
                lock (_gate)
                {
                    return _isRunning;
                }
            }
        }

        public bool HasPlan => _plan != null;

        public TestPlanController(MiniGuiLogListener logListener)
        {
            _logListener = logListener ?? throw new ArgumentNullException(nameof(logListener));
            Log.AddListener(_logListener);
        }

        public void LoadPlan(string path)
        {
            if (IsRunning)
                throw new InvalidOperationException("A test plan cannot be loaded while a run is active.");

            SetState(MiniGuiState.Loading);
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (!File.Exists(fullPath))
                    throw new FileNotFoundException("Test plan not found.", fullPath);
                if (!string.Equals(Path.GetExtension(fullPath), ".TapPlan", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Select a .TapPlan file.", nameof(path));

                var plan = TestPlan.Load(fullPath);
                if (plan == null)
                    throw new InvalidDataException("OpenTAP did not load a test plan.");

                _plan = plan;
                LoadedPath = fullPath;
                CurrentVerdict = null;
                SetState(MiniGuiState.Ready);
            }
            catch (Exception ex)
            {
                // A failed load leaves any previously loaded plan in place.
                FailedLoadPath = TryGetFullPath(path);
                LoadError = ex;
                SetState(MiniGuiState.LoadFailed);
                throw;
            }
        }

        private static string TryGetFullPath(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return path;
            }
        }

        public void UnloadPlan()
        {
            if (IsRunning)
                return;

            _plan = null;
            LoadedPath = null;
            CurrentVerdict = null;
            SetState(MiniGuiState.Idle);
        }

        public async Task<Verdict> StartAsync()
        {
            if (_plan == null)
                throw new InvalidOperationException("Load a valid .TapPlan before starting execution.");

            CancellationToken token;
            lock (_gate)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(TestPlanController));
                if (_isRunning)
                    throw new InvalidOperationException("A test plan is already running.");

                _isRunning = true;
                _runCancellation = new CancellationTokenSource();
                token = _runCancellation.Token;
                _resultListener = new MiniGuiResultListener();
            }

            CurrentVerdict = null;
            SetState(MiniGuiState.Running);
            try
            {
                _plan.PrintTestPlanRunSummary = true;
                var listeners = new List<IResultListener> { _resultListener };
                listeners.AddRange(ResultSettings.Current);
                var run = await _plan.ExecuteAsync(listeners, new List<ResultParameter>(), null, token)
                    .ConfigureAwait(false);

                CurrentVerdict = run.Verdict;
                return run.Verdict;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // OpenTAP normally returns an Aborted run on cancellation.
                CurrentVerdict = Verdict.Aborted;
                return Verdict.Aborted;
            }
            finally
            {
                lock (_gate)
                {
                    _isRunning = false;
                    _runCancellation.Dispose();
                    _runCancellation = null;
                }
                // The plan stays loaded after any run outcome, so it is ready to run again.
                SetState(MiniGuiState.Ready);
            }
        }

        public bool RequestStop()
        {
            lock (_gate)
            {
                if (!_isRunning || _runCancellation == null || _runCancellation.IsCancellationRequested)
                    return false;

                SetState(MiniGuiState.Stopping);
                _runCancellation.Cancel();
                return true;
            }
        }

        public IList<MiniGuiResultEntry> DrainResults(int maximumCount)
        {
            return _resultListener?.Drain(maximumCount) ?? new List<MiniGuiResultEntry>();
        }

        private void SetState(MiniGuiState state)
        {
            if (state != MiniGuiState.LoadFailed)
            {
                FailedLoadPath = null;
                LoadError = null;
            }
            State = state;
            StateChanged?.Invoke(state);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_isRunning)
                    throw new InvalidOperationException("Stop the active test plan before disposing the controller.");

                if (_disposed)
                    return;

                Log.RemoveListener(_logListener);
                _disposed = true;
                _plan = null;
                _resultListener = null;
            }
        }
    }
}
