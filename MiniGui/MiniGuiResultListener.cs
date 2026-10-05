using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using OpenTap;

namespace MiniGui
{
    internal sealed class MiniGuiResultEntry
    {
        public string Timestamp { get; set; }
        public string Source { get; set; }
        public string Table { get; set; }
        public string Field { get; set; }
        public string Value { get; set; }

        public override string ToString()
        {
            return $"{Timestamp} ; {Source} ; {Table} ; {Field} ; {Value}";
        }
    }

    internal sealed class MiniGuiResultListener : ResultListener
    {
        private const int MaxPendingEntries = 10000;
        private const int MaxStepNames = 10000;
        private readonly ConcurrentDictionary<Guid, string> _stepNames =
            new ConcurrentDictionary<Guid, string>();
        private readonly Queue<Guid> _stepOrder = new Queue<Guid>();
        private readonly object _stepGate = new object();
        private readonly object _gate = new object();
        private readonly Queue<MiniGuiResultEntry> _pending = new Queue<MiniGuiResultEntry>();

        public MiniGuiResultListener()
        {
            IsEnabled = true;
        }

        public override void OnTestPlanRunStart(TestPlanRun planRun)
        {
        }

        public override void OnTestPlanRunCompleted(TestPlanRun planRun, System.IO.Stream logStream)
        {
            lock (_stepGate)
            {
                _stepNames.Clear();
                _stepOrder.Clear();
            }
        }

        public override void OnTestStepRunStart(TestStepRun stepRun)
        {
            lock (_stepGate)
            {
                _stepNames[stepRun.Id] = stepRun.TestStepName;
                _stepOrder.Enqueue(stepRun.Id);
                while (_stepOrder.Count > MaxStepNames)
                    _stepNames.TryRemove(_stepOrder.Dequeue(), out _);
            }
        }

        public override void OnTestStepRunCompleted(TestStepRun stepRun)
        {
        }

        public override void OnResultPublished(Guid stepRunId, ResultTable result)
        {
            var source = _stepNames.TryGetValue(stepRunId, out var stepName) ? stepName : string.Empty;
            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.CurrentCulture);
            for (var row = 0; row < result.Rows; row++)
            {
                foreach (var column in result.Columns)
                {
                    var entry = new MiniGuiResultEntry
                    {
                        Timestamp = timestamp,
                        Source = source,
                        Table = result.Name,
                        Field = column.Name,
                        Value = Convert.ToString(column.Data.GetValue(row), CultureInfo.CurrentCulture) ??
                                string.Empty
                    };

                    lock (_gate)
                    {
                        if (_pending.Count == MaxPendingEntries)
                            _pending.Dequeue();
                        _pending.Enqueue(entry);
                    }
                }
            }
        }

        public IList<MiniGuiResultEntry> Drain(int maximumCount)
        {
            if (maximumCount <= 0)
                return new List<MiniGuiResultEntry>();

            var entries = new List<MiniGuiResultEntry>(maximumCount);
            lock (_gate)
            {
                while (entries.Count < maximumCount && _pending.Count > 0)
                    entries.Add(_pending.Dequeue());
            }

            return entries;
        }
    }
}
