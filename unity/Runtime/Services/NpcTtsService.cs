using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NpcSoulEngine.Runtime.Models;
using UnityEngine;

#if SPEECH_SDK_AVAILABLE
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
#endif

namespace NpcSoulEngine.Runtime.Services
{
    public sealed class SpeechResult
    {
        public AudioClip Clip { get; }
        public IReadOnlyList<WordBoundaryInfo> WordBoundaries { get; }
        public bool IsSuccess { get; }

        internal SpeechResult(AudioClip clip, IReadOnlyList<WordBoundaryInfo> boundaries, bool success)
        {
            Clip           = clip;
            WordBoundaries = boundaries;
            IsSuccess      = success;
        }

        public static SpeechResult Failed { get; } = new(null, Array.Empty<WordBoundaryInfo>(), false);
    }

    public sealed class WordBoundaryInfo
    {
        public string Word            { get; }
        public float AudioOffsetSecs  { get; }
        public float DurationSecs     { get; }

        internal WordBoundaryInfo(string word, float offsetSecs, float durationSecs)
        {
            Word           = word;
            AudioOffsetSecs = offsetSecs;
            DurationSecs   = durationSecs;
        }
    }

    /// <summary>
    /// Singleton facade for Azure Neural TTS.
    /// Synthesizes SSML to an AudioClip and exposes word-boundary events for lip sync.
    ///
    /// Requires Azure.CognitiveServices.Speech plugin placed in Assets/Plugins/ and the
    /// SPEECH_SDK_AVAILABLE scripting define set in Player Settings.
    ///
    /// Gracefully no-ops when the define is absent or credentials are missing.
    /// </summary>
    public sealed class NpcTtsService : IDisposable
    {
        public static NpcTtsService Instance { get; private set; }

        public bool IsConfigured { get; }

#if SPEECH_SDK_AVAILABLE
        private readonly SpeechConfig _speechConfig;
#endif

        private NpcTtsService(AzureSoulEngineConfig config)
        {
#if SPEECH_SDK_AVAILABLE
            if (string.IsNullOrEmpty(config.speechSubscriptionKey) || string.IsNullOrEmpty(config.speechRegion))
            {
                IsConfigured = false;
                return;
            }
            _speechConfig = SpeechConfig.FromSubscription(config.speechSubscriptionKey, config.speechRegion);
            // RIFF 24 kHz 16-bit mono — matches PcmToAudioClip
            _speechConfig.SetSpeechSynthesisOutputFormat(SpeechSynthesisOutputFormat.Riff24Khz16BitMonoPcm);
            IsConfigured = true;
#else
            IsConfigured = false;
#endif
        }

        public static NpcTtsService Create(AzureSoulEngineConfig config)
        {
            if (Instance != null) return Instance;
            Instance = new NpcTtsService(config);
            return Instance;
        }

        /// <summary>
        /// Synthesizes the given SSML string.
        /// Returns a <see cref="SpeechResult"/> containing the AudioClip and timed word boundaries.
        /// Must be awaited before accessing Unity AudioClip (main-thread only after await).
        /// </summary>
        public async Task<SpeechResult> SynthesizeAsync(string ssml, CancellationToken ct)
        {
#if SPEECH_SDK_AVAILABLE
            if (!IsConfigured) return SpeechResult.Failed;

            var boundaries = new List<WordBoundaryInfo>();

            using var audioConfig  = AudioConfig.FromStreamOutput(AudioOutputStream.CreatePullStream());
            using var synthesizer  = new SpeechSynthesizer(_speechConfig, audioConfig);

            synthesizer.WordBoundary += (_, e) =>
            {
                if (e.BoundaryType == SpeechSynthesisBoundaryType.Word)
                {
                    boundaries.Add(new WordBoundaryInfo(
                        e.Text,
                        (float)e.AudioOffset / 10_000_000f,    // 100-ns ticks → seconds
                        (float)e.Duration.Ticks / 10_000_000f));
                }
            };

            ct.ThrowIfCancellationRequested();
            using var result = await synthesizer.SpeakSsmlAsync(ssml);

            if (result.Reason != ResultReason.SynthesizingAudioCompleted)
            {
                Debug.LogWarning($"[NpcTtsService] Synthesis failed: {result.Reason}");
                return SpeechResult.Failed;
            }

            var clip = PcmToAudioClip(result.AudioData);
            return new SpeechResult(clip, boundaries, clip != null);
#else
            await Task.CompletedTask;
            return SpeechResult.Failed;
#endif
        }

        // RIFF WAV header = 44 bytes; payload is 16-bit signed PCM at 24 kHz mono.
        private static AudioClip PcmToAudioClip(byte[] pcm)
        {
            const int headerBytes = 44;
            const int sampleRate  = 24_000;
            const int channels    = 1;

            if (pcm == null || pcm.Length <= headerBytes)
                return null;

            var sampleCount = (pcm.Length - headerBytes) / 2;
            var samples = new float[sampleCount];
            for (var i = 0; i < sampleCount; i++)
            {
                var raw = (short)(pcm[headerBytes + i * 2] | (pcm[headerBytes + i * 2 + 1] << 8));
                samples[i] = raw / 32768f;
            }

            var clip = AudioClip.Create("npc_speech", sampleCount, channels, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        public void Dispose()
        {
#if SPEECH_SDK_AVAILABLE
            _speechConfig?.Dispose();
#endif
            if (Instance == this) Instance = null;
        }
    }
}
