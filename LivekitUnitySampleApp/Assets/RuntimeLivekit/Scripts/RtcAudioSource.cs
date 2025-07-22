using System;
using System.Collections;
using LiveKit.Proto;
using LiveKit.Internal;
using LiveKit.Internal.FFIClients.Requests;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using System.Diagnostics;
using UnityEngine;

namespace LiveKit
{
    /// <summary>
    /// Defines the type of audio source, influencing processing behavior.
    /// </summary>
    public enum RtcAudioSourceType
    {
        AudioSourceCustom = 0,
        AudioSourceMicrophone = 1
    }

    /// <summary>
    /// Capture source for a local audio track.
    /// </summary>
    public abstract class RtcAudioSource : IRtcSource, IDisposable
    {
        /// <summary>
        /// Event triggered when audio samples are captured from the underlying source.
        /// Provides the audio data, channel count, and sample rate.
        /// </summary>
        /// <remarks>
        /// This event is not guaranteed to be called on the main thread.
        /// </remarks>
        public abstract event Action<float[], int, int> AudioRead;

        #if UNITY_IOS && !UNITY_EDITOR
        // iOS microphone sample rate is 24k
        public static uint DefaultMicrophoneSampleRate = 24000;

        public static uint DefaultSampleRate = 48000;
        #else
        public static uint DefaultSampleRate = 48000;
        public static uint DefaultMicrophoneSampleRate = DefaultSampleRate;
        #endif
        public static uint DefaultChannels = 2;

        private readonly RtcAudioSourceType _sourceType;
        public RtcAudioSourceType SourceType => _sourceType;

        internal readonly FfiHandle Handle;
        protected AudioSourceInfo _info;

        /// <summary>
        /// Temporary frame buffer for invoking the FFI capture method.
        /// </summary>
        private NativeArray<short> _frameData;

        private bool _muted = false;
        public override bool Muted => _muted;

        private bool _started = false;
        private bool _disposed = false;

        protected RtcAudioSource(
            int channels = 2,
            RtcAudioSourceType audioSourceType = RtcAudioSourceType.AudioSourceCustom)
        {
            _sourceType = audioSourceType;

            using var request = FFIBridge.Instance.NewRequest<NewAudioSourceRequest>();
            var newAudioSource = request.request;
            newAudioSource.Type = AudioSourceType.AudioSourceNative;
            newAudioSource.NumChannels = (uint)channels;
            newAudioSource.SampleRate = _sourceType == RtcAudioSourceType.AudioSourceMicrophone
                ? DefaultMicrophoneSampleRate
                : DefaultSampleRate;

            UnityEngine.Debug.Log($"NewAudioSource: {newAudioSource.NumChannels} {newAudioSource.SampleRate}");

            newAudioSource.Options = request.TempResource<AudioSourceOptions>();
            newAudioSource.Options.EchoCancellation = true;
            newAudioSource.Options.AutoGainControl = true;
            newAudioSource.Options.NoiseSuppression = true;
            using var response = request.Send();
            FfiResponse res = response;
            _info = res.NewAudioSource.Source.Info;
            Handle = FfiHandle.FromOwnedHandle(res.NewAudioSource.Source.Handle);
        }

        /// <summary>
        /// Begin capturing audio samples from the underlying source.
        /// </summary>
        public virtual void Start()
        {
            if (_started) return;
            AudioRead += OnAudioRead;
            _started = true;
        }

        /// <summary>
        /// Stop capturing audio samples from the underlying source.
        /// </summary>
        public virtual void Stop()
        {
            if (!_started) return;
            AudioRead -= OnAudioRead;
            _started = false;
        }

        private float[] Resample44100To48000(float[] input, int channels)
        {
            int inputSamples = input.Length / channels;
            int outputSamples = inputSamples * 48000 / 44100;
            float[] output = new float[outputSamples * channels];

            for (int c = 0; c < channels; c++)
            {
                for (int i = 0; i < outputSamples; i++)
                {
                    float srcIndex = (float)i * 44100 / 48000;
                    int i0 = (int)Mathf.Floor(srcIndex);
                    int i1 = Mathf.Min(i0 + 1, inputSamples - 1);
                    float t = srcIndex - i0;

                    float sample0 = input[i0 * channels + c];
                    float sample1 = input[i1 * channels + c];
                    output[i * channels + c] = Mathf.Lerp(sample0, sample1, t);
                }
            }

            return output;
        }

        private void OnAudioRead(float[] data, int channels, int sampleRate)
        {
            if (_muted) return;

            if (sampleRate != DefaultSampleRate)
            {
                data = Resample44100To48000(data, channels);
                sampleRate = (int)DefaultSampleRate;
            }

            // The length of the data buffer corresponds to the DSP buffer size.
            if (_frameData.Length != data.Length)
            {
                if (_frameData.IsCreated) _frameData.Dispose();
                _frameData = new NativeArray<short>(data.Length, Allocator.Persistent);
            }

            // Copy from the audio read buffer into the frame buffer, converting
            // each sample to a 16-bit signed integer.
            static short FloatToS16(float v)
            {
                v *= 32768f;
                v = Math.Min(v, 32767f);
                v = Math.Max(v, -32768f);
                return (short)(v + Math.Sign(v) * 0.5f);
            }

            for (int i = 0; i < data.Length; i++)
                _frameData[i] = FloatToS16(data[i]);

            // Capture the frame.
            using var request = FFIBridge.Instance.NewRequest<CaptureAudioFrameRequest>();
            using var audioFrameBufferInfo = request.TempResource<AudioFrameBufferInfo>();

            var pushFrame = request.request;
            pushFrame.SourceHandle = (ulong)Handle.DangerousGetHandle();
            pushFrame.Buffer = audioFrameBufferInfo;
            unsafe
            {
                pushFrame.Buffer.DataPtr = (ulong)NativeArrayUnsafeUtility
                    .GetUnsafePtr(_frameData);
            }

            pushFrame.Buffer.NumChannels = (uint)channels;
            pushFrame.Buffer.SampleRate = (uint)sampleRate;
            pushFrame.Buffer.SamplesPerChannel = (uint)data.Length / (uint)channels;

            using var response = request.Send();
            FfiResponse res = response;

            // Wait for async callback, log an error if the capture fails.
            var asyncId = res.CaptureAudioFrame.AsyncId;

            void Callback(CaptureAudioFrameCallback callback)
            {
                if (callback.AsyncId != asyncId) return;
                if (callback.HasError)
                    Utils.Error(
                        $"Audio capture failed with channels: {channels} and sampleRate: {sampleRate}, error: {callback.Error}");
                FfiClient.Instance.CaptureAudioFrameReceived -= Callback;
            }

            FfiClient.Instance.CaptureAudioFrameReceived += Callback;
        }

        /// <summary>
        /// Mutes or unmutes the audio source.
        /// </summary>
        public override void SetMute(bool muted)
        {
            _muted = muted;
        }

        /// <summary>
        /// Disposes of the audio source, stopping it first if necessary.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed && disposing) Stop();
            if (_frameData.IsCreated) _frameData.Dispose();
            _disposed = true;
        }

        ~RtcAudioSource()
        {
            Dispose(false);
        }

        [Obsolete("No longer used, audio sources should perform any preparation in Start() asynchronously")]
        public virtual IEnumerator Prepare(float timeout = 0)
        {
            yield break;
        }

        [Obsolete("Use Start() instead")]
        public IEnumerator PrepareAndStart()
        {
            Start();
            yield break;
        }
    }
}