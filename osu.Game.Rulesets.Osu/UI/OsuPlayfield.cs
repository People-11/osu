// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Primitives;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Objects.Drawables;
using osu.Game.Rulesets.Objects.Legacy;
using osu.Game.Rulesets.Osu.Beatmaps;
using osu.Game.Rulesets.Osu.Configuration;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Osu.Objects.Drawables;
using osu.Game.Rulesets.Osu.Objects.Drawables.Connections;
using osu.Game.Rulesets.Osu.Scoring;
using osu.Game.Rulesets.Osu.Skinning.Argon;
using osu.Game.Rulesets.Osu.UI.Cursor;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.UI;
using osuTK;

namespace osu.Game.Rulesets.Osu.UI
{
    [Cached]
    public partial class OsuPlayfield : Playfield
    {
        private readonly Container borderContainer;
        private readonly PlayfieldBorder playfieldBorder;
        private readonly ProxyContainer approachCircles;
        private readonly ProxyContainer spinnerProxies;
        private readonly JudgementContainer<DrawableOsuJudgement> judgementLayer;

        private JudgementPooler<DrawableOsuJudgement> judgementPooler = null!;

        // For osu! gameplay, everything is always on screen.
        // Skipping masking calculations improves performance in intense beatmaps (ie. https://osu.ppy.sh/beatmapsets/150945#osu/372245)
        public override bool UpdateSubTreeMasking() => false;

        protected override bool CanUpdateChildOnWorkerThread(Drawable child) =>
            Environment.ProcessorCount > 1 && child == FollowPoints && FollowPoints.AliveEntries.Count >= 128;

        protected override HitObjectContainer CreateHitObjectContainer() => new ConcurrentHitObjectContainer();

        private sealed partial class ConcurrentHitObjectContainer : HitObjectContainer
        {
            protected override bool BatchWorkerThreadChildUpdates => Environment.ProcessorCount > 1;

            protected override int MinimumWorkerThreadChildCount => 64;

            protected override int MaximumConcurrentChildUpdateThreads => Math.Min(4, Environment.ProcessorCount);

            protected override bool CanUpdateChildOnWorkerThread(Drawable child)
            {
                // Judgement rewinds are applied by Playfield.Update() before this container is traversed. Restrict
                // the worker to completed standalone circles so input, result calculation and sliders stay serial.
                return child.GetType() == typeof(DrawableHitCircle)
                       && ((DrawableHitCircle)child).IsWorkerThreadUpdateSafe
                       && ((DrawableHitCircle)child).CirclePiece.Drawable is ArgonMainCirclePiece;
            }
        }

        public SmokeContainer Smoke { get; }
        public FollowPointRenderer FollowPoints { get; }

        public static readonly Vector2 BASE_SIZE = new Vector2(512, 384);

        protected override GameplayCursorContainer? CreateCursor() => new OsuCursorContainer();

        public override Quad SkinnableComponentScreenSpaceDrawQuad => playfieldBorder.ScreenSpaceDrawQuad;

        private readonly Container judgementAboveHitObjectLayer;

        public OsuPlayfield()
        {
            Anchor = Anchor.Centre;
            Origin = Anchor.Centre;

            InternalChildren = new Drawable[]
            {
                borderContainer = new Container
                {
                    RelativeSizeAxes = Axes.Both,
                    Child = playfieldBorder = new PlayfieldBorder { RelativeSizeAxes = Axes.Both },
                },
                Smoke = new SmokeContainer { RelativeSizeAxes = Axes.Both },
                spinnerProxies = new ProxyContainer { RelativeSizeAxes = Axes.Both },
                FollowPoints = new FollowPointRenderer { RelativeSizeAxes = Axes.Both },
                judgementLayer = new JudgementContainer<DrawableOsuJudgement> { RelativeSizeAxes = Axes.Both },
                HitObjectContainer,
                judgementAboveHitObjectLayer = new Container { RelativeSizeAxes = Axes.Both },
                approachCircles = new ProxyContainer { RelativeSizeAxes = Axes.Both },
            };

            HitPolicy = new StartTimeOrderedHitPolicy();

            NewResult += onNewResult;
        }

        private IHitPolicy hitPolicy;

        public IHitPolicy HitPolicy
        {
            get => hitPolicy;
            [MemberNotNull(nameof(hitPolicy))]
            set
            {
                hitPolicy = value ?? throw new ArgumentNullException(nameof(value));
                hitPolicy.HitObjectContainer = HitObjectContainer;
            }
        }

        protected override void OnNewDrawableHitObject(DrawableHitObject drawable)
        {
            ((DrawableOsuHitObject)drawable).CheckHittable = hitPolicy.CheckHittable;

            Debug.Assert(!drawable.IsLoaded, $"Already loaded {nameof(DrawableHitObject)} is added to {nameof(OsuPlayfield)}");
            drawable.OnLoadComplete += onDrawableHitObjectLoaded;
        }

        private void onDrawableHitObjectLoaded(Drawable drawable)
        {
            // note: `Slider`'s `ProxiedLayer` is added when its nested `DrawableHitCircle` is loaded.
            switch (drawable)
            {
                case DrawableSpinner:
                    spinnerProxies.Add(drawable.CreateProxy());
                    break;

                case DrawableHitCircle hitCircle:
                    approachCircles.Add(hitCircle.ProxiedLayer.CreateProxy());
                    break;
            }
        }

        private void onJudgementLoaded(DrawableOsuJudgement judgement)
        {
            judgementAboveHitObjectLayer.Add(judgement.ProxiedAboveHitObjectsContent);
        }

        [BackgroundDependencyLoader]
        private void load(OsuRulesetConfigManager? config, IBeatmap? beatmap)
        {
            config?.BindWith(OsuRulesetSetting.PlayfieldBorderStyle, playfieldBorder.PlayfieldBorderStyle);

            var osuBeatmap = (OsuBeatmap?)beatmap;

            int greatJudgementPoolSize = osuBeatmap == null ? 20 : calculateGreatJudgementPoolSize(osuBeatmap);
            int hitCirclePoolSize = osuBeatmap == null ? 20 : CalculateHitCirclePoolSize(osuBeatmap);

            // Dense autoplay maps can exhaust the default pool during gameplay. A replacement judgement must
            // then load its skinnable hierarchy synchronously on the update thread (20ms in the captured log).
            // Preload only the peak number of Great judgements this beatmap can keep alive, while the playfield
            // itself is still loading asynchronously.
            AddInternal(judgementPooler = new JudgementPooler<DrawableOsuJudgement>(new[]
            {
                HitResult.Great,
                HitResult.Ok,
                HitResult.Meh,
                HitResult.Miss,
                HitResult.LargeTickHit,
                HitResult.SliderTailHit,
                HitResult.LargeTickMiss,
                HitResult.IgnoreMiss,
            }, onJudgementLoaded, result => result == HitResult.Great ? greatJudgementPoolSize : 20));

            RegisterPool<HitCircle, DrawableHitCircle>(hitCirclePoolSize, 1000);

            // handle edge cases where a beatmap has a slider with many repeats.
            int maxRepeatsOnOneSlider = 0;
            int maxTicksOnOneSlider = 0;

            if (osuBeatmap != null)
            {
                foreach (var slider in osuBeatmap.HitObjects.OfType<Slider>())
                {
                    maxRepeatsOnOneSlider = Math.Max(maxRepeatsOnOneSlider, slider.RepeatCount);
                    maxTicksOnOneSlider = Math.Max(maxTicksOnOneSlider, slider.NestedHitObjects.OfType<SliderTick>().Count());
                }
            }

            RegisterPool<Slider, DrawableSlider>(20, 100);
            RegisterPool<SliderHeadCircle, DrawableSliderHead>(20, 100);
            RegisterPool<SliderTailCircle, DrawableSliderTail>(20, 100);
            RegisterPool<SliderTick, DrawableSliderTick>(Math.Max(maxTicksOnOneSlider, 20), Math.Max(maxTicksOnOneSlider, 200));
            RegisterPool<SliderRepeat, DrawableSliderRepeat>(Math.Max(maxRepeatsOnOneSlider, 20), Math.Max(maxRepeatsOnOneSlider, 200));

            RegisterPool<Spinner, DrawableSpinner>(2, 20);
            RegisterPool<SpinnerTick, DrawableSpinnerTick>(10, 200);
            RegisterPool<SpinnerBonusTick, DrawableSpinnerBonusTick>(10, 200);

            if (beatmap != null)
                ApplyCircleSizeToPlayfieldBorder(beatmap);
        }

        private static int calculateGreatJudgementPoolSize(OsuBeatmap beatmap)
        {
            // The unmodified default judgement can remain alive for 1800ms. Use end times because sliders are
            // judged at their tail, and cap pathological input to the same bound as the hit-circle pool.
            var judgementTimes = beatmap.HitObjects.Select(h => h.GetEndTime()).OrderBy(t => t).ToArray();

            int first = 0;
            int peak = 20;

            for (int last = 0; last < judgementTimes.Length; last++)
            {
                while (judgementTimes[last] - judgementTimes[first] > 1800)
                    first++;

                peak = Math.Max(peak, last - first + 1);
            }

            return Math.Min(peak, 1000);
        }

        internal static int CalculateHitCirclePoolSize(OsuBeatmap beatmap)
        {
            // A circle enters the pool-backed update tree at preempt and can remain there until a late miss has
            // completed the common 800ms hit-state tail. Preload the peak overlap so none are synchronously
            // constructed during gameplay; end events sort before start events because LifetimeEnd is exclusive.
            var lifetimeEvents = new List<(double time, int change)>();

            foreach (var circle in beatmap.HitObjects.OfType<HitCircle>())
            {
                lifetimeEvents.Add((circle.StartTime - circle.TimePreempt, 1));
                lifetimeEvents.Add((circle.StartTime + OsuHitWindows.MISS_WINDOW + 800, -1));
            }

            lifetimeEvents.Sort((a, b) =>
            {
                int timeComparison = a.time.CompareTo(b.time);
                return timeComparison != 0 ? timeComparison : a.change.CompareTo(b.change);
            });

            int alive = 0;
            int peak = 20;

            foreach (var lifetimeEvent in lifetimeEvents)
            {
                alive += lifetimeEvent.change;
                peak = Math.Max(peak, alive);
            }

            return Math.Min(peak, 1000);
        }

        protected void ApplyCircleSizeToPlayfieldBorder(IBeatmap beatmap)
        {
            borderContainer.Padding = new MarginPadding(OsuHitObject.OBJECT_RADIUS * -LegacyRulesetExtensions.CalculateScaleFromCircleSize(beatmap.Difficulty.CircleSize, true));
        }

        protected override HitObjectLifetimeEntry CreateLifetimeEntry(HitObject hitObject) => new OsuHitObjectLifetimeEntry(hitObject);

        protected override void OnHitObjectAdded(HitObject hitObject)
        {
            base.OnHitObjectAdded(hitObject);
            FollowPoints.AddFollowPoints((OsuHitObject)hitObject);
        }

        protected override void OnHitObjectRemoved(HitObject hitObject)
        {
            base.OnHitObjectRemoved(hitObject);
            FollowPoints.RemoveFollowPoints((OsuHitObject)hitObject);
        }

        private void onNewResult(DrawableHitObject judgedObject, JudgementResult result)
        {
            // Hitobjects that block future hits should miss previous hitobjects if they're hit out-of-order.
            hitPolicy.HandleHit(judgedObject);

            if (!judgedObject.DisplayResult || !DisplayJudgements.Value)
                return;

            var explosion = judgementPooler.Get(result.Type, doj => doj.Apply(result, judgedObject));

            if (explosion == null)
                return;

            judgementLayer.Add(explosion);

            // the proxied content is added to judgementAboveHitObjectLayer once, on first load, and never removed from it.
            // ensure that ordering is consistent with expectations (latest judgement should be front-most).
            judgementAboveHitObjectLayer.ChangeChildDepth(explosion.ProxiedAboveHitObjectsContent, (float)-result.TimeAbsolute);
        }

        public override bool ReceivePositionalInputAt(Vector2 screenSpacePos) => HitObjectContainer.ReceivePositionalInputAt(screenSpacePos);

        private OsuResumeOverlay.OsuResumeOverlayInputBlocker? resumeInputBlocker;

        public void AttachResumeOverlayInputBlocker(OsuResumeOverlay.OsuResumeOverlayInputBlocker resumeInputBlocker)
        {
            Debug.Assert(this.resumeInputBlocker == null);
            this.resumeInputBlocker = resumeInputBlocker;
            AddInternal(resumeInputBlocker);
        }

        private partial class ProxyContainer : LifetimeManagementContainer
        {
            public void Add(Drawable proxy) => AddInternal(proxy);
        }

        private class OsuHitObjectLifetimeEntry : HitObjectLifetimeEntry
        {
            public OsuHitObjectLifetimeEntry(HitObject hitObject)
                : base(hitObject)
            {
                // Prevent past objects in idles states from remaining alive as their end times are skipped in non-frame-stable contexts.
                LifetimeEnd = HitObject.GetEndTime() + HitObject.HitWindows.WindowFor(HitResult.Miss);
            }

            protected override double InitialLifetimeOffset => ((OsuHitObject)HitObject).TimePreempt;
        }
    }
}
