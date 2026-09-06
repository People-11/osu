// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Graphics.Shaders;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Utils;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Osu.Skinning.Default;
using osu.Game.Rulesets.Scoring;
using osuTK;

namespace osu.Game.Rulesets.Osu.Skinning.Argon
{
    public partial class ArgonJudgementPiece : TextJudgementPiece, IAnimatableJudgement
    {
        private RingExplosion? ringExplosion;

        private double animationStartTime;
        private bool animationApplied;
        private bool requireFullChildUpdate = true;
        private bool animationFinished;

        /// <summary>
        /// The point after which this piece is fully transparent.
        /// </summary>
        /// <remarks>
        /// Hit judgements retain a scale transform until 1800ms, but the whole piece has faded out by 800ms.
        /// The remaining transform must not keep the pooled drawable in the update tree.
        /// </remarks>
        public double VisibleDuration => Result == HitResult.IgnoreMiss || Result == HitResult.LargeTickMiss ? 400 : 800;

        [Resolved]
        private OsuColour colours { get; set; } = null!;

        public ArgonJudgementPiece(HitResult result)
            : base(result)
        {
            AutoSizeAxes = Axes.Both;

            Origin = Anchor.Centre;
        }

        [BackgroundDependencyLoader]
        private void load()
        {
            if (Result.IsHit())
            {
                AddInternal(ringExplosion = new RingExplosion(Result)
                {
                    Colour = colours.ForHitResult(Result),
                });
            }
        }

        protected override SpriteText CreateJudgementText() =>
            new OsuSpriteText
            {
                Anchor = Anchor.Centre,
                Origin = Anchor.Centre,
                Blending = BlendingParameters.Additive,
                Spacing = new Vector2(5, 0),
                Font = OsuFont.Default.With(size: 20, weight: FontWeight.Bold),
            };

        /// <summary>
        /// Plays the default animation for this judgement piece.
        /// </summary>
        /// <remarks>
        /// The base implementation only handles fade (for all result types) and misses.
        /// Individual rulesets are recommended to implement their appropriate hit animations.
        /// </remarks>
        public virtual void PlayAnimation()
        {
            animationStartTime = TransformStartTime;
            animationApplied = true;
            animationFinished = false;

            ringExplosion?.PlayAnimation();
            applyAnimationAt(animationStartTime);
        }

        public override bool UpdateSubTree()
        {
            if (animationApplied)
                applyAnimationAt(Time.Current);

            return base.UpdateSubTree();
        }

        protected override bool RequiresChildrenUpdate => requireFullChildUpdate && base.RequiresChildrenUpdate;

        protected override void UpdateAfterChildren()
        {
            base.UpdateAfterChildren();
            requireFullChildUpdate = false;
        }

        private void applyAnimationAt(double time)
        {
            double elapsed = time - animationStartTime;

            if (elapsed >= VisibleDuration)
            {
                if (animationFinished)
                    return;

                animationFinished = true;
            }
            else
                animationFinished = false;

            if (Result == HitResult.IgnoreMiss || Result == HitResult.LargeTickMiss)
            {
                Rotation = -45;
                Scale = new Vector2(valueAt(elapsed, 1.6f, 1.2f, 100, Easing.In));
                Position = Vector2.Zero;
                Alpha = valueAt(elapsed, 1, 0, 400);
            }
            else if (Result.IsMiss())
            {
                float movement = easedProgress(elapsed, 800, Easing.InQuint);

                Alpha = valueAt(elapsed, 1, 0, 800);
                Scale = new Vector2(valueAt(elapsed, 1.6f, 1, 100, Easing.In));
                Position = new Vector2(0, 100 * movement);
                Rotation = 40 * movement;
            }
            else
            {
                Alpha = valueAt(elapsed, 1, 0, 800);
                Scale = Vector2.One;
                Position = Vector2.Zero;
                Rotation = 0;

                JudgementText.Alpha = easedProgress(elapsed, 300, Easing.OutQuint);
                JudgementText.Scale = new Vector2(valueAt(elapsed, 1, 1.2f, 1800, Easing.OutQuint));
            }

            ringExplosion?.ApplyAnimationAt(elapsed);
        }

        private static float valueAt(double elapsed, float start, float end, double duration, Easing easing = Easing.None) =>
            start + (end - start) * easedProgress(elapsed, duration, easing);

        private static float easedProgress(double elapsed, double duration, Easing easing) =>
            (float)Interpolation.ApplyEasing(easing, Math.Clamp(elapsed / duration, 0, 1));

        public Drawable? GetAboveHitObjectsProxiedContent() => JudgementText.CreateProxy();

        /// <summary>
        /// The expanding ring burst played on a hit.
        /// </summary>
        /// <remarks>
        /// Every ring is the same shape driven by one animation, so this is a single leaf drawable whose draw
        /// node emits all the ring quads, rather than N child drawables each carrying their own pair of
        /// transforms. On dense maps hundreds of these are alive at once, so that was thousands of drawables
        /// and thousands of transform applications per frame.
        /// </remarks>
        private partial class RingExplosion : Drawable
        {
            private const float thickness = 4;
            private const float small_size = 9;
            private const float large_size = 14;

            private readonly float travel = 52;
            private readonly int countSmall;
            private readonly int countLarge;

            private IShader shader = null!;

            private readonly List<(Vector2 direction, float distance, float size)> rings = new List<(Vector2, float, float)>();

            private float progress;

            /// <summary>
            /// Outward travel of every ring, 0 to 1. Transformed by <see cref="PlayAnimation"/>.
            /// </summary>
            public float Progress
            {
                get => progress;
                set
                {
                    if (progress == value)
                        return;

                    progress = value;
                    Invalidate(Invalidation.DrawNode);
                }
            }

            public RingExplosion(HitResult result)
            {
                Anchor = Anchor.Centre;
                Origin = Anchor.Centre;

                Blending = BlendingParameters.Additive;

                switch (result)
                {
                    case HitResult.Meh:
                        countSmall = 3;
                        travel *= 0.3f;
                        break;

                    case HitResult.Ok:
                    case HitResult.Good:
                        countSmall = 4;
                        travel *= 0.6f;
                        break;

                    case HitResult.Great:
                    case HitResult.Perfect:
                        countSmall = 4;
                        countLarge = 4;
                        break;
                }

                // Large enough to bound every ring at full travel so this is never culled. The parent
                // auto-sizes and this drawable used to be zero-sized, so it must not contribute to that.
                Size = new Vector2((travel + large_size) * 2);
                BypassAutoSizeAxes = Axes.Both;
            }

            [BackgroundDependencyLoader]
            private void load(ShaderManager shaders)
            {
                shader = shaders.Load(VertexShaderDescriptor.TEXTURE_2, "FastRing");
            }

            public void PlayAnimation()
            {
                rings.Clear();

                for (int i = 0; i < countSmall + countLarge; i++)
                {
                    // Note: the original fed a 0-360 value straight into Cos/Sin (i.e. as radians). That is
                    // preserved here so the spread of directions is unchanged.
                    float direction = RNG.NextSingle(0, 360);
                    float distance = RNG.NextSingle(travel / 2, travel);

                    rings.Add((new Vector2(MathF.Cos(direction), MathF.Sin(direction)), distance,
                        i < countSmall ? small_size : large_size));
                }

                Progress = 0;
                Alpha = 1;
            }

            public void ApplyAnimationAt(double elapsed)
            {
                Progress = easedProgress(elapsed, 600, Easing.OutQuint);
                Alpha = 1 - easedProgress(elapsed, 1000, Easing.OutQuint);
            }

            protected override DrawNode CreateDrawNode() => new RingExplosionDrawNode(this);

            private class RingExplosionDrawNode : DrawNode
            {
                protected new RingExplosion Source => (RingExplosion)base.Source;

                public RingExplosionDrawNode(RingExplosion source)
                    : base(source)
                {
                }

                private readonly List<(Quad quad, float size)> rings = new List<(Quad, float)>();

                private IShader shader = null!;

                public override void ApplyState()
                {
                    base.ApplyState();

                    shader = Source.shader;

                    rings.Clear();

                    Vector2 centre = Source.DrawSize / 2;

                    // Rings originally started at 30% of their travel and eased outwards to 100%.
                    float travelled = 0.3f + 0.7f * Source.progress;

                    foreach ((Vector2 direction, float distance, float size) in Source.rings)
                    {
                        Vector2 offset = direction * distance * travelled;

                        var rect = new RectangleF(centre.X + offset.X - size / 2, centre.Y + offset.Y - size / 2, size, size);

                        rings.Add((Source.ToScreenSpace(rect), size));
                    }
                }

                protected override void Draw(IRenderer renderer)
                {
                    base.Draw(renderer);

                    FastRing.DrawRings(renderer, shader, CollectionsMarshal.AsSpan(rings), thickness, DrawColourInfo.Colour);
                }
            }
        }
    }
}
