using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using OpenTap;

namespace MiniGui
{
    public partial class MainWindow : Window
    {
        private const int MaxDisplayedResults = 10000;
        private readonly MiniGuiLogListener _logListener;
        private readonly TestPlanController _controller;
        private readonly ObservableCollection<MiniGuiResultEntry> _results =
            new ObservableCollection<MiniGuiResultEntry>();
        private readonly DispatcherTimer _resultTimer;
        private ScrollViewer _resultsScrollViewer;
        private bool _followResultsTail = true;
        private string _lastAttemptedPath;
        private bool _closeRequested;
        private bool _listenersStopped;

        public bool HadError { get; private set; }

        public MainWindow(string initialPath)
        {
            InitializeComponent();
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
                PlanPathBox.Text = dialog.FileName;
                LoadPlan(dialog.FileName);
            }
        }

        private void PlanPathBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _lastAttemptedPath = null;
            if (_controller == null || !_controller.HasPlan ||
                string.Equals(PlanPathBox.Text, _controller.LoadedPath, StringComparison.Ordinal))
                return;

            _controller.UnloadPlan();
            StateText.Text = "Idle";
            VerdictText.Text = "Verdict: -";
            UpdateControls();
        }

        private void PlanPathBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter)
                return;
            e.Handled = true;
            LoadTypedPath();
        }

        private void PlanPathBox_LostFocus(object sender, RoutedEventArgs e)
        {
            LoadTypedPath();
        }

        private void LoadTypedPath()
        {
            var path = PlanPathBox.Text;
            if (_controller.IsRunning || _closeRequested || _controller.HasPlan ||
                string.IsNullOrWhiteSpace(path) ||
                string.Equals(path, _lastAttemptedPath, StringComparison.Ordinal))
                return;
            LoadPlan(path);
        }

        private bool LoadPlan(string path)
        {
            _lastAttemptedPath = path;
            try
            {
                _controller.LoadPlan(path);
                PlanPathBox.Text = _controller.LoadedPath;
                _lastAttemptedPath = _controller.LoadedPath;
                HadError = false;
                VerdictText.Text = "Verdict: -";
                UpdateControls();
                return true;
            }
            catch (Exception ex)
            {
                HadError = true;
                UpdateControls();
                Log.CreateSource("MiniGui").Error("Unable to load test plan: {0}", ex);
                MessageBox.Show(this, ex.Message, "Unable to load test plan",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        private async void Start_Click(object sender, RoutedEventArgs e)
        {
            if (_controller.IsRunning || _closeRequested || !_controller.HasPlan)
                return;

            HadError = false;
            _logListener.Clear();
            _results.Clear();
            _followResultsTail = true;
            VerdictText.Text = "Verdict: -";

            try
            {
                var run = _controller.StartAsync();
                UpdateControls();
                var verdict = await run;
                VerdictText.Text = _controller.State == MiniGuiState.Stopped
                    ? "Verdict: STOPPED"
                    : "Verdict: " + verdict;
            }
            catch (Exception ex)
            {
                HadError = true;
                Log.CreateSource("MiniGui").Error("Test plan execution failed: {0}", ex);
                if (!_closeRequested)
                    MessageBox.Show(this, ex.Message, "Test plan execution failed",
                        MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                UpdateControls();
                if (_closeRequested)
                    Close();
            }
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            _controller.RequestStop();
            UpdateControls();
        }

        private void ClearLogPanel_Click(object sender, RoutedEventArgs e)
        {
            _logListener.Clear();
        }

        private void CopyLogPanel_Click(object sender, RoutedEventArgs e)
        {
            var text = _logListener.GetSelectedText();
            if (!string.IsNullOrEmpty(text))
                Clipboard.SetText(text);
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
            StateText.Text = state.ToString();
            UpdateControls();
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
                if (_resultsScrollViewer != null)
                    _resultsScrollViewer.ScrollChanged -= OnResultsScrollChanged;
                _logListener.Stop();
                _controller.Dispose();
                _listenersStopped = true;
            }

            base.OnClosing(e);
        }
    }
}
