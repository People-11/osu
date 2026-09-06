// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using osu.Framework.Allocation;
using osu.Framework.Extensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Pooling;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Testing;
using osu.Framework.Timing;
using osu.Framework.Utils;
using osu.Game.Configuration;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Osu.Objects.Drawables;
using osu.Game.Rulesets.Osu.Skinning.Argon;
using osu.Game.Rulesets.Scoring;
using osu.Game.Skinning;
using osuTK;

namespace osu.Game.Rulesets.Osu.Tests
{
    public partial class TestSceneDrawableJudgement : OsuSkinnableTestScene
    {
        [Resolved]
        private OsuConfigManager config { get; set; } = null!;

        private readonly List<DrawablePool<TestDrawableOsuJudgement>> pools = new List<DrawablePool<TestDrawableOsuJudgement>>();

        [TestCaseSource(nameof(validResults))]
        public void Test(HitResult result)
        {
            showResult(result);
        }

        private static IEnumerable<HitResult> validResults => Enum.GetValues<HitResult>().Skip(1);

        [Test]
        public void TestHitLightingDisabled()
        {
            AddStep("hit lighting disabled", () => config.SetValue(OsuSetting.HitLighting, false));

            showResult(HitResult.Great);

            AddUntilStep("judgements shown", () => this.ChildrenOfType<TestDrawableOsuJudgement>().Any());
            AddAssert("hit lighting has no transforms", () => this.ChildrenOfType<TestDrawableOsuJudgement>().All(judgement => !judgement.Lighting.Transforms.Any()));
            AddAssert("hit lighting hidden", () => this.ChildrenOfType<TestDrawableOsuJudgement>().All(judgement => judgement.Lighting.Alpha == 0));
        }

        [Test]
        public void TestHitLightingEnabled()
        {
            AddStep("hit lighting enabled", () => config.SetValue(OsuSetting.HitLighting, true));

            showResult(HitResult.Great);

            AddUntilStep("judgements shown", () => this.ChildrenOfType<TestDrawableOsuJudgement>().Any());
            AddUntilStep("hit lighting shown", () => this.ChildrenOfType<TestDrawableOsuJudgement>().Any(judgement => judgement.Lighting.Alpha > 0));
        }

        [Test]
        public void TestArgonAnimationAfterRewind()
        {
            ManualClock manualClock = null!;
            Container clockedContainer = null!;
            TestArgonJudgementPiece piece = null!;

            AddStep("create judgement", () =>
            {
                manualClock = new ManualClock();

                Add(clockedContainer = new Container
                {
                    Clock = new FramedClock(manualClock),
                    Child = piece = new TestArgonJudgementPiece(HitResult.Great)
                });
            });

            AddStep("start animation", () =>
            {
                using (piece.BeginAbsoluteSequence(1000))
                    piece.PlayAnimation();
            });

            assertState(1100);
            assertState(1400);
            assertState(1100);

            void assertState(double time)
            {
                AddAssert($"state at {time}", () =>
                {
                    manualClock.CurrentTime = time;
                    clockedContainer.UpdateSubTree();

                    double elapsed = time - 1000;
                    float expectedTextProgress = (float)Interpolation.ApplyEasing(Easing.OutQuint, Math.Clamp(elapsed / 300, 0, 1));
                    float expectedScaleProgress = (float)Interpolation.ApplyEasing(Easing.OutQuint, Math.Clamp(elapsed / 1800, 0, 1));

                    return Precision.AlmostEquals(piece.Alpha, (float)(1 - elapsed / 800))
                           && Precision.AlmostEquals(piece.Text.Alpha, expectedTextProgress)
                           && Precision.AlmostEquals(piece.Text.Scale, new Vector2(1 + 0.2f * expectedScaleProgress));
                });
            }
        }

        private void showResult(HitResult result)
        {
            AddStep("Show " + result.GetDescription(), () =>
            {
                int poolIndex = 0;

                SetContents(_ =>
                {
                    DrawablePool<TestDrawableOsuJudgement> pool;

                    if (poolIndex >= pools.Count)
                        pools.Add(pool = new DrawablePool<TestDrawableOsuJudgement>(1));
                    else
                    {
                        // We need to make sure neither the pool nor the judgement get disposed when new content is set, and they both share the same parent.
                        pool = pools[poolIndex];
                        ((Container)pool.Parent!).Clear(false);
                    }

                    var container = new Container
                    {
                        RelativeSizeAxes = Axes.Both,
                        Child = pool,
                    };

                    // Must be scheduled so the pool is loaded before we try and retrieve from it.
                    Schedule(() =>
                    {
                        container.Add(pool.Get(j => j.Apply(new JudgementResult(new HitObject
                        {
                            StartTime = Time.Current
                        }, new Judgement())
                        {
                            Type = result,
                        }, null)).With(j =>
                        {
                            j.Anchor = Anchor.Centre;
                            j.Origin = Anchor.Centre;
                        }));
                    });

                    poolIndex++;
                    return container;
                });
            });
        }

        private partial class TestDrawableOsuJudgement : DrawableOsuJudgement
        {
            public new SkinnableSprite Lighting => base.Lighting;
            public new SkinnableDrawable? JudgementBody => base.JudgementBody;
        }

        private partial class TestArgonJudgementPiece : ArgonJudgementPiece
        {
            public SpriteText Text => JudgementText;

            public TestArgonJudgementPiece(HitResult result)
                : base(result)
            {
            }
        }
    }
}
