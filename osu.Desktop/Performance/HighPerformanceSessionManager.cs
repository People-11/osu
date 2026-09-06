// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Runtime;
using System.Threading;
using osu.Framework.Allocation;
using osu.Framework.Logging;
using osu.Game.Performance;

namespace osu.Desktop.Performance
{
    public class HighPerformanceSessionManager : IHighPerformanceSessionManager
    {
        public bool IsSessionActive => activeSessions > 0;

        private int activeSessions;

        private GCLatencyMode originalGCMode;

        public IDisposable BeginSession()
        {
            enterSession();
            return new InvokeOnDisposal<HighPerformanceSessionManager>(this, static m => m.exitSession());
        }

        private void enterSession()
        {
            if (Interlocked.Increment(ref activeSessions) > 1)
            {
                Logger.Log($"High performance session requested ({activeSessions} running in total)");
                return;
            }

            Logger.Log("Starting high performance session");

            originalGCMode = GCSettings.LatencyMode;

            // EXPERIMENT: `LowLatency` disables gen2 collections entirely, and the large object heap is only
            // ever collected as part of a gen2 collection. On long, dense maps the LOH therefore grows without
            // bound (measured: 45 MB -> 4.8 GB over ~two minutes, with zero gen2 collections), and as it grows
            // the gen0/1 collections get steadily more expensive until GC is ~50% of every frame.
            // `SustainedLowLatency` still avoids blocking gen2 collections but permits background ones.
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;

            // Without doing this, the new GC mode won't kick in until the next GC, which could be at a more noticeable point in time.
            GC.Collect(0);
        }

        private void exitSession()
        {
            if (Interlocked.Decrement(ref activeSessions) > 0)
            {
                Logger.Log($"High performance session finished ({activeSessions} others remain)");
                return;
            }

            Logger.Log("Ending high performance session");

            if (GCSettings.LatencyMode == GCLatencyMode.SustainedLowLatency)
                GCSettings.LatencyMode = originalGCMode;

            // No GC.Collect() as we were already collecting at a higher frequency in the old mode.
        }
    }
}
