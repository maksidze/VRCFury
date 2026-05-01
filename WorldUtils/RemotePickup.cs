using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
public class RemotePickup : UdonSharpBehaviour
{
    [Header("Target")]
    [Tooltip("VibratorPickup controlled by this remote.")]
    public VibratorPickup targetVibrator;

    [Header("Distance")]
    [Tooltip("Optional point on this remote used for distance measurement. If empty, this transform is used.")]
    public Transform distanceOrigin;
    [Tooltip("Optional point on the target used for distance measurement. If empty, targetVibrator.transform is used.")]
    public Transform targetDistancePoint;
    [Tooltip("Optional control range. Set to 0 or below to allow control at any distance.")]
    public float maxControlDistance = 0f;
    public float distanceUpdateInterval = 0.25f;

    [Header("Preset Settings")]
    [Tooltip("Seconds between preset steps.")]
    public float presetStepSeconds = 0.25f;
    public bool loopPresets = true;
    public bool stopPresetWhenManualLevelSet = true;

    [Header("Preset 0")]
    public string preset0Name = "Pulse";
    public int[] preset0Levels = { 0, 20, 0, 20 };

    [Header("Preset 1")]
    public string preset1Name = "Wave";
    public int[] preset1Levels = { 0, 5, 10, 15, 20, 15, 10, 5 };

    [Header("Preset 2")]
    public string preset2Name = "Ramp";
    public int[] preset2Levels = { 0, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20 };

    [Header("Preset 3")]
    public string preset3Name = "Tease";
    public int[] preset3Levels = { 0, 0, 4, 0, 8, 0, 12, 0, 16, 0, 20 };

    [Header("Preset 4")]
    public string preset4Name = "Max";
    public int[] preset4Levels = { 20 };

    [Header("Preset 5")]
    public string preset5Name = "Custom 5";
    public int[] preset5Levels;

    [Header("Preset 6")]
    public string preset6Name = "Custom 6";
    public int[] preset6Levels;

    [Header("Preset 7")]
    public string preset7Name = "Custom 7";
    public int[] preset7Levels;

    [Header("Optional UI / Feedback")]
    public TMPro.TextMeshPro statusLabel;
    public AudioSource clickAudio;
    public AudioClip clickClip;

    [Header("Debug")]
    public bool showDebugInfo = true;

    private float _lastDistance;
    private float _nextDistanceUpdateTime;
    private int _activePresetIndex = -1;
    private int _activePresetStep;
    private float _nextPresetStepTime;

    private void Start()
    {
        RefreshStatus();
    }

    private void Update()
    {
        if (Time.time >= _nextDistanceUpdateTime)
        {
            _nextDistanceUpdateTime = Time.time + Mathf.Max(0.05f, distanceUpdateInterval);
            RefreshStatus();
        }

        if (_activePresetIndex >= 0 && Time.time >= _nextPresetStepTime)
        {
            ApplyPresetStep();
        }
    }

    public override void OnPickupUseDown()
    {
        CycleLevel();
    }

    public override void Interact()
    {
        CycleLevel();
    }

    public void CycleLevel()
    {
        if (targetVibrator == null) return;

        SetLevel((targetVibrator.GetPowerLevel() + 1) % 21);
    }

    public void TurnOff()
    {
        SetLevel(0);
    }

    public void SetLevel(int level)
    {
        if (stopPresetWhenManualLevelSet)
        {
            StopPreset();
        }

        SetTargetLevel(level);
    }

    public void SetLevel00() { SetLevel(0); }
    public void SetLevel01() { SetLevel(1); }
    public void SetLevel02() { SetLevel(2); }
    public void SetLevel03() { SetLevel(3); }
    public void SetLevel04() { SetLevel(4); }
    public void SetLevel05() { SetLevel(5); }
    public void SetLevel06() { SetLevel(6); }
    public void SetLevel07() { SetLevel(7); }
    public void SetLevel08() { SetLevel(8); }
    public void SetLevel09() { SetLevel(9); }
    public void SetLevel10() { SetLevel(10); }
    public void SetLevel11() { SetLevel(11); }
    public void SetLevel12() { SetLevel(12); }
    public void SetLevel13() { SetLevel(13); }
    public void SetLevel14() { SetLevel(14); }
    public void SetLevel15() { SetLevel(15); }
    public void SetLevel16() { SetLevel(16); }
    public void SetLevel17() { SetLevel(17); }
    public void SetLevel18() { SetLevel(18); }
    public void SetLevel19() { SetLevel(19); }
    public void SetLevel20() { SetLevel(20); }

    public void PlayPreset0() { StartPreset(0); }
    public void PlayPreset1() { StartPreset(1); }
    public void PlayPreset2() { StartPreset(2); }
    public void PlayPreset3() { StartPreset(3); }
    public void PlayPreset4() { StartPreset(4); }
    public void PlayPreset5() { StartPreset(5); }
    public void PlayPreset6() { StartPreset(6); }
    public void PlayPreset7() { StartPreset(7); }

    public void StopPreset()
    {
        _activePresetIndex = -1;
        _activePresetStep = 0;
        RefreshStatus();
    }

    public void StartPreset(int presetIndex)
    {
        Debug.Log("Starting preset " + presetIndex);
        if (!HasPresetSteps(presetIndex))
        {
            StopPreset();
            return;
        }

        _activePresetIndex = Mathf.Clamp(presetIndex, 0, 7);
        _activePresetStep = 0;
        ApplyPresetStep();
    }

    private void ApplyPresetStep()
    {
        int length = GetPresetLength(_activePresetIndex);
        if (length <= 0)
        {
            StopPreset();
            return;
        }

        if (_activePresetStep >= length)
        {
            if (!loopPresets)
            {
                StopPreset();
                return;
            }

            _activePresetStep = 0;
        }

        int level = GetPresetLevel(_activePresetIndex, _activePresetStep);
        _activePresetStep++;
        SetTargetLevel(level);
        _nextPresetStepTime = Time.time + Mathf.Max(0.05f, presetStepSeconds);
    }

    private void SetTargetLevel(int level)
    {
        RefreshDistance();

        if (targetVibrator == null)
        {
            RefreshStatus();
            return;
        }

        if (!IsTargetInRange())
        {
            RefreshStatus();
            return;
        }

        targetVibrator.SetPowerLevel(Mathf.Clamp(level, 0, 20));
        PlayClickSound();
        RefreshStatus();
    }

    private bool HasPresetSteps(int presetIndex)
    {
        return GetPresetLength(presetIndex) > 0;
    }

    private int GetPresetLength(int presetIndex)
    {
        if (presetIndex == 0 && preset0Levels != null) return preset0Levels.Length;
        if (presetIndex == 1 && preset1Levels != null) return preset1Levels.Length;
        if (presetIndex == 2 && preset2Levels != null) return preset2Levels.Length;
        if (presetIndex == 3 && preset3Levels != null) return preset3Levels.Length;
        if (presetIndex == 4 && preset4Levels != null) return preset4Levels.Length;
        if (presetIndex == 5 && preset5Levels != null) return preset5Levels.Length;
        if (presetIndex == 6 && preset6Levels != null) return preset6Levels.Length;
        if (presetIndex == 7 && preset7Levels != null) return preset7Levels.Length;
        return 0;
    }

    private int GetPresetLevel(int presetIndex, int stepIndex)
    {
        if (presetIndex == 0) return preset0Levels[stepIndex];
        if (presetIndex == 1) return preset1Levels[stepIndex];
        if (presetIndex == 2) return preset2Levels[stepIndex];
        if (presetIndex == 3) return preset3Levels[stepIndex];
        if (presetIndex == 4) return preset4Levels[stepIndex];
        if (presetIndex == 5) return preset5Levels[stepIndex];
        if (presetIndex == 6) return preset6Levels[stepIndex];
        if (presetIndex == 7) return preset7Levels[stepIndex];
        return 0;
    }

    private string GetPresetName(int presetIndex)
    {
        if (presetIndex == 0) return preset0Name;
        if (presetIndex == 1) return preset1Name;
        if (presetIndex == 2) return preset2Name;
        if (presetIndex == 3) return preset3Name;
        if (presetIndex == 4) return preset4Name;
        if (presetIndex == 5) return preset5Name;
        if (presetIndex == 6) return preset6Name;
        if (presetIndex == 7) return preset7Name;
        return "None";
    }

    private void RefreshStatus()
    {
        RefreshDistance();

        if (statusLabel == null) return;

        string targetText = "No target";
        string distanceText = "-- m";
        string presetText = "Manual";

        if (targetVibrator != null)
        {
            int powerLevel = targetVibrator.GetPowerLevel();
            float roundedDistance = Mathf.Round(_lastDistance * 100f) / 100f;
            targetText = powerLevel == 0 ? "OFF" : (powerLevel * 5) + " %";
            distanceText = roundedDistance + " m";
        }

        if (_activePresetIndex >= 0)
        {
            presetText = GetPresetName(_activePresetIndex) + " [" + _activePresetStep + "/" + GetPresetLength(_activePresetIndex) + "]";
        }

        if (showDebugInfo)
        {
            string rangeText = IsTargetInRange() ? "Range OK" : "Out of range";
            statusLabel.text = "Remote: " + targetText + "\nDistance: " + distanceText + "\nMode: " + presetText + "\n" + rangeText;
        }
        else
        {
            statusLabel.text = targetText + "\n" + distanceText + "\n" + presetText;
        }
    }

    private void RefreshDistance()
    {
        if (targetVibrator == null)
        {
            _lastDistance = 0f;
            return;
        }

        Transform from = distanceOrigin != null ? distanceOrigin : transform;
        Transform to = targetDistancePoint != null ? targetDistancePoint : targetVibrator.transform;
        _lastDistance = Vector3.Distance(from.position, to.position);
    }

    private bool IsTargetInRange()
    {
        return targetVibrator != null && (maxControlDistance <= 0f || _lastDistance <= maxControlDistance);
    }

    private void PlayClickSound()
    {
        if (clickAudio == null || clickClip == null) return;

        clickAudio.PlayOneShot(clickClip);
    }

    public float GetLastDistance()
    {
        return _lastDistance;
    }
}
