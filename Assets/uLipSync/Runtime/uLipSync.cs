using UnityEngine;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using System.Collections.Generic;

namespace uLipSync
{

public class uLipSync : MonoBehaviour
{
    public Profile profile;
    public LipSyncUpdateEvent onLipSyncUpdate = new LipSyncUpdateEvent();
    [Tooltip("If you want to supress the sound output, set this value to zero instead of setting the AudioSource volume to zero")]
    [Range(0f, 1f)] public float outputSoundGain = 1f;

    public uLipSyncAudioSource audioSource;
    uLipSyncAudioSource currentAudioSource_;

    JobHandle _jobHandle;
    object _lockObject = new object();
    int _index = 0;

    NativeArray<float> _rawInputData;
    NativeArray<float> _inputData;
    NativeArray<float> _mfcc;
    NativeArray<float> _mfccForOther;
    NativeArray<float> _phonemes;
    NativeArray<float> _distances;
    NativeArray<LipSyncJob.Info> _info;
    List<int> _requestedCalibrationVowels = new List<int>();

    public NativeArray<float> mfcc { get { return _mfccForOther; } }
    public LipSyncInfo result { get; private set; } = new LipSyncInfo();

    int inputSampleCount
    {
        get 
        {  
            float r = (float)AudioSettings.outputSampleRate / profile.targetSampleRate;
            return Mathf.CeilToInt(profile.sampleCount * r);
        }
    }

    void Awake()
    {
        UpdateAudioSource();
    }

    void OnEnable()
    {
        AllocateBuffers();
    }

    void OnDisable()
    {
        DisposeBuffers();
    }

    void Update()
    {
        if (!_jobHandle.IsCompleted) return;

        UpdateResult();
        InvokeCallback();
        UpdateCalibration();
        UpdatePhonemes();
        ScheduleJob();

        UpdateBuffers();
        UpdateAudioSource();
    }

    void AllocateBuffers()
    {
        lock (_lockObject)
        {
            int n = inputSampleCount;
            _rawInputData = new NativeArray<float>(n, Allocator.Persistent);
            _inputData = new NativeArray<float>(n, Allocator.Persistent); 
            _mfcc = new NativeArray<float>(12, Allocator.Persistent); 
            _distances = new NativeArray<float>(profile.mfccs.Count, Allocator.Persistent);
            _mfccForOther = new NativeArray<float>(12, Allocator.Persistent); 
            _phonemes = new NativeArray<float>(12 * profile.mfccs.Count, Allocator.Persistent);
            _info = new NativeArray<LipSyncJob.Info>(1, Allocator.Persistent);
        }
    }

    void DisposeBuffers()
    {
        lock (_lockObject)
        {
            _jobHandle.Complete();
            _rawInputData.Dispose();
            _inputData.Dispose();
            _mfcc.Dispose();
            _mfccForOther.Dispose();
            _distances.Dispose();
            _phonemes.Dispose();
            _info.Dispose();
        }
    }

    void UpdateBuffers()
    {
        if (inputSampleCount != _rawInputData.Length ||
            profile.mfccs.Count * 12 != _phonemes.Length)
        {
            lock (_lockObject)
            {
                DisposeBuffers();
                AllocateBuffers();
            }
        }
    }

    void UpdateResult()
    {
        _jobHandle.Complete();
        _mfccForOther.CopyFrom(_mfcc);

        float sumInvDistance = 0f;
        float minDistance = float.MaxValue;
        int mainIndex = -1;
        string mainPhoneme = "";
        for (int i = 0; i < _distances.Length; ++i)
        {
            var d = _distances[i];
            if (d < minDistance)
            {
                minDistance = d;
                mainIndex = i;
                mainPhoneme = profile.GetPhoneme(i);
            }
            sumInvDistance += Mathf.Exp(-d);
        }

        var ratios = new Dictionary<string, float>();
        for (int i = 0; i < _distances.Length; ++i)
        {
            var phoneme = profile.GetPhoneme(i);
            var d = _distances[i];
            var invDistance = Mathf.Exp(-d);
            var ratio = sumInvDistance > 0f ? invDistance / sumInvDistance : 0f;
            if (!ratios.TryAdd(phoneme, ratio))
            {
                ratios[phoneme] += ratio;
            }
        }

        float rawVolume = _info[0].volume;
        float minVol = profile.minVolume;
        float maxVol = Mathf.Max(profile.maxVolume, minVol + 1e-4f);
        float normVol = Mathf.Log10(rawVolume);
        normVol = (normVol - minVol) / (maxVol - minVol);
        normVol = Mathf.Clamp(normVol, 0f, 1f);

        result = new LipSyncInfo()
        {
            index = mainIndex,
            phoneme = mainPhoneme,
            volume = normVol,
            rawVolume = rawVolume,
            distance = minDistance,
            phonemeRatios = ratios,
        };
    }

    void InvokeCallback()
    {
        if (onLipSyncUpdate == null) return;

        onLipSyncUpdate.Invoke(result);
    }

    void UpdatePhonemes()
    {
        int index = 0;
        foreach (var data in profile.mfccs)
        {
            foreach (var value in data.mfccNativeArray)
            {
                if (index >= _phonemes.Length) break;
                _phonemes[index++] = value;
            }
        }
    }

    void ScheduleJob()
    {
        int index = 0;
        lock (_lockObject)
        {
            _inputData.CopyFrom(_rawInputData);
            index = _index;
        }

        var lipSyncJob = new LipSyncJob()
        {
            input = _inputData,
            startIndex = index,
            outputSampleRate = AudioSettings.outputSampleRate,
            targetSampleRate = profile.targetSampleRate,
            volumeThresh = Mathf.Pow(10f, profile.minVolume),
            melFilterBankChannels = profile.melFilterBankChannels,
            mfcc = _mfcc,
            phonemes = _phonemes,
            distances = _distances,
            info = _info,
        };

        _jobHandle = lipSyncJob.Schedule();
    }

    public void RequestCalibration(int index)
    {
        _requestedCalibrationVowels.Add(index);
    }

    void UpdateCalibration()
    {
        if (profile == null) return;

        foreach (var index in _requestedCalibrationVowels)
        {
            profile.UpdateMfcc(index, mfcc, true);
        }

        _requestedCalibrationVowels.Clear();
    }

    void UpdateAudioSource()
    {
        if (audioSource == currentAudioSource_) return;

        if (currentAudioSource_)
        {
            currentAudioSource_.onAudioFilterRead.RemoveListener(OnDataReceived);
        }

        if (audioSource)
        {
            audioSource.onAudioFilterRead.AddListener(OnDataReceived);
        }

        currentAudioSource_ = audioSource;
    }

    public void OnDataReceived(float[] input, int channels)
    {
        if (_rawInputData == null || _rawInputData.Length == 0) return;

        lock (_lockObject)
        {
            int n = _rawInputData.Length;
            _index = _index % n;
            for (int i = 0; i < input.Length; i += channels) 
            {
                _rawInputData[_index++ % n] = input[i];
            }
        }

        if (math.abs(outputSoundGain - 1f) > math.EPSILON)
        {
            int n = input.Length;
            for (int i = 0; i < n; ++i) 
            {
                input[i] *= outputSoundGain;
            }
        }
    }

    void OnAudioFilterRead(float[] input, int channels)
    {
        if (!audioSource)
        {
            OnDataReceived(input, channels);
        }
    }
}

}