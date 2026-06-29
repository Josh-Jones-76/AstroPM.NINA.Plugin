using System;
using System.Collections.Generic;
using System.Linq;

namespace AstroPM.NINA.Plugin.Models {

    public class TimeSlot {
        public DateTime UtcStart { get; set; }
        public double SunAltDeg { get; set; }
        public double MoonAltDeg { get; set; }
        public double MoonIllumPct { get; set; }
    }

    public class TargetProfile {
        public ProjectTarget Target { get; set; }
        public ObservingConstraints Constraints { get; set; }
        public double[] AltitudePerSlot { get; set; } = Array.Empty<double>();
        public double[] MoonSepPerSlot { get; set; } = Array.Empty<double>();
        public bool[] SlotUsable { get; set; } = Array.Empty<bool>();
        public bool[] SlotMoonOk { get; set; } = Array.Empty<bool>();

        // V4: per-tier moon avoidance (tier 0 = No LA, ascending restrictiveness)
        public TierInfo[] Tiers { get; set; } = Array.Empty<TierInfo>();
        public double[] TierRemainingSec { get; set; } = Array.Empty<double>();
        public bool[][] TierSlotSafe { get; set; } = Array.Empty<bool[]>();

        public double RemainingLunarFreeSec { get; set; }
        public double RemainingNonLunarSec { get; set; }
        public double RemainingTotalSec { get; set; }
        public int WindowStartSlot { get; set; } = -1;
        public int WindowEndSlot { get; set; } = -1;
        public double AllocatedSec { get; set; }
        public int? PanelIndex { get; set; }
        public string DisplayName => PanelIndex.HasValue
            ? $"{Target.TargetName} Panel {PanelIndex.Value + 1}"
            : Target.TargetName;
    }

    public class SimLogEntry {
        public string Command { get; set; } = "";
        // Friendlier label for the log's Command column. "Slew" is really NINA's
        // Slew, Center & Rotate instruction (rotate only when a rotator is present).
        // Internal Command keeps the short key so all engine/grouping logic is unaffected.
        public string CommandDisplay => Command == "Slew" ? "Slew, Center, & Rotate" : Command;
        public string Time { get; set; } = "";
        public string Target { get; set; } = "";
        public string Panel { get; set; } = "";
        public string SubNum { get; set; } = "";
        public string Filter { get; set; } = "";
        public string Exposure { get; set; } = "";
        public string Gain { get; set; } = "";
        public string Offset { get; set; } = "";
        public string Bin { get; set; } = "";
        public string ReadoutMode { get; set; } = "";
        public string Rotation { get; set; } = "";
        public string RA { get; set; } = "";
        public string DEC { get; set; } = "";
        public string Sort1 { get; set; } = "";
        public string Sort2 { get; set; } = "";
        public string Sort3 { get; set; } = "";
        public string Sort4 { get; set; } = "";
        public string Altitude { get; set; } = "";
        public string MoonSep { get; set; } = "";
        public string MoonSafe { get; set; } = "";
        public string MoonAvoidSep { get; set; } = "";
        public string DarkSafe { get; set; } = "";
        public string LaEnabled { get; set; } = "";
        public string LaSafe { get; set; } = "";
        public string FilterProfile { get; set; } = "";
        public string AcceptedProfiles { get; set; } = "";
        public int SlotIndex { get; set; } = -1;
        public DateTime UtcTime { get; set; }
    }

    public class ScheduleSessionState {
        public Dictionary<TargetProfile, double> AllocatedSec = new Dictionary<TargetProfile, double>();
        public Dictionary<int, int> EmittedSubsByEsIdx = new Dictionary<int, int>();
        public Dictionary<TargetProfile, (string FilterName, string PanelLabel, int SubsOnFilter)> FilterCycle
            = new Dictionary<TargetProfile, (string, string, int)>();
        public Dictionary<TargetProfile, int> SubCounter = new Dictionary<TargetProfile, int>();
        public Dictionary<(TargetProfile, string), double> PanelTimeOnTarget
            = new Dictionary<(TargetProfile, string), double>();

        private int EsKey(int targetIdx, int panelIdx, int esIdx) => targetIdx * 10000 + panelIdx * 100 + esIdx;

        public int RemainingForEs(int targetIdx, int panelIdx, int esIdx, ExposureSetData es) {
            int key = EsKey(targetIdx, panelIdx, esIdx);
            return Math.Max(0, es.Remaining) - (EmittedSubsByEsIdx.ContainsKey(key) ? EmittedSubsByEsIdx[key] : 0);
        }

        public void RecordExposure(TargetProfile target, int targetIdx, int panelIdx, int esIdx, ExposureSetData es) {
            if (!AllocatedSec.ContainsKey(target)) AllocatedSec[target] = 0;
            AllocatedSec[target] += es.ExposureLengthSec;
            int key = EsKey(targetIdx, panelIdx, esIdx);
            if (!EmittedSubsByEsIdx.ContainsKey(key)) EmittedSubsByEsIdx[key] = 0;
            EmittedSubsByEsIdx[key]++;
        }

        public int NextSub(TargetProfile target) {
            if (!SubCounter.ContainsKey(target)) SubCounter[target] = 0;
            SubCounter[target]++;
            return SubCounter[target];
        }

        public bool HasImageableWork(TargetProfile target, int targetIdx, bool moonDown) {
            for (int pi = 0; pi < target.Target.Panels.Count; pi++) {
                var panel = target.Target.Panels[pi];
                for (int ei = 0; ei < panel.ExposureSets.Count; ei++) {
                    var es = panel.ExposureSets[ei];
                    if (RemainingForEs(targetIdx, pi, ei, es) > 0 && (!es.HasMoonAvoidance || moonDown))
                        return true;
                }
            }
            return false;
        }

        public bool HasLunarAvoidWork(TargetProfile target, int targetIdx) {
            for (int pi = 0; pi < target.Target.Panels.Count; pi++) {
                var panel = target.Target.Panels[pi];
                for (int ei = 0; ei < panel.ExposureSets.Count; ei++) {
                    var es = panel.ExposureSets[ei];
                    if (es.HasMoonAvoidance && RemainingForEs(targetIdx, pi, ei, es) > 0)
                        return true;
                }
            }
            return false;
        }

        public double RemainingWorkSec(TargetProfile target, int targetIdx, bool moonDown) {
            double total = 0;
            for (int pi = 0; pi < target.Target.Panels.Count; pi++) {
                var panel = target.Target.Panels[pi];
                for (int ei = 0; ei < panel.ExposureSets.Count; ei++) {
                    var es = panel.ExposureSets[ei];
                    int rem = RemainingForEs(targetIdx, pi, ei, es);
                    if (rem > 0 && (!es.HasMoonAvoidance || moonDown))
                        total += rem * es.ExposureLengthSec;
                }
            }
            return total;
        }
    }

    public static class SessionScheduler {

        public static List<TimeSlot> BuildTimeSlots(DateTime date, double latDeg, double lonDeg, TimeZoneInfo tz) {
            var scanStart = new DateTime(date.Year, date.Month, date.Day, 16, 0, 0, DateTimeKind.Unspecified);
            if (tz.IsInvalidTime(scanStart)) scanStart = scanStart.AddHours(1);
            var utcScan = TimeZoneInfo.ConvertTimeToUtc(scanStart, tz);

            DateTime? civilDusk = null, civilDawn = null;
            double prevSun = AstroCalculator.SunAltitudeAtTime(utcScan, latDeg, lonDeg);

            for (int i = 1; i <= 216; i++) {
                var utc = utcScan.AddMinutes(i * 5);
                double sunAlt = AstroCalculator.SunAltitudeAtTime(utc, latDeg, lonDeg);

                if (civilDusk == null && prevSun >= -6 && sunAlt < -6)
                    civilDusk = utc;
                if (civilDusk != null && civilDawn == null && prevSun < -6 && sunAlt >= -6)
                    civilDawn = utc;

                prevSun = sunAlt;
            }

            var local6pm = new DateTime(date.Year, date.Month, date.Day, 18, 0, 0, DateTimeKind.Unspecified);
            if (tz.IsInvalidTime(local6pm)) local6pm = local6pm.AddHours(1);

            var utcStart = civilDusk != null
                ? civilDusk.Value.AddHours(-1)
                : TimeZoneInfo.ConvertTimeToUtc(local6pm, tz);
            var utcEnd = civilDawn != null
                ? civilDawn.Value.AddHours(1)
                : utcStart.AddHours(12);

            utcStart = new DateTime(utcStart.Year, utcStart.Month, utcStart.Day,
                utcStart.Hour, (utcStart.Minute / 5) * 5, 0, DateTimeKind.Utc);
            utcEnd = utcEnd.AddMinutes(4);
            utcEnd = new DateTime(utcEnd.Year, utcEnd.Month, utcEnd.Day,
                utcEnd.Hour, (utcEnd.Minute / 5) * 5, 0, DateTimeKind.Utc);

            int slotCount = Math.Max(1, (int)((utcEnd - utcStart).TotalMinutes / 5));
            var slots = new List<TimeSlot>(slotCount);
            for (int i = 0; i < slotCount; i++) {
                var utc = utcStart.AddMinutes(i * 5);
                slots.Add(new TimeSlot {
                    UtcStart = utc,
                    SunAltDeg = AstroCalculator.SunAltitudeAtTime(utc, latDeg, lonDeg),
                    MoonAltDeg = AstroCalculator.MoonAltitudeAtMidnight(utc, latDeg, lonDeg),
                    MoonIllumPct = AstroCalculator.MoonIllumination(utc),
                });
            }
            return slots;
        }

        public static List<TargetProfile> BuildTargetProfiles(List<ProjectTarget> targets, List<TimeSlot> slots,
            double latDeg, double lonDeg, bool mosaicPanelPreference = false, HorizonProfile customHorizon = null) {
            // Custom .hrz horizon (NINA's profile horizon file, if loaded): a slot is only
            // usable when the target clears the obstruction line at its azimuth, in
            // addition to MinTargetAltitude. The desktop simulator applies the same check
            // using the site's stored .hrz, so both produce identical schedules when the
            // same file is loaded on both sides.
            var profiles = new List<TargetProfile>();
            foreach (var target in targets) {
                var constraints = target.ToObservingConstraints();

                var altitudes = new double[slots.Count];
                var moonSeps = new double[slots.Count];
                var usable = new bool[slots.Count];
                var moonOk = new bool[slots.Count];

                int windowStart = -1, windowEnd = -1;

                for (int i = 0; i < slots.Count; i++) {
                    var slot = slots[i];
                    altitudes[i] = AstroCalculator.TargetAltitudeAtTime(
                        slot.UtcStart, target.RaHours, target.DecDegrees, latDeg, lonDeg);
                    moonSeps[i] = AstroCalculator.MoonTargetSeparation(
                        slot.UtcStart, target.RaHours, target.DecDegrees, lonDeg);

                    bool isDark = slot.SunAltDeg < constraints.SunAltitudeThreshold;
                    bool aboveMinAlt = altitudes[i] >= constraints.MinTargetAltitude;
                    if (aboveMinAlt && customHorizon != null) {
                        var az = AstroCalculator.TargetAzimuthAtTime(
                            slot.UtcStart, target.RaHours, target.DecDegrees, latDeg, lonDeg);
                        aboveMinAlt = altitudes[i] >= customHorizon.AltitudeAt(az);
                    }
                    usable[i] = isDark && aboveMinAlt;

                    if (usable[i]) {
                        if (windowStart < 0) windowStart = i;
                        windowEnd = i;
                    }

                    if (!constraints.MoonAvoidanceEnabled) {
                        moonOk[i] = true;
                    } else {
                        if (slot.MoonAltDeg <= constraints.MaxMoonAltitude)
                            moonOk[i] = true;
                        else if (slot.MoonIllumPct <= constraints.MaxMoonIlluminationPct)
                            moonOk[i] = true;
                        else {
                            var requiredSep = AstroCalculator.RequiredMoonSeparation(slot.UtcStart, slot.MoonAltDeg, constraints);
                            moonOk[i] = moonSeps[i] >= requiredSep;
                        }
                    }
                }

                double lunarFreeSec = 0, nonLunarSec = 0;
                foreach (var panel in target.Panels) {
                    foreach (var es in panel.ExposureSets) {
                        var remaining = es.Remaining * es.ExposureLengthSec;
                        if (es.HasMoonAvoidance)
                            lunarFreeSec += remaining;
                        else
                            nonLunarSec += remaining;
                    }
                }

                double usableHrs = 0;
                if (windowStart >= 0) {
                    int longestRun = 0, curRun = 0;
                    for (int i = 0; i < slots.Count; i++) {
                        if (usable[i]) { curRun++; if (curRun > longestRun) longestRun = curRun; }
                        else curRun = 0;
                    }
                    usableHrs = longestRun * 5.0 / 60.0;
                }

                if (usableHrs < constraints.MinTimeOnTargetHrs)
                    continue;

                // V4: Classify all exposure sets into tiers and compute per-tier slot safety
                var allEs = target.Panels.SelectMany(pn => pn.ExposureSets).ToList();
                var (tiers, esToTier) = TierClassifier.ClassifyExposureSets(allEs, constraints);

                var tierSlotSafe = new bool[tiers.Length][];
                for (int t = 0; t < tiers.Length; t++) {
                    tierSlotSafe[t] = new bool[slots.Count];
                    var tier = tiers[t];

                    if (tier.TierIndex == 0) {
                        // Tier 0 (No LA) — always safe
                        Array.Fill(tierSlotSafe[t], true);
                    } else if (tier.RequiresMoonDown) {
                        // No Moon tier — only safe when moon is below horizon
                        for (int i = 0; i < slots.Count; i++)
                            tierSlotSafe[t][i] = slots[i].MoonAltDeg <= 0;
                    } else {
                        // Named profile or Project Default — use IsExposureSetMoonSafe with a representative ES
                        var repEs = allEs.FirstOrDefault(es => esToTier.TryGetValue(es, out var et) && et == t);
                        for (int i = 0; i < slots.Count; i++) {
                            if (slots[i].MoonAltDeg <= 0)
                                tierSlotSafe[t][i] = true;
                            else if (repEs != null)
                                tierSlotSafe[t][i] = IsExposureSetMoonSafe(repEs, slots[i], moonSeps[i], constraints);
                            else
                                tierSlotSafe[t][i] = false;
                        }
                    }
                }

                // Derive moonOk[] from tiers (any tier safe → slot is moon-ok)
                for (int i = 0; i < slots.Count; i++) {
                    for (int t = 0; t < tiers.Length; t++) {
                        if (tierSlotSafe[t][i]) { moonOk[i] = true; break; }
                    }
                }

                var orderedPanels = target.Panels.OrderBy(p => p.PanelIndex).ToList();
                if (mosaicPanelPreference && orderedPanels.Count > 1) {
                    for (int pi = 0; pi < orderedPanels.Count; pi++) {
                        var panel = orderedPanels[pi];
                        double panelLaSec = 0, panelNonLaSec = 0;

                        // V4: per-tier work for this panel
                        var panelTierSec = new double[tiers.Length];
                        foreach (var es in panel.ExposureSets) {
                            var remaining = es.Remaining * es.ExposureLengthSec;
                            if (es.HasMoonAvoidance) panelLaSec += remaining;
                            else panelNonLaSec += remaining;
                            if (esToTier.TryGetValue(es, out var tierIdx))
                                panelTierSec[tierIdx] += remaining;
                        }
                        if (panelLaSec + panelNonLaSec <= 0) continue;

                        profiles.Add(new TargetProfile {
                            Target = target,
                            Constraints = constraints,
                            AltitudePerSlot = altitudes,
                            MoonSepPerSlot = moonSeps,
                            SlotUsable = usable,
                            SlotMoonOk = moonOk,
                            Tiers = tiers,
                            TierRemainingSec = panelTierSec,
                            TierSlotSafe = tierSlotSafe,
                            RemainingLunarFreeSec = panelLaSec,
                            RemainingNonLunarSec = panelNonLaSec,
                            RemainingTotalSec = panelLaSec + panelNonLaSec,
                            WindowStartSlot = windowStart,
                            WindowEndSlot = windowEnd,
                            PanelIndex = pi,
                        });
                    }
                } else {
                    // V4: per-tier work totals
                    var tierSec = new double[tiers.Length];
                    foreach (var es in allEs) {
                        var remaining = es.Remaining * es.ExposureLengthSec;
                        if (esToTier.TryGetValue(es, out var tierIdx))
                            tierSec[tierIdx] += remaining;
                    }

                    profiles.Add(new TargetProfile {
                        Target = target,
                        Constraints = constraints,
                        AltitudePerSlot = altitudes,
                        MoonSepPerSlot = moonSeps,
                        SlotUsable = usable,
                        SlotMoonOk = moonOk,
                        Tiers = tiers,
                        TierRemainingSec = tierSec,
                        TierSlotSafe = tierSlotSafe,
                        RemainingLunarFreeSec = lunarFreeSec,
                        RemainingNonLunarSec = nonLunarSec,
                        RemainingTotalSec = lunarFreeSec + nonLunarSec,
                        WindowStartSlot = windowStart,
                        WindowEndSlot = windowEnd,
                    });
                }
            }
            return profiles;
        }

        public static List<SimLogEntry> RunSchedule(List<TimeSlot> slots, List<TargetProfile> profiles,
            TimeZoneInfo tz, bool ditherEnabled = true, int ditherEvery = 3,
            bool filterSwitchEnabled = true, int filterSwitchCount = 10,
            bool bonusEnabled = false,
            ImagingStrategy strategy = ImagingStrategy.SharedTime,
            double filterSwitchTolerance = 0.5,
            List<int> priorityOrder = null) {

            foreach (var p in profiles) p.AllocatedSec = 0;

            if (profiles.Count == 0)
                return new List<SimLogEntry> { new SimLogEntry { Command = "Info", Target = "No active targets." } };

            var order = priorityOrder ?? Enumerable.Range(0, profiles.Count).ToList();
            var matrix = ScheduleEngine.BuildMatrix(slots, profiles, order);

            if (matrix.FirstUsableSlot < 0)
                return new List<SimLogEntry> { new SimLogEntry { Command = "Info", Target = "No usable time window for any target." } };

            ScheduleEngine.ComputeOverlap(matrix);
            ScheduleEngine.OrganizeMoonBlocks(matrix);

            if (strategy == ImagingStrategy.ManualPriority)
                ScheduleEngine.PaintSlotsGreedy(matrix, order, bonusEnabled);
            else
                ScheduleEngine.PaintSlots(matrix, ScheduleEngine.DefaultSortChain, ScheduleEngine.DefaultMoonDownSortChain, bonusEnabled);

            var state = new ScheduleSessionState();
            return ScheduleEngine.WalkToLog(matrix, state, tz,
                ditherEnabled, ditherEvery, filterSwitchEnabled, filterSwitchCount,
                bonusEnabled: bonusEnabled,
                filterSwitchTolerance: filterSwitchTolerance);
        }

        public static (ExposureSetData Es, string PanelLabel, int PanelIdx, int EsIdx) PickExposureSet(
            TargetProfile target, int targetIdx, int slotIdx, List<TimeSlot> slots,
            ScheduleSessionState state, bool filterSwitchEnabled, int filterSwitchCount,
            HashSet<int> allowedPanels = null, bool includeCompleted = false,
            double filterSwitchTolerance = 0.5, double? targetRemainingSec = null) {

            var panels = target.Target.Panels.OrderBy(p => p.PanelIndex).ToList();
            bool isMultiPanel = panels.Count > 1;
            bool moonDown = slots[slotIdx].MoonAltDeg <= 0;
            var slot = slots[slotIdx];
            double moonSepDeg = slotIdx < target.MoonSepPerSlot.Length ? target.MoonSepPerSlot[slotIdx] : 0;
            var projectConstraints = target.Constraints;

            var candidates = new List<(ExposureSetData Es, string PanelLabel, int PanelIdx, int EsIdx, int Remaining, bool IsLunar)>();
            for (int pi = 0; pi < panels.Count; pi++) {
                if (allowedPanels != null && !allowedPanels.Contains(pi)) continue;
                var panel = panels[pi];
                string pl = isMultiPanel ? $"P{pi + 1}" : "";
                for (int ei = 0; ei < panel.ExposureSets.Count; ei++) {
                    var es = panel.ExposureSets[ei];
                    // Guard against degenerate data: a 0-length exposure would never
                    // advance the clock (infinite walk) and can never fit a runway.
                    if (es.ExposureLengthSec <= 0) continue;
                    if (!es.Enabled) continue; // disabled filter line — never schedule it (even bonus fill)
                    int rem = state.RemainingForEs(targetIdx, pi, ei, es);
                    if (rem <= 0 && !includeCompleted) continue;
                    if (es.HasMoonAvoidance && !IsExposureSetMoonSafe(es, slot, moonSepDeg, projectConstraints)) continue;
                    candidates.Add((es, pl, pi, ei, rem, es.HasMoonAvoidance));
                }
            }

            if (candidates.Count == 0) return (null, "", -1, -1);

            string activePanel = null;
            if (state.FilterCycle.TryGetValue(target, out var prevCycle))
                activePanel = prevCycle.PanelLabel;

            // Mosaic panel rotation: treat each panel as a sub-target with MinTimeOnTarget blocks
            bool shouldRotatePanel = false;
            bool forceOffCurrentPanel = false;

            if (activePanel != null && isMultiPanel) {
                var ptKey = (target, activePanel);
                double timeOnPanel = state.PanelTimeOnTarget.ContainsKey(ptKey) ? state.PanelTimeOnTarget[ptKey] : 0;
                double minTimeSec = (target.Target.Constraints?.MinTimeOnTargetHrs ?? 1.0) * 3600;
                bool activePanelHasLaWork = moonDown && candidates.Any(c => c.PanelLabel == activePanel && c.IsLunar);
                bool otherPanelsHaveLaWork = moonDown && candidates.Any(c => c.PanelLabel != activePanel && c.IsLunar);

                if (moonDown && !activePanelHasLaWork && otherPanelsHaveLaWork)
                    forceOffCurrentPanel = true;

                if (timeOnPanel >= minTimeSec && candidates.Any(c => c.PanelLabel != activePanel))
                    shouldRotatePanel = true;
            }

            bool anyLaWork = moonDown && candidates.Any(c => c.IsLunar);
            bool moonUpButSafe = !moonDown && candidates.Any(c => c.IsLunar);

            List<(ExposureSetData Es, string PanelLabel, int PanelIdx, int EsIdx, int Remaining, bool IsLunar)> panelCandidates;
            if (anyLaWork) {
                panelCandidates = candidates.Where(c => c.IsLunar).ToList();
                if (forceOffCurrentPanel || shouldRotatePanel) {
                    var otherPanelLa = panelCandidates.Where(c => c.PanelLabel != activePanel).ToList();
                    if (otherPanelLa.Count > 0) panelCandidates = otherPanelLa;
                } else if (activePanel != null) {
                    var currentPanelLa = panelCandidates.Where(c => c.PanelLabel == activePanel).ToList();
                    if (currentPanelLa.Count > 0) panelCandidates = currentPanelLa;
                }
            } else if (moonUpButSafe) {
                if (activePanel != null && isMultiPanel && !forceOffCurrentPanel && !shouldRotatePanel) {
                    var onPanel = candidates.Where(c => c.PanelLabel == activePanel).ToList();
                    if (onPanel.Count > 0) {
                        var nonLaOnPanel = onPanel.Where(c => !c.IsLunar).ToList();
                        panelCandidates = nonLaOnPanel.Count > 0 ? nonLaOnPanel : onPanel;
                    } else {
                        var nonLa = candidates.Where(c => !c.IsLunar).ToList();
                        panelCandidates = nonLa.Count > 0 ? nonLa : candidates;
                    }
                } else if (activePanel != null && isMultiPanel && (forceOffCurrentPanel || shouldRotatePanel)) {
                    var otherPanels = candidates.Where(c => c.PanelLabel != activePanel).ToList();
                    if (otherPanels.Count > 0) {
                        var nonLaOther = otherPanels.Where(c => !c.IsLunar).ToList();
                        panelCandidates = nonLaOther.Count > 0 ? nonLaOther : otherPanels;
                    } else {
                        var nonLa = candidates.Where(c => !c.IsLunar).ToList();
                        panelCandidates = nonLa.Count > 0 ? nonLa : candidates;
                    }
                } else {
                    var nonLa = candidates.Where(c => !c.IsLunar).ToList();
                    panelCandidates = nonLa.Count > 0 ? nonLa : candidates;
                }
            } else if (activePanel != null) {
                if (forceOffCurrentPanel || shouldRotatePanel) {
                    var otherPanels = candidates.Where(c => c.PanelLabel != activePanel).ToList();
                    panelCandidates = otherPanels.Count > 0 ? otherPanels : candidates;
                } else {
                    panelCandidates = candidates.Where(c => c.PanelLabel == activePanel).ToList();
                    if (panelCandidates.Count == 0) panelCandidates = candidates;
                }
            } else {
                panelCandidates = candidates;
            }

            // Moon-down exclusivity: No Moon profile filters can ONLY image while the
            // moon is down, so they get exclusive claim to dark time until exhausted.
            // Once their work completes they drop out and the other tiers take over.
            if (moonDown) {
                var noMoonOnly = panelCandidates.Where(c => c.Es.MoonAvoidanceProfile != null
                    && TierClassifier.IsNoMoonProfile(c.Es.MoonAvoidanceProfile)).ToList();
                if (noMoonOnly.Count > 0)
                    panelCandidates = noMoonOnly;
            }

            // Urgency sort: filters with the least remaining safe time run first.
            // When the moon is low, strict profiles have closing windows so they get
            // priority; as the moon climbs they become unsafe and drop out of the
            // candidate list, shifting work to relaxed profiles naturally. With no
            // urgency (everything safe all night) fall back to the hill-direction order.
            double HeadroomSecFor(ExposureSetData es) {
                if (!es.HasMoonAvoidance) return double.MaxValue;
                double sec = 0;
                for (int k = slotIdx; k < slots.Count; k++) {
                    double sep = k < target.MoonSepPerSlot.Length ? target.MoonSepPerSlot[k] : 0;
                    if (!IsExposureSetMoonSafe(es, slots[k], sep, target.Constraints)) break;
                    sec += 300.0;
                }
                return sec;
            }
            var headroomSec = new Dictionary<ExposureSetData, double>();
            foreach (var c in panelCandidates)
                if (!headroomSec.ContainsKey(c.Es)) headroomSec[c.Es] = HeadroomSecFor(c.Es);

            // Tiebreak by restrictiveness depends on which side of the hill we're on:
            // moon down or rising → most restrictive first (use the best/closing time
            // for the pickiest filters). Moon up and setting → least restrictive first
            // (relaxed soaks up the marginal time now; strict waits for a lower moon).
            bool moonRising = slotIdx + 1 < slots.Count && slots[slotIdx + 1].MoonAltDeg > slot.MoonAltDeg;
            bool preferRelaxed = !moonDown && !moonRising;

            // Within a tier (equal headroom + restrictiveness) the filter with the
            // MOST remaining work runs first, so colour channels stay balanced and no
            // single filter is perpetually cut off at the window's end (e.g. B in an
            // R,G,B set). The sort key is the START-of-night remaining
            // (PlannedCount - AcceptedCount), which is constant for the whole walk —
            // NOT the live decrementing count, which would reshuffle after every batch
            // and break the round-robin's clean one-pass-per-cycle advance. Definition
            // order is the final tiebreak so the sort stays deterministic.
            var defOrder = new Dictionary<ExposureSetData, int>();
            for (int ci = 0; ci < panelCandidates.Count; ci++)
                if (!defOrder.ContainsKey(panelCandidates[ci].Es))
                    defOrder[panelCandidates[ci].Es] = ci;
            int StartRemaining(ExposureSetData es) => es.Remaining;

            double RestrictivenessFor(ExposureSetData es) {
                if (es.MoonAvoidanceProfile != null)
                    return TierClassifier.IsNoMoonProfile(es.MoonAvoidanceProfile)
                        ? double.MaxValue
                        : TierClassifier.ComputeRestrictiveness(es.MoonAvoidanceProfile);
                if (es.AvoidLunar)
                    return TierClassifier.ComputeRestrictiveness(
                        projectConstraints.MinMoonSeparationDeg, projectConstraints.MaxMoonIlluminationPct);
                return 0;
            }

            panelCandidates.Sort((a, b) => {
                int hCmp = headroomSec[a.Es].CompareTo(headroomSec[b.Es]);
                if (hCmp != 0) return hCmp;
                double aR = RestrictivenessFor(a.Es);
                double bR = RestrictivenessFor(b.Es);
                int reqCmp = preferRelaxed ? aR.CompareTo(bR) : bR.CompareTo(aR);
                if (reqCmp != 0) return reqCmp;
                int remCmp = StartRemaining(b.Es).CompareTo(StartRemaining(a.Es)); // most remaining first
                if (remCmp != 0) return remCmp;
                return defOrder[a.Es].CompareTo(defOrder[b.Es]);
            });

            void ResetPanelTimersIfChanged(string newPanel) {
                if (isMultiPanel && activePanel != null && newPanel != activePanel)
                    foreach (var k in state.PanelTimeOnTarget.Keys.Where(k => k.Item1 == target).ToList())
                        state.PanelTimeOnTarget[k] = 0;
            }

            // Filter-switch cycling: stick with current filter for filterSwitchCount subs
            if (filterSwitchEnabled && filterSwitchCount > 0) {
                // Tolerance runway: a switch may only start a filter that can fit at
                // least filterSwitchCount × tolerance subs before its runway ends —
                // runway being the sooner of (a) remaining time on target and (b) the
                // filter's own moon-safety headroom (window close / moonrise).
                double remainingTargetSec = targetRemainingSec ?? (slots.Count - 1 - slotIdx) * 300.0;
                int minSubsTol = Math.Max(1, (int)Math.Ceiling(filterSwitchCount * filterSwitchTolerance));
                bool FitsTolerance((ExposureSetData Es, string PanelLabel, int PanelIdx, int EsIdx, int Remaining, bool IsLunar) c) {
                    double runway = Math.Min(remainingTargetSec, headroomSec.GetValueOrDefault(c.Es, double.MaxValue));
                    int fit = (int)(runway / c.Es.ExposureLengthSec);
                    return fit >= Math.Min(minSubsTol, c.Remaining);
                }

                if (state.FilterCycle.TryGetValue(target, out var cycle)) {
                    // Continue current filter if under count and it still has work on this panel
                    if (cycle.SubsOnFilter < filterSwitchCount) {
                        var same = panelCandidates.FirstOrDefault(c => c.Es.FilterName == cycle.FilterName);
                        if (same.Es != null) {
                            ResetPanelTimersIfChanged(same.PanelLabel);
                            state.FilterCycle[target] = (cycle.FilterName, same.PanelLabel, cycle.SubsOnFilter + 1);
                            return (same.Es, same.PanelLabel, same.PanelIdx, same.EsIdx);
                        }
                    } else {
                        // Tolerance check: before switching, see if any other filter has
                        // enough runway (target time AND its own safe window)
                        var nextCandidates = panelCandidates.Where(c => c.Es.FilterName != cycle.FilterName).ToList();
                        if (nextCandidates.Count > 0 && !nextCandidates.Any(FitsTolerance)) {
                            // No alternative has enough runway — continue current filter
                            var same = panelCandidates.FirstOrDefault(c => c.Es.FilterName == cycle.FilterName);
                            if (same.Es != null) {
                                ResetPanelTimersIfChanged(same.PanelLabel);
                                state.FilterCycle[target] = (cycle.FilterName, same.PanelLabel, cycle.SubsOnFilter + 1);
                                return (same.Es, same.PanelLabel, same.PanelIdx, same.EsIdx);
                            }
                        }
                    }
                }

                // Switch to next filter on this panel. The sorted candidate list is the
                // direction-ordered tier loop (rising: strict→relaxed; setting:
                // relaxed→strict). Advance to the NEXT filter after the current one,
                // wrapping around — so every tier gets its turn before the loop restarts.
                var pick = panelCandidates[0];
                if (state.FilterCycle.TryGetValue(target, out var prev2)
                    && prev2.SubsOnFilter >= filterSwitchCount && panelCandidates.Count > 1) {
                    int curIdx = panelCandidates.FindIndex(c => c.Es.FilterName == prev2.FilterName);
                    if (curIdx >= 0) {
                        var cur = panelCandidates[curIdx];

                        // Advance to the next filter in the loop that passes the tolerance
                        // runway check; filters with too little safe time left are skipped.
                        var next = cur;
                        for (int step = 1; step < panelCandidates.Count; step++) {
                            var cand = panelCandidates[(curIdx + step) % panelCandidates.Count];
                            if (FitsTolerance(cand)) { next = cand; break; }
                        }

                        if (ReferenceEquals(next.Es, cur.Es)) {
                            // No rotation target fits its runway — stay on current filter
                            pick = cur;
                        } else {
                            // Time-critical guard: hold the current filter ONLY when its safe
                            // window closes before the next filter's (true urgency) AND lending
                            // a batch would cost it work it could otherwise finish. If both
                            // windows close together, rotating loses nothing — keep the loop.
                            double curHeadroom = headroomSec.GetValueOrDefault(cur.Es, double.MaxValue);
                            double nextHeadroom = headroomSec.GetValueOrDefault(next.Es, double.MaxValue);
                            double curWorkSec = cur.Remaining * cur.Es.ExposureLengthSec;
                            double lendSec = Math.Min(next.Remaining, Math.Max(1, filterSwitchCount))
                                             * next.Es.ExposureLengthSec;
                            if (curHeadroom >= nextHeadroom || curHeadroom >= curWorkSec + lendSec) {
                                pick = next;
                            } else {
                                // Current filter is racing a closing window — don't lend time
                                // to a leisurely tier. But DO keep rotating within the group
                                // of equally-urgent filters sharing that closing window.
                                pick = cur;
                                for (int step = 1; step < panelCandidates.Count; step++) {
                                    var cand = panelCandidates[(curIdx + step) % panelCandidates.Count];
                                    if (ReferenceEquals(cand.Es, cur.Es)) continue;
                                    if (headroomSec.GetValueOrDefault(cand.Es, double.MaxValue) <= curHeadroom
                                        && FitsTolerance(cand)) {
                                        pick = cand;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    // Current filter no longer a candidate (unsafe/completed) —
                    // fall through to the top of the sorted order (loop restart).
                }

                ResetPanelTimersIfChanged(pick.PanelLabel);
                state.FilterCycle[target] = (pick.Es.FilterName, pick.PanelLabel, 1);
                return (pick.Es, pick.PanelLabel, pick.PanelIdx, pick.EsIdx);
            }

            var final = panelCandidates[0];
            ResetPanelTimersIfChanged(final.PanelLabel);
            return (final.Es, final.PanelLabel, final.PanelIdx, final.EsIdx);
        }


        public static int GetSlotIndex(DateTime utc, List<TimeSlot> slots) {
            if (slots.Count == 0) return 0;
            double secFromStart = (utc - slots[0].UtcStart).TotalSeconds;
            return Math.Clamp((int)(secFromStart / 300.0), 0, slots.Count - 1);
        }


        public static string FormatRa(double raHours) {
            int h = (int)raHours;
            double rem = (raHours - h) * 60;
            int m = (int)rem;
            double s = (rem - m) * 60;
            return $"{h:D2}h{m:D2}m{s:00.0}s";
        }

        public static string FormatDec(double decDeg) {
            string sign = decDeg >= 0 ? "+" : "-";
            double abs = Math.Abs(decDeg);
            int d = (int)abs;
            double rem = (abs - d) * 60;
            int m = (int)rem;
            double s = (rem - m) * 60;
            return $"{sign}{d:D2}°{m:D2}'{s:00.0}\"";
        }

        private static readonly (string Name, double Sep, double Width, double Relax, double MinAlt, double MaxAlt, double MaxIllum)[] BuiltInProfiles = {
            ("No Moon",   180.0, 14.0, 0.0, -90.0, -2.0,  0.0),
            ("Strict",     90.0,  8.0, 0.0, -15.0,  5.0, 30.0),
            ("Moderate",   60.0,  5.0, 2.0, -15.0,  5.0, 60.0),
            ("Relaxed",    25.0,  3.0, 3.0, -15.0,  5.0, 80.0),
        };

        internal static string GetFilterProfileName(ExposureSetData es) {
            if (es.MoonAvoidanceProfile != null) return es.MoonAvoidanceProfile.Name;
            if (es.AvoidLunar) return "Project Default";
            return "No LA";
        }

        internal static string GetAcceptedProfiles(ExposureSetData es, TimeSlot slot, double moonSepDeg, ProjectTarget project = null) {
            if (!es.HasMoonAvoidance) return "—";

            // Only consider profiles actually assigned to this project's exposure
            // sets — not every profile applies to every target.
            var assigned = new List<(string Name, double Sep, double Width, double Relax, double MinAlt, double MaxAlt, double MaxIllum)>();
            if (project != null) {
                foreach (var pn in project.Panels) {
                    foreach (var pEs in pn.ExposureSets) {
                        var mp = pEs.MoonAvoidanceProfile;
                        if (mp == null) continue;
                        if (assigned.Any(a => a.Name == mp.Name)) continue;
                        assigned.Add((mp.Name, mp.MoonSeparationDeg, mp.MoonAvoidanceWidthDays,
                            mp.MoonRelaxScale, mp.MoonMinAltitude, mp.MoonMaxAltitude, mp.MaxMoonIlluminationPct));
                    }
                }
            }
            var pool = assigned.Count > 0 ? assigned.ToArray() : BuiltInProfiles;

            if (slot.MoonAltDeg <= 0)
                return string.Join(", ", pool.Select(p => p.Name));

            var accepted = new List<string>();
            foreach (var p in pool) {
                var c = new ObservingConstraints {
                    MoonAvoidanceEnabled = true,
                    MinMoonSeparationDeg = p.Sep,
                    MoonAvoidanceWidthDays = p.Width,
                    MoonRelaxScale = p.Relax,
                    MinMoonAltitude = p.MinAlt,
                    MaxMoonAltitude = p.MaxAlt,
                    MaxMoonIlluminationPct = p.MaxIllum,
                };
                if (slot.MoonAltDeg <= c.MaxMoonAltitude)
                    accepted.Add(p.Name);
                else if (slot.MoonIllumPct <= c.MaxMoonIlluminationPct)
                    accepted.Add(p.Name);
                else {
                    var reqSep = AstroCalculator.RequiredMoonSeparation(slot.UtcStart, slot.MoonAltDeg, c);
                    if (moonSepDeg >= reqSep)
                        accepted.Add(p.Name);
                }
            }
            return accepted.Count > 0 ? string.Join(", ", accepted) : "None";
        }

        /// <summary>
        /// Per-ExposureSet moon safety: evaluates the ES's own profile curve,
        /// or falls back to project-level constraints.
        /// </summary>
        internal static bool IsExposureSetMoonSafe(ExposureSetData es, TimeSlot slot, double moonSepDeg, ObservingConstraints projectConstraints) {
            // No moon avoidance for this filter at all
            if (!es.HasMoonAvoidance) return true;

            // Moon below horizon — always safe
            if (slot.MoonAltDeg <= 0) return true;

            // Build constraints from the ES's profile, or fall back to project-level
            ObservingConstraints c;
            if (es.MoonAvoidanceProfile != null) {
                c = new ObservingConstraints {
                    MoonAvoidanceEnabled = true,
                    MinMoonSeparationDeg = es.MoonAvoidanceProfile.MoonSeparationDeg,
                    MoonAvoidanceWidthDays = es.MoonAvoidanceProfile.MoonAvoidanceWidthDays,
                    MoonRelaxScale = es.MoonAvoidanceProfile.MoonRelaxScale,
                    MinMoonAltitude = es.MoonAvoidanceProfile.MoonMinAltitude,
                    MaxMoonAltitude = es.MoonAvoidanceProfile.MoonMaxAltitude,
                    MaxMoonIlluminationPct = es.MoonAvoidanceProfile.MaxMoonIlluminationPct,
                };
            } else {
                // AvoidLunar=true with no profile → use project-level constraints
                c = projectConstraints;
            }

            if (!c.MoonAvoidanceEnabled) return true;
            if (slot.MoonAltDeg <= c.MaxMoonAltitude) return true;
            if (slot.MoonIllumPct <= c.MaxMoonIlluminationPct) return true;

            var requiredSep = AstroCalculator.RequiredMoonSeparation(slot.UtcStart, slot.MoonAltDeg, c);
            return moonSepDeg >= requiredSep;
        }
    }
}
