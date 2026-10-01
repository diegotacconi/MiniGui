using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using Microsoft.Win32;
using OpenTap;

namespace MiniGui
{
    public partial class MainWindow : Window
    {
        private readonly OperatorLogListener _logListener;
        private TestPlan _plan;
        private CancellationTokenSource _runCancellation;
        private bool _running;
        private bool _stopRequested;
        private bool _closeRequested;
        private bool _listenerRemoved;

        public bool HadError { get; private set; }

        public MainWindow(string initialPath)
        {
            InitializeComponent();
            _logListener = new OperatorLogListener(Dispatcher, LogList);
            Log.AddListener(_logListener);
            PlanPathBox.Text = initialPath ?? string.Empty;
            UpdateControls();
            Loaded += (sender, args) =>
            {
                if (!string.IsNullOrWhiteSpace(PlanPathBox.Text))
                    LoadPlan(PlanPathBox.Text);
            };
        }

        private void Browse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "OpenTAP plans (*.TapPlan)|*.TapPlan",
                DefaultExt = ".TapPlan",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) == true)
            {
                PlanPathBox.Text = dialog.FileName;
                LoadPlan(dialog.FileName);
            }
        }

        private bool LoadPlan(string path)
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                if (!File.Exists(fullPath))
                    throw new FileNotFoundException("Test plan not found.", fullPath);
                if (!string.Equals(Path.GetExtension(fullPath), ".TapPlan", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Select a .TapPlan file.", nameof(path));

                _plan = TestPlan.Load(fullPath);
                if (_plan == null)
                    throw new InvalidDataException("OpenTAP did not load a test plan.");
                LoadedPlanText.Text = "Loaded: " + fullPath;
                StateText.Text = "Ready";
                VerdictText.Text = "Verdict: -";
                HadError = false;
                return true;
            }
            catch (Exception ex)
            {
                _plan = null;
                LoadedPlanText.Text = "No plan loaded";
                StateText.Text = "Load failed";
                HadError = true;
                Log.Error(Log.CreateSource("MiniGui"), "Unable to load test plan: {0}", ex);
                MessageBox.Show(this, ex.Message, "Unable to load test plan",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private void Start_Click(object sender, RoutedEventArgs e)
        {
            if (_running)
                return;
            if (string.IsNullOrWhiteSpace(PlanPathBox.Text))
            {
                StateText.Text = "Select a plan";
                MessageBox.Show(this, "Select a .TapPlan file before starting.", "No test plan",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!LoadPlan(PlanPathBox.Text))
                return;

            _runCancellation = new CancellationTokenSource();
            _running = true;
            _stopRequested = false;
            HadError = false;
            StateText.Text = "Running";
            VerdictText.Text = "Verdict: -";
            UpdateControls();
            var plan = _plan;
            var token = _runCancellation.Token;
            try
            {
                TapThread.Start(() =>
                {
                    Verdict verdict = Verdict.Inconclusive;
                    Exception error = null;
                    try
                    {
                        verdict = TestPlanRunner.RunPlan(plan, Enumerable.Empty<IResultListener>(), token);
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                    }
                    Dispatcher.BeginInvoke(new Action(() => FinishRun(verdict, error)));
                });
            }
            catch (Exception ex)
            {
                FinishRun(Verdict.Inconclusive, ex);
            }
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            RequestStop();
        }

        private void RequestStop()
        {
            if (!_running || _runCancellation.IsCancellationRequested)
                return;
            StateText.Text = "Stopping";
            _stopRequested = true;
            StopButton.IsEnabled = false;
            _runCancellation.Cancel();
        }

        private void FinishRun(Verdict verdict, Exception error)
        {
            _runCancellation.Dispose();
            _runCancellation = null;
            _running = false;
            if (error != null)
            {
                HadError = true;
                StateText.Text = "Run failed";
                Log.Error(Log.CreateSource("MiniGui"), "Test plan execution failed: {0}", error);
                if (!_closeRequested)
                    MessageBox.Show(this, error.Message, "Test plan execution failed",
                        MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                StateText.Text = _stopRequested ? "Stopped" : "Finished";
                VerdictText.Text = "Verdict: " + verdict;
            }
            UpdateControls();
            if (_closeRequested)
                Close();
        }

        private void UpdateControls()
        {
            StartButton.IsEnabled = !_running && !_closeRequested;
            BrowseButton.IsEnabled = !_running && !_closeRequested;
            PlanPathBox.IsEnabled = !_running && !_closeRequested;
            StopButton.IsEnabled = _running && !_runCancellation.IsCancellationRequested;
        }

        public void RequestShutdown()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(RequestShutdown));
                return;
            }
            Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_running)
            {
                _closeRequested = true;
                RequestStop();
                UpdateControls();
                e.Cancel = true;
            }
            else if (!_listenerRemoved)
            {
                Log.RemoveListener(_logListener);
                _logListener.Stop();
                _listenerRemoved = true;
            }
            base.OnClosing(e);
        }
    }
}
