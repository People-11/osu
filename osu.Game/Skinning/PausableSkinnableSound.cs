// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

#nullable disable

using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Threading;
using osu.Game.Audio;

namespace osu.Game.Skinning
{
    public partial class PausableSkinnableSound : SkinnableSound
    {
        private ISampleInfo[] lastUpdatedSamples;
        private bool childrenReady;

        public double Length => !DrawableSamples.Any() ? 0 : DrawableSamples.Max(sample => sample.Length);

        public bool RequestedPlaying { get; private set; }

        public PausableSkinnableSound()
        {
        }

        public PausableSkinnableSound([NotNull] IEnumerable<ISampleInfo> samples)
            : base(samples)
        {
        }

        public PausableSkinnableSound([NotNull] ISampleInfo sample)
            : base(sample)
        {
        }

        private readonly IBindable<bool> samplePlaybackDisabled = new Bindable<bool>();

        private ScheduledDelegate scheduledStart;

        [BackgroundDependencyLoader(true)]
        private void load(ISamplePlaybackDisabler samplePlaybackDisabler)
        {
            // if in a gameplay context, pause sample playback when gameplay is paused.
            if (samplePlaybackDisabler != null)
            {
                samplePlaybackDisabled.BindTo(samplePlaybackDisabler.SamplePlaybackDisabled);
                samplePlaybackDisabled.BindValueChanged(SamplePlaybackDisabledChanged);
            }
        }

        protected virtual void SamplePlaybackDisabledChanged(ValueChangedEvent<bool> disabled)
        {
            if (!RequestedPlaying) return;

            // let non-looping samples that have already been started play out to completion (sounds better than abruptly cutting off).
            if (!Looping) return;

            cancelPendingStart();

            if (disabled.NewValue)
                base.Stop();
            else
            {
                // schedule so we don't start playing a sample which is no longer alive.
                scheduledStart = Schedule(() =>
                {
                    if (RequestedPlaying)
                        base.Play();
                });
            }
        }

        protected override void SkinChanged(ISkinSource skin)
        {
            childrenReady = false;
            base.SkinChanged(skin);
        }

        protected override bool RequiresChildrenUpdate
        {
            get
            {
                if (!ReferenceEquals(lastUpdatedSamples, Samples))
                {
                    lastUpdatedSamples = Samples;
                    childrenReady = false;
                }

                // Looping slider/spinner samples retain the normal live drawable path. One-shot hit samples
                // only need their hierarchy updated when loading, replacing samples or changing skin; the
                // SampleChannel continues playback independently on the audio thread.
                return (Looping || !childrenReady) && base.RequiresChildrenUpdate;
            }
        }

        protected override void UpdateAfterChildren()
        {
            base.UpdateAfterChildren();
            childrenReady = true;
        }

        public override void Play()
        {
            cancelPendingStart();
            RequestedPlaying = true;

            if (samplePlaybackDisabled.Value)
                return;

            base.Play();
        }

        public override void Stop()
        {
            cancelPendingStart();
            RequestedPlaying = false;
            base.Stop();
        }

        private void cancelPendingStart()
        {
            scheduledStart?.Cancel();
            scheduledStart = null;
        }
    }
}
