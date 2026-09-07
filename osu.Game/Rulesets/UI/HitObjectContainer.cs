// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.TypeExtensions;
using osu.Framework.Graphics;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Objects.Drawables;
using osu.Game.Rulesets.Objects.Pooling;

namespace osu.Game.Rulesets.UI
{
    public partial class HitObjectContainer : PooledDrawableWithLifetimeContainer<HitObjectLifetimeEntry, DrawableHitObject>, IHitObjectContainer
    {
        public IEnumerable<DrawableHitObject> Objects => InternalChildren.Cast<DrawableHitObject>().OrderBy(h => h.HitObject.StartTime);

        public IEnumerable<DrawableHitObject> AliveObjects => aliveObjects ??= AliveEntries.Values.OrderBy(h => h.HitObject.StartTime).ToArray();

        public ulong StateVersion { get; private set; }

        /// <summary>
        /// Invoked when a <see cref="DrawableHitObject"/> is judged.
        /// </summary>
        public event Action<DrawableHitObject, JudgementResult> NewResult;

        /// <summary>
        /// Invoked when a <see cref="HitObject"/> becomes used by a <see cref="DrawableHitObject"/>.
        /// </summary>
        /// <remarks>
        /// If this <see cref="HitObjectContainer"/> uses pooled objects, this represents the time when the <see cref="HitObject"/>s become alive.
        /// </remarks>
        internal event Action<HitObject> HitObjectUsageBegan;

        /// <summary>
        /// Invoked when a <see cref="HitObject"/> becomes unused by a <see cref="DrawableHitObject"/>.
        /// </summary>
        /// <remarks>
        /// If this <see cref="HitObjectContainer"/> uses pooled objects, this represents the time when the <see cref="HitObject"/>s become dead.
        /// </remarks>
        internal event Action<HitObject> HitObjectUsageFinished;

        private readonly Dictionary<DrawableHitObject, IBindable> startTimeMap = new Dictionary<DrawableHitObject, IBindable>();

        private DrawableHitObject[] aliveObjects;

        private readonly Dictionary<HitObjectLifetimeEntry, DrawableHitObject> nonPooledDrawableMap = new Dictionary<HitObjectLifetimeEntry, DrawableHitObject>();

        [Resolved(CanBeNull = true)]
        private IPooledHitObjectProvider pooledObjectProvider { get; set; }

        public HitObjectContainer()
        {
            RelativeSizeAxes = Axes.Both;
        }

        // Entry lifetime changes are the sole source of child lifetime changes in gameplay. The pooled lifetime
        // manager has already evaluated every active entry, so a second full scan is redundant on stable frames.
        protected override bool RequiresContinuousChildLifeChecks => false;

        protected override void LoadAsyncComplete()
        {
            base.LoadAsyncComplete();

            // Application of hitobjects during load() may have changed their start times, so ensure the correct sorting order.
            SortInternal();
        }

        #region Pooling support

        public override bool Remove(HitObjectLifetimeEntry entry)
        {
            if (!base.Remove(entry)) return false;

            // This logic is not in `Remove(DrawableHitObject)` because a non-pooled drawable may be removed by specifying its entry.
            if (nonPooledDrawableMap.Remove(entry, out var drawable))
                removeDrawable(drawable);

            return true;
        }

        protected sealed override DrawableHitObject GetDrawable(HitObjectLifetimeEntry entry)
        {
            if (nonPooledDrawableMap.TryGetValue(entry, out var drawable))
                return drawable;

            return pooledObjectProvider?.GetPooledDrawableRepresentation(entry.HitObject, null) ??
                   throw new InvalidOperationException($"A drawable representation could not be retrieved for hitobject type: {entry.HitObject.GetType().ReadableName()}.");
        }

        protected override void AddDrawable(HitObjectLifetimeEntry entry, DrawableHitObject drawable)
        {
            if (nonPooledDrawableMap.ContainsKey(entry)) return;

            addDrawable(drawable);
            HitObjectUsageBegan?.Invoke(entry.HitObject);
        }

        protected override void RemoveDrawable(HitObjectLifetimeEntry entry, DrawableHitObject drawable)
        {
            drawable.OnKilled();
            if (nonPooledDrawableMap.ContainsKey(entry)) return;

            removeDrawable(drawable);
            HitObjectUsageFinished?.Invoke(entry.HitObject);
        }

        private void addDrawable(DrawableHitObject drawable)
        {
            drawable.OnNewResult += onNewResult;
            drawable.OnRevertResult += onRevertResult;
            drawable.HitObject.DefaultsApplied += onDefaultsApplied;
            invalidateAliveObjects();

            bindStartTime(drawable);
            AddInternal(drawable);
        }

        private void removeDrawable(DrawableHitObject drawable)
        {
            drawable.OnNewResult -= onNewResult;
            drawable.OnRevertResult -= onRevertResult;
            drawable.HitObject.DefaultsApplied -= onDefaultsApplied;
            invalidateAliveObjects();

            unbindStartTime(drawable);

            RemoveInternal(drawable, false);
        }

        #endregion

        #region Non-pooling support

        public virtual void Add(DrawableHitObject drawable)
        {
            if (drawable.Entry == null)
                throw new InvalidOperationException($"May not add a {nameof(DrawableHitObject)} without {nameof(HitObject)} associated");

            nonPooledDrawableMap.Add(drawable.Entry, drawable);
            addDrawable(drawable);
            Add(drawable.Entry);
        }

        public virtual bool Remove(DrawableHitObject drawable)
        {
            if (drawable.Entry == null)
                return false;

            return Remove(drawable.Entry);
        }

        public int IndexOf(DrawableHitObject hitObject) => IndexOfInternal(hitObject);

        #endregion

        private void onNewResult(DrawableHitObject d, JudgementResult r) => NewResult?.Invoke(d, r);

        private void onRevertResult(DrawableHitObject d, JudgementResult r) => invalidateAliveObjects();

        private void onDefaultsApplied(HitObject h) => invalidateAliveObjects();

        private void invalidateAliveObjects()
        {
            aliveObjects = null;
            StateVersion++;
        }

        #region Comparator + StartTime tracking

        private void bindStartTime(DrawableHitObject hitObject)
        {
            var bindable = hitObject.StartTimeBindable.GetBoundCopy();

            bindable.BindValueChanged(_ =>
            {
                invalidateAliveObjects();

                if (LoadState >= LoadState.Ready)
                    SortInternal();
            });

            startTimeMap[hitObject] = bindable;
        }

        private void unbindStartTime(DrawableHitObject hitObject)
        {
            startTimeMap[hitObject].UnbindAll();
            startTimeMap.Remove(hitObject);
        }

        private void unbindAllStartTimes()
        {
            foreach (var kvp in startTimeMap)
                kvp.Value.UnbindAll();
            startTimeMap.Clear();
        }

        protected override int Compare(Drawable x, Drawable y)
        {
            if (!(x is DrawableHitObject xObj) || !(y is DrawableHitObject yObj))
                return base.Compare(x, y);

            // Put earlier hitobjects towards the end of the list, so they handle input first
            int i = yObj.HitObject.StartTime.CompareTo(xObj.HitObject.StartTime);
            return i == 0 ? CompareReverseChildID(x, y) : i;
        }

        #endregion

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);
            aliveObjects = null;
            unbindAllStartTimes();
        }
    }
}
