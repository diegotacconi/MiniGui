using System.Collections.Generic;
using OpenTap;

namespace MiniGui.TestSteps
{
    [Display("MiniGui Results Demo")]
    public sealed class ResultsDemoStep : TestStep
    {
        public override void Run()
        {
            Results.Publish("Scalar Values", new
            {
                Integer = 42,
                Decimal = 3.14159,
                Text = "MiniGui results demo",
                Boolean = true
            });

            Results.PublishTable(
                "Typed Rows",
                new List<string> { "Index", "Temperature", "Label", "In Range" },
                new[] { 1, 2 },
                new[] { 21.5, 22.75 },
                new[] { "Sample A", "Sample B" },
                new[] { true, false });

            Results.Publish("Status", new
            {
                Verdict = "PASS",
                Accepted = true
            });
        }
    }
}
