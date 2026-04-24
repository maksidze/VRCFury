using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

/// <summary>
/// World-side Pickupable vibrator that talks to a nearby avatar via VRC Contacts.
///
/// HOW IT WORKS
/// ────────────
/// 4 Contact Senders with tags VRCF_VibPow_L1..L4.
/// Only ONE sender is active at a time (exclusive).
/// Avatar has 4 matching Constant Receivers:
///   L1 active → avatar param = 0.25  (25 %)
///   L2 active → avatar param = 0.50  (50 %)
///   L3 active → avatar param = 0.75  (75 %)
///   L4 active → avatar param = 1.00 (100 %)
///   none      → avatar param = 0.00  (off)
///
/// Enable "Touch Animations → Use World Pickup Power Levels" on the avatar's
/// SPS Plug or SPS Socket.  VRCFury bakes the math:
///   wPower = L1×0.25 + L2×0.50 + L3×0.75 + L4×1.00
///   (since only one is active this always equals the correct fraction)
///   → smoothed → drives VibIntensity OSC param (0…1) for OGB.
///
/// Optional TPS senders (tpsPenetrating etc.) drive SPS Socket depth reaction.
///
/// WORLD OBJECT HIERARCHY
/// ─────────────────────
/// VibratorObject
/// ├── SenderL1   VRC Contact Sender  tag: VRCF_VibPow_L1  (exclusive, starts disabled)
/// ├── SenderL2   VRC Contact Sender  tag: VRCF_VibPow_L2  (exclusive, starts disabled)
/// ├── SenderL3   VRC Contact Sender  tag: VRCF_VibPow_L3  (exclusive, starts disabled)
/// ├── SenderL4   VRC Contact Sender  tag: VRCF_VibPow_L4  (exclusive, starts disabled)
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
    [Header("Power-Level Senders (exclusive — only one active at a time)")]
    [Tooltip("VRC Contact Sender — Tag: VRCF_VibPow_L1  →  25 %")]
    public GameObject senderL1;
    [Tooltip("VRC Contact Sender — Tag: VRCF_VibPow_L2  →  50 %")]
    public GameObject senderL2;
    [Tooltip("VRC Contact Sender — Tag: VRCF_VibPow_L3  →  75 %")]
    public GameObject senderL3;
    [Tooltip("VRC Contact Sender — Tag: VRCF_VibPow_L4  → 100 %")]
    public GameObject senderL4;

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
    [Tooltip("5 clips for levels 0-4.  Index 0 = Off.")]
    public AudioClip[] levelClips = new AudioClip[5];

    [Header("Debug")]
    public bool showDebugInfo = true;

    // ── Synced state ──────────────────────────────────────────────────────────
    [UdonSynced(UdonSyncMode.None)]
    private int _powerLevel = 0;   // 0=Off  1=25%  2=50%  3=75%  4=100%

    private int[] _powerPercents;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Start()
    {
        _powerPercents = new int[] { 0, 25, 50, 75, 100 };
        ApplyPowerLevel();
    }

    // ── VRC events ────────────────────────────────────────────────────────────

    /// <summary>Press Use to cycle: OFF → 25 % → 50 % → 75 % → 100 % → OFF</summary>
    public override void OnPickupUseDown()
    {
        _powerLevel = (_powerLevel + 1) % 5;
        ApplyPowerLevel();
        RequestSerialization();
    }

    public override void OnDeserialization()
    {
        ApplyPowerLevel();
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private void ApplyPowerLevel()
    {
        bool on = _powerLevel > 0;

        // Exclusive: only the sender for the current level is active.
        // Avatar math: L1×0.25 + L2×0.50 + L3×0.75 + L4×1.00 = correct fraction.
        SetActive(senderL1, _powerLevel == 1);
        SetActive(senderL2, _powerLevel == 2);
        SetActive(senderL3, _powerLevel == 3);
        SetActive(senderL4, _powerLevel == 4);

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

        string powerText = _powerLevel == 0 ? "OFF" : _powerPercents[_powerLevel] + " %";

        if (showDebugInfo)
        {
            string l1  = IsOn(senderL1)       ? "L1✓" : "L1✗";
            string l2  = IsOn(senderL2)       ? "L2✓" : "L2✗";
            string l3  = IsOn(senderL3)       ? "L3✓" : "L3✗";
            string l4  = IsOn(senderL4)       ? "L4✓" : "L4✗";
            string hnd = IsOn(handSender)     ? "Hand✓" : "Hand✗";
            string tps = IsOn(tpsPenetrating) ? "TPS✓"  : "TPS✗";
            statusLabel.text = powerText + "\n[" + l1 + " " + l2 + " " + l3 + " " + l4 + " " + hnd + " " + tps + "]";
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

