using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

/// <summary>
/// World-side Pickupable vibrator that talks to a nearby avatar via VRC Contacts.
///
/// HOW IT WORKS
/// ────────────
/// 5 Contact Senders with tags VRCF_VibPow_B0..B4.
/// Each sender is one bit in a 5-bit power number.
/// Avatar has 5 matching Constant Receivers:
///   B0 active -> +1
///   B1 active -> +2
///   B2 active -> +4
///   B3 active -> +8
///   B4 active -> +16
///   decoded 0..20 -> avatar param 0.00..1.00 in 5 % steps.
///   decoded 21..31 are reserved and are not sent by this script.
///
/// Enable "Touch Animations → Use World Pickup Power Levels" on the avatar's
/// SPS Plug or SPS Socket.  VRCFury bakes the math:
///   wPower = (B0×1 + B1×2 + B2×4 + B3×8 + B4×16) / 20
///   → smoothed → drives VibIntensity OSC param (0…1) for OGB.
///
/// Optional TPS senders (tpsPenetrating etc.) drive SPS Socket depth reaction.
///
/// WORLD OBJECT HIERARCHY
/// ─────────────────────
/// VibratorObject
/// ├── SenderB0   VRC Contact Sender  tag: VRCF_VibPow_B0  (bit 1, starts disabled)
/// ├── SenderB1   VRC Contact Sender  tag: VRCF_VibPow_B1  (bit 2, starts disabled)
/// ├── SenderB2   VRC Contact Sender  tag: VRCF_VibPow_B2  (bit 4, starts disabled)
/// ├── SenderB3   VRC Contact Sender  tag: VRCF_VibPow_B3  (bit 8, starts disabled)
/// ├── SenderB4   VRC Contact Sender  tag: VRCF_VibPow_B4  (bit 16, starts disabled)
/// ├── HandSender VRC Contact Sender  tag: Hand             (starts disabled)
/// └── TpsSenders/ (optional)
///     ├── Penetrating  tag: TPS_Pen_Penetrating
///     ├── WidthHelper  tag: TPS_Pen_Width
///     ├── Envelope     tag: TPS_Pen_Close
///     └── Root         tag: TPS_Pen_Root
/// </summary>
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class VibratorPickup : UdonSharpBehaviour
{
    // ── Power-level senders ───────────────────────────────────────────────────
    private const int MaxPowerLevel = 20;

    [Header("Power-Level Bit Senders (5-bit value, levels 0-20 used)")]
    [Tooltip("VRC Contact Sender — Tag: VRCF_VibPow_B0  ->  bit value 1")]
    public GameObject senderB0;
    [Tooltip("VRC Contact Sender — Tag: VRCF_VibPow_B1  ->  bit value 2")]
    public GameObject senderB1;
    [Tooltip("VRC Contact Sender — Tag: VRCF_VibPow_B2  ->  bit value 4")]
    public GameObject senderB2;
    [Tooltip("VRC Contact Sender — Tag: VRCF_VibPow_B3  ->  bit value 8")]
    public GameObject senderB3;
    [Tooltip("VRC Contact Sender — Tag: VRCF_VibPow_B4  ->  bit value 16")]
    public GameObject senderB4;

    [Header("Standard body sender (tag: Hand, activates when power > 0)")]
    public GameObject handSender;

    // ── TPS senders (SPS Socket depth reaction, optional) ────────────────────
    [Header("TPS Senders for SPS Socket depth reaction (optional)")]
    public GameObject tpsPenetrating;
    public GameObject tpsWidthHelper;
    public GameObject tpsEnvelope;
    public GameObject tpsRoot;

    // ── UI / Feedback ─────────────────────────────────────────────────────────
    [Header("Optional UI / Feedback")]
    public TMPro.TextMeshPro statusLabel;
    public AudioSource clickAudio;
    [Tooltip("21 clips for levels 0-20. Index 0 = Off.")]
    public AudioClip[] levelClips = new AudioClip[21];

    [Header("Debug")]
    public bool showDebugInfo = true;

    [Header("Control")]
    [Tooltip("If false, this object can only be controlled by another script, such as RemotePickup.")]
    public bool allowDirectControl = false;

    // ── Synced state ──────────────────────────────────────────────────────────
    [UdonSynced(UdonSyncMode.None)]
    private int _powerLevel = 0;   // 0=Off, 1..20 = 5%..100%

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Start()
    {
        ApplyPowerLevel();
    }

    // ── VRC events ────────────────────────────────────────────────────────────

    /// <summary>Press Use to cycle: OFF -> 5 % -> 10 % -> ... -> 100 % -> OFF</summary>
    public override void OnPickupUseDown()
    {
        if (allowDirectControl)
        {
            CyclePowerLevel();
        }
    }

    public override void Interact()
    {
        if (allowDirectControl)
        {
            CyclePowerLevel();
        }
    }

    public void CyclePowerLevel()
    {
        SetPowerLevel((_powerLevel + 1) % (MaxPowerLevel + 1));
    }

    public void SetPowerLevel(int powerLevel)
    {
        if (Networking.LocalPlayer != null && !Networking.IsOwner(gameObject))
        {
            Networking.SetOwner(Networking.LocalPlayer, gameObject);
        }

        _powerLevel = Mathf.Clamp(powerLevel, 0, MaxPowerLevel);
        ApplyPowerLevel();
        RequestSerialization();
    }

    public void TurnOff()
    {
        SetPowerLevel(0);
    }

    public int GetPowerLevel()
    {
        return _powerLevel;
    }

    public override void OnDeserialization()
    {
        ApplyPowerLevel();
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private void ApplyPowerLevel()
    {
        bool on = _powerLevel > 0;

        // Bitfield protocol: decoded value / 20 = Lovense-compatible intensity.
        SetActive(senderB0, (_powerLevel & 1) != 0);
        SetActive(senderB1, (_powerLevel & 2) != 0);
        SetActive(senderB2, (_powerLevel & 4) != 0);
        SetActive(senderB3, (_powerLevel & 8) != 0);
        SetActive(senderB4, (_powerLevel & 16) != 0);

        // Hand sender — activates standard body-contact haptics when on
        SetActive(handSender, on);

        // TPS senders — drive SPS Socket depth reaction
        SetActive(tpsPenetrating, on);
        SetActive(tpsWidthHelper, on);
        SetActive(tpsEnvelope, on);
        SetActive(tpsRoot, on);

        UpdateLabel();
        PlayClickSound();
    }

    private void UpdateLabel()
    {
        if (statusLabel == null) return;

        string powerText = _powerLevel == 0 ? "OFF" : (_powerLevel * 5) + " %";

        if (showDebugInfo)
        {
            string b0  = IsOn(senderB0)       ? "B0✓" : "B0✗";
            string b1  = IsOn(senderB1)       ? "B1✓" : "B1✗";
            string b2  = IsOn(senderB2)       ? "B2✓" : "B2✗";
            string b3  = IsOn(senderB3)       ? "B3✓" : "B3✗";
            string b4  = IsOn(senderB4)       ? "B4✓" : "B4✗";
            string hnd = IsOn(handSender)     ? "Hand✓" : "Hand✗";
            string tps = IsOn(tpsPenetrating) ? "TPS✓"  : "TPS✗";
            statusLabel.text = powerText + "\n[" + b0 + " " + b1 + " " + b2 + " " + b3 + " " + b4 + " " + hnd + " " + tps + "]";
        }
        else
        {
            statusLabel.text = powerText;
        }
    }

    private void PlayClickSound()
    {
        if (clickAudio == null || levelClips == null) return;
        if (_powerLevel >= levelClips.Length) return;
        var clip = levelClips[_powerLevel];
        if (clip != null) clickAudio.PlayOneShot(clip);
    }

    private void SetActive(GameObject obj, bool active)
    {
        if (obj != null) obj.SetActive(active);
    }

    private bool IsOn(GameObject obj)
    {
        return obj != null && obj.activeSelf;
    }
}

