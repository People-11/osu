// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Utils;
using osu.Game.Configuration;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Objects.Drawables;
using osu.Game.Rulesets.Osu.Skinning.Argon;
using osu.Game.Rulesets.Scoring;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Rulesets.Osu.Objects.Drawables
{
    public partial class DrawableOsuJudgement : DrawableJudgement
    {
        internal Color4 AccentColour { get; private set; }

        internal SkinnableLighting Lighting { get; private set; } = null!;

        [Resolved]
        private OsuConfigManager config { get; set; } = null!;

        private Vector2? screenSpacePosition;

        private bool hitLightingEnabled;
        private bool hitLightingAnimationApplied;
        private bool hitLightingAnimationFinished;
        private double hitLightingAnimationStartTime;

        [BackgroundDependencyLoader]
        private void load()
        {
            AddInternal(Lighting = new SkinnableLighting
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Blending = BlendingParameters.Additive,
                Depth = float.MaxValue,
                Alpha = 0
            });
        }

        public override void Apply(JudgementResult result, DrawableHitObject? judgedObject)
        {
            base.Apply(result, judgedObject);

            if (judgedObject is not DrawableOsuHitObject osuObject)
                return;

            AccentColour = osuObject.AccentColour.Value;

            switch (osuObject)
            {
                case DrawableSlider slider:
                    screenSpacePosition = slider.TailCircle.ToScreenSpace(slider.TailCircle.OriginPosition);
                    break;

                default:
                    screenSpacePosition = osuObject.ToScreenSpace(osuObject.OriginPosition);
                    break;
            }

            Scale = new Vector2(osuObject.HitObject.Scale);
        }

        protected override void PrepareForUse()
        {
            hitLightingEnabled = false;
            hitLightingAnimationApplied = false;
            hitLightingAnimationFinished = false;

            base.PrepareForUse();

            Lighting.ResetAnimation();
            Lighting.SetColourFrom(this, Result);

            if (screenSpacePosition != null)
                Position = Parent!.ToLocalSpace(screenSpacePosition.Value);

            // Argon's text keeps scaling for 1800ms, but its parent is completely transparent after 800ms.
            // Keeping hundreds of invisible judgements alive for that tail needlessly updates their whole
            // skinnable/proxy hierarchy. Do not affect custom skins, and retain the longer independent hit
            // lighting animation when enabled.
            if (JudgementBody?.Drawable is ArgonJudgementPiece argonJudgement && Result != null)
            {
                double visibleEnd = Result.TimeAbsolute + argonJudgement.VisibleDuration;

                if (hitLightingEnabled)
                    visibleEnd = double.Max(visibleEnd, hitLightingAnimationStartTime + 1400);

                LifetimeEnd = double.Min(LifetimeEnd, visibleEnd);
            }
        }

        protected override void ApplyHitAnimations()
        {
            hitLightingEnabled = config.Get<bool>(OsuSetting.HitLighting);

            Lighting.Alpha = 0;

            if (hitLightingEnabled)
            {
                // todo: this animation changes slightly based on new/old legacy skin versions.
                hitLightingAnimationStartTime = TransformStartTime;
                hitLightingAnimationApplied = true;
                applyHitLightingAt(hitLightingAnimationStartTime);

                // extend the lifetime to cover lighting fade
                LifetimeEnd = hitLightingAnimationStartTime + 1400;
            }

            base.ApplyHitAnimations();
        }

        public override bool UpdateSubTree()
        {
            if (hitLightingAnimationApplied)
                applyHitLightingAt(Time.Current);

            return base.UpdateSubTree();
        }

        private void applyHitLightingAt(double time)
        {
            double elapsed = time - hitLightingAnimationStartTime;

            if (elapsed >= 1400)
            {
                if (hitLightingAnimationFinished)
                    return;

                hitLightingAnimationFinished = true;
            }
            else
                hitLightingAnimationFinished = false;

            float scaleProgress = (float)Interpolation.ApplyEasing(Easing.Out, Math.Clamp(elapsed / 600, 0, 1));
            Lighting.Scale = new Vector2(0.8f + 0.4f * scaleProgress);

            if (elapsed < 200)
                Lighting.Alpha = (float)Math.Clamp(elapsed / 200, 0, 1);
            else if (elapsed <= 400)
                Lighting.Alpha = 1;
            else
                Lighting.Alpha = (float)(1 - Math.Clamp((elapsed - 400) / 1000, 0, 1));
        }

        protected override Drawable CreateDefaultJudgement(HitResult result) =>
            // Tick hits don't show a judgement by default
            result.IsHit() && result.IsTick() ? Empty() : new OsuJudgementPiece(result);

        private partial class OsuJudgementPiece : DefaultJudgementPiece
        {
            public OsuJudgementPiece(HitResult result)
                : base(result)
            {
            }

            public override void PlayAnimation()
            {
                if (Result != HitResult.Miss)
                {
                    JudgementText
                        .ScaleTo(new Vector2(0.8f, 1))
                        .ScaleTo(new Vector2(1.2f, 1), 1800, Easing.OutQuint);
                }

                base.PlayAnimation();
            }
        }
    }
}
