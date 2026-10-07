using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using OpenTap;

namespace MiniGui
{
    public partial class MainWindow : Window
    {
        private const int MaxDisplayedResults = 10000;
        public static readonly RoutedCommand ClearCommand = new RoutedCommand();
        private readonly MiniGuiLogListener _logListener;
        private readonly TestPlanController _controller;
        private readonly ObservableCollection<MiniGuiResultEntry> _results =
            new ObservableCollection<MiniGuiResultEntry>();
        private readonly DispatcherTimer _resultTimer;
        private readonly Stopwatch _runStopwatch = new Stopwatch();
        private readonly DispatcherTimer _runTimer;
        private readonly DispatcherTimer _activityDelayTimer;
        private MiniGuiState _displayState = MiniGuiState.Idle;
        private bool _runActive;
        private bool _stopRequested;
        private string _runOutcome;
        private ScrollViewer _resultsScrollViewer;
        private bool _followResultsTail = true;
        private bool _closeRequested;
        private bool _listenersStopped;

        public bool HadError { get; private set; }

        public MainWindow(string initialPath)
        {
            InitializeComponent();
            _runTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            _runTimer.Tick += (sender, args) =>
            {
                if (_runActive)
                    RefreshStateText();
                else
                    _runTimer.Stop();
            };
            // The activity ring appears only once a run has lasted longer than this delay.
            _activityDelayTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(150)
            };
            _activityDelayTimer.Tick += (sender, args) =>
            {
                _activityDelayTimer.Stop();
                if (_runActive)
                    ShowActivityRing();
            };
            _logListener = new MiniGuiLogListener(Dispatcher, LogList);
            _controller = new TestPlanController(_logListener);
            _controller.StateChanged += OnStateChanged;
            ResultsList.ItemsSource = _results;
            ResultsList.Loaded += (sender, args) => AttachResultsScrollViewer();
            _resultTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background,
                (sender, args) => DrainResults(), Dispatcher);
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
                // With no plan loaded, show the chosen path first so a failed load leaves it there to fix.
                // With a plan loaded, leave the path box alone so a failed load keeps the current plan.
                if (!_controller.HasPlan)
                    PlanPathBox.Text = dialog.FileName;
                LoadPlan(dialog.FileName);
            }
        }

        private bool LoadPlan(string path)
        {
            try
            {
                _controller.LoadPlan(path);
                PlanPathBox.Text = _controller.LoadedPath;
                HadError = false;
                VerdictText.Text = "Verdict: -";
                UpdateControls();
                return true;
            }
            catch (Exception ex)
            {
                // The failure is reported through the LoadFailed state in the status area.
                HadError = true;
                UpdateControls();
                Log.CreateSource("MiniGui").Error("Unable to load test plan '{0}': {1}", path, ex);
                return false;
            }
        }

        internal static string FormatLoadError(string path, Exception ex)
        {
            var details = new System.Text.StringBuilder(ex.Message);
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
            {
                if (!string.IsNullOrWhiteSpace(inner.Message) && !details.ToString().Contains(inner.Message))
                    details.AppendLine().Append(inner.Message);
            }

            return "The test plan could not be loaded:" + Environment.NewLine + path +
                   Environment.NewLine + Environment.NewLine + details;
        }

        private async void Start_Click(object sender, RoutedEventArgs e)
        {
            if (_controller.IsRunning || _closeRequested || !_controller.HasPlan)
                return;

            HadError = false;
            _results.Clear();
            _followResultsTail = true;
            VerdictText.Text = "Verdict: -";
            _runOutcome = null;
            _stopRequested = false;
            _runActive = true;
            _runStopwatch.Restart();
            _runTimer.Start();
            _activityDelayTimer.Start();

            try
            {
                var run = _controller.StartAsync();
                UpdateControls();
                var verdict = await run;
                VerdictText.Text = "Verdict: " + verdict;
                _runOutcome = _stopRequested || verdict == Verdict.Aborted
                    ? "Aborted after " + FormatSeconds(_runStopwatch.Elapsed)
                    : "Completed in " + FormatSeconds(_runStopwatch.Elapsed);
            }
            catch (Exception ex)
            {
                HadError = true;
                _runOutcome = "Failed after " + FormatSeconds(_runStopwatch.Elapsed);
                FinishRunTiming();
                Log.CreateSource("MiniGui").Error("Test plan execution failed: {0}", ex);
                if (!_closeRequested)
                    MessageBox.Show(this, ex.Message, "Test plan execution failed",
                        MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                FinishRunTiming();
                UpdateControls();
                if (_closeRequested)
                    Close();
            }
        }

        private void FinishRunTiming()
        {
            if (!_runActive)
                return;
            _runStopwatch.Stop();
            _runTimer.Stop();
            _activityDelayTimer.Stop();
            _runActive = false;
            // The controller has already left Running/Stopping even if its dispatched notification is still queued.
            _displayState = _controller.State;
            RefreshStateText();
            HideActivityRing();
        }

        private static string FormatSeconds(TimeSpan elapsed)
        {
            return elapsed.TotalSeconds.ToString("0.00", CultureInfo.InvariantCulture) + " s";
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            _controller.RequestStop();
            UpdateControls();
        }

        private void OnStateChanged(MiniGuiState state)
        {
            if (Dispatcher.CheckAccess())
                ApplyState(state);
            else
                Dispatcher.BeginInvoke(new Action(() => ApplyState(state)));
        }

        private void ApplyState(MiniGuiState state)
        {
            _displayState = state;
            if (state == MiniGuiState.Stopping)
                _stopRequested = true;
            else if (state != MiniGuiState.Running && state != MiniGuiState.Ready)
                _runOutcome = null;

            RefreshStateText();
            UpdateControls();
        }

        private void RefreshStateText()
        {
            var state = _displayState;
            if (state == MiniGuiState.LoadFailed && _controller.LoadError != null)
            {
                var details = FormatLoadError(_controller.FailedLoadPath, _controller.LoadError);
                if (_controller.HasPlan)
                    details += Environment.NewLine + Environment.NewLine +
                               "The previous test plan is still loaded:" + Environment.NewLine + _controller.LoadedPath;

                StateText.Text = "Load failed";
                StateText.Foreground = Brushes.Firebrick;
                StateText.ToolTip = details;
                AutomationProperties.SetHelpText(StateText, details);
                return;
            }

            StateText.ClearValue(TextBlock.ForegroundProperty);
            StateText.ClearValue(ToolTipProperty);
            StateText.ClearValue(AutomationProperties.HelpTextProperty);

            if (_runActive && (state == MiniGuiState.Running || state == MiniGuiState.Stopping ||
                               state == MiniGuiState.Ready))
            {
                // A Ready notification can arrive before the awaited run result; keep timing until it does.
                var elapsed = FormatSeconds(_runStopwatch.Elapsed);
                StateText.Text = _stopRequested ? "Stopping after " + elapsed : elapsed;
                return;
            }

            switch (state)
            {
                case MiniGuiState.Idle:
                    StateText.Text = "Idle";
                    break;
                case MiniGuiState.Loading:
                    StateText.Text = "Loading...";
                    break;
                case MiniGuiState.Ready:
                    StateText.Text = _runOutcome ?? "Ready";
                    break;
                default:
                    StateText.Text = state.ToString();
                    break;
            }
        }

        // Shown by the run's delay timer and hidden when the run finishes; state notifications
        // (including Running -> Stopping) do not restart the delay.
        private void ShowActivityRing()
        {
            if (ActivityRing.Visibility == Visibility.Visible)
                return;

            ActivityRing.Visibility = Visibility.Visible;
            var spin = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(1)))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
            ActivityRingRotation.BeginAnimation(RotateTransform.AngleProperty, spin);
        }

        private void HideActivityRing()
        {
            ActivityRingRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            ActivityRing.Visibility = Visibility.Hidden;
        }

        private void UpdateControls()
        {
            if (StartButton == null)
                return;

            StartButton.IsEnabled = _controller.HasPlan && !_controller.IsRunning && !_closeRequested;
            BrowseButton.IsEnabled = !_controller.IsRunning && !_closeRequested;
            PlanPathBox.IsEnabled = !_controller.IsRunning && !_closeRequested;
            StopButton.IsEnabled = _controller.IsRunning && _controller.State == MiniGuiState.Running;
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
            if (_controller.IsRunning)
            {
                _closeRequested = true;
                _controller.RequestStop();
                UpdateControls();
                e.Cancel = true;
            }
            else if (!_listenersStopped)
            {
                _resultTimer.Stop();
                _runTimer.Stop();
                _activityDelayTimer.Stop();
                if (_resultsScrollViewer != null)
                    _resultsScrollViewer.ScrollChanged -= OnResultsScrollChanged;
                _controller.Dispose();
                _logListener.Stop();
                _listenersStopped = true;
            }

            base.OnClosing(e);
        }
        
        private void ClearLogPanel_Click(object sender, RoutedEventArgs e)
        {
            _logListener.Clear();
        }

        private void CopyLogPanel_Click(object sender, RoutedEventArgs e)
        {
            var items = GetSelectedItems<MiniGuiLogEntry>(LogList);
            if (items.Count == 0)
                return;
            Clipboard.SetText(string.Join(Environment.NewLine, items.Select(entry => entry.ToString())));
        }

        private void ClearResultsPanel_Click(object sender, RoutedEventArgs e)
        {
            _results.Clear();
        }

        private void CopyResultsPanel_Click(object sender, RoutedEventArgs e)
        {
            var items = GetSelectedItems<MiniGuiResultEntry>(ResultsList);
            if (items.Count == 0)
                return;
            Clipboard.SetText(string.Join(Environment.NewLine, items.Select(entry => entry.ToString())));
        }

        // Returns the selected rows of a list, or every row when nothing is selected, preserving list order.
        private static List<T> GetSelectedItems<T>(ListView list)
        {
            var selected = new HashSet<object>(list.SelectedItems.Cast<object>());
            return list.Items.Cast<object>()
                .OfType<T>()
                .Where(item => list.SelectedItems.Count == 0 || selected.Contains(item))
                .ToList();
        }

        private void DrainResults()
        {
            var batch = _controller.DrainResults(500);
            if (batch.Count == 0)
                return;

            AttachResultsScrollViewer();
            var offset = _resultsScrollViewer?.VerticalOffset ?? 0;
            var removed = 0;
            var overflow = _results.Count + batch.Count - MaxDisplayedResults;
            for (; removed < overflow && _results.Count != 0; removed++)
                _results.RemoveAt(0);
            foreach (var entry in batch)
                _results.Add(entry);

            if (!_followResultsTail && removed != 0 && _resultsScrollViewer != null)
                _resultsScrollViewer.ScrollToVerticalOffset(Math.Max(0, offset - removed));

            if (_followResultsTail)
            {
                if (_resultsScrollViewer != null)
                    _resultsScrollViewer.ScrollToBottom();
                else
                    ResultsList.ScrollIntoView(batch[batch.Count - 1]);
            }
        }

        private void AttachResultsScrollViewer()
        {
            if (_resultsScrollViewer != null)
                return;
            _resultsScrollViewer = FindVisualChild<ScrollViewer>(ResultsList);
            if (_resultsScrollViewer != null)
                _resultsScrollViewer.ScrollChanged += OnResultsScrollChanged;
        }

        private void OnResultsScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
                _followResultsTail =
                    _resultsScrollViewer.VerticalOffset >= _resultsScrollViewer.ScrollableHeight - 0.5;
            else if (_followResultsTail)
                _resultsScrollViewer.ScrollToBottom();
        }

        private static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match)
                    return match;
                var descendant = FindVisualChild<T>(child);
                if (descendant != null)
                    return descendant;
            }

            return null;
        }
    }
}
