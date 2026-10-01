using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenTap;
using OpenTap.Diagnostic;

namespace MiniGui
{
    internal sealed class OperatorLogListener : TraceListener
    {
        private const int MaxEntries = 1000;
        private readonly Dispatcher _dispatcher;
        private readonly ListBox _list;
        private readonly Queue<string> _pending = new Queue<string>();
        private readonly object _gate = new object();
        private bool _scheduled;
        private bool _disposed;

        public OperatorLogListener(Dispatcher dispatcher, ListBox list)
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
                    _pending.Enqueue($"{new DateTime(entry.Timestamp):HH:mm:ss.fff} [{entry.EventType}] {entry.Source}: {entry.Message?.TrimEnd('\r', '\n')}");
                }
                if (!_scheduled && _pending.Count != 0 && !_dispatcher.HasShutdownStarted)
                {
                    _scheduled = true;
                    _dispatcher.BeginInvoke(new Action(Drain));
                }
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
