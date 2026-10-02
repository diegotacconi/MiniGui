using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OpenTap;
using OpenTap.Diagnostic;

namespace MiniGui
{
    internal sealed class LogPanelEntry
    {
        public string Timestamp { get; set; }
        public string Source { get; set; }
        public string Message { get; set; }
        public Brush Foreground { get; set; }

        public override string ToString() => $"{Timestamp} ; {Source} ; {Message}";
    }

    internal sealed class LogPanel : TraceListener
    {
        private const int MaxEntries = 1000;
        private readonly Dispatcher _dispatcher;
        private readonly ListView _list;
        private readonly Queue<LogPanelEntry> _pending = new Queue<LogPanelEntry>();
        private readonly object _gate = new object();
        private readonly DispatcherTimer _timer;
        private ScrollViewer _scrollViewer;
        private bool _followTail = true;
        private bool _disposed;

        // Must be created on the UI thread. Pending log events are drained in batches at Background
        // priority, so heavy logging never delays input or rendering.
        public LogPanel(Dispatcher dispatcher, ListView list)
        {
            _dispatcher = dispatcher;
            _list = list;
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background,
                (sender, args) => Drain(), dispatcher);
        }

        public override void TraceEvents(IEnumerable<Event> events)
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                foreach (var entry in events)
                {
                    if (_pending.Count == MaxEntries)
                        _pending.Dequeue();
                    _pending.Enqueue(new LogPanelEntry
                    {
                        Timestamp = new DateTime(entry.Timestamp).ToString("HH:mm:ss.fff"),
                        Source = entry.Source,
                        Message = $"{entry.Message?.TrimEnd('\r', '\n')}",
                        Foreground = GetColorForTraceLevel((LogEventType)entry.EventType)
                    });
                }
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _pending.Clear();
                _list.Items.Clear();
            }
            _followTail = true;
        }

        public string GetSelectedText()
        {
            var selected = new HashSet<object>(_list.SelectedItems.Cast<object>());
            return string.Join(Environment.NewLine, _list.Items.Cast<LogPanelEntry>()
                .Where(selected.Contains).Select(entry => entry.ToString()));
        }

        private static Brush GetColorForTraceLevel(LogEventType eventType)
        {
            switch (eventType)
            {
                case LogEventType.Debug:
                    return Brushes.Gray;
                case LogEventType.Information:
                    return Brushes.Black;
                case LogEventType.Warning:
                    return Brushes.DarkOrange;
                case LogEventType.Error:
                    return Brushes.DarkRed;
                default:
                    return Brushes.Red;
            }
        }

        private void Drain()
        {
            LogPanelEntry[] batch;
            lock (_gate)
            {
                if (_disposed || _pending.Count == 0)
                    return;
                batch = _pending.ToArray();
                _pending.Clear();
            }

            AttachScrollViewer();

            var offset = _scrollViewer?.VerticalOffset ?? 0;
            var removed = 0;
            var overflow = _list.Items.Count + batch.Length - MaxEntries;
            for (; removed < overflow && _list.Items.Count != 0; removed++)
                _list.Items.RemoveAt(0);
            foreach (var entry in batch)
                _list.Items.Add(entry);

            // Keep the rows being read in place when the oldest entries are trimmed (item-based scrolling).
            if (!_followTail && removed != 0 && _scrollViewer != null)
                _scrollViewer.ScrollToVerticalOffset(Math.Max(0, offset - removed));

            if (_followTail)
            {
                if (_scrollViewer != null)
                    _scrollViewer.ScrollToBottom();
                else
                    _list.ScrollIntoView(batch[batch.Length - 1]);
            }
        }

        private void AttachScrollViewer()
        {
            if (_scrollViewer != null)
                return;
            _scrollViewer = FindVisualChild<ScrollViewer>(_list);
            if (_scrollViewer != null)
                _scrollViewer.ScrollChanged += OnScrollChanged;
        }

        // Automatic scroll only while the last item is in view. Only scrolling by the user (no change in
        // content or viewport size) updates that choice, so bursts of new entries cannot cancel it before
        // the previous scroll-to-bottom has been laid out.
        private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
                _followTail = _scrollViewer.VerticalOffset >= _scrollViewer.ScrollableHeight - 0.5;
            else if (_followTail)
                _scrollViewer.ScrollToBottom();
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

        public void Stop()
        {
            lock (_gate)
            {
                _disposed = true;
                _pending.Clear();
            }
            _timer.Stop();
            if (_scrollViewer != null)
                _scrollViewer.ScrollChanged -= OnScrollChanged;
        }
    }
}
