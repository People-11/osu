// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osuTK;
using osuTK.Graphics;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Effects;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Utils;
using osu.Game.Rulesets.Osu.Skinning.Argon;
using osu.Game.Skinning;

namespace osu.Game.Rulesets.Osu.Objects.Drawables.Connections
{
    /// <summary>
    /// A single follow point positioned between two adjacent <see cref="DrawableOsuHitObject"/>s.
    /// </summary>
    // Thousands of these are alive at once on dense maps, so this *is* the `SkinnableDrawable` rather than
    // wrapping one - the wrapper layer was pure overhead for zero visual benefit.
    public partial class FollowPoint : SkinnableDrawable, IAnimationTimeReference
    {
        private const float width = 8;

        private Vector2 animationStartPosition;
        private Vector2 animationEndPosition;
        private Vector2 animationStartScale;
        private Vector2 animationEndScale;
        private double animationFadeInTime;
        private double animationFadeOutTime;
        private double animationFadeDuration;
        private bool animationApplied;
        private AnimationStaticState animationStaticState;

        public override bool RemoveWhenNotAlive => false;

        public FollowPoint()
            : base(new OsuSkinComponentLookup(OsuSkinComponents.FollowPoint), _ => new CircularContainer
            {
                Masking = true,
                // Fixed size rather than auto-size: the centre-anchored 8x8 box below resolves to exactly
                // 8x8 anyway, so this is identical but skips a layout pass per follow point.
                Size = new Vector2(width),
                EdgeEffect = new EdgeEffectParameters
                {
                    Type = EdgeEffectType.Glow,
                    Colour = Color4.White.Opacity(0.2f),
                    Radius = 4,
                },
                Child = new Box
                {
                    Size = new Vector2(width),
                    Blending = BlendingParameters.Additive,
                    Origin = Anchor.Centre,
                    Anchor = Anchor.Centre,
                    Alpha = 0.5f,
                }
            })
        {
            // `SkinnableDrawable` fills its parent by default; follow points are positioned points of zero size.
            RelativeSizeAxes = Axes.None;
            Size = Vector2.Zero;

            Origin = Anchor.Centre;
        }

        public Bindable<double> AnimationStartTime { get; } = new BindableDouble();

        // The default Argon component is a static leaf. Its visual state is inherited from this drawable, so
        // traversing into it every frame cannot change anything. Custom/animated skin components keep the full path.
        protected override bool RequiresChildrenUpdate => Drawable is not ArgonFollowPoint { LoadState: LoadState.Loaded } && base.RequiresChildrenUpdate;

        /// <summary>
        /// Applies the complete follow-point animation from absolute times. This avoids maintaining separate alpha,
        /// scale and position transform tracks for every visible follow point.
        /// </summary>
        internal void ApplyAnimation(Vector2 startPosition, Vector2 endPosition, Vector2 startScale, Vector2 endScale, float rotation,
                                     double fadeInTime, double fadeOutTime, double fadeDuration)
        {
            ClearTransforms();

            animationStartPosition = startPosition;
            animationEndPosition = endPosition;
            animationStartScale = startScale;
            animationEndScale = endScale;
            animationFadeInTime = fadeInTime;
            animationFadeOutTime = fadeOutTime;
            animationFadeDuration = fadeDuration;
            animationApplied = true;

            Rotation = rotation;
            AnimationStartTime.Value = fadeInTime;
            LifetimeStart = fadeInTime;
            LifetimeEnd = fadeOutTime + fadeDuration;
            ApplyAnimationAt(fadeInTime);
        }

        public override bool UpdateSubTree()
        {
            // Evaluate before the base presence check, as the point starts at zero alpha.
            if (animationApplied)
                ApplyAnimationAt(Time.Current);

            return base.UpdateSubTree();
        }

        internal void ApplyAnimationAt(double time)
        {
            AnimationStaticState staticState = AnimationStaticState.None;

            if (time <= animationFadeInTime)
                staticState = AnimationStaticState.Before;
            else if (time >= animationFadeInTime + animationFadeDuration && time <= animationFadeOutTime)
                staticState = AnimationStaticState.Steady;
            else if (time >= Math.Max(animationFadeInTime, animationFadeOutTime) + animationFadeDuration)
                staticState = AnimationStaticState.After;

            if (staticState != AnimationStaticState.None && staticState == animationStaticState)
                return;

            animationStaticState = staticState;

            double appearProgress = animationFadeDuration == 0 ? 1 : Math.Clamp((time - animationFadeInTime) / animationFadeDuration, 0, 1);
            float easedProgress = (float)Interpolation.ApplyEasing(Easing.Out, appearProgress);

            Position = animationStartPosition + (animationEndPosition - animationStartPosition) * easedProgress;
            Scale = animationStartScale + (animationEndScale - animationStartScale) * easedProgress;

            double fadeInAlpha = appearProgress;

            if (time <= animationFadeOutTime)
            {
                Alpha = (float)fadeInAlpha;
                return;
            }

            double fadeOutStartAlpha = animationFadeDuration == 0
                ? 1
                : Math.Clamp((animationFadeOutTime - animationFadeInTime) / animationFadeDuration, 0, 1);
            double fadeOutProgress = animationFadeDuration == 0 ? 1 : Math.Clamp((time - animationFadeOutTime) / animationFadeDuration, 0, 1);
            Alpha = (float)(fadeOutStartAlpha * (1 - fadeOutProgress));
        }

        private enum AnimationStaticState
        {
            None,
            Before,
            Steady,
            After
        }
    }
}
