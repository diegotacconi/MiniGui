using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using OpenTap;

namespace MiniGui
{
    internal static class TestPlanRunner
    {
        public static Verdict RunPlan(TestPlan plan, IEnumerable<IResultListener> resultListeners, CancellationToken cancellationToken)
        {
            plan.PrintTestPlanRunSummary = true;
            resultListeners = resultListeners.Concat(ResultSettings.Current);
            try
            {
                return plan.ExecuteAsync(resultListeners, new List<ResultParameter>(), null, cancellationToken).Result.Verdict;
            }
            catch (AggregateException ex) when (ex.InnerExceptions.Count == 1)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }
}
