// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Pooling;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Objects.Pooling;
using osu.Game.Rulesets.Osu.Skinning.Argon;
using osu.Game.Skinning;
using osuTK;

namespace osu.Game.Rulesets.Osu.Objects.Drawables.Connections
{
    /// <summary>
    /// Visualises the <see cref="FollowPoint"/>s between two <see cref="DrawableOsuHitObject"/>s.
    /// </summary>
    public partial class FollowPointConnection : PoolableDrawableWithLifetime<FollowPointLifetimeEntry>
    {
        // Todo: These shouldn't be constants
        public const int SPACING = 32;
        public const double PREEMPT = 800;

        public DrawablePool<FollowPoint>? Pool { private get; set; }

        private readonly List<FollowPoint> points = new List<FollowPoint>();
        private int aliveStart;
        private int aliveEnd;
        private double lastLifeCheckTime = double.NaN;
        private bool lifetimesOrdered;
        private volatile bool batchArgonUpdates;

        private ISkinSource skinSource = null!;

        [BackgroundDependencyLoader]
        private void load(ISkinSource skinSource)
        {
            this.skinSource = skinSource;
            skinSource.SourceChanged += onSkinChanged;
        }

        private void onSkinChanged() => batchArgonUpdates = false;

        protected override void OnApply(FollowPointLifetimeEntry entry)
        {
            base.OnApply(entry);

            entry.Invalidated += scheduleRefresh;

            // Our clock may not be correct at this point if `LoadComplete` has not run yet.
            // Without a schedule, animations referencing FollowPoint's clock (see `IAnimationTimeReference`) would be incorrect on first pool usage.
            scheduleRefresh();
        }

        protected override void OnFree(FollowPointLifetimeEntry entry)
        {
            base.OnFree(entry);

            entry.Invalidated -= scheduleRefresh;
            // Return points to the pool.
            clearPoints();
        }

        private void scheduleRefresh() => Scheduler.AddOnce(() =>
        {
            Debug.Assert(Pool != null);

            clearPoints();

            var entry = Entry;

            if (entry?.End == null) return;

            OsuHitObject start = entry.Start;
            OsuHitObject end = entry.End;

            double startTime = start.GetEndTime();

            Vector2 startPosition = start.StackedEndPosition;
            Vector2 endPosition = end.StackedPosition;

            Vector2 distanceVector = endPosition - startPosition;
            int distance = (int)distanceVector.Length;
            float rotation = (float)(Math.Atan2(distanceVector.Y, distanceVector.X) * (180 / Math.PI));

            lifetimesOrdered = end.StartTime >= startTime;

            double finalTransformEndTime = startTime;

            for (int d = (int)(SPACING * 1.5); d < distance - SPACING; d += SPACING)
            {
                float fraction = (float)d / distance;
                Vector2 pointStartPosition = startPosition + (fraction - 0.1f) * distanceVector;
                Vector2 pointEndPosition = startPosition + fraction * distanceVector;

                GetFadeTimes(start, end, (float)d / distance, out double fadeInTime, out double fadeOutTime);

                FollowPoint fp;

                fp = Pool.Get();

                fp.ApplyAnimation(pointStartPosition, pointEndPosition, new Vector2(1.5f * end.Scale), new Vector2(end.Scale), rotation,
                    fadeInTime, fadeOutTime, end.TimeFadeIn);

                points.Add(fp);
                AddInternal(fp);

                finalTransformEndTime = fp.LifetimeEnd;
            }

            entry.LifetimeEnd = finalTransformEndTime;
        });

        private void clearPoints()
        {
            ClearInternal(false);
            points.Clear();
            aliveStart = aliveEnd = 0;
            lastLifeCheckTime = double.NaN;
            lifetimesOrdered = false;
            batchArgonUpdates = false;
        }

        protected override bool RequiresChildrenUpdate => !batchArgonUpdates && base.RequiresChildrenUpdate;

        protected override bool CheckChildrenLife()
        {
            if (!lifetimesOrdered || points.Count != InternalChildren.Count)
            {
                bool fallbackChanged = base.CheckChildrenLife();

                if (batchArgonUpdates)
                {
                    double currentTime = Time.Current;

                    for (int i = 0; i < AliveInternalChildren.Count; i++)
                        ((FollowPoint)AliveInternalChildren[i]).ApplyAnimationAt(currentTime);
                }

                return fallbackChanged;
            }

            double time = Time.Current;
            int newStart = aliveStart;
            int newEnd = aliveEnd;

            // Most frames remain between the same two lifetime boundaries. Preserve the binary-search path for
            // rewinds/seeks and only skip it when the cached range is provably still current.
            if (time < lastLifeCheckTime
                || (newStart < points.Count && points[newStart].LifetimeEnd <= time)
                || (newEnd < points.Count && points[newEnd].LifetimeStart <= time))
            {
                newStart = firstPointEndingAfter(time);
                newEnd = firstPointStartingAfter(time);
            }

            lastLifeCheckTime = time;
            bool changed = false;

            for (int i = aliveStart; i < aliveEnd; i++)
            {
                if ((i < newStart || i >= newEnd) && points[i].IsAlive)
                {
                    MakeChildDead(points[i]);
                    changed = true;
                }
            }

            for (int i = newStart; i < newEnd; i++)
            {
                if ((i < aliveStart || i >= aliveEnd) && !points[i].IsAlive)
                {
                    MakeChildAlive(points[i]);
                    changed = true;
                }
            }

            aliveStart = newStart;
            aliveEnd = newEnd;

            if (batchArgonUpdates)
            {
                for (int i = newStart; i < newEnd; i++)
                    points[i].ApplyAnimationAt(time);
            }

            return changed;
        }

        protected override void UpdateAfterChildren()
        {
            base.UpdateAfterChildren();

            if (batchArgonUpdates || points.Count == 0)
                return;

            for (int i = 0; i < points.Count; i++)
            {
                if (points[i].Drawable is not ArgonFollowPoint { LoadState: LoadState.Loaded })
                    return;
            }

            batchArgonUpdates = true;
        }

        private int firstPointEndingAfter(double time)
        {
            int low = 0;
            int high = points.Count;

            while (low < high)
            {
                int middle = (low + high) / 2;

                if (points[middle].LifetimeEnd <= time)
                    low = middle + 1;
                else
                    high = middle;
            }

            return low;
        }

        private int firstPointStartingAfter(double time)
        {
            int low = 0;
            int high = points.Count;

            while (low < high)
            {
                int middle = (low + high) / 2;

                if (points[middle].LifetimeStart <= time)
                    low = middle + 1;
                else
                    high = middle;
            }

            return low;
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            if (skinSource != null)
                skinSource.SourceChanged -= onSkinChanged;
        }

        /// <summary>
        /// Computes the fade time of follow point positioned between two hitobjects.
        /// </summary>
        /// <param name="start">The first <see cref="OsuHitObject"/>, where follow points should originate from.</param>
        /// <param name="end">The second <see cref="OsuHitObject"/>, which follow points should target.</param>
        /// <param name="fraction">The fractional distance along <paramref name="start"/> and <paramref name="end"/> at which the follow point is to be located.</param>
        /// <param name="fadeInTime">The fade-in time of the follow point/</param>
        /// <param name="fadeOutTime">The fade-out time of the follow point.</param>
        public static void GetFadeTimes(OsuHitObject start, OsuHitObject end, float fraction, out double fadeInTime, out double fadeOutTime)
        {
            double startTime = start.GetEndTime();
            double duration = end.StartTime - startTime;

            // Preempt time can go below 800ms. Normally, this is achieved via the DT mod which uniformly speeds up all animations game wide regardless of AR.
            // This uniform speedup is hard to match 1:1, however we can at least make AR>10 (via mods) feel good by extending the upper linear preempt function (see: OsuHitObject).
            // Note that this doesn't exactly match the AR>10 visuals as they're classically known, but it feels good.
            double preempt = PREEMPT * Math.Min(1, start.TimePreempt / OsuHitObject.PREEMPT_MIN);

            fadeOutTime = startTime + fraction * duration;
            fadeInTime = fadeOutTime - preempt;
        }
    }
}
