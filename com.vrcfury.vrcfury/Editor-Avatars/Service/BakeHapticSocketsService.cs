using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VF.Builder;
using VF.Builder.Haptics;
using VF.Component;
using VF.Exceptions;
using VF.Feature;
using VF.Feature.Base;
using VF.Injector;
using VF.Inspector;
using VF.Menu;
using VF.Model.Feature;
using VF.Utils;
using VF.Utils.Controller;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using static VF.Utils.BlendtreeMath;

namespace VF.Service {
    [VFService]
    internal class BakeHapticSocketsService {

        [VFAutowired] private readonly GlobalsService globals;
        [VFAutowired] private readonly VFGameObject avatarObject;
        [VFAutowired] private readonly ActionClipService actionClipService;
        [VFAutowired] private readonly HapticAnimContactsService _hapticAnimContactsService;
        [VFAutowired] private readonly ForceStateInAnimatorService _forceStateInAnimatorService;
        [VFAutowired] private readonly SpsOptionsService spsOptions;
        [VFAutowired] private readonly HapticContactsService hapticContacts;
        [VFAutowired] private readonly DbtLayerService directTreeService;
        [VFAutowired] private readonly ClipFactoryService clipFactory;
        [VFAutowired] private readonly ScaleFactorService scaleFactorService;
        [VFAutowired] private readonly VRCAvatarDescriptor avatar;
        [VFAutowired] private readonly ControllersService controllers;
        [VFAutowired] private readonly FrameTimeService frameTimeService;
        [VFAutowired] private readonly SmoothingService smoothingService;
        private ControllerManager fx => controllers.GetFx();
        [VFAutowired] private readonly MenuService menuService;
        private MenuManager menu => menuService.GetMenu();

        [FeatureBuilderAction]
        public void Apply() {
            var saved = spsOptions.GetOptions().saveSockets;

            var fxRaw = fx.GetRaw();
            var fxPath = fxRaw != null ? AssetDatabase.GetAssetPath(fxRaw) : "<null>";
            if (string.IsNullOrEmpty(fxPath))
                fxPath = $"<in-memory controller: name='{fxRaw?.name ?? "null"}', instanceID={fxRaw?.GetInstanceID()}>";
            Debug.Log($"[VRCFury] Avatar: {avatarObject.GetPath()} | FX controller: {fxPath}");

            var enableAuto = avatarObject.GetComponentsInSelfAndChildren<VRCFuryHapticSocket>()
                .Where(o => o.addMenuItem && o.enableAuto)
                .ToArray()
                .Length >= 2;
            VFABool autoOn = null;
            AnimationClip autoOnClip = null;
            if (enableAuto) {
                autoOn = fx.NewBool("autoMode", synced: true, networkSynced: false, saved: saved);
                menu.NewMenuToggle($"{spsOptions.GetOptionsPath()}/<b>Auto Mode<\\/b>\n<size=20>Activates hole nearest to a VRCFury plug", autoOn);
                autoOnClip = clipFactory.NewClip("Enable SPS Auto Contacts");
                var directTree = directTreeService.Create($"Auto Mode Toggle");
                directTree.Add(
                    BlendtreeMath.GreaterThan(fx.IsLocal().AsFloat(), 0, name: "SPS: Auto Contacts").And(
                    BlendtreeMath.GreaterThan(autoOn.AsFloat(), 0, name: "When Local")
                ).create(autoOnClip, null));
            }
            
            var enableStealth = avatarObject.GetComponentsInSelfAndChildren<VRCFuryHapticSocket>()
                .Where(o => o.addMenuItem)
                .ToArray()
                .Length >= 1;
            VFABool stealthOn = null;
            if (enableStealth) {
                stealthOn = fx.NewBool("stealth", synced: true, saved: saved);
                menu.NewMenuToggle($"{spsOptions.GetOptionsPath()}/<b>Stealth Mode<\\/b>\n<size=20>Only local haptics,\nInvisible to others", stealthOn);
            }
            
            var enableMulti = avatarObject.GetComponentsInSelfAndChildren<VRCFuryHapticSocket>()
                .Where(o => o.addMenuItem)
                .ToArray()
                .Length >= 2;
            VFABool multiOn = null;
            if (enableMulti) {
                multiOn = fx.NewBool("multi", synced: true, networkSynced: false, saved: saved);
                var multiFolder = $"{spsOptions.GetOptionsPath()}/<b>Dual Mode<\\/b>\n<size=20>Allows 2 active sockets";
                menu.NewMenuToggle($"{multiFolder}/Enable Dual Mode", multiOn);
                menu.NewMenuButton($"{multiFolder}/<b>WARNING<\\/b>\n<size=20>Everyone else must use SPS or TPS - NO DPS!");
                menu.NewMenuButton($"{multiFolder}/<b>WARNING<\\/b>\n<size=20>Nobody else can use a hole at the same time");
                menu.NewMenuButton($"{multiFolder}/<b>WARNING<\\/b>\n<size=20>DO NOT ENABLE MORE THAN 2");
            }

            var autoSockets = new List<Tuple<string, VFABool, VFAFloat>>();
            var exclusiveTriggers = new List<(string,VFABool)>();
            var usedMenuNames = new HashSet<string>();
            var usedOscIds = new HashSet<string>();
            foreach (var socket in avatarObject.GetComponentsInSelfAndChildren<VRCFuryHapticSocket>()) {
                try {
                    VFGameObject obj = socket.owner();
                    PhysboneUtils.RemoveFromPhysbones(socket.owner());

                    var menuName = HapticUtils.MakeUniqueId(
                        usedMenuNames,
                        HapticUtils.GetPreferredId(socket, s => s.name, s => HapticUtils.GetFallbackId(s.owner()))
                    );
                    var oscId = HapticUtils.MakeUniqueId(
                        usedOscIds,
                        HapticUtils.GetPreferredId(socket, s => s.oscId, _ => menuName)
                    );
                    Debug.Log("Baking haptic component in " + socket.owner().GetPath() + " as " + oscId);
                    
                    VFABool toggleParam = null;
                    if (socket.addMenuItem) {
                        toggleParam = fx.NewBool(oscId, synced: true, saved: saved);
                        var icon = socket.menuIcon?.Get();
                        menu.NewMenuToggle($"{spsOptions.GetMenuPath()}/{menuName}", toggleParam, icon: icon);
                        exclusiveTriggers.Add((oscId, toggleParam));
                    }

                    if (!BuildTargetUtils.IsDesktop()) {
                        continue;
                    }

                    var bakeResult = VRCFuryHapticSocketEditor.Bake(socket);
                    if (bakeResult == null) continue;
                    
                    globals.addOtherFeature(new ShowInFirstPerson {
                        useObjOverride = true,
                        objOverride = bakeResult.bakeRoot,
                        onlyIfChildOfHead = true
                    });

                    VFGameObject haptics = null;
                    if (HapticsToggleMenuItem.Get() && !socket.fromSpsForAll) {
                        // Haptic receivers

                        // This is *90 because capsule length is actually "height", so we have to rotate it to make it a length
                        var capsuleRotation = Quaternion.Euler(90,0,0);
                        
                        var paramPrefix = "OGB/Orf/" + oscId.Replace('/','_');
                    
                        // Receivers
                        var handTouchZoneSize = VRCFuryHapticSocketEditor.GetHandTouchZoneSize(socket);
                        haptics = GameObjects.Create("Haptics", bakeResult.worldSpace);

                        var baseReq = new HapticContactsService.ReceiverRequest() {
                            obj = haptics,
                            usePrefix = false,
                            localOnly = true,
                            useHipAvoidance = socket.useHipAvoidance
                        };

                        if (handTouchZoneSize != null) {
                            var oscDepth = handTouchZoneSize.Item1;
                            var closeRadius = handTouchZoneSize.Item2;
                            {
                                var r = baseReq.Clone();
                                r.pos = Vector3.forward * -oscDepth;
                                r.paramName = paramPrefix + "/TouchSelf";
                                r.objName = "TouchSelf";
                                r.radius = oscDepth;
                                r.tags = HapticUtils.SelfContacts;
                                r.party = HapticUtils.ReceiverParty.Self;
                                hapticContacts.AddReceiver(r);
                                r.paramName = paramPrefix + "/TouchOthers";
                                r.objName = "TouchOthers";
                                r.tags = HapticUtils.BodyContacts;
                                r.party = HapticUtils.ReceiverParty.Others;
                                hapticContacts.AddReceiver(r);
                                // Legacy non-upgraded TPS detection
                                r.paramName = paramPrefix + "/PenOthers";
                                r.objName = "PenOthers";
                                r.tags = new[] { HapticUtils.CONTACT_PEN_MAIN };
                                hapticContacts.AddReceiver(r);
                            }
                            {
                                var r = baseReq.Clone();
                                r.pos = Vector3.forward * -(oscDepth / 2);
                                r.paramName = paramPrefix + "/TouchSelfClose";
                                r.objName = "TouchSelfClose";
                                r.radius = closeRadius;
                                r.tags = HapticUtils.SelfContacts;
                                r.party = HapticUtils.ReceiverParty.Self;
                                r.height = oscDepth;
                                r.rotation = capsuleRotation;
                                r.type = ContactReceiver.ReceiverType.Constant;
                                hapticContacts.AddReceiver(r);
                                r.paramName = paramPrefix + "/TouchOthersClose";
                                r.objName = "TouchOthersClose";
                                r.tags = HapticUtils.BodyContacts;
                                r.party = HapticUtils.ReceiverParty.Others;
                                hapticContacts.AddReceiver(r);
                                // Legacy non-upgraded TPS detection
                                r.paramName = paramPrefix + "/PenOthersClose";
                                r.objName = "PenOthersClose";
                                r.tags = new[] { HapticUtils.CONTACT_PEN_MAIN };
                                hapticContacts.AddReceiver(r);
                            }
                            {
                                var frotRadius = 0.1f;
                                var frotPos = 0.05f;
                                var r = baseReq.Clone();
                                r.pos = Vector3.forward * frotPos;
                                r.paramName = paramPrefix + "/FrotOthers";
                                r.objName = "FrotOthers";
                                r.radius = frotRadius;
                                r.tags = new[] { HapticUtils.TagTpsOrfRoot };
                                r.party = HapticUtils.ReceiverParty.Others;
                                hapticContacts.AddReceiver(r);
                            }
                        }

                        var req = baseReq.Clone();
                        req.radius = 1f;
                        req.paramName = paramPrefix + "/PenSelfNewRoot";
                        req.objName = "PenSelfNewRoot";
                        req.tags = new[] { HapticUtils.CONTACT_PEN_ROOT };
                        req.party = HapticUtils.ReceiverParty.Self;
                        hapticContacts.AddReceiver(req);
                        req.paramName = paramPrefix + "/PenOthersNewRoot";
                        req.objName = "PenOthersNewRoot";
                        req.party = HapticUtils.ReceiverParty.Others;
                        hapticContacts.AddReceiver(req);
                        req.paramName = paramPrefix + "/PenSelfNewTip";
                        req.objName = "PenSelfNewTip";
                        req.tags = new[] { HapticUtils.CONTACT_PEN_MAIN };
                        req.party = HapticUtils.ReceiverParty.Self;
                        hapticContacts.AddReceiver(req);
                        req.paramName = paramPrefix + "/PenOthersNewTip";
                        req.objName = "PenOthersNewTip";
                        req.party = HapticUtils.ReceiverParty.Others;
                        hapticContacts.AddReceiver(req);
                    }

                    var animObjects = new List<VFGameObject>();
                    var Contacts = new Lazy<SpsDepthContacts>(() => {
                        var scale = scaleFactorService.GetAdv(bakeResult.bakeRoot, bakeResult.worldSpace);
                        if (scale == null) throw new Exception("Scale cannot be null at this point. Is this a mobile build somehow?");
                        var (scaleFactor, scaleFactorContact1, scaleFactorContact2) = scale.Value;
                        var animRoot = GameObjects.Create("Animations", bakeResult.worldSpace);
                        animObjects.Add(animRoot);
                        animObjects.Add(scaleFactorContact1);
                        animObjects.Add(scaleFactorContact2);
                        var directTree = directTreeService.Create($"{oscId} - Depth Calculations");
                        var math = directTreeService.GetMath(directTree);
                        return new SpsDepthContacts(animRoot, oscId, hapticContacts, directTree, math, fx, frameTimeService, socket.useHipAvoidance, scaleFactor);
                    });

                    if (socket.depthActions2.Count > 0) {
                        _hapticAnimContactsService.CreateAnims(
                            $"{oscId} - Depth Animations",
                            socket.depthActions2,
                            socket.owner(),
                            oscId,
                            Contacts.Value
                        );
                    }

                    if (socket.touchActions.Count > 0) {
                        Debug.Log($"[VRCFury Socket {oscId}] Processing {socket.touchActions.Count} touch action(s)");
                        for (var tai = 0; tai < socket.touchActions.Count; tai++) {
                            var ta = socket.touchActions[tai];
                            Debug.Log($"[VRCFury Socket {oscId}] TouchAction[{tai}]: useWorldPowerLevels={ta.useWorldPowerLevels}");
                        }
                        var touchActParent = GameObjects.Create("TouchActions", bakeResult.worldSpace);
                        animObjects.Add(touchActParent);

                        const float touchRadius = 0.1f;
                        var baseReqTouch = new HapticContactsService.ReceiverRequest() {
                            obj = touchActParent,
                            radius = touchRadius,
                            type = ContactReceiver.ReceiverType.Constant,
                            localOnly = true,
                            useHipAvoidance = socket.useHipAvoidance,
                            usePrefix = false
                        };

                        var selfReqT = baseReqTouch.Clone();
                        selfReqT.paramName = $"{oscId}/SockTouchSelf";
                        selfReqT.objName = "SockTouchSelf";
                        selfReqT.tags = HapticUtils.SelfContacts;
                        selfReqT.party = HapticUtils.ReceiverParty.Self;
                        var socketTouchSelf = hapticContacts.AddReceiver(selfReqT);

                        var othersReqT = baseReqTouch.Clone();
                        othersReqT.paramName = $"{oscId}/SockTouchOthers";
                        othersReqT.objName = "SockTouchOthers";
                        othersReqT.tags = HapticUtils.BodyContacts;
                        othersReqT.party = HapticUtils.ReceiverParty.Others;
                        var socketTouchOthers = hapticContacts.AddReceiver(othersReqT);

                        var socketPowerParamCache = new Dictionary<string, VFAFloat>();

                        // Single synced OSC parameter (0…1): 0=no contact, 0.25=L1, 0.50=L2, 0.75=L3, 1.0=L4 (or proportional touch).
                        // usePrefix:false → clean OSC path /avatar/parameters/{oscId}/VibratePower
                        var vibIntensityParam = fx.NewFloat("OGB/Orf/" + oscId.Replace('/','_') + "/VibratePower", synced: true, def: 0f, usePrefix: false);
                        VFAFloat lastEffectiveWeight = null;
                        // For useWorldPowerLevels: store wRecvs from last iteration to drive VibratePower via
                        // VRCAvatarParameterDriver instead of AAP blend tree.
                        // AAP-driven synced floats are NOT sent via VRChat OSC — only ParameterDriver values are.
                        VFAFloat[] lastWRecvs = null;

                        var socketTouchNum = 0;
                        foreach (var touchAction in socket.touchActions) {
                            socketTouchNum++;
                            var prefix = $"{oscId}/SockTouch/{socketTouchNum}";

                            var dbt = directTreeService.Create($"{oscId} - Socket Touch {socketTouchNum} - Smooth");
                            var math = directTreeService.GetMath(dbt);

                            VFAFloat effectiveWeight;

                            if (touchAction.useWorldPowerLevels) {
                                // 4 Constant receivers, ONE active at a time (exclusive senders).
                                // Tags: VRCF_VibPow_L1..L4.  Level N activates only LN.
                                // wPower = L1*0.25 + L2*0.50 + L3*0.75 + L4*1.00  →  0 / 0.25 / 0.50 / 0.75 / 1.00
                                // Smoothed directly so level transitions are gradual.
                                var wParent = GameObjects.Create($"SockWorldPow_{socketTouchNum}", bakeResult.worldSpace);
                                animObjects.Add(wParent);
                                var wBaseReq = new HapticContactsService.ReceiverRequest() {
                                    obj             = wParent,
                                    radius          = 0.1f,
                                    type            = ContactReceiver.ReceiverType.Constant,
                                    localOnly       = true,
                                    useHipAvoidance = false,
                                    usePrefix       = false,
                                    party           = HapticUtils.ReceiverParty.Others
                                };
                                var wRecvs = new VFAFloat[4];
                                for (var li = 0; li < 4; li++) {
                                    var r = wBaseReq.Clone();
                                    r.paramName = $"{prefix}/WP{li + 1}";
                                    r.objName   = $"WP{li + 1}";
                                    r.tags      = new[] { $"VRCF_VibPow_L{li + 1}" };
                                    wRecvs[li]  = hapticContacts.AddReceiver(r);
                                }
                                var wPower = math.Add($"{prefix}/WorldPower",
                                    ((VFAFloatOrConst)wRecvs[0], 0.25f),
                                    ((VFAFloatOrConst)wRecvs[1], 0.50f),
                                    ((VFAFloatOrConst)wRecvs[2], 0.75f),
                                    ((VFAFloatOrConst)wRecvs[3], 1.00f));
                                effectiveWeight = smoothingService.Smooth(dbt, $"{prefix}/WSmoothed", wPower,
                                    touchAction.smoothingSeconds, useAcceleration: false);
                                // Save receiver refs so the post-loop block can drive VibratePower via ParameterDriver
                                lastWRecvs = wRecvs;
                            } else {
                                lastWRecvs = null;
                                VFAFloat rawContact;
                                if (touchAction.enableSelf) {
                                    rawContact = math.Max(socketTouchSelf, socketTouchOthers, $"{prefix}/RawContact");
                                } else {
                                    rawContact = socketTouchOthers;
                                }
                                var smoothed = smoothingService.Smooth(dbt, $"{prefix}/Smoothed", rawContact,
                                    touchAction.smoothingSeconds, useAcceleration: false);

                                if (touchAction.enablePowerLevels && !string.IsNullOrWhiteSpace(touchAction.powerMenuPath)) {
                                    var menuPath = touchAction.powerMenuPath;
                                    if (!socketPowerParamCache.TryGetValue(menuPath, out var powerParam)) {
                                        powerParam = fx.NewFloat($"{oscId}/SockPower", synced: true, def: 1f);
                                        menu.NewMenuSlider(menuPath, powerParam);
                                        socketPowerParamCache[menuPath] = powerParam;
                                    }
                                    effectiveWeight = math.Multiply($"{prefix}/Weighted", smoothed, powerParam);
                                } else {
                                    effectiveWeight = smoothed;
                                }
                            }

                            // Track the last effectiveWeight for VibIntensity output after the loop
                            lastEffectiveWeight = effectiveWeight;

                            var layer = fx.NewLayer($"{oscId} - Socket Touch {socketTouchNum}");
                            var off = layer.NewState("Off");
                            var on = layer.NewState("On");

                            var actionAdv = actionClipService.LoadStateAdv(prefix, touchAction.actionSet, socket.owner(), ActionClipService.MotionTimeMode.Auto);
                            if (actionAdv.useMotionTime) {
                                on.WithAnimation(actionAdv.onClip).MotionTime(effectiveWeight);
                            } else {
                                var tree = VFBlendTree1D.Create($"{prefix} tree", effectiveWeight);
                                tree.Add(0, clipFactory.GetEmptyClip());
                                tree.Add(1, actionAdv.onClip);
                                on.WithAnimation(tree);
                            }

                            var onWhen = effectiveWeight.IsGreaterThan(0.01f);
                            off.TransitionsTo(on).When(onWhen);
                            on.TransitionsTo(off).When(onWhen.Not());
                        }

                        // Drive vibIntensityParam for OSC output.
                        // IMPORTANT: AAP-driven synced floats are NOT sent via VRChat OSC — the OSC system only
                        // reads values set through VRCAvatarParameterDriver. Therefore:
                        //   • useWorldPowerLevels → state machine with Drives() (ParameterDriver), OSC-compatible
                        //   • continuous touch     → blend tree fallback (AAP), may not reach OSC in VRChat
                        if (lastEffectiveWeight != null) {
                            if (lastWRecvs != null) {
                                // useWorldPowerLevels: drive VibratePower via state-machine ParameterDriver
                                // values: 0 / 0.25 / 0.50 / 0.75 / 1.00  (one level active at a time)
                                var wPowLayer = fx.NewLayer($"{oscId} - VibIntensity");
                                var offState = wPowLayer.NewState("Off");
                                offState.Drives(vibIntensityParam, 0f);

                                var levelValues = new[] { 0.25f, 0.50f, 0.75f, 1.00f };
                                var levelStates = new VFState[4];
                                for (var li = 0; li < 4; li++) {
                                    levelStates[li] = wPowLayer.NewState($"L{li + 1}");
                                    levelStates[li].Drives(vibIntensityParam, levelValues[li]);
                                    // Return to Off when this level's receiver drops out
                                    levelStates[li].TransitionsTo(offState).When(lastWRecvs[li].IsLessThan(0.5f));
                                }
                                // From Off, activate highest available level (higher index = higher priority)
                                for (var li = 3; li >= 0; li--) {
                                    offState.TransitionsTo(levelStates[li]).When(lastWRecvs[li].IsGreaterThan(0.5f));
                                }
                            } else {
                                // Continuous touch: blend tree (AAP) — best effort, OSC output unreliable in VRChat.
                                // CopyInPlace (AAP accumulation) does NOT work for synced floats either.
                                var vibOff = clipFactory.NewClip($"{oscId} VibOff");
                                vibOff.SetAap(vibIntensityParam, 0f);
                                var vibOn = clipFactory.NewClip($"{oscId} VibOn");
                                vibOn.SetAap(vibIntensityParam, 1f);
                                var vibBlend = VFBlendTree1D.Create($"{oscId} VibBlend", lastEffectiveWeight);
                                vibBlend.Add(0, vibOff);
                                vibBlend.Add(1, vibOn);
                                var vibLayer = fx.NewLayer($"{oscId} - VibIntensity");
                                vibLayer.NewState("Drive").WithAnimation(vibBlend);
                                Debug.Log($"[VRCFury Socket {oscId}] VibratePower layer created (blend-tree mode)." +
                                    $"\n  OSC path: /avatar/parameters/{oscId}/VibratePower" +
                                    $"\n  BlendParam: '{lastEffectiveWeight.Name()}'" +
                                    $"\n  Values: 0=none … 1.0=full (proportional touch)" +
                                    $"\n  toggleControlled={toggleParam != null}");
                            }
                        }
                    }
                    
                    var injectDepthToFullControllerParams = globals.allBuildersInRun
                        .OfType<FullControllerBuilder>()
                        .Where(fc => fc.featureBaseObject.IsChildOf(socket.owner()))
                        .Select(fc => fc.injectSpsDepthParam)
                        .NotNull()
                        .ToList();
                    foreach (var i in injectDepthToFullControllerParams) {
                        directTreeService.GetMath(Contacts.Value.directTree)
                            .CopyInPlace(Contacts.Value.closestDistancePlugLengths.Value, i);
                    }
                    var injectVelocityToFullControllerParams = globals.allBuildersInRun
                        .OfType<FullControllerBuilder>()
                        .Where(fc => fc.featureBaseObject.IsChildOf(socket.owner()))
                        .Select(fc => fc.injectSpsVelocityParam)
                        .NotNull()
                        .ToList();
                    foreach (var i in injectVelocityToFullControllerParams) {
                        directTreeService.GetMath(Contacts.Value.directTree)
                            .CopyInPlace(Contacts.Value.velocity.Value, i);
                    }
                    if (socket.IsValidPlugLength) {
                        directTreeService.GetMath(Contacts.Value.directTree)
                            .CopyInPlace(Contacts.Value.closestLength.Value, socket.plugLengthParameterName);
                    }
                    if (socket.IsValidPlugWidth) {
                        directTreeService.GetMath(Contacts.Value.directTree)
                            .CopyInPlace(Contacts.Value.closestRadius.Value, socket.plugWidthParameterName);
                    }

                    // Do the toggle last so all the objects have been generated and can be toggled on/off
                    if (toggleParam != null) {
                        obj.active = true;
                        _forceStateInAnimatorService.ForceEnable(obj);

                        foreach (var child in new []{bakeResult.bakeRoot, bakeResult.senders, haptics, bakeResult.lights}.Concat(animObjects).NotNull()) {
                            child.active = false;
                        }

                        var onLocalClip = clipFactory.NewClip($"{oscId} (Local)");
                        foreach (var child in new []{bakeResult.bakeRoot, bakeResult.senders, haptics, bakeResult.lights}.Concat(animObjects).NotNull()) {
                            onLocalClip.SetEnabled(child, true);
                        }

                        var onRemoteClip = clipFactory.NewClip($"{oscId} (Remote)");
                        foreach (var child in new []{bakeResult.bakeRoot, bakeResult.senders, bakeResult.lights}.Concat(animObjects).NotNull()) {
                            onRemoteClip.SetEnabled(child, true);
                        }
                        
                        var onStealthClip = clipFactory.NewClip($"{oscId} (Stealth)");
                        foreach (var child in new []{bakeResult.bakeRoot, haptics}.NotNull()) {
                            onStealthClip.SetEnabled(child, true);
                        }

                        var activeClip = actionClipService.LoadState($"SPS - Active Animation for {oscId}", socket.activeActions);
                        if (new AnimatorIterator.Clips().From(activeClip).SelectMany(clip => clip.GetAllBindings()).Any()) {
                            var activeAnimParam = fx.NewFloat($"SPS - Active Animation for {oscId}");
                            var activeAnimLayer = fx.NewLayer($"SPS - Active Animation for {oscId}");
                            var off = activeAnimLayer.NewState("Off");
                            var on = activeAnimLayer.NewState("On").WithAnimation(activeClip);

                            off.TransitionsTo(on).When(activeAnimParam.IsGreaterThan(0));
                            on.TransitionsTo(off).When(activeAnimParam.IsLessThan(1));

                            onLocalClip.SetAap(activeAnimParam, 1);
                            onRemoteClip.SetAap(activeAnimParam, 1);
                        }

                        var gizmo = obj.GetComponent<VRCFurySocketGizmo>();
                        if (gizmo != null) {
                            gizmo.show = false;
                            onLocalClip.SetCurve(gizmo, "show", 1);
                            onRemoteClip.SetCurve(gizmo, "show", 1);
                        }

                        var localTree = BlendtreeMath.GreaterThan(stealthOn.AsFloat(), 0, name: "When Local")
                            .create(onStealthClip, onLocalClip);
                        var remoteTree = BlendtreeMath.GreaterThan(stealthOn.AsFloat(), 0, name: "When Remote")
                            .create(null, onRemoteClip);
                        var onTree = BlendtreeMath.GreaterThan(fx.IsLocal().AsFloat(), 0, name: $"SPS: When {oscId} On")
                            .create(localTree, remoteTree);
                        var directTree = directTreeService.Create($"{oscId} - Toggle");
                        directTree.Add(toggleParam.AsFloat(), onTree);

                        if (socket.enableAuto && autoOnClip != null) {
                            var autoReceiverObj = GameObjects.Create("AutoDistance", bakeResult.worldSpace);
                            var distParam = hapticContacts.AddReceiver(new HapticContactsService.ReceiverRequest() {
                                obj = autoReceiverObj,
                                paramName = oscId + "/AutoDistance",
                                objName = "Receiver",
                                radius = 0.3f,
                                tags = new[] { HapticUtils.CONTACT_PEN_MAIN },
                                party = HapticUtils.ReceiverParty.Others,
                                useHipAvoidance = socket.useHipAvoidance
                            });
                            autoReceiverObj.active = false;
                            foreach (var child in new []{bakeResult.bakeRoot, autoReceiverObj}.NotNull()) {
                                autoOnClip.SetEnabled(child, true);
                            }
                            autoSockets.Add(Tuple.Create(oscId, toggleParam, distParam));
                        }
                    }
                } catch (Exception e) {
                    throw new ExceptionWithCause($"Failed to bake SPS Socket: {socket.owner().GetPath(avatarObject)}", e);
                }
            }

            if (exclusiveTriggers.Count >= 2) {
                var exclusiveLayer = fx.NewLayer("SPS - Socket Exclusivity");
                exclusiveLayer.NewState("Start");
                foreach (var i in Enumerable.Range(0, exclusiveTriggers.Count)) {
                    var (name, on) = exclusiveTriggers[i];
                    var state = exclusiveLayer.NewState(name);
                    var when = on.IsTrue();
                    when = when.And(fx.IsLocal().IsTrue());
                    if (multiOn != null) when = when.And(multiOn.IsFalse());
                    if (stealthOn != null) when = when.And(stealthOn.IsFalse());
                    state.TransitionsFromAny().When(when);
                    foreach (var j in Enumerable.Range(0, exclusiveTriggers.Count)) {
                        if (i == j) continue;
                        var (_, otherOn) = exclusiveTriggers[j];
                        state.Drives(otherOn, false);
                    }
                }
            }

            if (autoOn != null && autoSockets.Count > 0) {
                var layer = fx.NewLayer("SPS - Auto Socket Comparison");
                var remoteTrap = layer.NewState("Remote trap");
                var stopped = layer.NewState("Stopped");
                remoteTrap.TransitionsTo(stopped).When(fx.IsLocal().IsTrue());
                var start = layer.NewState("Start").Move(stopped, 1, 0);
                stopped.TransitionsTo(start).When(autoOn.IsTrue());
                var stop = layer.NewState("Stop").Move(start, 1, 0);
                start.TransitionsTo(stop).When(autoOn.IsFalse());
                foreach (var auto in autoSockets) {
                    var (name, enabled, dist) = auto;
                    stop.Drives(enabled, false);
                }
                stop.TransitionsTo(stopped).When(fx.Always());

                var vsParam = fx.MakeAap("comparison");

                var states = new Dictionary<Tuple<int, int>, VFState>();
                for (var i = 0; i < autoSockets.Count; i++) {
                    var (aName, aEnabled, aDist) = autoSockets[i];
                    var triggerOn = layer.NewState($"Start {aName}").Move(start, i, 2);
                    triggerOn.Drives(aEnabled, true);
                    states[Tuple.Create(i,-1)] = triggerOn;
                    var triggerOff = layer.NewState($"Stop {aName}");
                    triggerOff.Drives(aEnabled, false);
                    triggerOff.TransitionsTo(start).When(fx.Always());
                    states[Tuple.Create(i,-2)] = triggerOff;
                    for (var j = 0; j < autoSockets.Count; j++) {
                        if (i == j) continue;
                        var (bName, bEnabled, bDist) = autoSockets[j];
                        var vs = layer.NewState($"{aName} vs {bName}").Move(triggerOff, 0, j+1);
                        var tree = VFBlendTreeDirect.Create($"{aName} vs {bName}");
                        tree.Add(bDist, vsParam.MakeSetter(1));
                        tree.Add(aDist, vsParam.MakeSetter(-1));
                        vs.WithAnimation(tree);
                        states[Tuple.Create(i,j)] = vs;
                    }
                }

                for (var i = 0; i < autoSockets.Count; i++) {
                    var (name, enabled, dist) = autoSockets[i];
                    var triggerOn = states[Tuple.Create(i, -1)];
                    var triggerOff = states[Tuple.Create(i, -2)];
                    var firstComparison = states[Tuple.Create(i, i == 0 ? 1 : 0)];
                    start.TransitionsTo(firstComparison).When(enabled.IsTrue());
                    triggerOn.TransitionsTo(firstComparison).When(fx.Always());
                    
                    for (var j = 0; j < autoSockets.Count; j++) {
                        if (i == j) continue;
                        var current = states[Tuple.Create(i, j)];
                        var otherActivate = states[Tuple.Create(j, -1)];

                        current.TransitionsTo(otherActivate).When(vsParam.AsFloat().IsGreaterThan(0));
                        
                        var nextI = j + 1;
                        if (nextI == i) nextI++;
                        if (nextI == autoSockets.Count) {
                            current.TransitionsTo(triggerOff).When(dist.IsGreaterThan(0).Not());
                            current.TransitionsTo(start).When(fx.Always());
                        } else {
                            var next = states[Tuple.Create(i, nextI)];
                            current.TransitionsTo(next).When(fx.Always());
                        }
                    }
                }

                var firstSocket = autoSockets[0];
                // If this isn't here, the first socket will never activate unless another one is already active
                start.TransitionsTo(states[Tuple.Create(0, -1)])
                    .When(firstSocket.Item2.IsFalse().And(firstSocket.Item3.IsGreaterThan(0)));
                start.TransitionsTo(states[Tuple.Create(0, 1)]).When(fx.Always());
            }

            // Diagnostic: dump all FX layers after Apply() completes
            {
                var fxRawEnd = fx.GetRaw();
                var allLayers = fx.GetLayers();
                var layerList = string.Join("\n  ", allLayers.Select((l, i) => $"[{i}] {l.name}"));
                Debug.Log($"[VRCFury] BakeHapticSockets Apply() DONE." +
                    $"\n  FX instanceID={fxRawEnd?.GetInstanceID()}, layerCount={allLayers.Count}" +
                    $"\n  {layerList}");
            }
        }
    }
}
