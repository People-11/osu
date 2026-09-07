// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.Color4Extensions;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Colour;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Effects;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Utils;
using osu.Game.Configuration;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Rulesets.Objects.Drawables;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Osu.Objects.Drawables;
using osu.Game.Rulesets.Osu.Skinning.Default;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Rulesets.Osu.Skinning.Argon
{
    public partial class ArgonMainCirclePiece : CompositeDrawable
    {
        public const float BORDER_THICKNESS = (OsuHitObject.OBJECT_RADIUS * 2) * (2f / 58);

        public const float GRADIENT_THICKNESS = BORDER_THICKNESS * 2.5f;

        public const float OUTER_GRADIENT_SIZE = (OsuHitObject.OBJECT_RADIUS * 2) - BORDER_THICKNESS * 4;

        public const float INNER_GRADIENT_SIZE = OUTER_GRADIENT_SIZE - GRADIENT_THICKNESS * 2;
        public const float INNER_FILL_SIZE = INNER_GRADIENT_SIZE - GRADIENT_THICKNESS * 2;

        // `FastCircle` renders through a dedicated shader instead of a masking container, so these no longer
        // break the draw batch (masking state changes were ~78% of all draw calls during dense gameplay) and
        // each is one drawable instead of two.
        private readonly FastCircle outerFill;
        private readonly FastCircle outerGradient;
        private readonly FastCircle innerGradient;
        private readonly FastCircle innerFill;

        private readonly FastRing border;
        private readonly OsuSpriteText number;

        private readonly IBindable<Color4> accentColour = new Bindable<Color4>();
        private readonly IBindable<int> indexInCurrentCombo = new Bindable<int>();
        private readonly FlashPiece flash;
        private readonly Container kiaiContainer;

        private bool requireFullChildUpdate = true;
        private readonly bool withOuterFill;
        private ColourInfo baseOuterGradientColour;
        private ColourInfo hitBorderEndColour;
        private double hitAnimationStartTime;
        private bool hitAnimationApplied;
        private bool hitAnimationFinished;
        private bool hitAnimationTailApplied;
        private bool hitLightingEnabled;

        private Bindable<bool> configHitLighting = null!;

        private static readonly Vector2 circle_size = OsuHitObject.OBJECT_DIMENSIONS;

        [Resolved]
        private DrawableHitObject drawableObject { get; set; } = null!;

        public ArgonMainCirclePiece(bool withOuterFill)
        {
            this.withOuterFill = withOuterFill;

            Size = circle_size;

            Anchor = Anchor.Centre;
            Origin = Anchor.Centre;

            InternalChildren = new Drawable[]
            {
                outerFill = new FastCircle // renders dark fill
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    // Slightly inset to prevent bleeding outside the ring
                    Size = circle_size - new Vector2(1),
                    Alpha = withOuterFill ? 1 : 0,
                },
                outerGradient = new FastCircle // renders the outer bright gradient
                {
                    Size = new Vector2(OUTER_GRADIENT_SIZE),
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                },
                innerGradient = new FastCircle // renders the inner bright gradient
                {
                    Size = new Vector2(INNER_GRADIENT_SIZE),
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                },
                innerFill = new FastCircle // renders the inner dark fill
                {
                    Size = new Vector2(INNER_FILL_SIZE),
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                },
                kiaiContainer = new KiaiFlash
                {
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Size = circle_size,
                    Child = new FastCircle
                    {
                        RelativeSizeAxes = Axes.Both,
                        // Replaces the box `KiaiFlash` creates for itself, so it must start hidden as that one
                        // does. `KiaiFlash` only ever fades this in on a kiai beat.
                        Alpha = 0,
                    }
                },
                number = new OsuSpriteText
                {
                    Font = OsuFont.Default.With(size: 52, weight: FontWeight.Bold),
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                    Y = -2,
                    Text = @"1",
                },
                flash = new FlashPiece(),
                border = new FastRing
                {
                    Thickness = BORDER_THICKNESS,
                    Size = circle_size,
                    Anchor = Anchor.Centre,
                    Origin = Anchor.Centre,
                },
            };
        }

        [BackgroundDependencyLoader]
        private void load(OsuConfigManager config)
        {
            var drawableOsuObject = (DrawableOsuHitObject)drawableObject;

            accentColour.BindTo(drawableObject.AccentColour);
            indexInCurrentCombo.BindTo(drawableOsuObject.IndexInCurrentComboBindable);

            configHitLighting = config.GetBindable<bool>(OsuSetting.HitLighting);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            indexInCurrentCombo.BindValueChanged(index =>
            {
                number.Text = (index.NewValue + 1).ToString();
                requireFullChildUpdate = true;
            }, true);

            accentColour.BindValueChanged(colour =>
            {
                // A colour transform is applied.
                // Without removing transforms first, when it is rewound it may apply an old colour.
                outerGradient.ClearTransforms(targetMember: nameof(Colour));
                outerGradient.Colour = baseOuterGradientColour = ColourInfo.GradientVertical(colour.NewValue, colour.NewValue.Darken(0.1f));

                kiaiContainer.Colour = colour.NewValue;
                outerFill.Colour = innerFill.Colour = colour.NewValue.Darken(4);
                innerGradient.Colour = ColourInfo.GradientVertical(colour.NewValue.Darken(0.5f), colour.NewValue.Darken(0.6f));
                flash.Colour = colour.NewValue;
                flash.RefreshEdgeEffect();

                // Accent colour may be changed many times during a paused gameplay state.
                // Schedule the change to avoid transforms piling up.
                Scheduler.AddOnce(() =>
                {
                    requireFullChildUpdate = true;

                    ApplyTransformsAt(double.MinValue, true);
                    ClearTransformsAfter(double.MinValue, true);

                    updateStateTransforms(drawableObject, drawableObject.State.Value);
                });
            }, true);

            drawableObject.ApplyCustomUpdateState += updateStateTransforms;
        }

        private void updateStateTransforms(DrawableHitObject drawableHitObject, ArmedState state)
        {
            hitAnimationApplied = state == ArmedState.Hit;
            hitAnimationFinished = false;
            hitAnimationTailApplied = false;

            if (!hitAnimationApplied)
            {
                applyIdleState();
                return;
            }

            hitAnimationStartTime = drawableObject.HitStateUpdateTime;
            hitLightingEnabled = configHitLighting.Value;
            hitBorderEndColour = ColourInfo.GradientVertical(accentColour.Value.Opacity(0.5f), accentColour.Value.Opacity(0));
            flash.HitLighting = hitLightingEnabled;
            flash.RefreshEdgeEffect();
            applyHitAnimationAt(hitAnimationStartTime);
        }

        protected override bool RequiresChildrenUpdate => requireFullChildUpdate && base.RequiresChildrenUpdate;

        public override bool UpdateSubTree()
        {
            if (hitAnimationApplied)
                applyHitAnimationAt(Time.Current);

            bool updateAllChildren = requireFullChildUpdate;
            bool result = base.UpdateSubTree();

            // Idle circle layers have no transforms or update logic. The beat-synchronised flash is the sole
            // exception, so keep updating only that branch instead of walking every static layer on every frame.
            if (!updateAllChildren && IsPresent)
                kiaiContainer.UpdateSubTree();

            return result;
        }

        private void applyIdleState()
        {
            Alpha = 1;
            outerFill.Alpha = withOuterFill ? 1 : 0;
            outerGradient.Alpha = 1;
            outerGradient.Size = new Vector2(OUTER_GRADIENT_SIZE);
            outerGradient.Colour = baseOuterGradientColour;
            innerGradient.Alpha = 1;
            innerFill.Alpha = 1;
            number.Alpha = 1;
            flash.Alpha = 0;
            border.Size = circle_size;
            border.Colour = Color4.White;
            kiaiContainer.Size = circle_size;
            kiaiContainer.Alpha = 1;
        }

        private void applyHitAnimationAt(double time)
        {
            double elapsed = time - hitAnimationStartTime;

            if (elapsed >= 800)
            {
                if (hitAnimationFinished)
                    return;

                hitAnimationFinished = true;
            }
            else
                hitAnimationFinished = false;

            const double flash_in_duration = 150;
            const double resize_duration = 400;
            const double outer_delay = flash_in_duration / 12;
            const float shrink_size = 0.8f;

            // After the outer gradient finishes resizing, only the border colour and parent fade are still moving.
            // Assign the completed child state once instead of repeating every finished interpolation until expiry.
            if (elapsed >= outer_delay + resize_duration)
            {
                if (!hitAnimationTailApplied)
                {
                    hitAnimationTailApplied = true;
                    number.Alpha = 0;
                    outerFill.Alpha = 0;
                    innerFill.Alpha = 0;
                    innerGradient.Alpha = 0;
                    border.Size = circle_size * shrink_size + new Vector2(border.Thickness);
                    kiaiContainer.Size = circle_size * shrink_size;
                    kiaiContainer.Alpha = 0;
                    outerGradient.Size = new Vector2(OUTER_GRADIENT_SIZE * shrink_size);
                    outerGradient.Colour = Color4.White;
                    outerGradient.Alpha = 0;
                    flash.Alpha = hitLightingEnabled ? 1 : 0;
                }

                border.Colour = Interpolation.ValueAt(easedProgress(elapsed, 800, Easing.None), ColourInfo.SingleColour(Color4.White), hitBorderEndColour, 0, 1);
                Alpha = valueAt(elapsed, 1, 0, hitLightingEnabled ? 800 : 640, Easing.OutQuad);
                return;
            }

            hitAnimationTailApplied = false;

            number.Alpha = valueAt(elapsed, 1, 0, flash_in_duration / 2);
            outerFill.Alpha = (withOuterFill ? 1 : 0) * valueAt(elapsed, 1, 0, flash_in_duration, Easing.OutQuint);
            innerFill.Alpha = valueAt(elapsed, 1, 0, flash_in_duration, Easing.OutQuint);
            innerGradient.Alpha = valueAt(elapsed, 1, 0, flash_in_duration, Easing.OutQuint);

            float resizeProgress = easedProgress(elapsed, resize_duration, Easing.OutElasticHalf);
            border.Size = Vector2.Lerp(circle_size, circle_size * shrink_size + new Vector2(border.Thickness), resizeProgress);
            kiaiContainer.Size = Vector2.Lerp(circle_size, circle_size * shrink_size, resizeProgress);
            kiaiContainer.Alpha = valueAt(elapsed, 1, 0, flash_in_duration, Easing.OutQuint);

            float borderColourProgress = easedProgress(elapsed, 800, Easing.None);
            border.Colour = Interpolation.ValueAt(borderColourProgress, ColourInfo.SingleColour(Color4.White), hitBorderEndColour, 0, 1);

            double outerElapsed = elapsed - outer_delay;
            outerGradient.Size = new Vector2(valueAt(outerElapsed, OUTER_GRADIENT_SIZE, OUTER_GRADIENT_SIZE * shrink_size, resize_duration, Easing.OutElasticHalf));
            outerGradient.Colour = Interpolation.ValueAt(easedProgress(outerElapsed, 80, Easing.None), baseOuterGradientColour, ColourInfo.SingleColour(Color4.White), 0, 1);
            outerGradient.Alpha = valueAt(outerElapsed - 80, 1, 0, flash_in_duration);

            flash.Alpha = elapsed <= flash_in_duration || hitLightingEnabled
                ? valueAt(elapsed, 0, 1, flash_in_duration, Easing.OutQuint)
                : valueAt(elapsed - flash_in_duration, 1, 0, flash_in_duration, Easing.OutQuint);

            Alpha = valueAt(elapsed, 1, 0, hitLightingEnabled ? 800 : 640, Easing.OutQuad);
        }

        private static float valueAt(double elapsed, float start, float end, double duration, Easing easing = Easing.None) =>
            start + (end - start) * easedProgress(elapsed, duration, easing);

        private static float easedProgress(double elapsed, double duration, Easing easing) =>
            (float)Interpolation.ApplyEasing(easing, Math.Clamp(elapsed / duration, 0, 1));

        protected override void UpdateAfterChildren()
        {
            base.UpdateAfterChildren();
            requireFullChildUpdate = false;
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);

            if (drawableObject.IsNotNull())
                drawableObject.ApplyCustomUpdateState -= updateStateTransforms;
        }

        private partial class FlashPiece : Circle
        {
            public FlashPiece()
            {
                Size = new Vector2(OsuHitObject.OBJECT_RADIUS);

                Anchor = Anchor.Centre;
                Origin = Anchor.Centre;

                Alpha = 0;
                Blending = BlendingParameters.Additive;

                // The edge effect provides the fill due to not being rendered hollow.
                Child.Alpha = 0;
                Child.AlwaysPresent = true;
            }

            public bool HitLighting { get; set; }

            private ColourInfo lastColour;
            private bool lastHitLighting;
            private bool edgeEffectApplied;

            public void RefreshEdgeEffect()
            {
                // the edge effect only depends on colour and hit lighting, both of which change rarely.
                // Refresh it at those mutation sites rather than polling every frame.
                if (edgeEffectApplied && lastColour.Equals(Colour) && lastHitLighting == HitLighting)
                    return;

                edgeEffectApplied = true;
                lastColour = Colour;
                lastHitLighting = HitLighting;

                EdgeEffect = new EdgeEffectParameters
                {
                    Type = EdgeEffectType.Glow,
                    Colour = Colour,
                    Radius = OsuHitObject.OBJECT_RADIUS * (HitLighting ? 1.2f : 0.6f),
                };
            }
        }
    }
}
