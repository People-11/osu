// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Objects.Drawables;
using osu.Game.Rulesets.Osu.Objects.Drawables;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.UI;

namespace osu.Game.Rulesets.Osu.UI
{
    /// <summary>
    /// Ensures that <see cref="HitObject"/>s are hit in-order of their start times. Affectionately known as "note lock".
    /// If a <see cref="HitObject"/> is hit out of order:
    /// <list type="number">
    /// <item><description>The hit is blocked if it occurred earlier than the previous <see cref="HitObject"/>'s start time.</description></item>
    /// <item><description>The hit causes all previous <see cref="HitObject"/>s to missed otherwise.</description></item>
    /// </list>
    /// </summary>
    public class StartTimeOrderedHitPolicy : IHitPolicy
    {
        private IHitObjectContainer? hitObjectContainer;

        public IHitObjectContainer? HitObjectContainer
        {
            get => hitObjectContainer;
            set
            {
                hitObjectContainer = value;
                blockingObjectVersion = ulong.MaxValue;
                clearedVersion = ulong.MaxValue;
            }
        }

        private ulong blockingObjectVersion = ulong.MaxValue;
        private double blockingObjectTargetTime;
        private DrawableHitObject? blockingObject;

        private ulong clearedVersion = ulong.MaxValue;
        private double clearedTargetTime;

        private DrawableHitObject? lastCheckedObject;
        private double lastCheckedTime;
        private HitResult lastCheckedResult;
        private ClickAction lastCheckedAction;

        public ClickAction CheckHittable(DrawableHitObject hitObject, double time, HitResult result)
        {
            if (HitObjectContainer == null)
                throw new InvalidOperationException($"{nameof(HitObjectContainer)} should be set before {nameof(CheckHittable)} is called.");

            var blockingObject = getBlockingObject(hitObject.HitObject.StartTime);

            ClickAction action;

            if (blockingObject != null)
            {
                // A hit is disallowed if:
                // 1. The last blocking hitobject has not yet been judged.
                // 2. The current time is before the last hitobject's start time.
                //
                // Hits at exactly the same time as the blocking hitobject are allowed for maps that contain simultaneous hitobjects (e.g. /b/372245).
                if (!blockingObject.Judged && time < blockingObject.HitObject.StartTime)
                    action = ClickAction.Shake;
                else
                    action = result == HitResult.None ? ClickAction.Shake : ClickAction.Hit;
            }
            else
                // Generally when the user has hit way too early.
                action = result == HitResult.None ? ClickAction.Shake : ClickAction.Hit;

            lastCheckedObject = hitObject;
            lastCheckedTime = time;
            lastCheckedResult = result;
            lastCheckedAction = action;
            return action;
        }

        public void HandleHit(DrawableHitObject hitObject)
        {
            if (HitObjectContainer == null)
                throw new InvalidOperationException($"{nameof(HitObjectContainer)} should be set before {nameof(HandleHit)} is called.");

            // Hitobjects which themselves don't block future hitobjects don't cause misses (e.g. slider ticks, spinners).
            if (!hitObjectCanBlockFutureHits(hitObject))
                return;

            double hitTime = hitObject.HitObject.StartTime + hitObject.Result.TimeOffset;
            bool alreadyChecked = ReferenceEquals(lastCheckedObject, hitObject)
                                  && lastCheckedTime == hitTime
                                  && lastCheckedResult == hitObject.Result.Type;

            ClickAction action = alreadyChecked ? lastCheckedAction : CheckHittable(hitObject, hitTime, hitObject.Result.Type);
            lastCheckedObject = null;

            if (action != ClickAction.Hit)
                throw new InvalidOperationException($"A {hitObject} was hit before it became hittable!");

            if (clearedVersion == HitObjectContainer.StateVersion && clearedTargetTime == hitObject.HitObject.StartTime)
                return;

            // Miss all hitobjects prior to the hit one.
            foreach (var obj in enumerateHitObjectsUpTo(hitObject.HitObject.StartTime))
            {
                if (obj.Judged)
                    continue;

                if (hitObjectCanBlockFutureHits(obj))
                    ((DrawableOsuHitObject)obj).MissForcefully();
            }

            clearedVersion = HitObjectContainer.StateVersion;
            clearedTargetTime = hitObject.HitObject.StartTime;
        }

        /// <summary>
        /// Whether a <see cref="HitObject"/> blocks hits on future <see cref="HitObject"/>s until its start time is reached.
        /// </summary>
        /// <param name="hitObject">The <see cref="HitObject"/> to test.</param>
        private static bool hitObjectCanBlockFutureHits(DrawableHitObject hitObject)
            => hitObject is DrawableHitCircle;

        private DrawableHitObject? getBlockingObject(double targetTime)
        {
            if (blockingObjectVersion == HitObjectContainer!.StateVersion && blockingObjectTargetTime == targetTime)
                return blockingObject;

            blockingObject = null;

            foreach (var obj in enumerateHitObjectsUpTo(targetTime))
            {
                if (hitObjectCanBlockFutureHits(obj))
                    blockingObject = obj;
            }

            blockingObjectVersion = HitObjectContainer.StateVersion;
            blockingObjectTargetTime = targetTime;
            return blockingObject;
        }

        private IEnumerable<DrawableHitObject> enumerateHitObjectsUpTo(double targetTime)
        {
            foreach (var obj in HitObjectContainer!.AliveObjects)
            {
                if (obj.HitObject.StartTime >= targetTime)
                    yield break;

                yield return obj;

                foreach (var nestedObj in obj.NestedHitObjects)
                {
                    if (nestedObj.HitObject.StartTime >= targetTime)
                        break;

                    yield return nestedObj;
                }
            }
        }
    }
}
