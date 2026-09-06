// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Objects;

namespace osu.Game.Rulesets.UI
{
    public partial class JudgementContainer<T> : Container<T>
        where T : DrawableJudgement
    {
        private readonly Dictionary<HitObject, T> judgementsByHitObject = new Dictionary<HitObject, T>();

        public override void Add(T judgement)
        {
            ArgumentNullException.ThrowIfNull(judgement);

            // remove any existing judgements for the judged object.
            // this can be the case when rewinding.
            //
            // This used to scan every live judgement for every hit. Dense maps can have close to a thousand
            // judgements alive, making a burst of hits quadratic even though forward play almost never finds
            // a duplicate. Keep the same rewind behaviour with a direct lookup instead.
            if (judgement.JudgedHitObject is HitObject hitObject)
            {
                if (judgementsByHitObject.TryGetValue(hitObject, out T? existing))
                    Remove(existing, false);

                judgementsByHitObject[hitObject] = judgement;
            }
            else
            {
                // Preserve the previous behaviour for unassociated judgements.
                RemoveAll(c => c.JudgedHitObject == null, false);
            }

            base.Add(judgement);
        }

        protected override bool RemoveInternal(Drawable drawable, bool disposeImmediately)
        {
            // Remove the index before PoolableDrawable is detached and clears JudgedHitObject.
            if (drawable is T judgement
                && judgement.JudgedHitObject is HitObject hitObject
                && judgementsByHitObject.TryGetValue(hitObject, out T? indexed)
                && ReferenceEquals(indexed, judgement))
                judgementsByHitObject.Remove(hitObject);

            return base.RemoveInternal(drawable, disposeImmediately);
        }

        protected override void ClearInternal(bool disposeChildren = true)
        {
            judgementsByHitObject.Clear();
            base.ClearInternal(disposeChildren);
        }
    }
}
