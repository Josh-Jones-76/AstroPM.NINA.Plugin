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
        public string Time { get; set; } = "";
        public string Target { get; set; } = "";
        public string Panel { get; set; } = "";
        public string SubNum { get; set; } = "";
        public string Filter { get; set; } = "";
        public string Exposure { get; set; } = "";
        public string Gain { get; set; } = "";
        public string Offset { get; set; } = "";
        public string Bin { get; set; } = "";
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
                    if (RemainingForEs(targetIdx, pi, ei, es) > 0 && (!es.AvoidLunar || moonDown))
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
                    if (es.AvoidLunar && RemainingForEs(targetIdx, pi, ei, es) > 0)
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
                    if (rem > 0 && (!es.AvoidLunar || moonDown))
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
            double latDeg, double lonDeg, bool mosaicPanelPreference = false) {
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
                        if (es.AvoidLunar)
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

                var orderedPanels = target.Panels.OrderBy(p => p.PanelIndex).ToList();
                if (mosaicPanelPreference && orderedPanels.Count > 1) {
                    for (int pi = 0; pi < orderedPanels.Count; pi++) {
                        var panel = orderedPanels[pi];
                        double panelLaSec = 0, panelNonLaSec = 0;
                        foreach (var es in panel.ExposureSets) {
                            var remaining = Math.Max(0, es.PlannedCount - es.AcceptedCount) * es.ExposureLengthSec;
                            if (es.AvoidLunar) panelLaSec += remaining;
                            else panelNonLaSec += remaining;
                        }
                        if (panelLaSec + panelNonLaSec <= 0) continue;

                        profiles.Add(new TargetProfile {
                            Target = target,
                            Constraints = constraints,
                            AltitudePerSlot = altitudes,
                            MoonSepPerSlot = moonSeps,
                            SlotUsable = usable,
                            SlotMoonOk = moonOk,
                            RemainingLunarFreeSec = panelLaSec,
                            RemainingNonLunarSec = panelNonLaSec,
                            RemainingTotalSec = panelLaSec + panelNonLaSec,
                            WindowStartSlot = windowStart,
                            WindowEndSlot = windowEnd,
                            PanelIndex = pi,
                        });
                    }
                } else {
                    profiles.Add(new TargetProfile {
                        Target = target,
                        Constraints = constraints,
                        AltitudePerSlot = altitudes,
                        MoonSepPerSlot = moonSeps,
                        SlotUsable = usable,
                        SlotMoonOk = moonOk,
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
            bool bonusEnabled = false) {

            foreach (var p in profiles) p.AllocatedSec = 0;

            if (profiles.Count == 0)
                return new List<SimLogEntry> { new SimLogEntry { Command = "Info", Target = "No active targets." } };

            var order = Enumerable.Range(0, profiles.Count).ToList();
            var matrix = ScheduleEngine.BuildMatrix(slots, profiles, order);

            if (matrix.FirstUsableSlot < 0)
                return new List<SimLogEntry> { new SimLogEntry { Command = "Info", Target = "No usable time window for any target." } };

            ScheduleEngine.PaintSlots(matrix, ScheduleEngine.DefaultSortChain, ScheduleEngine.DefaultMoonDownSortChain, bonusEnabled);

            var state = new ScheduleSessionState();
            return ScheduleEngine.WalkToLog(matrix, state, tz,
                ditherEnabled, ditherEvery, filterSwitchEnabled, filterSwitchCount);
        }

        public static (ExposureSetData Es, string PanelLabel, int PanelIdx, int EsIdx) PickExposureSet(
            TargetProfile target, int targetIdx, int slotIdx, List<TimeSlot> slots,
            ScheduleSessionState state, bool filterSwitchEnabled, int filterSwitchCount,
            HashSet<int> allowedPanels = null) {

            var panels = target.Target.Panels.OrderBy(p => p.PanelIndex).ToList();
            bool isMultiPanel = panels.Count > 1;
            bool moonDown = slots[slotIdx].MoonAltDeg <= 0;
            bool moonSafe = moonDown || target.SlotMoonOk[slotIdx];

            var candidates = new List<(ExposureSetData Es, string PanelLabel, int PanelIdx, int EsIdx, int Remaining, bool IsLunar)>();
            for (int pi = 0; pi < panels.Count; pi++) {
                if (allowedPanels != null && !allowedPanels.Contains(pi)) continue;
                var panel = panels[pi];
                string pl = isMultiPanel ? $"P{pi + 1}" : "";
                for (int ei = 0; ei < panel.ExposureSets.Count; ei++) {
                    var es = panel.ExposureSets[ei];
                    int rem = state.RemainingForEs(targetIdx, pi, ei, es);
                    if (rem <= 0) continue;
                    if (es.AvoidLunar && !moonSafe) continue;
                    candidates.Add((es, pl, pi, ei, rem, es.AvoidLunar));
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
            bool moonUpButSafe = !moonDown && moonSafe;

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

            panelCandidates.Sort((a, b) => {
                if (moonDown) {
                    int lunarCmp = b.IsLunar.CompareTo(a.IsLunar);
                    if (lunarCmp != 0) return lunarCmp;
                }
                return b.Remaining.CompareTo(a.Remaining);
            });

            void ResetPanelTimersIfChanged(string newPanel) {
                if (isMultiPanel && activePanel != null && newPanel != activePanel)
                    foreach (var k in state.PanelTimeOnTarget.Keys.Where(k => k.Item1 == target).ToList())
                        state.PanelTimeOnTarget[k] = 0;
            }

            if (filterSwitchEnabled && filterSwitchCount > 0) {
                if (state.FilterCycle.TryGetValue(target, out var cycle)) {
                    if (cycle.SubsOnFilter < filterSwitchCount) {
                        var same = panelCandidates.FirstOrDefault(c => c.Es.FilterName == cycle.FilterName);
                        if (same.Es != null) {
                            bool cycleFilterIsLunar = same.Es.AvoidLunar;
                            if (moonDown && anyLaWork && !cycleFilterIsLunar) {
                                // Moon is down — break cycle to prioritize lunar-avoid filters
                            } else {
                                ResetPanelTimersIfChanged(same.PanelLabel);
                                state.FilterCycle[target] = (cycle.FilterName, same.PanelLabel, cycle.SubsOnFilter + 1);
                                return (same.Es, same.PanelLabel, same.PanelIdx, same.EsIdx);
                            }
                        }
                    }
                }

                var pick = panelCandidates[0];
                string pickName = pick.Es.FilterName;

                if (state.FilterCycle.TryGetValue(target, out var prev2) && prev2.FilterName == pickName
                    && prev2.SubsOnFilter >= filterSwitchCount && panelCandidates.Count > 1) {
                    var next = panelCandidates.FirstOrDefault(c => c.Es.FilterName != pickName);
                    if (next.Es != null) {
                        pick = next;
                        pickName = pick.Es.FilterName;
                    }
                }

                ResetPanelTimersIfChanged(pick.PanelLabel);
                state.FilterCycle[target] = (pickName, pick.PanelLabel, 1);
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
    }
}
