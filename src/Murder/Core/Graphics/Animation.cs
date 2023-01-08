﻿using System.Collections.Immutable;

namespace Murder.Core.Graphics
{
    public readonly struct Animation
    {
        public readonly ImmutableArray<int> Frames = ImmutableArray<int>.Empty;
        public readonly ImmutableArray<float> FramesDuration = ImmutableArray<float>.Empty;
        public readonly float AnimationDuration = 1;
        
        public Animation()
        {
            Frames = ImmutableArray<int>.Empty;
            FramesDuration = ImmutableArray<float>.Empty;
        }
        public Animation(int[] frames, float[] framesDuration)
        {
            (Frames, FramesDuration) = (frames.ToImmutableArray(), framesDuration.ToImmutableArray());

            var duration = 0f;
            foreach (var d in framesDuration)
            {
                duration += d;
            }
            AnimationDuration = duration / 1000f;
        }

        public int FrameCount => Frames.Length;
        
        /// <param name="startTime">Time when the animation first played</param>
        /// <param name="currentTime">Current game time</param>
        /// <returns>The name of the current frame</returns>
        public (int animationFrame, bool complete) Evaluate(float startTime, float currentTime) => Evaluate(startTime, currentTime, -1);
        public (int animationFrame, bool complete) Evaluate(float startTime, float currentTime, float forceAnimationDuration)
        {
            var fullTime = (currentTime - startTime);
            var animationDuration = AnimationDuration;
            var factor = 1f;

            if (forceAnimationDuration > 0)
            {
                factor = forceAnimationDuration / AnimationDuration;
                animationDuration = forceAnimationDuration;
            }

            if (animationDuration == 0)
                return (0, true);

            while (fullTime < 0) fullTime += animationDuration;

            var delta = fullTime % animationDuration;

            if (FrameCount > 0)
            {
                int frame = -1;
                for (float current = 0; current <= delta; current += factor * FramesDuration[frame % FramesDuration.Length] / 1000f) 
                {
                    frame++;
                }
                return (Frames[frame % FramesDuration.Length], fullTime + Game.FixedDeltaTime * 2 >= animationDuration);
            }
            else
                return (0, true);
        }
    }
}
