using System;
using System.Collections.Generic;
using System.Linq;
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
        private bool _scheduled;
        private bool _disposed;

        public LogPanel(Dispatcher dispatcher, ListView list)
        {
            _dispatcher = dispatcher;
            _list = list;
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
                        Message = $"[{entry.EventType}] {entry.Message?.TrimEnd('\r', '\n')}",
                        Foreground = GetColorForTraceLevel((LogEventType)entry.EventType)
                    });
                }
                if (!_scheduled && _pending.Count != 0 && !_dispatcher.HasShutdownStarted)
                {
                    _scheduled = true;
                    _dispatcher.BeginInvoke(new Action(Drain));
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
            lock (_gate)
            {
                if (!_disposed)
                {
                    while (_pending.Count != 0)
                    {
                        if (_list.Items.Count == MaxEntries)
                            _list.Items.RemoveAt(0);
                        _list.Items.Add(_pending.Dequeue());
                    }
                    if (_list.Items.Count != 0)
                        _list.ScrollIntoView(_list.Items[_list.Items.Count - 1]);
                }
                _scheduled = false;
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                _disposed = true;
                _pending.Clear();
            }
        }
    }
}
