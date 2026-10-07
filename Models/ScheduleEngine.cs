using System;
using System.Collections.Generic;
using System.Linq;

namespace AstroPM.NINA.Plugin.Models {

    internal enum SlotWorkType { Any, LaPreferred, NonLaPreferred }

    public enum ImagingStrategy { SharedTime, ManualPriority }

    /// <summary>
    /// How the live exposure loop plays a target block's planned entries.
    /// TimeAware (default): after delays (autofocus, safety holds, meridian flips) it
    /// jumps to the entry that should be running now — right for long closures, but can
    /// skip the last subs of a block. Sequential: plays entries strictly in order and
    /// never skips; after a hold on the same target it resumes where it left off.
    /// Target boundaries (block end) are honored in both modes.
    /// </summary>
    public enum PlaybackMode { TimeAware, Sequential }

    public enum SortCriteria {
        SettingSoonest,
        Constrained,
        MostRemainingWork,
        MostLaWork,
        LowestPeakAltitude,
        MosaicGroup,
        UserPriority,
    }

    // ─── V4 Tier types ───────────────────────────────────────────────────

    public class TierInfo {
        public int TierIndex { get; set; }
        public double Restrictiveness { get; set; }
        public bool RequiresMoonDown { get; set; }
        public string Label { get; set; } = "";
    }

    internal class OverlapInfo {
        public int[] CandidateRows { get; set; } = Array.Empty<int>();
        public int Contention { get; set; }
        public bool IsExclusive => Contention == 1;
    }

    public static class TierClassifier {
        public static double ComputeRestrictiveness(MoonAvoidanceProfileData profile) {
            return profile.MoonSeparationDeg * (1.0 + 100.0 / (profile.MaxMoonIlluminationPct + 1.0));
        }

        public static double ComputeRestrictiveness(double moonSepDeg, double maxMoonIllumPct) {
            return moonSepDeg * (1.0 + 100.0 / (maxMoonIllumPct + 1.0));
        }

        public static bool IsNoMoonProfile(MoonAvoidanceProfileData profile) {
            return profile.Name.Equals("No Moon", StringComparison.OrdinalIgnoreCase)
                || (profile.MoonMaxAltitude <= 0 && profile.MaxMoonIlluminationPct <= 0);
        }

        public static (TierInfo[] Tiers, Dictionary<ExposureSetData, int> EsToTier) ClassifyExposureSets(
            IEnumerable<ExposureSetData> exposureSets,
            ObservingConstraints projectConstraints) {

            var tiers = new List<TierInfo>();
            var esMap = new Dictionary<ExposureSetData, int>();

            // Tier 0: No moon avoidance (always available)
            tiers.Add(new TierInfo {
                TierIndex = 0,
                Restrictiveness = 0,
                RequiresMoonDown = false,
                Label = "No LA",
            });

            // Group ES by their effective moon avoidance configuration
            var profileGroups = new Dictionary<string, (TierInfo Tier, List<ExposureSetData> Sets)>();

            foreach (var es in exposureSets) {
                if (!es.Enabled) continue; // disabled filter line — excluded from tier classification
                if (!es.HasMoonAvoidance) {
                    esMap[es] = 0;
                    continue;
                }

                string groupKey;
                double restrictiveness;
                bool requiresMoonDown;
                string label;

                if (es.MoonAvoidanceProfile != null) {
                    groupKey = $"profile:{es.MoonAvoidanceProfile.Name}";
                    requiresMoonDown = IsNoMoonProfile(es.MoonAvoidanceProfile);
                    restrictiveness = requiresMoonDown
                        ? double.MaxValue
                        : ComputeRestrictiveness(es.MoonAvoidanceProfile);
                    label = es.MoonAvoidanceProfile.Name;
                } else {
                    // AvoidLunar=true with no profile → project default
                    groupKey = "project-default";
                    requiresMoonDown = false;
                    restrictiveness = ComputeRestrictiveness(
                        projectConstraints.MinMoonSeparationDeg,
                        projectConstraints.MaxMoonIlluminationPct);
                    label = "Project Default";
                }

                if (!profileGroups.ContainsKey(groupKey)) {
                    var tier = new TierInfo {
                        TierIndex = -1, // assigned after sorting
                        Restrictiveness = restrictiveness,
                        RequiresMoonDown = requiresMoonDown,
                        Label = label,
                    };
                    profileGroups[groupKey] = (tier, new List<ExposureSetData> { es });
                } else {
                    profileGroups[groupKey].Sets.Add(es);
                }
            }

            // Sort tiers by restrictiveness (least → most), assign indices
            var sortedGroups = profileGroups.Values
                .OrderBy(g => g.Tier.Restrictiveness)
                .ToList();

            int tierIdx = 1;
            foreach (var (tier, sets) in sortedGroups) {
                tier.TierIndex = tierIdx;
                tiers.Add(tier);
                foreach (var es in sets)
                    esMap[es] = tierIdx;
                tierIdx++;
            }

            return (tiers.ToArray(), esMap);
        }
    }

    internal class TargetRow {
        public TargetProfile Profile { get; set; }
        public int RowIndex { get; set; }

        // V4 per-tier work tracking
        public double[] TierWorkSec { get; set; } = Array.Empty<double>();
        public int[] TierMoonUpSafeSlots { get; set; } = Array.Empty<int>();

        // V4 tier-aware usability, computed PRE-paint from initial tier work:
        // true only if some tier with work can actually image at this slot.
        // Unlike CanImage (altitude only) this accounts for moon safety per tier.
        public bool[] UsableSlot { get; set; } = Array.Empty<bool>();

        // V3 compat — derived from tiers
        public double RemainingLaSec {
            get => TierWorkSec.Length > 1 ? TierWorkSec.Skip(1).Sum() : 0;
            set { }
        }
        public double RemainingNonLaSec {
            get => TierWorkSec.Length > 0 ? TierWorkSec[0] : 0;
            set { }
        }

        public int FirstUsableSlot { get; set; } = -1;
        public int LastUsableSlot { get; set; } = -1;
        public int TotalUsableSlots { get; set; }
        public int MoonSafeSlots { get; set; }
        public int MoonDownSlots { get; set; }

        public double MinChunkSec { get; set; }
        public int MinChunkSlots { get; set; }

        // Min-Time Tolerance floor = MinChunk × (1 − tolerance): the shortest block
        // still worth a visit. Painting and min-enforcement still AIM for MinChunk;
        // a block that can't get there is kept if it reaches the floor and dropped
        // (its slots going to a neighbor) if it can't.
        public double MinFloorSec { get; set; }
        public int MinFloorSlots { get; set; }

        public double TotalWorkSec => TierWorkSec.Length > 0 ? TierWorkSec.Sum() : 0;
        public bool HasLaWork => TierWorkSec.Length > 1 && TierWorkSec.Skip(1).Any(x => x > 0);
        public bool HasNonLaWork => TierWorkSec.Length > 0 && TierWorkSec[0] > 0;
        public bool IsConstrained { get; set; }

        public double PeakAltitude { get; set; }
        public int UserPriorityIndex { get; set; }
        public bool PreFiltered { get; set; }

        // Set by PreClaimFixedWindows when this row claimed a locked exo transit
        // window: its allocation is externally defined (the recommended capture
        // window IS the block) — minimum-time enforcement and bonus gap-fill must
        // neither extend nor shrink it.
        public bool HasLockedWindow { get; set; }

        public int MostRestrictiveTierWithWork() {
            for (int t = TierWorkSec.Length - 1; t >= 1; t--)
                if (TierWorkSec[t] > 0) return t;
            return 0;
        }

        public int LeastRestrictiveSafeTierWithWork(bool[][] tierSafe, int slot) {
            for (int t = 1; t < TierWorkSec.Length; t++)
                if (TierWorkSec[t] > 0 && tierSafe[t][slot]) return t;
            if (TierWorkSec.Length > 0 && TierWorkSec[0] > 0) return 0;
            return -1;
        }
    }

    internal class ScheduleMatrix {
        /// <summary>Rows whose assignments EnforceMinimumAllocations cleared tonight (their time could not meet the minimum).</summary>
        public HashSet<int> ClearedRows { get; } = new HashSet<int>();
        public List<TimeSlot> Slots { get; set; } = new List<TimeSlot>();
        public List<TargetRow> Rows { get; set; } = new List<TargetRow>();

        public bool[][] CanImage { get; set; } = Array.Empty<bool[]>();
        public bool[][] MoonSafe { get; set; } = Array.Empty<bool[]>();
        public bool[] MoonDown { get; set; } = Array.Empty<bool>();

        // V4 per-tier safety: TierSafe[row][tier][slot]
        public bool[][][] TierSafe { get; set; } = Array.Empty<bool[][]>();

        // V4 slot allocation tier: which tier of work was allocated at each slot (-1 = none)
        public int[] SlotAllocTier { get; set; } = Array.Empty<int>();

        // V4 overlap analysis
        public OverlapInfo[] SlotOverlap { get; set; } = Array.Empty<OverlapInfo>();

        public int[] SlotAssignment { get; set; } = Array.Empty<int>();
        public SlotWorkType[] SlotWorkHint { get; set; } = Array.Empty<SlotWorkType>();
        public bool[] PrunedSlot { get; set; } = Array.Empty<bool>();

        // Exoplanet transit lock: slots pre-claimed by PreClaimFixedWindows. Locked
        // slots must never be reassigned, borrowed, pruned, or defragmented away.
        // All false (a strict no-op in every guard) when no transit falls tonight.
        public bool[] LockedSlot { get; set; } = Array.Empty<bool>();

        // Min-Time Tolerance (0–0.95): how far below MinTimeOnTarget a block may fall
        // and still be scheduled. See TargetRow.MinFloorSlots.
        public double MinTimeTolerance { get; set; } = 0.5;

        public int FirstUsableSlot { get; set; } = -1;
        public int LastUsableSlot { get; set; } = -1;
        public int FirstDarkSlot { get; set; } = -1;
        public int LastDarkSlot { get; set; } = -1;
    }

    internal class ScheduleWarning {
        public string Severity { get; set; } = "warn";
        public string Message { get; set; } = "";
        public DateTime? UtcTime { get; set; }
    }

    internal static class ScheduleEngine {
        internal static System.Text.StringBuilder PaintTrace = new System.Text.StringBuilder();

        /// <summary>Tagged line in the paint trace (the plugin's stand-in for the desktop EngineTrace.Log).</summary>
        private static void Tr(string tag, string msg) {
            PaintTrace.AppendLine($"{tag}: {msg}");
        }

        private static void TraceSnapshot(ScheduleMatrix matrix, string label) {
            PaintTrace.AppendLine($"\n--- {label} ---");
            int start = Math.Max(0, matrix.FirstUsableSlot);
            int end = Math.Min(matrix.LastUsableSlot, matrix.Slots.Count - 1);
            foreach (var row in matrix.Rows) {
                int assigned = 0;
                for (int s = start; s <= end; s++)
                    if (matrix.SlotAssignment[s] == row.RowIndex) assigned++;
                PaintTrace.AppendLine($"  {row.Profile.DisplayName}: {assigned} slots, LA={row.RemainingLaSec/60:F0}m NonLA={row.RemainingNonLaSec/60:F0}m");
            }
        }

        // ─── BuildMatrix ─────────────────────────────────────────────────────

        public static ScheduleMatrix BuildMatrix(
            List<TimeSlot> slots,
            List<TargetProfile> profiles,
            List<int> priorityOrder,
            double minTimeTolerance = 0.5) {

            int slotCount = slots.Count;
            int rowCount = profiles.Count;
            if (double.IsNaN(minTimeTolerance)) minTimeTolerance = 0.5;
            minTimeTolerance = Math.Max(0.0, Math.Min(0.95, minTimeTolerance));

            var matrix = new ScheduleMatrix {
                MinTimeTolerance = minTimeTolerance,
                Slots = slots,
                Rows = new List<TargetRow>(rowCount),
                CanImage = new bool[rowCount][],
                MoonSafe = new bool[rowCount][],
                MoonDown = new bool[slotCount],
                TierSafe = new bool[rowCount][][],
                SlotAllocTier = new int[slotCount],
                SlotOverlap = new OverlapInfo[slotCount],
                SlotAssignment = new int[slotCount],
                SlotWorkHint = new SlotWorkType[slotCount],
                PrunedSlot = new bool[slotCount],
                LockedSlot = new bool[slotCount],
            };

            Array.Fill(matrix.SlotAssignment, -1);
            Array.Fill(matrix.SlotAllocTier, -1);

            for (int s = 0; s < slotCount; s++)
                matrix.MoonDown[s] = slots[s].MoonAltDeg <= 0;

            for (int r = 0; r < rowCount; r++) {
                var prof = profiles[r];
                var canImage = new bool[slotCount];
                var moonSafe = new bool[slotCount];
                int firstUsable = -1, lastUsable = -1, totalUsable = 0;
                int moonSafeCount = 0, moonDownCount = 0;
                double peakAlt = 0;

                for (int s = 0; s < slotCount; s++) {
                    canImage[s] = prof.SlotUsable[s];
                    moonSafe[s] = matrix.MoonDown[s] || prof.SlotMoonOk[s];

                    if (canImage[s]) {
                        if (firstUsable < 0) firstUsable = s;
                        lastUsable = s;
                        totalUsable++;
                        if (moonSafe[s]) moonSafeCount++;
                        if (matrix.MoonDown[s]) moonDownCount++;
                        if (prof.AltitudePerSlot[s] > peakAlt) peakAlt = prof.AltitudePerSlot[s];
                    }
                }

                matrix.CanImage[r] = canImage;
                matrix.MoonSafe[r] = moonSafe;

                // V4: populate per-tier safety from profile
                matrix.TierSafe[r] = prof.TierSlotSafe.Length > 0
                    ? prof.TierSlotSafe
                    : new[] { Enumerable.Repeat(true, slotCount).ToArray() };

                // V4: compute per-tier moon-up safe slot counts
                int tierCount = prof.Tiers.Length > 0 ? prof.Tiers.Length : 1;
                var tierMoonUpSafe = new int[tierCount];
                for (int t = 0; t < tierCount; t++) {
                    for (int s = 0; s < slotCount; s++) {
                        if (!canImage[s] || matrix.MoonDown[s]) continue;
                        if (t < matrix.TierSafe[r].Length && matrix.TierSafe[r][t][s])
                            tierMoonUpSafe[t]++;
                    }
                }

                // V4: per-tier work seconds
                var tierWorkSec = prof.TierRemainingSec.Length > 0
                    ? (double[])prof.TierRemainingSec.Clone()
                    : new[] { prof.RemainingNonLunarSec + prof.RemainingLunarFreeSec };

                double minChunkSec = prof.Constraints.MinTimeOnTargetHrs * 3600;

                // End-of-target completion: when a target's *entire* remaining
                // workload is less than one minimum block, it can never satisfy
                // the configured minimum again — it would sit just below the
                // threshold forever (pre-filtered out every night) and the
                // project's final frames would never get scheduled. Shrink the
                // minimum block to the work that's actually left so those last
                // frames can pass the pre-filter, paint as one contiguous block,
                // and survive min-enforcement. NO-OP for any target that still
                // has a full block of work (min(minChunkSec, remaining) ==
                // minChunkSec); a target that's genuinely sky-limited still gets
                // pre-filtered because accessibleSec stays below this value.
                double remainingWorkSec = tierWorkSec.Sum();
                if (remainingWorkSec > 0 && remainingWorkSec < minChunkSec)
                    minChunkSec = remainingWorkSec;

                int userPri = priorityOrder.IndexOf(r);
                if (userPri < 0) userPri = r;

                // V4: tier-aware usability — a slot only counts if some tier with
                // actual work can image there (moon-down is safe for all tiers).
                var usableSlot = new bool[slotCount];
                for (int s = 0; s < slotCount; s++) {
                    if (!canImage[s]) continue;
                    for (int t = 0; t < tierWorkSec.Length; t++) {
                        if (tierWorkSec[t] <= 0) continue;
                        if (t == 0 || matrix.MoonDown[s]
                            || (t < matrix.TierSafe[r].Length && matrix.TierSafe[r][t][s])) {
                            usableSlot[s] = true;
                            break;
                        }
                    }
                }

                var row = new TargetRow {
                    Profile = prof,
                    RowIndex = r,
                    TierWorkSec = tierWorkSec,
                    TierMoonUpSafeSlots = tierMoonUpSafe,
                    UsableSlot = usableSlot,
                    FirstUsableSlot = firstUsable,
                    LastUsableSlot = lastUsable,
                    TotalUsableSlots = totalUsable,
                    MoonSafeSlots = moonSafeCount,
                    MoonDownSlots = moonDownCount,
                    MinChunkSec = minChunkSec,
                    MinChunkSlots = (int)Math.Ceiling(minChunkSec / 300.0),
                    MinFloorSec = minChunkSec * (1.0 - minTimeTolerance),
                    MinFloorSlots = Math.Max(1, (int)Math.Ceiling(minChunkSec * (1.0 - minTimeTolerance) / 300.0)),
                    IsConstrained = totalUsable * 300 < minChunkSec * 2,
                    PeakAltitude = peakAlt,
                    UserPriorityIndex = userPri,
                };
                matrix.Rows.Add(row);
            }

            for (int s = 0; s < slotCount; s++) {
                for (int r = 0; r < rowCount; r++) {
                    if (matrix.CanImage[r][s]) {
                        if (matrix.FirstUsableSlot < 0) matrix.FirstUsableSlot = s;
                        matrix.LastUsableSlot = s;
                        break;
                    }
                }
                if (slots[s].SunAltDeg <= -18) {
                    if (matrix.FirstDarkSlot < 0) matrix.FirstDarkSlot = s;
                    matrix.LastDarkSlot = s;
                }
            }

            return matrix;
        }

        // ─── Overlap Analysis ────────────────────────────────────────────────

        public static void ComputeOverlap(ScheduleMatrix matrix) {
            int slotCount = matrix.Slots.Count;
            for (int s = 0; s < slotCount; s++) {
                var candidates = new List<int>();
                for (int r = 0; r < matrix.Rows.Count; r++) {
                    if (matrix.Rows[r].PreFiltered) continue;
                    if (matrix.CanImage[r][s] && matrix.Rows[r].TotalWorkSec > 0)
                        candidates.Add(r);
                }
                matrix.SlotOverlap[s] = new OverlapInfo {
                    CandidateRows = candidates.ToArray(),
                    Contention = candidates.Count,
                };
            }
        }

        // ─── Moon Block Organization ─────────────────────────────────────────

        public static void OrganizeMoonBlocks(ScheduleMatrix matrix) {
            int slotCount = matrix.Slots.Count;
            var rows = matrix.Rows;

            for (int s = 0; s < slotCount; s++) {
                if (matrix.SlotAssignment[s] >= 0) continue;

                if (matrix.MoonDown[s]) {
                    // Moon-down: assign tier hint by most restrictive first
                    // No Moon tiers (RequiresMoonDown) get absolute priority
                    int bestRow = -1;
                    int bestTier = -1;
                    double bestRestrictiveness = -1;

                    foreach (int r in matrix.SlotOverlap[s].CandidateRows) {
                        var row = rows[r];
                        if (!matrix.CanImage[r][s]) continue;

                        var tiers = row.Profile.Tiers;
                        for (int t = tiers.Length - 1; t >= 1; t--) {
                            if (row.TierWorkSec[t] <= 0) continue;
                            double restr = tiers[t].Restrictiveness;
                            if (tiers[t].RequiresMoonDown) restr = double.MaxValue;

                            if (restr > bestRestrictiveness) {
                                bestRestrictiveness = restr;
                                bestRow = r;
                                bestTier = t;
                            }
                        }
                    }

                    if (bestRow >= 0)
                        matrix.SlotAllocTier[s] = bestTier;
                } else {
                    // Moon-up: determine direction (rising or setting)
                    bool moonRising = s > 0
                        ? matrix.Slots[s].MoonAltDeg > matrix.Slots[s - 1].MoonAltDeg
                        : matrix.Slots[s].MoonAltDeg > 0;

                    // Moon rising: use relaxed first (save restrictive for when they can't image)
                    // Moon setting: use restrictive first (conditions improving, relaxed can wait)
                    int bestRow = -1;
                    int bestTier = -1;
                    double bestScore = moonRising ? double.MaxValue : -1;

                    foreach (int r in matrix.SlotOverlap[s].CandidateRows) {
                        var row = rows[r];
                        if (!matrix.CanImage[r][s]) continue;

                        var tiers = row.Profile.Tiers;
                        for (int t = 1; t < tiers.Length; t++) {
                            if (row.TierWorkSec[t] <= 0) continue;
                            if (t >= matrix.TierSafe[r].Length || !matrix.TierSafe[r][t][s]) continue;

                            double restr = tiers[t].Restrictiveness;
                            if (moonRising && restr < bestScore) {
                                bestScore = restr;
                                bestRow = r;
                                bestTier = t;
                            } else if (!moonRising && restr > bestScore) {
                                bestScore = restr;
                                bestRow = r;
                                bestTier = t;
                            }
                        }
                    }

                    if (bestRow >= 0)
                        matrix.SlotAllocTier[s] = bestTier;
                    else
                        matrix.SlotAllocTier[s] = 0; // fallback to non-LA
                }
            }
        }

        // ─── Pass EXO — fixed transit-window pre-claim ───────────────────────
        // An exoplanet target with a transit tonight owns its recommended capture
        // window (ingress−1h … egress+1h, from TargetProfile.FixedWindow*) outright:
        // every slot whose MIDPOINT falls in the window (nearest-boundary rounding
        // on the 5-minute grid) where the target clears altitude/darkness is
        // assigned and LOCKED before any other pass runs, so the rest of the night
        // schedules around it. The target's remaining work is then zeroed — the
        // transit IS its night; it doesn't also compete for slots elsewhere.
        // Mirrors the desktop engine exactly; with no transit tonight this pass
        // assigns nothing and every downstream lock guard is a no-op.
        public static void PreClaimFixedWindows(ScheduleMatrix matrix) {
            foreach (var row in matrix.Rows) {
                var prof = row.Profile;
                if (!prof.FixedWindowStartUtc.HasValue || !prof.FixedWindowEndUtc.HasValue) continue;
                DateTime winStart = prof.FixedWindowStartUtc.Value;
                DateTime winEnd = prof.FixedWindowEndUtc.Value;

                int ri = row.RowIndex;
                int claimed = 0, firstSlot = -1, lastSlot = -1;
                for (int s = 0; s < matrix.Slots.Count; s++) {
                    var slotMid = matrix.Slots[s].UtcStart.AddSeconds(150);
                    if (slotMid < winStart) continue;
                    if (slotMid >= winEnd) break;
                    if (!matrix.CanImage[ri][s]) continue;
                    if (matrix.SlotAssignment[s] >= 0) continue; // earlier fixed window won the overlap

                    matrix.SlotAssignment[s] = ri;
                    matrix.LockedSlot[s] = true;
                    matrix.SlotWorkHint[s] = SlotWorkType.Any;
                    claimed++;
                    if (firstSlot < 0) firstSlot = s;
                    lastSlot = s;
                }

                // A transit target only ever images inside its window — zero its work
                // even when nothing was claimable (e.g. below min altitude for the
                // whole window) so it can't fall back to normal scheduling.
                for (int t = 0; t < row.TierWorkSec.Length; t++)
                    row.TierWorkSec[t] = 0;
                row.PreFiltered = true;

                if (claimed == 0) {
                    PaintTrace.AppendLine($"EXO-LOCK: [{prof.DisplayName}] transit window {winStart:HH:mm}-{winEnd:HH:mm} UTC has no claimable slots — target sits out tonight");
                    continue;
                }
                row.HasLockedWindow = true;

                PaintTrace.AppendLine($"EXO-LOCK: [{prof.DisplayName}] transit window {winStart:HH:mm}-{winEnd:HH:mm} UTC locked: slots {firstSlot}-{lastSlot} ({claimed} slots, {claimed * 5} min)");
            }
        }

        /// <summary>An exoplanet transit target ignores filter moon rules for tonight's capture.</summary>
        internal static bool ExoMoonExempt(TargetProfile prof) {
            return !EngineExperiments.LegacyExoMoonRules && prof.FixedWindowStartUtc != null;
        }

        // ─── PaintSlotsGreedy (ManualPriority strategy) ──────────────────────

        public static void PaintSlotsGreedy(
            ScheduleMatrix matrix,
            List<int> priorityOrder,
            bool bonusEnabled) {

            PaintTrace.Clear();
            PaintTrace.AppendLine("=== PAINT TRACE (Greedy / ManualPriority) ===");

            // Exoplanet transit windows claim their slots before anything else runs
            PreClaimFixedWindows(matrix);

            // Pre-filter: skip targets whose total accessible work < minimum
            foreach (var row in matrix.Rows) {
                double accessibleSec = 0;
                int ri = row.RowIndex;
                for (int t = 0; t < row.TierWorkSec.Length; t++) {
                    int safeSlots = 0;
                    for (int s = 0; s < matrix.Slots.Count; s++) {
                        if (!matrix.CanImage[ri][s]) continue;
                        if (t == 0 || matrix.MoonDown[s] || (t < matrix.TierSafe[ri].Length && matrix.TierSafe[ri][t][s]))
                            safeSlots++;
                    }
                    accessibleSec += Math.Min(row.TierWorkSec[t], safeSlots * 300.0);
                }
                if (accessibleSec < row.MinFloorSec) { // Min-Time Tolerance floor, not the full minimum
                    for (int t = 0; t < row.TierWorkSec.Length; t++)
                        row.TierWorkSec[t] = 0;
                    row.PreFiltered = true;
                }
            }

            // No exclusive-window pre-claim here (unlike SharedTime's Pass 0): an
            // exclusive slot can only ever go to its one candidate anyway, and
            // pre-claiming it consumes that target's budget in late slots when
            // earlier shared slots may be free — delaying its start for no gain.
            // Strict priority order + earliest-first painting covers it naturally.

            // Greedy allocation: walk priority order, give each target as much as it can use
            foreach (int orderIdx in priorityOrder) {
                if (orderIdx < 0 || orderIdx >= matrix.Rows.Count) continue;
                var row = matrix.Rows[orderIdx];
                if (row.PreFiltered || row.TotalWorkSec <= 0) continue;
                int ri = row.RowIndex;

                // Phase A: Moon-down pre-claim — ONLY for tier work that cannot run
                // while the moon is up tonight (zero safe moon-up slots, e.g. No Moon
                // profiles under a bright moon). Moon-up-safe LA work must NOT be
                // parked here: doing so shoves a priority-1 target to the post-moonset
                // hours and hands its prime early slots to lower priorities.
                for (int t = row.TierWorkSec.Length - 1; t >= 1; t--) {
                    if (row.TierWorkSec[t] <= 0) continue;
                    bool moonDownOnly = t >= row.TierMoonUpSafeSlots.Length || row.TierMoonUpSafeSlots[t] == 0;
                    if (!moonDownOnly) continue;
                    double budget = row.TierWorkSec[t];

                    for (int s = 0; s < matrix.Slots.Count && budget > 0; s++) {
                        if (!matrix.MoonDown[s]) continue;
                        if (matrix.SlotAssignment[s] >= 0) continue;
                        if (!matrix.CanImage[ri][s]) continue;

                        matrix.SlotAssignment[s] = ri;
                        matrix.SlotAllocTier[s] = t;
                        matrix.SlotWorkHint[s] = SlotWorkType.LaPreferred;
                        row.TierWorkSec[t] = Math.Max(0, row.TierWorkSec[t] - 300.0);
                        budget -= 300.0;
                    }
                }

                // Phase B: everything else paints chronologically earliest-first — this
                // is what makes priority order equal imaging order. Moon-down slots
                // accept any tier (all safe there); moon-up slots need a tier that is
                // safe at that slot, most restrictive first so scarce windows are used.
                for (int s = 0; s < matrix.Slots.Count; s++) {
                    if (matrix.SlotAssignment[s] >= 0) continue;
                    if (!matrix.CanImage[ri][s]) continue;
                    if (row.TotalWorkSec <= 0) break;

                    int bestTier = -1;
                    for (int t = row.TierWorkSec.Length - 1; t >= 1; t--) {
                        if (row.TierWorkSec[t] <= 0) continue;
                        if (matrix.MoonDown[s] || (t < matrix.TierSafe[ri].Length && matrix.TierSafe[ri][t][s])) { bestTier = t; break; }
                    }
                    if (bestTier < 0 && row.TierWorkSec.Length > 0 && row.TierWorkSec[0] > 0)
                        bestTier = 0;
                    if (bestTier < 0) continue;

                    matrix.SlotAssignment[s] = ri;
                    matrix.SlotAllocTier[s] = bestTier;
                    var hint2 = bestTier > 0
                        ? (matrix.MoonDown[s] ? SlotWorkType.LaPreferred : SlotWorkType.NonLaPreferred)
                        : SlotWorkType.Any;
                    matrix.SlotWorkHint[s] = hint2;
                    row.TierWorkSec[bestTier] = Math.Max(0, row.TierWorkSec[bestTier] - 300.0);
                }
            }
            TraceSnapshot(matrix, "After greedy allocation");

            EnforceMinimumAllocations(matrix);
            PruneSlivers(matrix);

            // Bonus / gap fill
            if (bonusEnabled) {
                AbsorbGaps(matrix);
            }

            DefragmentSlots(matrix);
            PruneSlivers(matrix);

            // Absorb pruned slots into adjacent targets
            AbsorbGaps(matrix);
        }

        // ─── PaintSlots ──────────────────────────────────────────────────────

        public static void PaintSlots(
            ScheduleMatrix matrix,
            List<SortCriteria> sortChain,
            List<SortCriteria> moonDownSortChain,
            bool bonusEnabled) {

            PaintTrace.Clear();
            PaintTrace.AppendLine("=== PAINT TRACE ===");

            // Exoplanet transit windows claim their slots before anything else runs
            PreClaimFixedWindows(matrix);

            foreach (var row in matrix.Rows) {
                int ri = row.RowIndex;
                int safeSlots = 0, unsafeSlots = 0;
                for (int s = 0; s < matrix.Slots.Count; s++) {
                    if (!matrix.CanImage[ri][s]) continue;
                    if (matrix.MoonDown[s] || matrix.MoonSafe[ri][s]) safeSlots++;
                    else unsafeSlots++;
                }
                double laInSafe = Math.Min(row.RemainingLaSec, safeSlots * 300.0);
                double safeRemaining = Math.Max(0, safeSlots * 300.0 - laInSafe);
                double nonLaInSafe = Math.Min(row.RemainingNonLaSec, safeRemaining);
                double nonLaLeft = row.RemainingNonLaSec - nonLaInSafe;
                double accessibleSec = laInSafe + nonLaInSafe + Math.Min(nonLaLeft, unsafeSlots * 300.0);
                Tr("PREFILTER", $"[{row.Profile.DisplayName}] minAlt={row.Profile.Constraints.MinTargetAltitude:F0}° safe={safeSlots} unsafe={unsafeSlots} LA={row.RemainingLaSec / 60:F0}m nonLA={row.RemainingNonLaSec / 60:F0}m accessible={accessibleSec / 60:F0}m floor={row.MinFloorSec / 60:F0}m filtered={accessibleSec < row.MinFloorSec}");
                if (accessibleSec < row.MinFloorSec) { // Min-Time Tolerance floor, not the full minimum
                    for (int t = 0; t < row.TierWorkSec.Length; t++) row.TierWorkSec[t] = 0;
                    row.PreFiltered = true;
                }
            }

            TraceSnapshot(matrix, "Pre-filter");

            // Pass 0: Exclusive-window pre-allocation
            {
                var activeRows = matrix.Rows.Where(r => !r.PreFiltered && r.TotalWorkSec > 0).ToList();
                for (int s = 0; s < matrix.Slots.Count; s++) {
                    if (matrix.SlotAssignment[s] >= 0) continue; // locked exo window slot
                    int soleCandidate = -1;
                    bool multiple = false;
                    foreach (var row in activeRows) {
                        if (!matrix.CanImage[row.RowIndex][s]) continue;
                        // Tier-aware: a slot the target has no moon-safe planned work for is
                        // not its exclusive window (desktop mirror, lunar-cycle sweep 10/7/26).
                        // Bonus-only capability still counts when the target is alone by altitude.
                        var usable = row.UsableSlot;
                        if (!EngineExperiments.LegacyExclusiveByAltitude
                            && !(s < usable.Length && usable[s]) && !CanShootSomething(matrix, row.RowIndex, s)) continue;
                        if (soleCandidate < 0) { soleCandidate = row.RowIndex; }
                        else { multiple = true; break; }
                    }
                    if (soleCandidate >= 0 && !multiple) {
                        matrix.SlotAssignment[s] = soleCandidate;
                        var hint = matrix.MoonDown[s] ? SlotWorkType.LaPreferred
                                 : matrix.MoonSafe[soleCandidate][s] ? SlotWorkType.Any
                                 : SlotWorkType.NonLaPreferred;
                        matrix.SlotWorkHint[s] = hint;
                        DecrementWork(matrix.Rows[soleCandidate], 300.0, hint);
                    }
                }
            }
            TraceSnapshot(matrix, "After Pass 0 (exclusive)");

            // Pass 0b: Extend sub-minimum exclusive anchors
            for (int r = 0; r < matrix.Rows.Count; r++) {
                var row = matrix.Rows[r];
                if (row.PreFiltered || row.MinChunkSlots <= 0) continue;

                var runs = new List<(int Start, int Length)>();
                int rs = -1;
                for (int s = 0; s <= matrix.Slots.Count; s++) {
                    bool match = s < matrix.Slots.Count && matrix.SlotAssignment[s] == r;
                    if (match && rs < 0) rs = s;
                    else if (!match && rs >= 0) { runs.Add((rs, s - rs)); rs = -1; }
                }

                foreach (var run in runs) {
                    if (run.Length >= row.MinChunkSlots) continue;
                    int needed = row.MinChunkSlots - run.Length;
                    int extended = 0;
                    for (int e = run.Start + run.Length; e < matrix.Slots.Count && extended < needed; e++) {
                        if (matrix.SlotAssignment[e] >= 0 || !matrix.CanImage[r][e]) break;
                        var hint = matrix.MoonDown[e] ? SlotWorkType.LaPreferred
                                 : matrix.MoonSafe[r][e] ? SlotWorkType.Any
                                 : SlotWorkType.NonLaPreferred;
                        matrix.SlotAssignment[e] = r;
                        matrix.SlotWorkHint[e] = hint;
                        DecrementWork(row, 300.0, hint);
                        extended++;
                    }
                    for (int e = run.Start - 1; e >= 0 && extended < needed; e--) {
                        if (matrix.SlotAssignment[e] >= 0 || !matrix.CanImage[r][e]) break;
                        var hint = matrix.MoonDown[e] ? SlotWorkType.LaPreferred
                                 : matrix.MoonSafe[r][e] ? SlotWorkType.Any
                                 : SlotWorkType.NonLaPreferred;
                        matrix.SlotAssignment[e] = r;
                        matrix.SlotWorkHint[e] = hint;
                        DecrementWork(row, 300.0, hint);
                        extended++;
                    }
                }
            }

            TraceSnapshot(matrix, "After Pass 0b (anchor extend)");

            // Pass 1: Moon-down LA priority.
            // Moon-down slots are a scarce, restricted resource. They go FIRST to work
            // that can only be done moon-down (No Moon profiles, or tiers that are
            // moon-unsafe at every moon-up slot the target has tonight), fair-shared
            // across targets by that moon-down-only demand — never by a target's whole
            // workload. Only then does the remaining LA work compete for leftover dark
            // slots, with genuinely moon-flexible targets (at least a minimum block of
            // usable moon-up time) deferring to targets that have nowhere else to go.
            //
            // Before 9/9/26 the partition was per TARGET and binary: any single
            // moon-safe moon-up slot made a target "flexible", and an "exclusive"
            // target claimed dark time with its ENTIRE demand. On a 3% moon night
            // Cygnus Wall (sets before moonrise → exclusive) took the whole shared
            // window for Relaxed Ha/SII, while IC 348 (five dawn slots → flexible)
            // got no dark time at all for its No Moon RGB. Mirrors desktop ScheduleEngine.
            Func<Func<TargetRow, double>> makeGlobalCapOuter = null;
            {
                Func<int, int, bool> mdEligible = (ri, s) =>
                    matrix.MoonDown[s] && matrix.CanImage[ri][s] && matrix.SlotAssignment[s] < 0;

                // Proportional-share ceiling, used by Pass 1A and Pass 1B: a target's share of the WHOLE night's
                // usable supply in proportion to its remaining demand (never more than it can use), minus what
                // it already holds. Moon-down time is scarce only for work that cannot run elsewhere; capping
                // by the global share stops a No Moon target taking a whole moonless night from a target that
                // could have shared it (desktop mirror; auditor #6/#7, 10/7/26).
                Func<TargetRow, double> MakeGlobalCap() {
                    if (EngineExperiments.LegacyExclusiveBudget) return null;
                    var live = matrix.Rows.Where(r => !r.PreFiltered && !r.HasLockedWindow
                                                      && r.RemainingLaSec + r.RemainingNonLaSec > 0).ToList();
                    if (live.Count < 2) return null;
                    int UsableCount(TargetRow r) {
                        int n = 0;
                        for (int s = 0; s < matrix.Slots.Count; s++) if (r.UsableSlot[s] && !matrix.LockedSlot[s]) n++;
                        return n;
                    }
                    var union = new HashSet<int>();
                    foreach (var r in live)
                        for (int s = 0; s < matrix.Slots.Count; s++)
                            if (r.UsableSlot[s] && !matrix.LockedSlot[s]) union.Add(s);
                    double supplyAll = union.Count * 300.0;
                    double Dem(TargetRow r) => Math.Min(r.RemainingLaSec + r.RemainingNonLaSec, UsableCount(r) * 300.0);
                    double sumDem = live.Sum(Dem);
                    int Painted(TargetRow r) {
                        int n = 0;
                        for (int s = 0; s < matrix.SlotAssignment.Length; s++) if (matrix.SlotAssignment[s] == r.RowIndex) n++;
                        return n;
                    }
                    if (EngineExperiments.LegacyProportionalWeights)
                        return r => sumDem <= 0 ? double.MaxValue
                            : Math.Max(0, Math.Min(Dem(r), supplyAll * Dem(r) / sumDem) - Painted(r) * 300.0);

                    // Shares are proportional to each target's WORK (remaining demand); a target's usable time is only a
                    // CEILING on its share, and whatever a ceiling frees is redistributed to the others (water-filling).
                    // Work still to place PLUS what is already painted: painting decrements TierWorkSec, and the painted
                    // slots are subtracted from the entitlement below, so using the decremented work would count them
                    // twice and shrink the entitlement of a target that holds a Pass-0 window.
                    var work = live.ToDictionary(r => r, r => r.RemainingLaSec + r.RemainingNonLaSec
                        + (EngineExperiments.LegacyCapDoubleCount ? 0 : Painted(r) * 300.0));
                    var ceiling = live.ToDictionary(r => r, r => Math.Min(work[r], UsableCount(r) * 300.0));
                    var floorSec = live.ToDictionary(r => r, r => EngineExperiments.LegacyNoExclusiveFloor ? 0.0 : ExclusiveSlotCount(matrix, live, r) * 300.0);
                    var entitled = new Dictionary<TargetRow, double>();
                    var active = live.ToList();
                    double pool = supplyAll;
                    while (active.Count > 0) {
                        double sw = active.Sum(r => work[r]);
                        if (sw <= 0) break;
                        var capped = active.Where(r => pool * work[r] / sw > ceiling[r] + 1e-9).ToList();
                        var floored = active.Where(r => pool * work[r] / sw < floorSec[r] - 1e-9 && !capped.Contains(r)).ToList();
                        if (capped.Count == 0 && floored.Count == 0) {
                            foreach (var r in active) entitled[r] = pool * work[r] / sw;
                            break;
                        }
                        foreach (var r in capped) { entitled[r] = ceiling[r]; pool -= ceiling[r]; active.Remove(r); }
                        foreach (var r in floored) { entitled[r] = floorSec[r]; pool -= floorSec[r]; active.Remove(r); }
                    }
                    return r => entitled.TryGetValue(r, out var e) ? Math.Max(0, e - Painted(r) * 300.0) : double.MaxValue;
                }
                makeGlobalCapOuter = MakeGlobalCap;

                List<TargetRow> pass1aCapped = null;
                // Pass 1a: moon-down-only work, shared by moon-down-only demand
                {
                    var mdOnlyDemand = new Dictionary<int, double>();
                    foreach (var r in matrix.Rows) {
                        if (r.PreFiltered || !r.HasLaWork || r.MoonDownSlots <= 0) continue;
                        double d = MoonDownOnlyWorkSec(matrix, r);
                        if (d > 0) mdOnlyDemand[r.RowIndex] = d;
                    }
                    var candidates = matrix.Rows.Where(r => mdOnlyDemand.ContainsKey(r.RowIndex)).ToList();
                    var sorted = ApplySortChain(candidates, moonDownSortChain);
                    PaintTrace.AppendLine($"Pass 1a moon-down-only work ({sorted.Count}): {string.Join(", ", sorted.Select(c => $"{c.Profile.DisplayName} MDonly={mdOnlyDemand[c.RowIndex] / 60:F0}m"))}");
                    var capA = MakeGlobalCap();
                    // A target that sets before the moon-up block can reach its proportional entitlement ONLY with
                    // moon-down time: its moon-up usable run is shorter than a minimum block (or absent). Give those
                    // targets the moon-down time their window needs first, soonest-setting first, before the rest of
                    // the moon-down time is shared (auditor #472: T3 got 105 of 255 entitled min while the targets
                    // that could also shoot later took its moon-down window).
                    if (!EngineExperiments.LegacyPass1aNeed && capA != null) {
                        var need = new Dictionary<int, double>();
                        foreach (var r in sorted) {
                            double moonUpSec = MoonUpUsableSlotCount(matrix, r) >= r.MinChunkSlots ? MoonUpUsableSlotCount(matrix, r) * 300.0 : 0;
                            double n = Math.Min(mdOnlyDemand[r.RowIndex], Math.Max(0, capA(r) - moonUpSec));
                            if (n >= r.MinChunkSec) need[r.RowIndex] = n;
                        }
                        if (need.Count > 0) {
                            var needy = sorted.Where(r => need.ContainsKey(r.RowIndex)).OrderBy(r => r.LastUsableSlot).ToList();
                            Tr("PASS1A", $"Window-limited (needs moon-down to reach its share): [{string.Join(", ", needy.Select(r => $"{r.Profile.DisplayName}({need[r.RowIndex] / 60:F0}m)"))}]");
                            PaintPassFairShare(matrix, needy, mdEligible, SlotWorkType.LaPreferred, r => need[r.RowIndex], "PASS1A-NEED");
                            capA = MakeGlobalCap();
                        }
                    }
                    int HeldA(TargetRow r) {
                        int n = 0;
                        for (int s = 0; s < matrix.SlotAssignment.Length; s++) if (matrix.SlotAssignment[s] == r.RowIndex) n++;
                        return n;
                    }
                    PaintPassFairShare(matrix, sorted, mdEligible,
                        SlotWorkType.LaPreferred,
                        r => EngineExperiments.LegacyPass1aNeed ? mdOnlyDemand[r.RowIndex] : Math.Max(0, mdOnlyDemand[r.RowIndex] - HeldA(r) * 300.0),
                        "PASS1A", capA);
                    pass1aCapped = capA != null ? sorted : null;
                }
                TraceSnapshot(matrix, "After Pass 1a (moon-down-only work)");

                // Pass 1b: remaining LA work on whatever moon-down slots are left
                {
                    var candidates = matrix.Rows
                        .Where(r => !r.PreFiltered && r.HasLaWork && r.MoonDownSlots > 0
                                    && HasUnpaintedSlots(matrix, r, moonDownOnly: true))
                        .ToList();
                    var sorted = ApplySortChain(candidates, moonDownSortChain);

                    var exclusive = sorted.Where(r => MoonUpUsableSlotCount(matrix, r) < r.MinChunkSlots).ToList();
                    var flexible = sorted.Where(r => MoonUpUsableSlotCount(matrix, r) >= r.MinChunkSlots).ToList();
                    PaintTrace.AppendLine($"Pass 1b exclusive ({exclusive.Count}): {string.Join(", ", exclusive.Select(c => $"{c.Profile.DisplayName} LA={c.RemainingLaSec/60:F0}m"))}");
                    PaintTrace.AppendLine($"Pass 1b flexible ({flexible.Count}): {string.Join(", ", flexible.Select(c => $"{c.Profile.DisplayName} LA={c.RemainingLaSec/60:F0}m"))}");

                    // "Exclusive" targets (no real moon-up window) get the moon-down pool first -
                    // but only up to their proportional share of the WHOLE night, not of the
                    // moon-down pool among themselves. Otherwise they spend moon-down time the
                    // flexible targets need for most of their work (flexible targets can use
                    // moon-up time too, but that pool is usually far smaller).
                    Func<TargetRow, double> exclCap = exclusive.Count > 0 && flexible.Count > 0 ? MakeGlobalCap() : null;
                    PaintPassFairShare(matrix, exclusive, mdEligible,
                        SlotWorkType.LaPreferred,
                        r => r.RemainingLaSec + r.RemainingNonLaSec,
                        "PASS1B-EXCL", exclCap);
                    PaintPassFairShare(matrix, flexible, mdEligible,
                        SlotWorkType.LaPreferred,
                        r => r.RemainingLaSec + r.RemainingNonLaSec,
                        "PASS1B-FLEX");
                    // Spill: moon-down slots the flexible targets couldn't reach go back to the
                    // exclusive targets rather than sitting idle behind the cap.
                    if (exclCap != null) {
                        double Left(TargetRow r) {
                            int n = 0;
                            for (int s = 0; s < matrix.SlotAssignment.Length; s++) if (matrix.SlotAssignment[s] == r.RowIndex) n++;
                            return Math.Max(0, r.RemainingLaSec + r.RemainingNonLaSec - n * 300.0);
                        }
                        PaintPassFairShare(matrix, exclusive, mdEligible,
                            SlotWorkType.LaPreferred, Left, "PASS1B-EXCL-SPILL");
                    }
                }
                // Spill: moon-down slots nobody else could use go back to the moon-down-only targets that were
                // capped in Pass 1A, rather than sitting idle.
                if (pass1aCapped != null) {
                    double LeftA(TargetRow r) {
                        int n = 0;
                        for (int s = 0; s < matrix.SlotAssignment.Length; s++) if (matrix.SlotAssignment[s] == r.RowIndex) n++;
                        return Math.Max(0, MoonDownOnlyWorkSec(matrix, r) - n * 300.0);
                    }
                    PaintPassFairShare(matrix, pass1aCapped, mdEligible, SlotWorkType.LaPreferred, LeftA, "PASS1A-SPILL");
                }
            }
            TraceSnapshot(matrix, "After Pass 1 (moon-down LA)");

            // Pass 2: Moon-down remaining
            {
                var candidates = matrix.Rows
                    .Where(r => !r.PreFiltered && r.HasNonLaWork && HasUnpaintedSlots(matrix, r, moonDownOnly: true))
                    .ToList();
                PaintTrace.AppendLine($"Pass 2 candidates ({candidates.Count}): {string.Join(", ", candidates.Select(c => c.Profile.DisplayName))}");
                var sorted = ApplySortChain(candidates, sortChain);
                PaintPassFairShare(matrix, sorted,
                    (ri, s) => matrix.MoonDown[s] && matrix.CanImage[ri][s] && matrix.SlotAssignment[s] < 0,
                    SlotWorkType.Any,
                    r => r.TotalWorkSec);
            }
            TraceSnapshot(matrix, "After Pass 2 (moon-down remaining)");

            // Pass 3: Moon-up slots
            {
                var candidates = matrix.Rows
                    .Where(r => !r.PreFiltered
                                && (r.HasNonLaWork || (r.HasLaWork && HasMoonSafeMoonUpSlots(matrix, r)))
                                && HasUnpaintedSlots(matrix, r, moonDownOnly: false))
                    .ToList();
                PaintTrace.AppendLine($"Pass 3 candidates ({candidates.Count}): {string.Join(", ", candidates.Select(c => $"{c.Profile.DisplayName} NonLA={c.RemainingNonLaSec/60:F0}m LA={c.RemainingLaSec/60:F0}m"))}");
                var sorted = ApplySortChain(candidates, sortChain);

                var accessibleWork = new Dictionary<int, double>();
                foreach (var row in sorted) {
                    int ri = row.RowIndex;
                    int moonSafeMuSlots = 0;
                    for (int s = 0; s < matrix.Slots.Count; s++)
                        if (!matrix.MoonDown[s] && row.UsableSlot[s] && matrix.SlotAssignment[s] < 0)
                            moonSafeMuSlots++;
                    accessibleWork[ri] = row.RemainingNonLaSec + Math.Min(row.RemainingLaSec, moonSafeMuSlots * 300.0);
                    PaintTrace.AppendLine($"  {row.Profile.DisplayName}: moonSafeMuSlots={moonSafeMuSlots}, accessibleWork={accessibleWork[ri]/60:F0}m");
                }

                // === Pass 3a — window-exclusive pre-claim for setting-soon targets ===
                // A target whose imaging window closes well before the night's last usable
                // moon-up slot can only paint a scarce early sub-window. Without this, the
                // fungible fair-share pool below lets all-night targets eat those slots and
                // the setting-soon target is slivered (sort order is inert in the
                // proportional branch). Give each setting-soon target a BOUNDED first claim
                // on slots inside its OWN window; the unchanged fair-share allocator then
                // splits the remainder. Touches no shared-helper code — once these slots are
                // painted, SlotAssignment[s] >= 0 removes them from the pool automatically.
                {
                    const int SettingSoonMarginSlots = 6;   // 30 min: keeps staggered all-night targets OUT

                    int nightLastMu = -1;
                    for (int s = 0; s < matrix.Slots.Count; s++) {
                        if (matrix.MoonDown[s] || matrix.SlotAssignment[s] >= 0) continue;
                        if (sorted.Any(rr => rr.UsableSlot[s]) && s > nightLastMu) nightLastMu = s;
                    }

                    var settingSoon = sorted
                        .Where(r => r.LastUsableSlot >= 0 && r.LastUsableSlot < nightLastMu - SettingSoonMarginSlots)
                        .OrderBy(r => r.LastUsableSlot)
                        .ThenByDescending(r => accessibleWork.GetValueOrDefault(r.RowIndex))
                        .ToList();

                    foreach (var r in settingSoon) {
                        int ri = r.RowIndex;
                        double accSec = accessibleWork.GetValueOrDefault(ri);
                        if (accSec <= 0) continue;

                        // Free moon-up slots inside r's own window.
                        int W = 0;
                        for (int s = r.FirstUsableSlot; s >= 0 && s <= r.LastUsableSlot && s < matrix.Slots.Count; s++)
                            if (!matrix.MoonDown[s] && r.UsableSlot[s] && matrix.SlotAssignment[s] < 0) W++;
                        if (W == 0) continue;

                        // Demand competing INSIDE r's window: each overlapping row counts only
                        // the work it could place within r's window, not its whole-night demand.
                        double demandInWindow = 0;
                        foreach (var o in sorted) {
                            if (o.LastUsableSlot < r.FirstUsableSlot || o.FirstUsableSlot > r.LastUsableSlot) continue;
                            int oSlots = 0;
                            for (int s = r.FirstUsableSlot; s >= 0 && s <= r.LastUsableSlot && s < matrix.Slots.Count; s++)
                                if (!matrix.MoonDown[s] && o.UsableSlot[s] && matrix.SlotAssignment[s] < 0) oSlots++;
                            demandInWindow += Math.Min(Math.Max(0, accessibleWork.GetValueOrDefault(o.RowIndex)), oSlots * 300.0);
                        }
                        if (demandInWindow <= 0) continue;

                        // Pure proportional share of the window. Because demandInWindow sums
                        // the in-window demand of all overlapping rows, the reserves across
                        // co-windowed setting-soon targets are guaranteed to sum to <= W —
                        // they always fit, so they can never over-reserve and strand each
                        // other. (A multiplier > 1 here breaks that invariant.)
                        int capWork  = (int)Math.Ceiling(accSec / 300.0);
                        int capShare = (int)Math.Floor(W * accSec / demandInWindow);
                        int reserve  = Math.Min(Math.Min(capWork, capShare), W);
                        if (reserve <= 0) continue;

                        PaintTrace.AppendLine($"Pass 3a: [{r.Profile.DisplayName}] win={r.FirstUsableSlot}-{r.LastUsableSlot} W={W} acc={accSec/60:F0}m demandWin={demandInWindow/60:F0}m reserve={reserve}slots");

                        PaintChunks(matrix, r,
                            s => !matrix.MoonDown[s] && r.UsableSlot[s] && matrix.SlotAssignment[s] < 0
                                 && s >= r.FirstUsableSlot && s <= r.LastUsableSlot,
                            SlotWorkType.NonLaPreferred, reserve * 300.0);
                    }
                    TraceSnapshot(matrix, "After Pass 3a (window-exclusive pre-claim)");
                }

                // Moon-up time is split by each target's proportional entitlement over the WHOLE night, net of what it
                // already holds from the moon-down passes - not by a local share of the moon-up supply alone. A target
                // that is only up early (sets before the other's best time) otherwise gets a fraction of the one stretch
                // it can use while the other target, already over its share, takes the rest (auditor #369-371, 10/7/26).
                var p3Cap = EngineExperiments.LegacyPass3Entitlement ? null : makeGlobalCapOuter?.Invoke();
                if (p3Cap != null) Tr("PASS3", $"entitlement left: [{string.Join(", ", sorted.Select(r => $"{r.Profile.DisplayName}={p3Cap(r) / 60:F0}m"))}]");
                PaintPassFairShare(matrix, sorted,
                    (ri, s) => !matrix.MoonDown[s] && matrix.Rows[ri].UsableSlot[s] && matrix.SlotAssignment[s] < 0,
                    SlotWorkType.NonLaPreferred,
                    r => p3Cap == null ? accessibleWork.GetValueOrDefault(r.RowIndex)
                                       : Math.Min(accessibleWork.GetValueOrDefault(r.RowIndex), p3Cap(r)),
                    "PASS3");
            }
            TraceSnapshot(matrix, "After Pass 3 (moon-up)");

            EnforceMinimumAllocations(matrix);
            TraceSnapshot(matrix, "After EnforceMinimum");
            PruneSlivers(matrix);
            TraceSnapshot(matrix, "After PruneSlivers");
            ReofferFreedSlots(matrix);

            // Pass 4: Bonus / gap fill
            if (bonusEnabled) {
                AbsorbGaps(matrix);
            }

            // Defragment: merge A-B-A patterns into A-A-B to reduce target transitions
            DefragmentSlots(matrix);

            PruneSlivers(matrix);

            // Absorb pruned slots into adjacent targets
            AbsorbGaps(matrix);
            // Block consolidation: one contiguous block per target, earliest-closing window first; stubs below the
            // minimum are dropped; boundaries move toward each target's proportional entitlement.
            ConsolidateBlocks(matrix);
            AbsorbGaps(matrix);
            ReclaimBonusSlots(matrix);
            BalanceBoundaries(matrix);
            OptimizeBlockOrder(matrix);
            AbsorbGaps(matrix);
            DropSubFloorStubs(matrix);
            ReofferFreedSlots(matrix);   // idle stretches long enough for a real visit go to whoever can use them
            AbsorbGaps(matrix);
            TraceSnapshot(matrix, "FINAL (after defrag + prune + absorb)");
        }

        // ─── DefragmentSlots ─────────────────────────────────────────────────
        // Merge A-B-A patterns into A-A-B to reduce target transitions. A swap
        // requires both targets to be tier-aware usable in their swapped slots.

        private static void DefragmentSlots(ScheduleMatrix matrix) {
            bool changed = true;
            int iterations = 0;
            while (changed && iterations++ < 20) {
                changed = false;
                var blocks = new List<(int Target, int Start, int End)>();
                int cur = -2, bStart = 0;
                for (int s = 0; s <= matrix.Slots.Count; s++) {
                    int t = s < matrix.Slots.Count ? matrix.SlotAssignment[s] : -2;
                    if (t != cur) {
                        if (cur >= 0) blocks.Add((cur, bStart, s - 1));
                        cur = t;
                        bStart = s;
                    }
                }

                for (int i = 0; i + 2 < blocks.Count; i++) {
                    if (blocks[i].Target != blocks[i + 2].Target) continue;
                    if (blocks[i].Target < 0 || blocks[i + 1].Target < 0) continue;

                    // Blocks must be contiguous in slots — the block list skips
                    // unassigned gaps, and rewriting across a gap would paint slots
                    // nobody validated (no CanImage/UsableSlot check on gap slots).
                    if (blocks[i].End + 1 != blocks[i + 1].Start) continue;
                    if (blocks[i + 1].End + 1 != blocks[i + 2].Start) continue;

                    int targetA = blocks[i].Target;
                    int targetB = blocks[i + 1].Target;
                    int bSlotStart = blocks[i + 1].Start;
                    int bSlotEnd = blocks[i + 1].End;
                    int a2SlotStart = blocks[i + 2].Start;
                    int a2SlotEnd = blocks[i + 2].End;
                    int bLen = bSlotEnd - bSlotStart + 1;
                    int a2Len = a2SlotEnd - a2SlotStart + 1;

                    // Never rewrite locked exo transit slots — the swap region covers
                    // blocks i+1 and i+2, so check that whole span.
                    bool touchesLocked = false;
                    for (int s = bSlotStart; s <= a2SlotEnd && !touchesLocked; s++)
                        if (matrix.LockedSlot[s]) touchesLocked = true;
                    if (touchesLocked) continue;

                    // Both targets must be able to actually produce work in their
                    // swapped slots (tier-aware: moon safety, not just altitude)
                    // B lands on the LAST bLen slots of the region — only those need to be
                    // usable by B (desktop mirror).
                    // Moved target B needs planned work on its new slots; absorbing target A only
                    // needs to shoot something (bonus is fine) on the slots it takes over.
                    bool canSwap = true;
                    for (int s = bSlotStart; s <= bSlotEnd && canSwap; s++)
                        if (!(matrix.Rows[targetA].UsableSlot[s] || CanShootSomething(matrix, targetA, s))) canSwap = false;
                    for (int s = bSlotStart + a2Len; s <= a2SlotEnd && canSwap; s++)
                        if (!matrix.Rows[targetB].UsableSlot[s]) canSwap = false;
                    if (canSwap) {
                        var altB0 = matrix.Rows[targetB].Profile.AltitudePerSlot;
                        if (MeanAlt(altB0, bSlotStart + a2Len, a2SlotEnd) < MeanAlt(altB0, bSlotStart, bSlotEnd) - 10.0)
                            canSwap = false;
                    }

                    if (canSwap) {
                        int regionStart = bSlotStart;
                        for (int s = regionStart; s < regionStart + a2Len; s++)
                            matrix.SlotAssignment[s] = targetA;
                        for (int s = regionStart + a2Len; s < regionStart + a2Len + bLen; s++)
                            matrix.SlotAssignment[s] = targetB;
                        changed = true;
                        break;
                    }

                    // Other direction: B moves BEFORE A1 (A1 shifts right onto B's old slots and
                    // joins A2) — needs B usable across its new span, A usable across B's old
                    // span, no locked slots in A1, and B's sky there not much worse than where it
                    // sat. Desktop mirror (lunar-cycle sweep 10/7/26).
                    int a1SlotStart = blocks[i].Start, a1SlotEnd = blocks[i].End;
                    bool canBefore = true;
                    for (int s = a1SlotStart; s <= a1SlotEnd && canBefore; s++)
                        if (matrix.LockedSlot[s]) canBefore = false;
                    for (int s = a1SlotStart; s < a1SlotStart + bLen && canBefore; s++)
                        if (s <= a1SlotEnd && !matrix.Rows[targetB].UsableSlot[s]) canBefore = false;
                    for (int s = Math.Max(a1SlotStart + bLen, bSlotStart); s <= bSlotEnd && canBefore; s++)
                        if (!(matrix.Rows[targetA].UsableSlot[s] || CanShootSomething(matrix, targetA, s))) canBefore = false;
                    // The target that shifts (A) must keep its PLANNED time: a bonus-capable slot is not worth a planned one
                    // (stress 2T #3, 11-01: T1's only planned window was the early moon-down stretch; the merge slid it onto
                    // slots where it could only shoot bonus, and it ended with 30 of 275 entitled minutes).
                    if (canBefore && !EngineExperiments.LegacyDefragIgnoresPlanned) {
                        int UsableCnt(int t, int lo, int hi) {
                            int c = 0;
                            for (int x = lo; x <= hi; x++) if (matrix.Rows[t].UsableSlot[x]) c++;
                            return c;
                        }
                        int beforeA = UsableCnt(targetA, a1SlotStart, a1SlotEnd) + UsableCnt(targetA, a2SlotStart, a2SlotEnd);
                        int afterA = UsableCnt(targetA, a1SlotStart + bLen, a2SlotEnd);
                        if (afterA < beforeA - 2) {
                            Tr("DEFRAG", $"A-B-A before-merge REJECTED: [{matrix.Rows[targetA].Profile.DisplayName}] would lose planned-usable slots ({beforeA} -> {afterA})");
                            canBefore = false;
                        }
                    }
                    if (canBefore) {
                        var altB = matrix.Rows[targetB].Profile.AltitudePerSlot;
                        if (MeanAlt(altB, a1SlotStart, a1SlotStart + bLen - 1) < MeanAlt(altB, bSlotStart, bSlotEnd) - 10.0)
                            canBefore = false;
                    }
                    if (!canBefore) continue;

                    for (int s = a1SlotStart; s < a1SlotStart + bLen; s++)
                        matrix.SlotAssignment[s] = targetB;
                    for (int s = a1SlotStart + bLen; s <= a2SlotEnd; s++)
                        matrix.SlotAssignment[s] = targetA;
                    changed = true;
                    break;
                }
            }
        }

        /// <summary>The fair share of the night, in SLOTS, for each target: proportional to its work, never above what it can use
        /// (ceiling), never below the slots only it can image (floor) - AND never beyond what any GROUP of targets can use
        /// together. Two targets that both need the same short stretch of sky cannot each have their proportional share of it:
        /// the group's usable union is the limit, and the shortfall is shared in proportion to work (Hall's condition,
        /// water-filled). Used by the engine's cap, block balancing and order search (desktop mirror).</summary>
        internal static double[] FairEntitlements(double[] work, double[] ceil, double[] floor, bool[][] usable, int nS, double[] minVisit = null) {
            // A target whose share is smaller than its minimum visit cannot be given a valid visit tonight: it gets nothing and
            // its share goes to the others (the engine clears sub-minimum blocks the same way). A target whose whole remaining
            // work is below its minimum visit is exempt (end-of-target completion). Repeated until stable.
            if (minVisit != null && !EngineExperiments.LegacyNoMinVisitDrop) {
                var w = (double[])work.Clone();
                for (int it = 0; it < work.Length; it++) {
                    var e = FairEntitlementsCore(w, ceil, floor, usable, nS);
                    bool dropped = false;
                    for (int i = 0; i < w.Length; i++)
                        if (w[i] > 0 && e[i] < Math.Min(minVisit[i], work[i]) - 1e-9 && floor[i] <= 0) { w[i] = 0; dropped = true; }
                    if (!dropped) return e;
                }
                return FairEntitlementsCore(w, ceil, floor, usable, nS);
            }
            return FairEntitlementsCore(work, ceil, floor, usable, nS);
        }

        private static double[] FairEntitlementsCore(double[] work, double[] ceil, double[] floor, bool[][] usable, int nS) {
            // Nobody is owed more than the work it has left: an exclusive window larger than the work is not a floor above it.
            floor = floor.Select((f, i) => Math.Min(f, Math.Min(ceil[i], work[i]))).ToArray();
            int k = work.Length;
            var ent = new double[k];
            if (k == 0) return ent;
            if (k > 10 || EngineExperiments.LegacyNoHall) {
                // plain water-filling (ceiling + floor) without group limits
                int cnt = 0;
                for (int s = 0; s < nS; s++) {
                    bool any = false;
                    for (int i = 0; i < k; i++) if (usable[i][s]) { any = true; break; }
                    if (any) cnt++;
                }
                var act = Enumerable.Range(0, k).ToList();
                double pool = cnt;
                while (act.Count > 0) {
                    double sw = act.Sum(i => work[i]);
                    if (sw <= 0) { foreach (int i in act) ent[i] = 0; break; }
                    var capped = act.Where(i => pool * work[i] / sw > ceil[i] + 1e-9).ToList();
                    var floored = act.Where(i => pool * work[i] / sw < floor[i] - 1e-9 && !capped.Contains(i)).ToList();
                    if (capped.Count == 0 && floored.Count == 0) { foreach (int i in act) ent[i] = pool * work[i] / sw; break; }
                    foreach (int i in capped) { ent[i] = ceil[i]; pool -= ceil[i]; act.Remove(i); }
                    foreach (int i in floored) { ent[i] = floor[i]; pool -= floor[i]; act.Remove(i); }
                }
                return ent;
            }
            int full = (1 << k) - 1;
            var slotMask = new int[nS];
            for (int s = 0; s < nS; s++) for (int i = 0; i < k; i++) if (usable[i][s]) slotMask[s] |= 1 << i;
            var U = new int[full + 1];
            for (int m = 1; m <= full; m++) {
                int c = 0;
                for (int s = 0; s < nS; s++) if ((slotMask[s] & m) != 0) c++;
                U[m] = c;
            }
            var frozen = new bool[k];
            int left = k;
            while (left > 0) {
                double t = double.MaxValue;
                int tight = 0;
                for (int m = 1; m <= full; m++) {
                    double actW = 0, frozenSum = 0;
                    for (int i = 0; i < k; i++) if ((m >> i & 1) != 0) { if (frozen[i]) frozenSum += ent[i]; else actW += work[i]; }
                    if (actW <= 0) continue;
                    double tm = Math.Max(0, U[m] - frozenSum) / actW;
                    if (tm < t) { t = tm; tight = m; }
                }
                for (int i = 0; i < k; i++)
                    if (!frozen[i] && work[i] > 0 && ceil[i] / work[i] <= t + 1e-12) { t = ceil[i] / work[i]; tight = 1 << i; }
                if (tight == 0) {
                    for (int i = 0; i < k; i++) if (!frozen[i]) { ent[i] = 0; frozen[i] = true; }
                    break;
                }
                bool anyFloor = false;
                for (int i = 0; i < k; i++)
                    if (!frozen[i] && t * work[i] < floor[i] - 1e-9) { ent[i] = floor[i]; frozen[i] = true; left--; anyFloor = true; }
                if (anyFloor) continue;
                for (int i = 0; i < k; i++)
                    if (!frozen[i] && ((tight >> i & 1) != 0)) { ent[i] = t * work[i]; frozen[i] = true; left--; }
                if (left > 0 && Enumerable.Range(0, k).All(i => frozen[i] || (tight >> i & 1) == 0) && tight == 0) break;
            }
            return ent;
        }

        /// <summary>Slots only this target can image (no other live target is usable there): its share can never be below
        /// this, because nobody else can take that time.</summary>
        private static int ExclusiveSlotCount(ScheduleMatrix matrix, List<TargetRow> live, TargetRow r) {
            int n = 0;
            for (int s = 0; s < matrix.Slots.Count; s++) {
                if (!r.UsableSlot[s] || matrix.LockedSlot[s]) continue;
                bool other = false;
                foreach (var o in live) if (!ReferenceEquals(o, r) && o.UsableSlot[s]) { other = true; break; }
                if (!other) n++;
            }
            return n;
        }

        /// <summary>Per-target proportional entitlement in SLOTS: each target's share of the night's usable supply in
        /// proportion to its remaining work, capped by the time it can actually use, with freed time redistributed
        /// (water-filling). Same definition the fair-share budgets use.</summary>
        private static Dictionary<int, double> EntitlementSlots(ScheduleMatrix matrix) {
            var live = matrix.Rows.Where(r => !r.PreFiltered && !r.HasLockedWindow && r.Profile.RemainingTotalSec > 0).ToList();
            var ent = new Dictionary<int, double>();
            if (live.Count == 0) return ent;
            int nS = matrix.Slots.Count;
            var usable = live.Select(r => Enumerable.Range(0, nS).Select(x => r.UsableSlot[x] && !matrix.LockedSlot[x]).ToArray()).ToArray();
            var work = live.Select(r => r.Profile.RemainingTotalSec / 300.0).ToArray();
            var ceil = live.Select((r, i) => Math.Min(work[i], usable[i].Count(x => x))).ToArray();
            var floor = live.Select(r => EngineExperiments.LegacyNoExclusiveFloor ? 0.0 : (double)ExclusiveSlotCount(matrix, live, r)).ToArray();
            var res = FairEntitlements(work, ceil, floor, usable, nS, live.Select(r => (double)Math.Max(1, r.MinFloorSlots)).ToArray());
            for (int i = 0; i < live.Count; i++) ent[live[i].RowIndex] = res[i];
            return ent;
        }

        /// <summary>A slot held by a target that can only shoot BONUS there, while another target has planned work it can do there, goes
        /// to that other target (preferring the one already next to it, so no new visit is created). Planned work outranks bonus.
        /// Never touches locked exoplanet slots.</summary>
        private static void ReclaimBonusSlots(ScheduleMatrix matrix) {
            if (EngineExperiments.LegacyNoReclaim) return;
            int nS = matrix.Slots.Count;
            for (int s = 0; s < nS; s++) {
                int r = matrix.SlotAssignment[s];
                if (r < 0 || matrix.LockedSlot[s]) continue;
                if (s < matrix.Rows[r].UsableSlot.Length && matrix.Rows[r].UsableSlot[s]) continue;
                int best = -1;
                foreach (var o in matrix.Rows) {
                    if (o.RowIndex == r || o.PreFiltered || o.HasLockedWindow || s >= o.UsableSlot.Length || !o.UsableSlot[s] || o.TotalWorkSec <= 0) continue;
                    bool adjacent = (s > 0 && matrix.SlotAssignment[s - 1] == o.RowIndex) || (s + 1 < nS && matrix.SlotAssignment[s + 1] == o.RowIndex);
                    if (adjacent) { best = o.RowIndex; break; }
                    if (best < 0) best = o.RowIndex;
                }
                if (best >= 0) { matrix.SlotAssignment[s] = best; matrix.SlotWorkHint[s] = SlotWorkType.Any; }
            }
        }

        /// <summary>Time freed by pruning / clearing sub-minimum blocks is offered to targets that got NOTHING tonight (and were
        /// not themselves cleared) and can use it for at least their floor, before bonus fill hands it to a neighbour.</summary>
        private static void ReofferFreedSlots(ScheduleMatrix matrix) {
            if (EngineExperiments.LegacyNoReoffer) return;
            int nS = matrix.Slots.Count;
            var entR = EntitlementSlots(matrix);
            for (int guard = 0; guard < 16; guard++) {
                var held = new int[matrix.Rows.Count];
                for (int s = 0; s < nS; s++) if (matrix.SlotAssignment[s] >= 0) held[matrix.SlotAssignment[s]]++;
                int bestRow = -1, bestStart = -1, bestLen = 0;
                double bestScore = double.MinValue;
                foreach (var row in matrix.Rows) {
                    int r = row.RowIndex;
                    if (row.PreFiltered || row.HasLockedWindow || matrix.ClearedRows.Contains(r) || row.TotalWorkSec <= 0) continue;
                    int floor = Math.Max(1, row.MinFloorSlots);
                    for (int s = Math.Max(0, matrix.FirstUsableSlot); s <= matrix.LastUsableSlot && s < nS; s++) {
                        if (matrix.SlotAssignment[s] >= 0 || !row.UsableSlot[s]) continue;
                        int e = s;
                        while (e + 1 < nS && matrix.SlotAssignment[e + 1] < 0 && row.UsableSlot[e + 1]) e++;
                        int len = e - s + 1;
                        // a target with no time tonight comes first, then the one furthest below its share; a stretch long enough for a real visit
                        double score = (held[r] == 0 ? 1000 : 0) + (entR.TryGetValue(r, out var en) ? en - held[r] : 0) + len * 0.01;
                        if (len >= floor && score > bestScore) { bestRow = r; bestStart = s; bestLen = len; bestScore = score; }
                        s = e;
                    }
                }
                if (bestRow < 0) return;
                var take = Math.Min(bestLen, (int)Math.Ceiling(matrix.Rows[bestRow].TotalWorkSec / 300.0));
                take = Math.Max(take, Math.Min(bestLen, Math.Max(1, matrix.Rows[bestRow].MinFloorSlots)));
                Tr("REOFFER", $"[{matrix.Rows[bestRow].Profile.DisplayName}] gets freed slots {bestStart}-{bestStart + take - 1} offered to it");
                for (int x = bestStart; x < bestStart + take; x++) { matrix.SlotAssignment[x] = bestRow; matrix.SlotWorkHint[x] = SlotWorkType.Any; }
            }
        }

        /// <summary>A target visited twice whose SHORT visit is under its floor: that stub is not worth a slew there and back, so it
        /// idles (the target keeps its other, real block). The yield step already did this where the neighbour could use the slots;
        /// this covers a stub the neighbour cannot use.</summary>
        private static void DropSubFloorStubs(ScheduleMatrix matrix) {
            if (EngineExperiments.LegacyNoStubDrop) return;
            int nS = matrix.Slots.Count;
            foreach (var row in matrix.Rows) {
                int r = row.RowIndex;
                var runs = new List<(int S, int E)>();
                for (int s = 0; s < nS; s++) {
                    if (matrix.SlotAssignment[s] != r) continue;
                    int e = s;
                    while (e + 1 < nS && matrix.SlotAssignment[e + 1] == r) e++;
                    runs.Add((s, e));
                    s = e;
                }
                if (runs.Count < 2) continue;
                int floor = Math.Max(1, row.MinFloorSlots);
                foreach (var (S, E) in runs) {
                    int len = E - S + 1;
                    if (len >= floor || runs.Any(x => x != (S, E) && x.E - x.S + 1 >= floor) == false) continue;
                    bool locked = false;
                    for (int x = S; x <= E; x++) if (matrix.LockedSlot[x]) locked = true;
                    if (locked) continue;
                    Tr("STUBDROP", $"[{row.Profile.DisplayName}] stub slots {S}-{E} ({len * 5} min < floor {floor * 5}) idles - not worth a visit");
                    for (int x = S; x <= E; x++) matrix.SlotAssignment[x] = -1;
                }
            }
        }

        /// <summary>Last resort for a plan that is still off the proportional entitlement: searches every ORDER of the night's
        /// blocks (one contiguous block per target, k! orders, k &lt;= 5) with a small DP that picks each block's start and length
        /// inside its usable window so the blocks land as close to each target's entitlement as possible (shortfalls cost double:
        /// they are idle time too). The result replaces the plan only when it is clearly better (at least 3 slots less total
        /// deviation) and does not drop the mean altitude by more than 3 degrees. Never touches exoplanet nights.</summary>
        private static void OptimizeBlockOrder(ScheduleMatrix matrix) {
            if (EngineExperiments.LegacyNoOptimize) return;
            int nS = matrix.Slots.Count;
            if (matrix.Rows.Count < 3 || matrix.Rows.Any(r => r.HasLockedWindow)) return;
            for (int s = 0; s < nS; s++) if (matrix.LockedSlot[s]) return;
            var ent = EntitlementSlots(matrix);
            var rs = matrix.Rows.Where(r => ent.ContainsKey(r.RowIndex) && matrix.SlotAssignment.Contains(r.RowIndex)).Select(r => r.RowIndex).ToList();
            int k = rs.Count;
            if (k < 3 || k > 5 || rs.Count != matrix.SlotAssignment.Where(a => a >= 0).Distinct().Count()) return;

            // convex, so a shortfall is SPREAD over the targets that share a squeezed window instead of landing on one of them
            double Cost(int i, int len) { double e = ent[rs[i]]; double d = Math.Abs(len - e); return len < e ? 2 * d + 0.2 * d * d : d + 0.1 * d * d; }
            var held = new int[k];
            for (int s = 0; s < nS; s++) {
                int a = matrix.SlotAssignment[s];
                int ix = a >= 0 ? rs.IndexOf(a) : -1;
                if (ix >= 0) held[ix]++;
            }
            double curCost = 0;
            for (int i = 0; i < k; i++) curCost += Cost(i, held[i]);

            // usable run length ending at each slot, per block
            var run = new int[k][];
            for (int i = 0; i < k; i++) {
                run[i] = new int[nS + 1];
                var u = matrix.Rows[rs[i]].UsableSlot;
                for (int e = 1; e <= nS; e++) run[i][e] = (e - 1 < u.Length && u[e - 1]) ? run[i][e - 1] + 1 : 0;
            }

            double best = double.MaxValue;
            int[] bestOrder = null, bestStart = null, bestLen = null;
            var idx = Enumerable.Range(0, k).ToArray();
            void Permute(int at) {
                if (at == k) {
                    var f = new double[k + 1][];
                    var chLen = new int[k + 1][];
                    var chPrev = new int[k + 1][];
                    for (int i = 0; i <= k; i++) { f[i] = Enumerable.Repeat(double.MaxValue, nS + 1).ToArray(); chLen[i] = new int[nS + 1]; chPrev[i] = new int[nS + 1]; }
                    for (int e = 0; e <= nS; e++) f[0][e] = 0;
                    for (int i = 1; i <= k; i++) {
                        int t = idx[i - 1];
                        // prefix min of f[i-1] with argmin
                        var gm = new double[nS + 1];
                        var ga = new int[nS + 1];
                        double bm = double.MaxValue;
                        int ba = 0;
                        // h[p] = f[i-1][p] - GAP*p, so the cost of leaving (st - p) unused slots between blocks (they would be absorbed by the
                        // earlier block, unevenly) is added back as GAP*st
                        for (int p = 0; p <= nS; p++) {
                            double h = f[i - 1][p] == double.MaxValue ? double.MaxValue : f[i - 1][p] - 3.0 * p;
                            if (h < bm) { bm = h; ba = p; }
                            gm[p] = bm;
                            ga[p] = ba;
                        }
                        int floor = Math.Max(1, matrix.Rows[rs[t]].MinFloorSlots);
                        for (int e = 1; e <= nS; e++)
                            for (int len = floor; len <= run[t][e]; len++) {
                                int st = e - len;
                                if (gm[st] == double.MaxValue) continue;
                                double c = gm[st] + 3.0 * st + Cost(t, len) + (i == 1 ? 1.5 * Math.Max(0, st - Math.Max(0, matrix.FirstUsableSlot)) : 0);
                                if (c < f[i][e]) { f[i][e] = c; chLen[i][e] = len; chPrev[i][e] = ga[st]; }
                            }
                    }
                    double mn = double.MaxValue;
                    int me = -1;
                    for (int e = 0; e <= nS; e++) {
                        double fe = f[k][e] == double.MaxValue ? double.MaxValue : f[k][e] + 1.5 * Math.Max(0, matrix.LastUsableSlot + 1 - e);
                        if (fe < mn) { mn = fe; me = e; }
                    }
                    if (me < 0 || mn >= best) return;
                    var st2 = new int[k];
                    var ln2 = new int[k];
                    int cur = me;
                    for (int i = k; i >= 1; i--) { ln2[idx[i - 1]] = chLen[i][cur]; st2[idx[i - 1]] = cur - chLen[i][cur]; cur = chPrev[i][cur]; }
                    best = mn;
                    bestOrder = (int[])idx.Clone();
                    bestStart = st2;
                    bestLen = ln2;
                    return;
                }
                for (int j = at; j < k; j++) {
                    (idx[at], idx[j]) = (idx[j], idx[at]);
                    Permute(at + 1);
                    (idx[at], idx[j]) = (idx[j], idx[at]);
                }
            }
            Permute(0);
            if (bestOrder == null || best > curCost - 1) return;

            double MeanAltOwned(Func<int, int> owner) {
                double sum = 0;
                int n = 0;
                for (int s = 0; s < nS; s++) {
                    int a = owner(s);
                    if (a < 0) continue;
                    var al = matrix.Rows[a].Profile.AltitudePerSlot;
                    if (s < al.Length) { sum += al[s]; n++; }
                }
                return n > 0 ? sum / n : 0;
            }
            var newOwner = Enumerable.Repeat(-1, nS).ToArray();
            for (int i = 0; i < k; i++) for (int s = bestStart[i]; s < bestStart[i] + bestLen[i]; s++) newOwner[s] = rs[i];
            double altOld = MeanAltOwned(s => matrix.SlotAssignment[s]), altNew = MeanAltOwned(s => newOwner[s]);
            if (altNew < altOld - 3) { Tr("OPTIMIZE", $"better fit found (cost {curCost:F1} -> {best:F1}) but mean altitude would drop {altOld - altNew:F1} deg - kept"); return; }

            Tr("OPTIMIZE", $"cost {curCost:F1} -> {best:F1}: " + string.Join("  ", Enumerable.Range(0, k).OrderBy(i => bestStart[i]).Select(i => $"[{matrix.Rows[rs[i]].Profile.DisplayName}]={bestStart[i]}-{bestStart[i] + bestLen[i] - 1}")));
            Array.Fill(matrix.SlotAssignment, -1);
            for (int s = 0; s < nS; s++) if (newOwner[s] >= 0) { matrix.SlotAssignment[s] = newOwner[s]; matrix.SlotWorkHint[s] = SlotWorkType.Any; }
        }

        /// <summary>Moves the boundary between two ADJACENT blocks toward the proportional entitlement: a target below its
        /// share takes slots from a neighbour that is above its share (when it can use them and the neighbour keeps its
        /// minimum). Pruned slivers and gap absorption hand leftover slots to whichever block happens to sit before the gap,
        /// not to the one that is short; this undoes that. Never touches locked exoplanet slots.</summary>
        private static void BalanceBoundaries(ScheduleMatrix matrix) {
            if (EngineExperiments.LegacyNoBalance) return;
            int nS = matrix.Slots.Count;
            if (matrix.Rows.Count < 2 || matrix.Rows.Any(r => r.HasLockedWindow)) return;
            for (int s = 0; s < nS; s++) if (matrix.LockedSlot[s]) return;
            var ent = EntitlementSlots(matrix);
            if (ent.Count < 2) return;
            // Held = slots where the target can do PLANNED work: a bonus-only slot is not part of its share (stress 2T #3: a target
            // whose planned window was half given away still "held" 55 slots because 27 of them were bonus-only).
            int Count(int r) {
                int c = 0;
                for (int s = 0; s < nS; s++)
                    if (matrix.SlotAssignment[s] == r && (EngineExperiments.LegacyDefragIgnoresPlanned || (s < matrix.Rows[r].UsableSlot.Length && matrix.Rows[r].UsableSlot[s]))) c++;
                return c;
            }
            Tr("BALANCE", $"entitlement vs held (slots): {string.Join("  ", ent.Select(kv => $"[{matrix.Rows[kv.Key].Profile.DisplayName}] ent={kv.Value:F1} held={Count(kv.Key)}"))}");
            bool Usable(int r, int s) => s >= 0 && s < nS && s < matrix.Rows[r].UsableSlot.Length && matrix.Rows[r].UsableSlot[s];
            for (int pass = 0; pass < 12; pass++) {
                bool moved = false;
                for (int s = 0; s + 1 < nS; s++) {
                    int A = matrix.SlotAssignment[s], B = matrix.SlotAssignment[s + 1];
                    if (A < 0 || B < 0 || A == B || !ent.ContainsKey(A) || !ent.ContainsKey(B)) continue;
                    double defA = ent[A] - Count(A), defB = ent[B] - Count(B);
                    if (defA >= 1 && defB <= -1) {                       // A extends right into B
                        int k = (int)Math.Min(Math.Floor(defA), Math.Floor(-defB));
                        k = Math.Min(k, Count(B) - matrix.Rows[B].MinFloorSlots);
                        int run = 0;
                        while (s + 1 + run < nS && matrix.SlotAssignment[s + 1 + run] == B) run++;
                        int floorB = matrix.Rows[B].MinFloorSlots;
                        int kY = (int)Math.Min(Math.Floor(defA), run);
                        while (kY > 0 && !Enumerable.Range(s + 1, kY).All(x => Usable(A, x))) kY--;
                        int leftB = run - kY;
                        if (!EngineExperiments.LegacyNoYield && kY > 0 && kY > Math.Min(k, run - floorB) && leftB < floorB && Count(B) - run >= floorB) {
                            for (int x = s + 1; x <= s + kY; x++) matrix.SlotAssignment[x] = A;
                            for (int x = s + kY + 1; x <= s + run; x++) matrix.SlotAssignment[x] = -1;
                            Tr("BALANCE", $"[{matrix.Rows[A].Profile.DisplayName}] runs {kY} slot(s) longer; [{matrix.Rows[B].Profile.DisplayName}] stub of {leftB} slot(s) (< floor {floorB}) left idle");
                            moved = true;
                        } else {
                            k = Math.Min(k, run - floorB);     // the donor's remaining block must still meet its floor
                            while (k > 0 && !Enumerable.Range(s + 1, k).All(x => Usable(A, x))) k--;
                            if (k > 0) { for (int x = s + 1; x <= s + k; x++) matrix.SlotAssignment[x] = A; moved = true; }
                        }
                    } else if (defB >= 1 && defA <= -1) {                // B extends left into A
                        int k = (int)Math.Min(Math.Floor(defB), Math.Floor(-defA));
                        k = Math.Min(k, Count(A) - matrix.Rows[A].MinFloorSlots);
                        int run = 0;
                        while (s - run >= 0 && matrix.SlotAssignment[s - run] == A) run++;
                        int floorA = matrix.Rows[A].MinFloorSlots;
                        // Yield: the stub left behind would be under the donor's floor, so it is not worth a visit - B takes
                        // what it can use and the rest of the stub idles (the donor has another block elsewhere). B should
                        // start as early as it can (#386/#387: T2's 30-min stub held 3 slots T1 could have started on).
                        int kY = (int)Math.Min(Math.Floor(defB), run);
                        while (kY > 0 && !Enumerable.Range(s - kY + 1, kY).All(x => Usable(B, x))) kY--;
                        int leftA = run - kY;
                        if (!EngineExperiments.LegacyNoYield && kY > 0 && kY > Math.Min(k, run - floorA) && leftA < floorA && Count(A) - run >= floorA) {
                            for (int x = s - kY + 1; x <= s; x++) matrix.SlotAssignment[x] = B;
                            for (int x = s - run + 1; x <= s - kY; x++) matrix.SlotAssignment[x] = -1;
                            Tr("BALANCE", $"[{matrix.Rows[B].Profile.DisplayName}] starts {kY} slot(s) earlier; [{matrix.Rows[A].Profile.DisplayName}] stub of {leftA} slot(s) (< floor {floorA}) left idle");
                            moved = true;
                        } else {
                            k = Math.Min(k, run - floorA);
                            while (k > 0 && !Enumerable.Range(s - k + 1, k).All(x => Usable(B, x))) k--;
                            if (k > 0) { for (int x = s - k + 1; x <= s; x++) matrix.SlotAssignment[x] = B; moved = true; }
                        }
                    }
                    if (moved) break;
                }
                if (!moved) moved = CascadeBoundaries(matrix, ent, Count, Usable);
                if (!moved) break;
            }
        }

        /// <summary>Chain version of the boundary move: a target below its share and a target above it that are not
        /// neighbours still trade time - every block between them slides over by the same number of slots (each must be
        /// able to use the slots it gains). Needs one contiguous block per target between the two.</summary>
        private static bool CascadeBoundaries(ScheduleMatrix matrix, Dictionary<int, double> ent, Func<int, int> count, Func<int, int, bool> usable) {
            int nS = matrix.Slots.Count;
            var blocks = new List<(int R, int S, int E)>();
            for (int s = 0; s < nS; s++) {
                int a = matrix.SlotAssignment[s];
                if (a < 0) continue;
                if (blocks.Count > 0 && blocks[blocks.Count - 1].R == a && blocks[blocks.Count - 1].E == s - 1) blocks[blocks.Count - 1] = (a, blocks[blocks.Count - 1].S, s);
                else blocks.Add((a, s, s));
            }
            if (blocks.GroupBy(b => b.R).Any(g => g.Count() > 1)) return false;
            for (int i = 0; i < blocks.Count; i++)
                for (int j = 0; j < blocks.Count; j++) {
                    if (i == j || Math.Abs(i - j) < 2) continue;                       // neighbours are handled pairwise
                    int U = blocks[i].R, O = blocks[j].R;
                    if (!ent.ContainsKey(U) || !ent.ContainsKey(O)) continue;
                    double defU = ent[U] - count(U), defO = ent[O] - count(O);
                    if (defU < 1 || defO > -1) continue;
                    bool touching = true;
                    for (int t = Math.Min(i, j); t < Math.Max(i, j); t++) if (blocks[t].E + 1 != blocks[t + 1].S) touching = false;
                    if (!touching) continue;
                    int k = (int)Math.Min(Math.Floor(defU), Math.Floor(-defO));
                    int oLen = blocks[j].E - blocks[j].S + 1;
                    k = Math.Min(k, oLen - matrix.Rows[O].MinFloorSlots);
                    if (i < j) {                                                        // slide everything between right
                        while (k > 0) {
                            bool ok = true;
                            for (int t = i; t < j && ok; t++)
                                for (int x = blocks[t].E + 1; x <= blocks[t].E + k; x++) if (!usable(blocks[t].R, x)) { ok = false; break; }
                            if (ok) break;
                            k--;
                        }
                        if (k <= 0) continue;
                        for (int t = i; t < j; t++)
                            for (int x = blocks[t].E + 1; x <= blocks[t].E + k; x++) matrix.SlotAssignment[x] = blocks[t].R;
                    } else {                                                            // slide everything between left
                        while (k > 0) {
                            bool ok = true;
                            for (int t = i; t > j && ok; t--)
                                for (int x = blocks[t].S - k; x < blocks[t].S; x++) if (!usable(blocks[t].R, x)) { ok = false; break; }
                            if (ok) break;
                            k--;
                        }
                        if (k <= 0) continue;
                        for (int t = i; t > j; t--)
                            for (int x = blocks[t].S - k; x < blocks[t].S; x++) matrix.SlotAssignment[x] = blocks[t].R;
                    }
                    return true;
                }
            return false;
        }

        /// <summary>Re-lays the night out as ONE contiguous block per target. A visit is worth its slew only if it is a
        /// real block, so: targets whose total is under their minimum floor are dropped for tonight; the rest keep their
        /// slot budget but are placed earliest-deadline-first (the target whose window closes first goes first, so a
        /// setting target gets its whole window and nobody is interleaved); then each block boundary moves toward the
        /// proportional entitlement (a target under its share extends into a neighbour that is over, where it is usable).
        /// Only runs when the painted plan has a split target or a sub-floor stub; falls back to the painted plan if any
        /// target cannot be placed as one contiguous usable block.</summary>
        private static void ConsolidateBlocks(ScheduleMatrix matrix) {
            if (EngineExperiments.LegacyNoConsolidate) return;
            int nRows = matrix.Rows.Count, nS = matrix.Slots.Count;
            if (nRows < 2) return;
            if (matrix.Rows.Any(r => r.HasLockedWindow)) return;           // exoplanet nights keep their fixed windows
            for (int s = 0; s < nS; s++) if (matrix.LockedSlot[s]) return;

            var n = new int[nRows];
            var runs = new int[nRows];
            for (int s = 0; s < nS; s++) {
                int a = matrix.SlotAssignment[s];
                if (a >= 0) n[a]++;
            }
            for (int r = 0; r < nRows; r++) {
                int len = 0;
                for (int s = 0; s <= nS; s++) {
                    bool mine = s < nS && matrix.SlotAssignment[s] == r;
                    if (mine) len++;
                    else if (len > 0) { runs[r]++; len = 0; }
                }
            }
            bool needs = false;
            for (int r = 0; r < nRows; r++)
                if (n[r] > 0 && (runs[r] > 1 || n[r] < matrix.Rows[r].MinFloorSlots)) needs = true;
            if (!needs) return;

            for (int r = 0; r < nRows; r++)
                if (n[r] > 0 && n[r] < matrix.Rows[r].MinFloorSlots) {
                    Tr("CONSOL", $"[{matrix.Rows[r].Profile.DisplayName}] only {n[r]} slots (< floor {matrix.Rows[r].MinFloorSlots}) - dropped for tonight");
                    n[r] = 0;
                }
            var active = Enumerable.Range(0, nRows).Where(r => n[r] > 0).ToList();
            if (active.Count == 0) return;

            int Deadline(int r) { for (int s = nS - 1; s >= 0; s--) if (matrix.Rows[r].UsableSlot[s]) return s; return -1; }
            int Release(int r) { for (int s = 0; s < nS; s++) if (matrix.Rows[r].UsableSlot[s]) return s; return nS; }
            bool Fits(int r, int p, int len) {
                if (p < 0 || p + len > nS) return false;
                for (int s = p; s < p + len; s++) if (!matrix.Rows[r].UsableSlot[s]) return false;
                return true;
            }

            var start = new int[nRows];
            var order = new List<int>();
            var remaining = active.ToList();
            int pos = Math.Max(0, matrix.FirstUsableSlot);
            while (remaining.Count > 0 && pos < nS) {
                int best = -1;
                foreach (int r in remaining) {
                    if (!Fits(r, pos, n[r])) continue;
                    if (best < 0 || Deadline(r) < Deadline(best) || (Deadline(r) == Deadline(best) && Release(r) < Release(best))) best = r;
                }
                if (best < 0) { pos++; continue; }
                start[best] = pos;
                order.Add(best);
                remaining.Remove(best);
                pos += n[best];
            }
            if (remaining.Count > 0) {
                Tr("CONSOL", $"cannot place [{string.Join(", ", remaining.Select(r => matrix.Rows[r].Profile.DisplayName))}] as one contiguous usable block - keeping the painted plan");
                return;
            }

            // Move boundaries toward the proportional entitlement (slots).
            var ent = EntitlementSlots(matrix);
            for (int pass = 0; pass < 6; pass++) {
                bool moved = false;
                for (int k = 0; k + 1 < order.Count; k++) {
                    int A = order[k], B = order[k + 1];
                    int endA = start[A] + n[A];
                    if (endA != start[B]) continue;                          // not touching: nothing to trade
                    double defA = ent.GetValueOrDefault(A) - n[A], defB = ent.GetValueOrDefault(B) - n[B];
                    if (defA >= 1 && defB <= -1) {                          // A extends right into B
                        int shift = (int)Math.Min(Math.Floor(defA), Math.Floor(-defB));
                        shift = Math.Min(shift, n[B] - matrix.Rows[B].MinFloorSlots);
                        while (shift > 0 && !Fits(A, endA, shift)) shift--;
                        if (shift > 0) { n[A] += shift; n[B] -= shift; start[B] += shift; moved = true; }
                    } else if (defB >= 1 && defA <= -1) {                   // B extends left into A
                        int shift = (int)Math.Min(Math.Floor(defB), Math.Floor(-defA));
                        shift = Math.Min(shift, n[A] - matrix.Rows[A].MinFloorSlots);
                        while (shift > 0 && !Fits(B, start[B] - shift, shift)) shift--;
                        if (shift > 0) { n[B] += shift; n[A] -= shift; start[B] -= shift; moved = true; }
                    }
                }
                if (!moved) break;
            }

            Array.Fill(matrix.SlotAssignment, -1);
            foreach (int r in order)
                for (int s = start[r]; s < start[r] + n[r]; s++) { matrix.SlotAssignment[s] = r; matrix.SlotWorkHint[s] = SlotWorkType.Any; }
            Tr("CONSOL", "one block each: " + string.Join("  ", order.Select(r => $"[{matrix.Rows[r].Profile.DisplayName}]={start[r]}-{start[r] + n[r] - 1}")));
        }

        private static double MeanAlt(double[] alt, int from, int to) {
            if (alt == null || from > to || to >= alt.Length || from < 0) return 0;
            double sum = 0;
            for (int s = from; s <= to; s++) sum += alt[s];
            return sum / (to - from + 1);
        }

        private static void PaintPassFairShare(
            ScheduleMatrix matrix,
            List<TargetRow> sorted,
            Func<int, int, bool> slotEligible,
            SlotWorkType workHint,
            Func<TargetRow, double> demandFunc,
            string traceLabel = "FAIRSHARE",
            Func<TargetRow, double> budgetCap = null) {

            if (sorted.Count == 0) return;

            var uniqueEligible = new HashSet<int>();
            foreach (var row in sorted)
                for (int s = 0; s < matrix.Slots.Count; s++)
                    if (slotEligible(row.RowIndex, s))
                        uniqueEligible.Add(s);

            double totalSupply = uniqueEligible.Count * 300.0;
            if (totalSupply <= 0) return;

            double totalDemand = 0;
            var demands = new double[sorted.Count];
            for (int i = 0; i < sorted.Count; i++) {
                demands[i] = Math.Max(0, demandFunc(sorted[i]));
                totalDemand += demands[i];
            }
            if (totalDemand <= 0) return;

            var effectiveMins = new double[sorted.Count];
            for (int i = 0; i < sorted.Count; i++) {
                int existing = 0;
                for (int s = 0; s < matrix.SlotAssignment.Length; s++)
                    if (matrix.SlotAssignment[s] == sorted[i].RowIndex && (EngineExperiments.LegacyCountBonusAsBlock || (s < sorted[i].UsableSlot.Length && sorted[i].UsableSlot[s]))) existing++;
                effectiveMins[i] = existing >= sorted[i].MinChunkSlots
                    ? 0 : Math.Min(sorted[i].MinChunkSec, demands[i]);
            }

            PaintTrace.AppendLine($"  FairShare[{traceLabel}]: supply={totalSupply/60:F0}m demand={totalDemand/60:F0}m eligibleSlots={uniqueEligible.Count}");
            for (int i = 0; i < sorted.Count; i++) {
                int existing = 0;
                for (int s = 0; s < matrix.SlotAssignment.Length; s++)
                    if (matrix.SlotAssignment[s] == sorted[i].RowIndex) existing++;
                PaintTrace.AppendLine($"    [{i}] {sorted[i].Profile.DisplayName}: demand={demands[i]/60:F0}m effMin={effectiveMins[i]/60:F0}m existing={existing}slots");
            }

            var budgets = new double[sorted.Count];
            string branch;
            if (totalDemand <= totalSupply) {
                branch = "demand<=supply";
                for (int i = 0; i < sorted.Count; i++)
                    budgets[i] = demands[i];
            } else {
                double totalMin = effectiveMins.Sum();
                if (totalMin <= totalSupply) {
                    branch = $"totalMin({totalMin/60:F0}m)<=supply";
                    double excess = totalSupply - totalMin;
                    double excessDemand = sorted.Select((r, i) => Math.Max(0, demands[i] - effectiveMins[i])).Sum();
                    for (int i = 0; i < sorted.Count; i++) {
                        double extra = excessDemand > 0
                            ? excess * Math.Max(0, demands[i] - effectiveMins[i]) / excessDemand
                            : 0;
                        budgets[i] = Math.Min(demands[i], effectiveMins[i] + extra);
                    }
                } else {
                    branch = $"totalMin({totalMin/60:F0}m)>supply SCARCE";
                    double remaining = totalSupply;
                    var selected = new List<int>();
                    for (int i = 0; i < sorted.Count; i++) {
                        if (remaining >= effectiveMins[i]) {
                            selected.Add(i);
                            remaining -= effectiveMins[i];
                        }
                    }
                    PaintTrace.AppendLine($"    SCARCE: selected={string.Join(",", selected.Select(i => sorted[i].Profile.DisplayName))} leftover={remaining/60:F0}m");
                    if (selected.Count == 0 && sorted.Count > 0) {
                        selected.Add(0);
                        remaining = Math.Max(0, totalSupply - effectiveMins[0]);
                    }
                    double excessDemand = selected.Sum(i => Math.Max(0, demands[i] - effectiveMins[i]));
                    foreach (int i in selected) {
                        double extra = excessDemand > 0 && remaining > 0
                            ? remaining * Math.Max(0, demands[i] - effectiveMins[i]) / excessDemand
                            : 0;
                        budgets[i] = Math.Min(demands[i], effectiveMins[i] + Math.Max(0, extra));
                    }
                }
            }
            // Optional per-target ceiling (never below the minimum block): lets a pass that serves
            // only part of the field (Pass 1B's "exclusive" targets) stop short of its group-local
            // proportional share when that exceeds what the whole night entitles it to.
            if (budgetCap != null) {
                double before = budgets.Sum();
                for (int i = 0; i < sorted.Count; i++)
                    budgets[i] = Math.Min(budgets[i], Math.Max(effectiveMins[i], budgetCap(sorted[i])));
                // Water-filling: minutes a cap trimmed go to the targets that still have room under THEIR cap (and their
                // demand), in proportion to that room, so the pass still hands out the whole supply.
                if (!EngineExperiments.LegacyProportionalWeights) {
                    for (int iter = 0; iter < 4; iter++) {
                        double freed = before - budgets.Sum();
                        if (freed <= 1) break;
                        var room = new double[sorted.Count];
                        for (int i = 0; i < sorted.Count; i++)
                            room[i] = Math.Max(0, Math.Min(Math.Max(effectiveMins[i], budgetCap(sorted[i])), demands[i]) - budgets[i]);
                        double sumRoom = room.Sum();
                        if (sumRoom <= 1) break;
                        double give = Math.Min(freed, sumRoom);
                        for (int i = 0; i < sorted.Count; i++) budgets[i] += give * room[i] / sumRoom;
                    }
                }
            }

            PaintTrace.AppendLine($"  Branch: {branch}");
            for (int i = 0; i < sorted.Count; i++)
                PaintTrace.AppendLine($"    [{i}] {sorted[i].Profile.DisplayName}: budget={budgets[i]/60:F1}m");

            // Paint the TIGHTEST targets first (fewest eligible slots per minute of budget) so a
            // short-window target places its share before an all-night target consumes the
            // overlap — desktop mirror (Dome B 10/7: NGC 6823 budget ≈ 170 min, got 60).
            var paintOrder = Enumerable.Range(0, sorted.Count).Where(i => budgets[i] > 0).ToList();
            if (!EngineExperiments.LegacyFairShareOrder) {
                var eligible = paintOrder.ToDictionary(i => i,
                    i => Enumerable.Range(0, matrix.Slots.Count).Count(s => slotEligible(sorted[i].RowIndex, s)));
                paintOrder = paintOrder.OrderBy(i => eligible[i] - budgets[i] / 300.0).ThenBy(i => i).ToList();   // least slack first
            }
            foreach (int i in paintOrder) {
                var row = sorted[i];
                PaintChunks(matrix, row,
                    s => slotEligible(row.RowIndex, s),
                    workHint, budgets[i]);
            }
        }

        private static void EnforceMinimumAllocations(ScheduleMatrix matrix) {
            for (int r = 0; r < matrix.Rows.Count; r++) {
                // Exo transit rows: the locked window IS the allocation — exactly the
                // recommended capture span. Never extend it to MinTimeOnTarget, never
                // borrow for it, never clear it.
                if (matrix.Rows[r].HasLockedWindow) continue;

                int assigned = 0;
                int firstAssigned = -1, lastAssigned = -1;
                for (int s = 0; s < matrix.SlotAssignment.Length; s++) {
                    if (matrix.SlotAssignment[s] == r) {
                        assigned++;
                        if (firstAssigned < 0) firstAssigned = s;
                        lastAssigned = s;
                    }
                }

                if (assigned == 0 || assigned >= matrix.Rows[r].MinChunkSlots) continue;

                int needed = matrix.Rows[r].MinChunkSlots - assigned;
                int extended = 0;

                // Only pad with slots this target can actually USE (UsableSlot is
                // tier-aware: moon-up slots don't count for a No-Moon-only target).
                // Padding with merely-visible slots produced dead time the walk had
                // to release as idle — after taking it away from a neighbor.
                var usable = matrix.Rows[r].UsableSlot;
                bool Usable(int s) => s < usable.Length ? usable[s] : matrix.CanImage[r][s];

                for (int s = lastAssigned + 1; s < matrix.SlotAssignment.Length && extended < needed; s++) {
                    if (matrix.SlotAssignment[s] >= 0) break;
                    if (!Usable(s)) break;
                    matrix.SlotAssignment[s] = r;
                    matrix.SlotWorkHint[s] = SlotWorkType.Any;
                    extended++;
                }

                for (int s = firstAssigned - 1; s >= 0 && extended < needed; s--) {
                    if (matrix.SlotAssignment[s] >= 0) break;
                    if (!Usable(s)) break;
                    matrix.SlotAssignment[s] = r;
                    matrix.SlotWorkHint[s] = SlotWorkType.Any;
                    extended++;
                }

                // Phase 2: still short — borrow edge slots from adjacent targets that
                // can spare them (stay above their own minimum). Without this, a target
                // whose exclusive window is just short of the minimum gets cleared and
                // the exclusive slots go idle (nobody else can image them).
                if (assigned + extended < matrix.Rows[r].MinChunkSlots) {
                    int needed2 = matrix.Rows[r].MinChunkSlots - assigned - extended;

                    var slotCounts = new int[matrix.Rows.Count];
                    for (int s = 0; s < matrix.SlotAssignment.Length; s++)
                        if (matrix.SlotAssignment[s] >= 0)
                            slotCounts[matrix.SlotAssignment[s]]++;

                    bool CanBorrowFrom(int victim) =>
                        victim >= 0 && victim != r
                        && slotCounts[victim] - 1 >= matrix.Rows[victim].MinChunkSlots;

                    bool TryTake(int s) {
                        if (matrix.LockedSlot[s]) return false; // exo transit slot — never borrowed
                        if (!Usable(s)) return false;
                        int victim = matrix.SlotAssignment[s];
                        if (victim == r) return false;
                        if (victim >= 0) {
                            if (!CanBorrowFrom(victim)) return false;
                            slotCounts[victim]--;
                        }
                        matrix.SlotAssignment[s] = r;
                        matrix.SlotWorkHint[s] = SlotWorkType.Any;
                        slotCounts[r]++;
                        needed2--; extended++;
                        return true;
                    }

                    // Enumerate this target's runs and extend each run's edges,
                    // borrowing from neighbors who can spare the slots.
                    var runs2 = new List<(int Start, int End)>();
                    int rs2 = -1;
                    for (int s = 0; s <= matrix.SlotAssignment.Length; s++) {
                        bool match = s < matrix.SlotAssignment.Length && matrix.SlotAssignment[s] == r;
                        if (match && rs2 < 0) rs2 = s;
                        else if (!match && rs2 >= 0) { runs2.Add((rs2, s - 1)); rs2 = -1; }
                    }

                    foreach (var run in runs2.OrderByDescending(x => x.End - x.Start)) {
                        for (int s = run.End + 1; s < matrix.SlotAssignment.Length && needed2 > 0; s++)
                            if (!TryTake(s)) break;
                        for (int s = run.Start - 1; s >= 0 && needed2 > 0; s--)
                            if (!TryTake(s)) break;
                        if (needed2 <= 0) break;
                    }
                }

                // Still short of the minimum: keep the block if it reaches the
                // Min-Time Tolerance floor, otherwise drop it — a sliver that far
                // under the minimum isn't worth the slew, and its slots do more good
                // as a neighbor's planned or bonus time.
                if (assigned + extended < matrix.Rows[r].MinFloorSlots) {
                    PaintTrace.AppendLine($"MINENF: [{matrix.Rows[r].Profile.DisplayName}] {assigned + extended} slots < min {matrix.Rows[r].MinChunkSlots} and below tolerance floor {matrix.Rows[r].MinFloorSlots} — cleared");
                    for (int s = 0; s < matrix.SlotAssignment.Length; s++)
                        if (matrix.SlotAssignment[s] == r && !matrix.LockedSlot[s])
                            matrix.SlotAssignment[s] = -1;
                    matrix.ClearedRows.Add(r);
                } else if (assigned + extended < matrix.Rows[r].MinChunkSlots) {
                    PaintTrace.AppendLine($"MINENF: [{matrix.Rows[r].Profile.DisplayName}] {assigned + extended} slots < min {matrix.Rows[r].MinChunkSlots} but >= tolerance floor {matrix.Rows[r].MinFloorSlots} — kept");
                }
            }
        }

        private static void PruneSlivers(ScheduleMatrix matrix) {
            // A single adjacent target (just before or just after the run) that can image
            // EVERY slot of the run — so folding the run into it leaves no idle slots.
            bool NeighborCoversRun(int start, int len, int self) {
                int beforeT = start - 1 >= 0 ? matrix.SlotAssignment[start - 1] : -1;
                int afterT = start + len < matrix.SlotAssignment.Length ? matrix.SlotAssignment[start + len] : -1;
                foreach (int t in new[] { beforeT, afterT }) {
                    if (t < 0 || t == self) continue;
                    bool all = true;
                    for (int s = start; s < start + len; s++)
                        if (!matrix.CanImage[t][s]) { all = false; break; }
                    if (all) return true;
                }
                return false;
            }

            for (int r = 0; r < matrix.Rows.Count; r++) {
                var runs = new List<(int Start, int Length)>();
                int runStart = -1;
                for (int s = 0; s <= matrix.SlotAssignment.Length; s++) {
                    bool match = s < matrix.SlotAssignment.Length && matrix.SlotAssignment[s] == r;
                    if (match && runStart < 0) runStart = s;
                    else if (!match && runStart >= 0) {
                        runs.Add((runStart, s - runStart));
                        runStart = -1;
                    }
                }

                if (runs.Count < 2) continue;

                int minChunk = matrix.Rows[r].MinChunkSlots;
                bool hasGoodRun = runs.Any(run => run.Length >= minChunk);
                if (!hasGoodRun) continue;

                foreach (var run in runs) {
                    if (run.Length >= minChunk) continue;

                    // Locked exo transit slots are never pruned
                    bool lockedRun = false;
                    for (int s = run.Start; s < run.Start + run.Length; s++)
                        if (matrix.LockedSlot[s]) { lockedRun = true; break; }
                    if (lockedRun) continue;

                    // Don't prune if this run is separated from ALL good runs by
                    // another target's slots — it's an independent allocation (e.g.
                    // moon-up block), not a fragmented sliver of a nearby run.
                    bool adjacentToGoodRun = false;
                    foreach (var good in runs) {
                        if (good.Length < minChunk) continue;
                        int gapStart = good.Start < run.Start ? good.Start + good.Length : run.Start + run.Length;
                        int gapEnd = good.Start < run.Start ? run.Start : good.Start;
                        bool separated = false;
                        for (int g = gapStart; g < gapEnd; g++) {
                            if (matrix.SlotAssignment[g] >= 0 && matrix.SlotAssignment[g] != r) { separated = true; break; }
                        }
                        if (!separated) { adjacentToGoodRun = true; break; }
                    }
                    if (!adjacentToGoodRun) {
                        // A separated sub-min run is KEPT as an independent block (e.g. a real
                        // moon-up session worth its own visit) only when it clears the Min-Time
                        // Tolerance floor — the same undershoot the user allows everywhere else.
                        // Below the floor it's a fair-share leftover that doesn't justify its own
                        // two slews, so it folds into a neighbor that can cover the whole run; the
                        // target keeps its full (>= MinChunk) run elsewhere, so it loses no
                        // minimum. (10/6/26 Dome A: an 8-slot Lion run wedged between the two M45
                        // panels survived the old "tiny scrap" cutoff of MinChunk/4 = 3 slots
                        // despite a 9-slot floor at 25 % tolerance.)
                        if (run.Length >= matrix.Rows[r].MinFloorSlots || !NeighborCoversRun(run.Start, run.Length, r)) continue;
                    }

                    bool canAbsorb = false;
                    int before = run.Start - 1;
                    int after = run.Start + run.Length;
                    if (before >= 0 && matrix.SlotAssignment[before] >= 0 && matrix.SlotAssignment[before] != r) {
                        int adj = matrix.SlotAssignment[before];
                        if (matrix.CanImage[adj][run.Start]) canAbsorb = true;
                    }
                    if (!canAbsorb && after < matrix.SlotAssignment.Length && matrix.SlotAssignment[after] >= 0 && matrix.SlotAssignment[after] != r) {
                        int adj = matrix.SlotAssignment[after];
                        if (matrix.CanImage[adj][run.Start + run.Length - 1]) canAbsorb = true;
                    }
                    if (!canAbsorb) continue;

                    for (int s = run.Start; s < run.Start + run.Length; s++) {
                        matrix.SlotAssignment[s] = -1;
                        matrix.SlotWorkHint[s] = SlotWorkType.Any;
                    }
                }
            }
        }

        /// <summary>A/B switches for engine behavior changes (mirror of the desktop engine's), so
        /// the scenario audit can run old vs. new rules on the same build. Defaults = shipping.</summary>
        internal static class EngineExperiments {
            /// <summary>Pre-10/7/26: a chunk always painted from the start of its run.</summary>
            public static bool LegacyChunkPlacement;
            /// <summary>Pre-10/7/26: every visit cleared the target's filter cycle.</summary>
            public static bool LegacyFilterCycleReset;
            /// <summary>Pre-10/7/26: gap fill extended runs on altitude alone (CanImage).</summary>
            public static bool LegacyGapFill;
            /// <summary>Pre-10/7/26: Pass 0 judged exclusivity by altitude alone.</summary>
            public static bool LegacyExclusiveByAltitude;
            /// <summary>Pre-10/7/26: fair-share passes painted in sort order, not tightest-first.</summary>
            public static bool LegacyFairShareOrder;
            /// <summary>Pre-10/7/26: Pass 1B's exclusive targets split the whole moon-down pool
            /// among themselves, ahead of the flexible targets.</summary>
            public static bool LegacyExclusiveBudget = false;
            /// <summary>Pre-10/7/26: proportional shares were weighted by usable-capped demand, so a target with a
            /// longer window got a bigger slice for the same workload.</summary>
            public static bool LegacyProportionalWeights = false;
            /// <summary>Pre-10/7/26: no block consolidation - interleaved visits and sub-floor stubs stay as painted.</summary>
            public static bool LegacyNoConsolidate = false;
            /// <summary>Pre-10/7/26: bonus-only slots stayed with their holder even when another target had planned work there.</summary>
            public static bool LegacyNoReclaim = false;
            /// <summary>Pre-10/7/26: bonus-only slots (Pass 0 dawn claims) counted as the target's minimum block when deciding whether to give it a planned stretch.</summary>
            public static bool LegacyCountBonusAsBlock = false;
            /// <summary>Pre-10/7/26: a share smaller than the target's minimum visit was still "owed" to it.</summary>
            public static bool LegacyNoMinVisitDrop = false;
            /// <summary>Pre-10/7/26: the A-B-A before-merge let the shifted target trade planned-usable slots for bonus-only ones.</summary>
            public static bool LegacyDefragIgnoresPlanned = false;
            /// <summary>Pre-10/7/26: the next filter was the next one in the sorted loop, even after the rotation had been paused.</summary>
            public static bool LegacyNoLruRotation = false;
            /// <summary>Pre-10/7/26: slots freed by clearing/pruning sub-minimum blocks were not offered to targets that got no time.</summary>
            public static bool LegacyNoReoffer = false;
            /// <summary>Pre-10/7/26: a sub-floor second visit stayed in the plan.</summary>
            public static bool LegacyNoStubDrop = false;
            /// <summary>Pre-10/7/26: a walk-time reassignment (fallback / bonus target) took a whole run without checking the new owner stays above its minimum altitude.</summary>
            public static bool LegacyReassignNoAltCheck = false;
            /// <summary>Pre-10/7/26: a target opened on the top of its own sort order even when the wheel was already on an equally-ranked filter.</summary>
            public static bool LegacyNoWheelStart = false;
            /// <summary>Pre-10/7/26: the entitlement ignored limits shared by a GROUP of targets squeezed into the same stretch of sky.</summary>
            public static bool LegacyNoHall = false;
            /// <summary>Pre-10/7/26: no order search - blocks kept the order painting left, so a neighbour that could not use a slot blocked every trade.</summary>
            public static bool LegacyNoOptimize = false;
            /// <summary>Pre-10/7/26: a target whose stub is under its floor kept it even when the neighbour could use the slots.</summary>
            public static bool LegacyNoYield = false;
            /// <summary>Pre-10/7/26: the proportional entitlement ignored slots only one target can use (exclusive windows).</summary>
            public static bool LegacyNoExclusiveFloor = false;
            /// <summary>Pre-10/7/26: a leftover smaller than the tolerance got its own short batch instead of finishing in the current one.</summary>
            public static bool LegacyNoTailFold = false;
            /// <summary>Pre-10/7/26: adjacent blocks kept whatever boundary painting/gap absorption left, regardless of entitlement.</summary>
            public static bool LegacyNoBalance = false;
            /// <summary>Pre-10/7/26: the proportional cap used work AFTER painting yet also subtracted painted time (double count).</summary>
            public static bool LegacyCapDoubleCount = false;
            /// <summary>Pre-10/7/26: the walk could start a different filter for a frame or two at the end of a block / the night.</summary>
            public static bool LegacyTailHold = false;
            /// <summary>Pre-10/7/26: when the filter being cycled finished its work, the rotation restarted at the top of the sort order.</summary>
            public static bool LegacyCycleResume = false;
            /// <summary>Pre-10/7/26: Pass 1A shared moon-down time by moon-down demand only, with no first claim for targets whose window needs it.</summary>
            public static bool LegacyPass1aNeed = false;
            /// <summary>Pre-10/7/26: Pass 3 split moon-up time by a local share of the moon-up supply, ignoring the whole-night entitlement.</summary>
            public static bool LegacyPass3Entitlement = false;
            /// <summary>Pre-10/7/26: exoplanet transit captures were still judged by per-filter moon rules.</summary>
            public static bool LegacyExoMoonRules = false;
            /// <summary>Pre-10/7/26: "No Moon" filters were judged by the profile's phase-width formula, so they could be
            /// shot with the moon up when far enough away; now they need the moon DOWN, as the profile describes.</summary>
            public static bool LegacyNoMoonFormula = false;
        }

        /// <summary>"Some enabled exposure set can run here" — PickExposureSet's own candidate moon
        /// test, planned or completed (desktop mirror).</summary>
        private static bool CanShootSomething(ScheduleMatrix matrix, int r, int s) {
            if (!matrix.CanImage[r][s]) return false;
            var prof = matrix.Rows[r].Profile;
            var slot = matrix.Slots[s];
            double sep = s < prof.MoonSepPerSlot.Length ? prof.MoonSepPerSlot[s] : 0;
            foreach (var panel in prof.Target.Panels) {
                if (prof.PanelIndex.HasValue && panel.PanelIndex != prof.PanelIndex.Value) continue;
                foreach (var es in panel.ExposureSets) {
                    if (!es.Enabled || es.ExposureLengthSec <= 0) continue;
                    if (!es.HasMoonAvoidance || ExoMoonExempt(prof) || SessionScheduler.IsExposureSetMoonSafe(es, slot, sep, prof.Constraints)) return true;
                }
            }
            return false;
        }

        /// <summary>Extends painted runs into adjacent unassigned slots — forward from each run's
        /// end, then backward from the next run's start. Bonus/gap fill: a slot goes to a
        /// neighbor that is above the horizon (CanImage) AND moon-safe for at least one of its
        /// filters, planned or completed (SlotMoonOk) — not on altitude alone (a No Moon or
        /// Strict target would swallow moon-up slots it can't use), and not UsableSlot (needs
        /// remaining planned work, which denies a neighbor its bonus time). Desktop mirror.</summary>
        private static void AbsorbGaps(ScheduleMatrix matrix) {
            // Two rounds: planned neighbors (UsableSlot) first, then bonus-capable ones
            // (CanShootSomething) — desktop mirror.
            bool Usable(int r, int s, bool planned) {
                if (!matrix.CanImage[r][s]) return false;
                if (EngineExperiments.LegacyGapFill) return !planned;
                if (planned) {
                    var u = matrix.Rows[r].UsableSlot;
                    return s < u.Length && u[s];
                }
                return CanShootSomething(matrix, r, s);
            }
            foreach (bool planned in new[] { true, false }) {
                for (int s = 1; s < matrix.Slots.Count; s++) {
                    if (matrix.SlotAssignment[s] < 0 && matrix.SlotAssignment[s - 1] >= 0 && !matrix.LockedSlot[s - 1]) {
                        int ri = matrix.SlotAssignment[s - 1];
                        if (Usable(ri, s, planned)) { matrix.SlotAssignment[s] = ri; matrix.SlotWorkHint[s] = matrix.SlotWorkHint[s - 1]; }
                    }
                }
                for (int s = matrix.Slots.Count - 2; s >= 0; s--) {
                    if (matrix.SlotAssignment[s] < 0 && matrix.SlotAssignment[s + 1] >= 0 && !matrix.LockedSlot[s + 1]) {
                        int ri = matrix.SlotAssignment[s + 1];
                        if (Usable(ri, s, planned)) { matrix.SlotAssignment[s] = ri; matrix.SlotWorkHint[s] = matrix.SlotWorkHint[s + 1]; }
                    }
                }
            }
        }

        /// <summary>A pick used only to ask "could this target image here?" (the walk's entry,
        /// later-slot, fallback and bonus probes). PickExposureSet records the pick in the
        /// target's FilterCycle as a side effect; the cycle now carries across a target's
        /// visits, so every probe runs from a clean cycle (as before) and puts back whatever
        /// the live cycle was. Null/non-null is decided before any cycle logic.</summary>
        private static (ExposureSetData Es, string PanelLabel, int PanelIdx, int EsIdx) ProbePick(
            TargetProfile p, int targetIdx, int slotIdx, ScheduleMatrix matrix, ScheduleSessionState state,
            bool filterSwitchEnabled, int filterSwitchCount, HashSet<int> allowed, double filterSwitchTolerance,
            bool includeCompleted = false) {
            bool had = state.FilterCycle.TryGetValue(p, out var saved);
            state.FilterCycle.Remove(p);
            var r = SessionScheduler.PickExposureSet(p, targetIdx, slotIdx, matrix.Slots, state,
                filterSwitchEnabled, filterSwitchCount, allowed,
                includeCompleted: includeCompleted, filterSwitchTolerance: filterSwitchTolerance);
            if (had) state.FilterCycle[p] = saved; else state.FilterCycle.Remove(p);
            return r;
        }

        /// <summary>Where a chunk of <paramref name="len"/> slots lands inside a usable run
        /// (desktop mirror). Extending the target's own block → butt up against it, whichever
        /// side it is on. A fresh chunk → the window of the run where the target is highest
        /// (max altitude sum), not the run start, so a target peaking later doesn't squat on
        /// the dusk slots a setting-sooner target needs. Ties → earliest start.</summary>
        private static int ChunkStart(ScheduleMatrix matrix, TargetRow row, int runStart, int runLen, int len) {
            if (len >= runLen || EngineExperiments.LegacyChunkPlacement) return runStart;
            int ri = row.RowIndex;
            bool ownBefore = runStart > 0 && matrix.SlotAssignment[runStart - 1] == ri;
            if (ownBefore) return runStart;
            // No "own block on the right → paint from the run end" rule (see desktop).

            var alt = row.Profile.AltitudePerSlot;
            if (alt == null || alt.Length < runStart + runLen) return runStart;
            double sum = 0;
            for (int s = runStart; s < runStart + len; s++) sum += alt[s];
            double best = sum;
            int bestStart = runStart;
            for (int start = runStart + 1; start + len <= runStart + runLen; start++) {
                sum += alt[start + len - 1] - alt[start - 1];
                if (sum > best + 1e-9) { best = sum; bestStart = start; }
            }
            if (bestStart == runStart) return runStart;

            // A fresh chunk defaults to the run start; it moves to its best-altitude window
            // ONLY when the dusk slots that vacates are needed by a setting-sooner target that
            // has no block yet (desktop mirror — Dome B 10/6: NGC 6823 vs NGC 7331).
            foreach (var other in matrix.Rows) {
                if (other.RowIndex == ri || other.PreFiltered) continue;
                if (other.LastUsableSlot >= row.LastUsableSlot) continue;
                bool hasBlock = false;
                for (int s = 0; s < matrix.SlotAssignment.Length && !hasBlock; s++)
                    if (matrix.SlotAssignment[s] == other.RowIndex) hasBlock = true;
                if (hasBlock) continue;
                for (int s = runStart; s < bestStart; s++)
                    if (s < other.UsableSlot.Length && other.UsableSlot[s]) return bestStart;
            }
            return runStart;
        }

        private static void PaintChunks(
            ScheduleMatrix matrix, TargetRow row,
            Func<int, bool> slotFilter, SlotWorkType workHint, double maxWorkSec) {

            var runs = FindContiguousRuns(matrix, row, slotFilter);
            double workBudget = maxWorkSec;

            int ri = row.RowIndex;
            runs.Sort((a, b) => {
                bool aAdj = (a.Start > 0 && matrix.SlotAssignment[a.Start - 1] == ri)
                         || (a.Start + a.Length < matrix.SlotAssignment.Length && matrix.SlotAssignment[a.Start + a.Length] == ri);
                bool bAdj = (b.Start > 0 && matrix.SlotAssignment[b.Start - 1] == ri)
                         || (b.Start + b.Length < matrix.SlotAssignment.Length && matrix.SlotAssignment[b.Start + b.Length] == ri);
                if (aAdj != bAdj) return aAdj ? -1 : 1;
                return b.Length.CompareTo(a.Length);
            });

            foreach (var run in runs) {
                if (workBudget <= 0) break;

                // Per-block minimum: a fresh run too short to host MinTimeOnTarget is
                // only allowed while the target hasn't yet reached its minimum
                // allocation (fragments as a PRIMARY allocation are fine — e.g. a
                // mosaic panel taking leftover space between neighbors). Once a
                // target has its block, extra time must be a contiguous extension or
                // a real ≥minimum block — never a sliver visit requiring a slew.
                bool adjacentToOwn = (run.Start > 0 && matrix.SlotAssignment[run.Start - 1] == ri)
                    || (run.Start + run.Length < matrix.SlotAssignment.Length
                        && matrix.SlotAssignment[run.Start + run.Length] == ri);
                if (!adjacentToOwn && run.Length < row.MinChunkSlots) {
                    int alreadyPainted = 0;
                    // Count only slots where the target can do PLANNED work: bonus-only slots it was handed at dawn (Pass 0) are not a
                    // "block" that satisfies its minimum (desktop mirror).
                    for (int s2 = 0; s2 < matrix.SlotAssignment.Length; s2++)
                        if (matrix.SlotAssignment[s2] == ri && (EngineExperiments.LegacyCountBonusAsBlock || (s2 < row.UsableSlot.Length && row.UsableSlot[s2]))) alreadyPainted++;
                    if (alreadyPainted >= row.MinChunkSlots)
                        continue;
                }

                int slotsToAssign = Math.Min(run.Length, (int)Math.Ceiling(workBudget / 300.0));

                if (slotsToAssign < row.MinChunkSlots && run.Length >= row.MinChunkSlots
                    && maxWorkSec >= row.MinChunkSec && workBudget >= row.MinChunkSec)
                    slotsToAssign = row.MinChunkSlots;

                int chunkStart = ChunkStart(matrix, row, run.Start, run.Length, slotsToAssign);
                for (int i = 0; i < slotsToAssign && i < run.Length; i++) {
                    int s = chunkStart + i;
                    matrix.SlotAssignment[s] = row.RowIndex;
                    matrix.SlotWorkHint[s] = workHint;
                }

                double paintedSec = slotsToAssign * 300.0;
                workBudget -= paintedSec;
                DecrementWork(row, paintedSec, workHint);
            }
        }

        private static List<(int Start, int Length)> FindContiguousRuns(
            ScheduleMatrix matrix, TargetRow row, Func<int, bool> slotFilter) {

            var runs = new List<(int Start, int Length)>();
            int runStart = -1;

            for (int s = 0; s < matrix.Slots.Count; s++) {
                if (slotFilter(s)) {
                    if (runStart < 0) runStart = s;
                } else {
                    if (runStart >= 0) {
                        runs.Add((runStart, s - runStart));
                        runStart = -1;
                    }
                }
            }
            if (runStart >= 0)
                runs.Add((runStart, matrix.Slots.Count - runStart));

            return runs;
        }

        // Tier-aware work accounting. TierWorkSec is the only real store — the V3
        // RemainingLaSec/RemainingNonLaSec setters are no-ops, so until 9/9/26 this
        // never decremented anything and every pass saw each target's full
        // start-of-night demand regardless of what earlier passes had painted.
        //   LaPreferred    (moon-down slot): most restrictive tier first — the walk
        //                  spends dark time on No Moon work before Relaxed.
        //   NonLaPreferred (moon-up slot):   non-LA first, then least restrictive.
        //   Any                             : proportionally across tiers.
        private static void DecrementWork(TargetRow row, double sec, SlotWorkType hint) {
            var w = row.TierWorkSec;
            if (w.Length == 0 || sec <= 0) return;
            if (hint == SlotWorkType.LaPreferred) {
                for (int t = w.Length - 1; t >= 0 && sec > 0; t--) {
                    double use = Math.Min(sec, w[t]);
                    w[t] -= use; sec -= use;
                }
            } else if (hint == SlotWorkType.NonLaPreferred) {
                for (int t = 0; t < w.Length && sec > 0; t++) {
                    double use = Math.Min(sec, w[t]);
                    w[t] -= use; sec -= use;
                }
            } else {
                double total = w.Sum();
                if (total <= 0) return;
                double frac = Math.Min(1.0, sec / total);
                for (int t = 0; t < w.Length; t++) w[t] -= w[t] * frac;
            }
        }

        // Moon-up slots this row could actually use tonight (some tier with work is
        // safe there). Pass 1b treats a target as moon-flexible only when this is at
        // least a minimum block: a few dawn slots after a thin-crescent moonrise are
        // not an alternative to the dark window.
        private static int MoonUpUsableSlotCount(ScheduleMatrix matrix, TargetRow row) {
            int n = 0;
            for (int s = 0; s < matrix.Slots.Count; s++)
                if (!matrix.MoonDown[s] && row.UsableSlot[s]) n++;
            return n;
        }

        // Work that can ONLY be imaged with the moon down: tiers whose profile
        // requires moon-down (No Moon), plus tiers that are moon-unsafe at every
        // moon-up slot the target could otherwise image tonight. A target that sets
        // before moonrise has zero moon-up slots for EVERY tier, which says nothing
        // about the work itself — only its RequiresMoonDown tiers count there.
        private static double MoonDownOnlyWorkSec(ScheduleMatrix matrix, TargetRow row) {
            var tiers = row.Profile.Tiers;
            bool hasMoonUpImaging = false;
            for (int s = 0; s < matrix.Slots.Count && !hasMoonUpImaging; s++)
                if (!matrix.MoonDown[s] && matrix.CanImage[row.RowIndex][s]) hasMoonUpImaging = true;

            double sec = 0;
            for (int t = 1; t < row.TierWorkSec.Length; t++) {
                if (row.TierWorkSec[t] <= 0) continue;
                bool requiresMd = t < tiers.Length && tiers[t].RequiresMoonDown;
                bool unsafeAllMoonUp = hasMoonUpImaging
                    && (t >= row.TierMoonUpSafeSlots.Length || row.TierMoonUpSafeSlots[t] == 0);
                if (requiresMd || unsafeAllMoonUp) sec += row.TierWorkSec[t];
            }
            return sec;
        }

        private static bool HasUnpaintedSlots(ScheduleMatrix matrix, TargetRow row, bool moonDownOnly) {
            int ri = row.RowIndex;
            for (int s = 0; s < matrix.Slots.Count; s++) {
                if (matrix.SlotAssignment[s] >= 0) continue;
                if (!matrix.CanImage[ri][s]) continue;
                if (moonDownOnly && !matrix.MoonDown[s]) continue;
                return true;
            }
            return false;
        }

        private static bool HasMoonSafeMoonUpSlots(ScheduleMatrix matrix, TargetRow row) {
            for (int s = 0; s < matrix.Slots.Count; s++) {
                if (matrix.MoonDown[s]) continue;
                if (row.UsableSlot[s]) return true;
            }
            return false;
        }

        // ─── Sort Chain ──────────────────────────────────────────────────────

        public static List<TargetRow> ApplySortChain(List<TargetRow> candidates, List<SortCriteria> chain) {
            if (candidates.Count <= 1) return candidates;

            IOrderedEnumerable<TargetRow> ordered = null;

            for (int i = 0; i < chain.Count; i++) {
                var criteria = chain[i];
                Func<TargetRow, object> key = criteria switch {
                    SortCriteria.SettingSoonest => r => r.LastUsableSlot,
                    SortCriteria.Constrained => r => r.IsConstrained ? 0 : 1,
                    SortCriteria.MostRemainingWork => r => -r.TotalWorkSec,
                    SortCriteria.MostLaWork => r => -r.RemainingLaSec,
                    SortCriteria.LowestPeakAltitude => r => r.PeakAltitude,
                    SortCriteria.MosaicGroup => r => r.Profile.Target.Id,
                    SortCriteria.UserPriority => r => r.UserPriorityIndex,
                    _ => r => 0,
                };

                ordered = i == 0
                    ? candidates.OrderBy(key)
                    : ordered.ThenBy(key);
            }

            return ordered?.ToList() ?? candidates;
        }

        public static readonly List<SortCriteria> DefaultSortChain = new List<SortCriteria> {
            SortCriteria.LowestPeakAltitude,
            SortCriteria.SettingSoonest,
            SortCriteria.MostRemainingWork,
            SortCriteria.Constrained,
        };

        public static readonly List<SortCriteria> DefaultMoonDownSortChain = new List<SortCriteria> {
            SortCriteria.Constrained,
            SortCriteria.MostLaWork,
            SortCriteria.SettingSoonest,
        };

        // ─── WalkToLog ───────────────────────────────────────────────────────

        public static List<SimLogEntry> WalkToLog(
            ScheduleMatrix matrix,
            ScheduleSessionState state,
            TimeZoneInfo tz,
            bool ditherEnabled, int ditherEvery,
            bool filterSwitchEnabled, int filterSwitchCount,
            List<SortCriteria> sortChain = null,
            bool bonusEnabled = true,
            double filterSwitchTolerance = 0.5) {

            var log = new List<SimLogEntry>();

            if (matrix.FirstUsableSlot < 0) return log;

            var chain = sortChain ?? DefaultSortChain;
            var sortRanks = new Dictionary<int, string[]>();
            var activeRows = matrix.Rows.Where(r => r.TotalUsableSlots > 0).ToList();
            foreach (var row in matrix.Rows) {
                var ranks = new string[4];
                for (int ci = 0; ci < 4; ci++) {
                    if (ci >= chain.Count) { ranks[ci] = ""; continue; }
                    var criteria = chain[ci];
                    Func<TargetRow, double> keyFunc;
                    switch (criteria) {
                        case SortCriteria.SettingSoonest: keyFunc = r => r.LastUsableSlot; break;
                        case SortCriteria.Constrained: keyFunc = r => r.IsConstrained ? 0 : 1; break;
                        case SortCriteria.MostRemainingWork: keyFunc = r => -r.TotalWorkSec; break;
                        case SortCriteria.MostLaWork: keyFunc = r => -r.RemainingLaSec; break;
                        case SortCriteria.LowestPeakAltitude: keyFunc = r => r.PeakAltitude; break;
                        default: keyFunc = r => 0; break;
                    }
                    var sorted = activeRows.OrderBy(keyFunc).ToList();
                    int rank = sorted.IndexOf(row) + 1;
                    ranks[ci] = rank > 0 ? $"{rank}" : "";
                }
                sortRanks[row.RowIndex] = ranks;
            }

            DateTime nightStart = matrix.Slots[matrix.FirstUsableSlot].UtcStart;
            DateTime nightEnd = matrix.Slots[matrix.LastUsableSlot].UtcStart.AddSeconds(300);

            var startLocal = TimeZoneInfo.ConvertTimeFromUtc(nightStart, tz);
            log.Add(new SimLogEntry {
                Command = "Start",
                Time = startLocal.ToString("HH:mm"),
                Target = $"Session start — {startLocal:ddd MMM d, yyyy}",
                SlotIndex = matrix.FirstUsableSlot,
                UtcTime = nightStart,
            });

            DateTime currentUtc = nightStart;
            TargetProfile currentTarget = null;
            string currentFilter = null;
            string currentPanel = null;
            int subsSinceDither = 0;
            bool emittedDarkStart = false, emittedDarkEnd = false;
            bool endOfNightGraceUsed = false;
            ExposureSetData lastEs = null;
            string lastPanelLabel = "";
            int lastPanelIdx = -1;
            int lastEsIdx = -1;

            int lastAssignedRow = -1;
            DateTime waitStart = DateTime.MinValue;

            // Exoplanet transit targets run a continuous single-filter time series —
            // filter-switch batching is disabled for them (mirrors the desktop).
            bool FsFor(TargetProfile p) => filterSwitchEnabled && !p.FixedWindowStartUtc.HasValue;

            for (int s = matrix.FirstUsableSlot; s <= matrix.LastUsableSlot; s++) {
                int rowIdx = matrix.SlotAssignment[s];
                DateTime slotStart = matrix.Slots[s].UtcStart;
                DateTime slotEnd = slotStart.AddSeconds(300);

                if (currentUtc < slotStart)
                    currentUtc = slotStart;

                if (rowIdx < 0) {
                    if (lastAssignedRow >= 0) {
                        waitStart = currentUtc;
                        lastAssignedRow = -1;
                    }
                    continue;
                }

                if (lastAssignedRow < 0 && waitStart > DateTime.MinValue) {
                    double waitMin = (currentUtc - waitStart).TotalMinutes;
                    if (waitMin >= 5) {
                        var ws = TimeZoneInfo.ConvertTimeFromUtc(waitStart, tz);
                        var we = TimeZoneInfo.ConvertTimeFromUtc(currentUtc, tz);
                        log.Add(new SimLogEntry {
                            Command = "Wait",
                            Time = $"{ws:HH:mm}–{we:HH:mm}",
                            Target = $"Idle ({waitMin:F0} min)",
                            SlotIndex = s,
                            UtcTime = waitStart,
                        });
                    }
                }
                lastAssignedRow = rowIdx;

                var row = matrix.Rows[rowIdx];
                var prof = row.Profile;
                var target = prof.Target;
                int targetIdx = row.RowIndex;

                // Before slewing to a new target, check if it has planned work at this slot.
                // If not, find a target with planned work — don't waste time on bonus when others need it.
                if (currentTarget != prof) {
                    var probeAllowed = prof.PanelIndex.HasValue
                        ? new HashSet<int> { prof.PanelIndex.Value } : (HashSet<int>)null;
                    var probe = ProbePick(prof, targetIdx, s, matrix, state, FsFor(prof), filterSwitchCount, probeAllowed, filterSwitchTolerance);
                    if (probe.Es == null && matrix.LockedSlot[s]) {
                        // Locked exo transit slot: never hand the window to another
                        // target — stay on the transit target (bonus imaging covers
                        // any planned-count shortfall when enabled).
                        global::NINA.Core.Utility.Logger.Info(
                            $"AstroPM | Walk: [{prof.DisplayName}] entry probe failed at LOCKED slot {s} — keeping transit window");
                    } else if (probe.Es == null) {
                        Tr("WALK-PROBE", $"[{prof.DisplayName}] entry probe FAILED at slot {s} — no usable filter (moon alt {matrix.Slots[s].MoonAltDeg:F1}, MoonDown={matrix.MoonDown[s]}, target alt {(s < prof.AltitudePerSlot.Length ? prof.AltitudePerSlot[s] : 0):F0}, planned usable={(s < matrix.Rows[rowIdx].UsableSlot.Length && matrix.Rows[rowIdx].UsableSlot[s])})");

                        // Find where this target's contiguous run ends
                        int runEnd = s;
                        for (int fs = s + 1; fs <= matrix.LastUsableSlot; fs++) {
                            if (matrix.SlotAssignment[fs] != rowIdx) break;
                            runEnd = fs;
                        }

                        // Scan forward to find the first slot in this run where the target CAN image
                        int firstViableSlot = -1;
                        for (int fs = s + 1; fs <= runEnd; fs++) {
                            var laterProbe = ProbePick(prof, targetIdx, fs, matrix, state, FsFor(prof), filterSwitchCount,
                                prof.PanelIndex.HasValue ? new HashSet<int> { prof.PanelIndex.Value } : null, filterSwitchTolerance);
                            if (laterProbe.Es != null) { firstViableSlot = fs; break; }
                        }

                        // Determine how many leading slots to reassign
                        int reassignEnd = firstViableSlot >= 0 ? firstViableSlot : runEnd + 1;

                        int fallbackRow = -1;
                        for (int r = 0; r < matrix.Rows.Count; r++) {
                            if (r == rowIdx) continue;
                            if (!matrix.CanImage[r][s]) continue;
                            var fp = matrix.Rows[r].Profile;
                            var fAllowed = fp.PanelIndex.HasValue
                                ? new HashSet<int> { fp.PanelIndex.Value } : (HashSet<int>)null;
                            var fPick = ProbePick(fp, r, s, matrix, state, FsFor(fp), filterSwitchCount, fAllowed, filterSwitchTolerance);
                            if (fPick.Es != null) { fallbackRow = r; break; }
                        }
                        // A fallback that would get only a few leading slots - fewer than its own floor - and is not already
                        // the owner of the slot before them would just add a stub visit (and a slew each way): leave them idle.
                        if (!EngineExperiments.LegacyNoStubDrop && fallbackRow >= 0
                            && reassignEnd - s < Math.Max(1, matrix.Rows[fallbackRow].MinFloorSlots)
                            && !(s > 0 && matrix.SlotAssignment[s - 1] == fallbackRow)
                            && Enumerable.Range(0, matrix.SlotAssignment.Length).Any(x => x >= reassignEnd && matrix.SlotAssignment[x] == fallbackRow)) {
                            Tr("STUBDROP", $"[{matrix.Rows[fallbackRow].Profile.DisplayName}] would get only {reassignEnd - s} leading slot(s) of [{prof.DisplayName}] (< floor {matrix.Rows[fallbackRow].MinFloorSlots}) - left idle instead");
                            fallbackRow = -1;
                        }
                        if (fallbackRow >= 0) {
                            // Only reassign the leading unviable slots, not the entire run
                            for (int fs = s; fs < reassignEnd; fs++) {
                                if (matrix.LockedSlot[fs]) break; // never give away exo transit slots
                                if (!EngineExperiments.LegacyReassignNoAltCheck && !matrix.CanImage[fallbackRow][fs]) break;   // the new owner must be above its minimum altitude there
                                matrix.SlotAssignment[fs] = fallbackRow;
                            }

                            rowIdx = fallbackRow;
                            row = matrix.Rows[rowIdx];
                            prof = row.Profile;
                            target = prof.Target;
                            targetIdx = row.RowIndex;
                        } else {
                            // No fallback target has planned work here either. If this target can
                            // at least take bonus subs, keep the visit; otherwise release the dead
                            // slots and idle through them — a Slew row with no exposures becomes an
                            // orphan block that ExecuteNextBlock runs as a real slew + center + guide.
                            int releaseEnd = reassignEnd;
                            if (bonusEnabled) {
                                for (int fs = s; fs < reassignEnd; fs++) {
                                    var bonusProbe = ProbePick(prof, targetIdx, fs, matrix, state, FsFor(prof), filterSwitchCount, probeAllowed, filterSwitchTolerance, includeCompleted: true);
                                    if (bonusProbe.Es != null) { releaseEnd = fs; break; }
                                }
                            }
                            // The leading slots are dead for this target. Before idling
                            // through them, offer them as BONUS time to another target that
                            // can image here — the one we're already on first (no slew),
                            // then any other. Nobody had planned work here (checked above),
                            // so bonus subs are the only alternative to an idle mount.
                            int bonusRow = -1;
                            if (releaseEnd > s && bonusEnabled) {
                                var bonusOrder = Enumerable.Range(0, matrix.Rows.Count)
                                    .Where(r => r != rowIdx && !matrix.Rows[r].PreFiltered && matrix.CanImage[r][s])
                                    .OrderBy(r => currentTarget != null && matrix.Rows[r].Profile == currentTarget ? 0 : 1)
                                    .ToList();
                                foreach (int r in bonusOrder) {
                                    var bp = matrix.Rows[r].Profile;
                                    var bAllowed = bp.PanelIndex.HasValue
                                        ? new HashSet<int> { bp.PanelIndex.Value } : (HashSet<int>)null;
                                    var bPick = ProbePick(bp, r, s, matrix, state, FsFor(bp), filterSwitchCount, bAllowed, filterSwitchTolerance, includeCompleted: true);
                                    if (bPick.Es != null) { bonusRow = r; break; }
                                }
                            }
                            // A bonus target that would get only a few slots - fewer than its own floor - and is not already the owner of
                            // the slot before them would just add a stub visit: leave them idle instead.
                            if (!EngineExperiments.LegacyNoStubDrop && bonusRow >= 0) {
                                int bonusLen = 0;
                                for (int fs = s; fs < releaseEnd; fs++) { if (matrix.LockedSlot[fs] || !matrix.CanImage[bonusRow][fs]) break; bonusLen++; }
                                if (bonusLen < Math.Max(1, matrix.Rows[bonusRow].MinFloorSlots)
                                    && !(s > 0 && matrix.SlotAssignment[s - 1] == bonusRow)
                                    && Enumerable.Range(0, matrix.SlotAssignment.Length).Any(x => (x < s || x >= releaseEnd) && matrix.SlotAssignment[x] == bonusRow)) {
                                    Tr("STUBDROP", $"[{matrix.Rows[bonusRow].Profile.DisplayName}] would get only {bonusLen} bonus slot(s) (< floor {matrix.Rows[bonusRow].MinFloorSlots}) - left idle instead");
                                    bonusRow = -1;
                                }
                            }
                            if (bonusRow >= 0) {
                                global::NINA.Core.Utility.Logger.Info(
                                    $"AstroPM | Walk: [{prof.DisplayName}] no planned work anywhere at slot {s} — slots {s}–{releaseEnd - 1} → [{matrix.Rows[bonusRow].Profile.DisplayName}] for bonus imaging");
                                for (int fs = s; fs < releaseEnd; fs++) {
                                    if (matrix.LockedSlot[fs]) break;
                                    if (!EngineExperiments.LegacyReassignNoAltCheck && !matrix.CanImage[bonusRow][fs]) break;
                                    matrix.SlotAssignment[fs] = bonusRow;
                                }
                                rowIdx = bonusRow;
                                row = matrix.Rows[rowIdx];
                                prof = row.Profile;
                                target = prof.Target;
                                targetIdx = row.RowIndex;
                            } else if (releaseEnd > s) {
                                // Never release exo transit slots — stop at the first
                                // locked slot so the walk still visits the window.
                                int released = s;
                                for (int fs = s; fs < releaseEnd; fs++) {
                                    if (matrix.LockedSlot[fs]) break;
                                    matrix.SlotAssignment[fs] = -1;
                                    released = fs + 1;
                                }
                                if (released > s) {
                                    global::NINA.Core.Utility.Logger.Info(
                                        $"AstroPM | Walk: [{prof.DisplayName}] no plannable or bonus work at slot {s} — releasing {released - s} slot(s) as idle instead of slewing");
                                    waitStart = currentUtc;
                                    lastAssignedRow = -1;
                                    s = released - 1;
                                    continue;
                                }
                            } else {
                                // Staying for bonus imaging only makes sense as a real visit: a short tail (under this target's floor)
                                // after the walk has already left it would cost a slew back for a few bonus subs - idle instead.
                                int tailLen = runEnd - s + 1;
                                if (!EngineExperiments.LegacyNoStubDrop && tailLen < Math.Max(1, row.MinFloorSlots)
                                    && !(s > 0 && matrix.SlotAssignment[s - 1] == rowIdx && currentTarget == prof)
                                    && Enumerable.Range(0, matrix.SlotAssignment.Length).Any(x => (x < s || x > runEnd) && matrix.SlotAssignment[x] == rowIdx)) {
                                    int released2 = s;
                                    for (int fs = s; fs <= runEnd; fs++) {
                                        if (matrix.LockedSlot[fs]) break;
                                        matrix.SlotAssignment[fs] = -1;
                                        released2 = fs + 1;
                                    }
                                    if (released2 > s) {
                                        Tr("STUBDROP", $"[{prof.DisplayName}] bonus tail of {tailLen} slot(s) (< floor {row.MinFloorSlots}) at slot {s} - left idle");
                                        waitStart = currentUtc;
                                        lastAssignedRow = -1;
                                        s = released2 - 1;
                                        continue;
                                    }
                                }
                            }
                        }
                    }

                    // Probes put back the cycle they touched (ProbePick), so a repeat visit keeps
                    // the batch its previous visit was cut off in. Legacy: every visit started clean.
                    if (EngineExperiments.LegacyFilterCycleReset) state.FilterCycle.Remove(prof);
                }

                if (currentTarget != prof) {
                    EmitDarkness(log, matrix, tz, s, ref emittedDarkStart, ref emittedDarkEnd);

                    var slewPnl = prof.PanelIndex.HasValue
                        ? target.Panels.FirstOrDefault(p => p.PanelIndex == prof.PanelIndex.Value) : null;
                    double slewRa = slewPnl?.RaHours ?? target.RaHours;
                    double slewDec = slewPnl?.DecDegrees ?? target.DecDegrees;

                    var slewTime = TimeZoneInfo.ConvertTimeFromUtc(currentUtc, tz);
                    log.Add(new SimLogEntry {
                        Command = "Slew",
                        Time = slewTime.ToString("HH:mm"),
                        Target = prof.DisplayName,
                        Rotation = $"{target.RotationDeg:F1}°",
                        RA = SessionScheduler.FormatRa(slewRa),
                        DEC = SessionScheduler.FormatDec(slewDec),
                        SlotIndex = s,
                        UtcTime = currentUtc,
                    });
                    currentTarget = prof;
                    currentFilter = null;
                    currentPanel = null;
                    subsSinceDither = 0;

                    // The filter cycle carries across this target's visits: a second visit finishes
                    // the batch the first was cut off in, then rotates on — instead of restarting at
                    // the top of the sorted list (10/6/26 Dome B: NGC 7331's second visit ran G x30
                    // again while R was never taken all night). Desktop mirror.
                    if (EngineExperiments.LegacyFilterCycleReset) state.FilterCycle.Remove(prof);

                    if (target.Panels.Count > 1 && !prof.PanelIndex.HasValue) {
                        foreach (var key in state.PanelTimeOnTarget.Keys
                            .Where(k => k.Item1 == prof).ToList())
                            state.PanelTimeOnTarget[key] = 0;
                    }
                }

                while (currentUtc < slotEnd && currentUtc < nightEnd) {
                    int currentSlotIdx = SessionScheduler.GetSlotIndex(currentUtc, matrix.Slots);
                    if (currentSlotIdx > s) {
                        int nextAssignment = currentSlotIdx < matrix.SlotAssignment.Length
                            ? matrix.SlotAssignment[currentSlotIdx] : -1;
                        if (nextAssignment != rowIdx)
                            break;
                    }

                    EmitDarkness(log, matrix, tz, currentSlotIdx, ref emittedDarkStart, ref emittedDarkEnd);

                    HashSet<int> allowedPanels = prof.PanelIndex.HasValue
                        ? new HashSet<int> { prof.PanelIndex.Value } : null;

                    // Panel-lock when remaining time is too short for rotation
                    if (allowedPanels == null && target.Panels.Count > 1 && currentPanel != null) {
                        double remainingSec = (nightEnd - currentUtc).TotalSeconds;
                        for (int fs = currentSlotIdx + 1; fs < matrix.SlotAssignment.Length; fs++) {
                            if (matrix.SlotAssignment[fs] != rowIdx) {
                                remainingSec = Math.Min(remainingSec, (matrix.Slots[fs].UtcStart - currentUtc).TotalSeconds);
                                break;
                            }
                        }
                        double minPanelSec = prof.Constraints.MinTimeOnTargetHrs * 3600;
                        if (remainingSec < minPanelSec) {
                            int panelIdx = target.Panels.OrderBy(p => p.PanelIndex).ToList()
                                .FindIndex(p => $"P{p.PanelIndex + 1}" == currentPanel);
                            if (panelIdx >= 0)
                                allowedPanels = new HashSet<int> { panelIdx };
                        }
                    }

                    // Compute remaining seconds assigned to this target for tolerance check
                    double targetRemainingSec = Math.Max(0, (slotEnd - currentUtc).TotalSeconds);
                    for (int fs = s + 1; fs <= matrix.LastUsableSlot; fs++) {
                        if (matrix.SlotAssignment[fs] != rowIdx) break;
                        targetRemainingSec += 300.0;
                    }

                    var pick = SessionScheduler.PickExposureSet(
                        prof, targetIdx, currentSlotIdx, matrix.Slots, state,
                        FsFor(prof), filterSwitchCount, allowedPanels,
                        filterSwitchTolerance: filterSwitchTolerance,
                        targetRemainingSec: targetRemainingSec);

                    // Tail hold: a switch to a DIFFERENT filter that cannot fit the filter-switch tolerance before this
                    // block (or the night) ends is not worth the filter change - stay on the filter we are on for the
                    // last frame(s) instead (a Bonus frame if that filter already finished its plan).
                    if (!EngineExperiments.LegacyTailHold && pick.Es != null && lastEs != null && !ReferenceEquals(pick.Es, lastEs)
                        && target.Panels.Any(pn => pn.ExposureSets.Contains(lastEs))   // lastEs must belong to THIS target (it survives a slew)
                        && FsFor(prof) && filterSwitchCount > 0
                        && (state.RemainingForEs(targetIdx, lastPanelIdx, lastEsIdx, lastEs) > 0 || bonusEnabled)) {
                        int minSubsTail = Math.Max(1, (int)Math.Ceiling(filterSwitchCount * filterSwitchTolerance));
                        double runwayTail = Math.Min(targetRemainingSec, (nightEnd - currentUtc).TotalSeconds);
                        int fitTail = (int)(runwayTail / pick.Es.ExposureLengthSec);
                        int needTail = Math.Min(minSubsTail, Math.Max(1, state.RemainingForEs(targetIdx, pick.PanelIdx, pick.EsIdx, pick.Es)));
                        var tailSlot = currentSlotIdx >= 0 && currentSlotIdx < matrix.Slots.Count ? matrix.Slots[currentSlotIdx] : null;
                        double tailSep = currentSlotIdx >= 0 && currentSlotIdx < prof.MoonSepPerSlot.Length ? prof.MoonSepPerSlot[currentSlotIdx] : 0;
                        bool lastOk = tailSlot != null && (!lastEs.HasMoonAvoidance || ExoMoonExempt(prof)
                            || SessionScheduler.IsExposureSetMoonSafe(lastEs, tailSlot, tailSep, prof.Constraints));
                        if (fitTail < needTail && lastOk) {
                            Tr("WALK-TAILHOLD", $"[{prof.DisplayName}] slot {currentSlotIdx}: {pick.Es.FilterName} would get only {fitTail} sub(s) (< {needTail}) - staying on {lastEs.FilterName}");
                            pick = (lastEs, lastPanelLabel, lastPanelIdx, lastEsIdx);
                            state.FilterCycle[prof] = (lastEs.FilterName, lastPanelLabel, filterSwitchCount);
                        }
                    }

                    if (pick.Es == null && bonusEnabled)
                    {
                        // Bonus imaging runs the REGULAR pick over completed sets, so the
                        // normal filter-switch cadence rotates bonus subs across every
                        // filter that still passes its moon/safety tests — instead of
                        // repeating the last filter for the rest of the night.
                        pick = SessionScheduler.PickExposureSet(
                            prof, targetIdx, currentSlotIdx, matrix.Slots, state,
                            FsFor(prof), filterSwitchCount, allowedPanels, includeCompleted: true,
                            filterSwitchTolerance: filterSwitchTolerance,
                            targetRemainingSec: targetRemainingSec);
                        if (pick.Es == null && lastEs != null)
                        {
                            // Last resort (e.g. panel restriction excluded every set):
                            // repeat the previous exposure if it's still moon-safe here.
                            var gapSlot = currentSlotIdx >= 0 && currentSlotIdx < matrix.Slots.Count
                                ? matrix.Slots[currentSlotIdx] : null;
                            double gapMoonSep = currentSlotIdx >= 0 && currentSlotIdx < prof.MoonSepPerSlot.Length
                                ? prof.MoonSepPerSlot[currentSlotIdx] : 0;
                            bool gapMoonOk = !lastEs.HasMoonAvoidance || ExoMoonExempt(prof) || (gapSlot != null
                                && SessionScheduler.IsExposureSetMoonSafe(lastEs, gapSlot, gapMoonSep, prof.Constraints));
                            if (gapMoonOk)
                                pick = (lastEs, lastPanelLabel, lastPanelIdx, lastEsIdx);
                        }
                    }
                    if (pick.Es == null) break;

                    var es = pick.Es;
                    if (currentUtc.AddSeconds(es.ExposureLengthSec) > nightEnd) {
                        // End-of-night grace: rather than cutting the last sub of the night,
                        // take one final exposure that starts before night end and runs past it.
                        // Interior block boundaries are unaffected — this only fires at session
                        // end, and only once (currentUtc passes nightEnd, ending all loops).
                        if (endOfNightGraceUsed) break;
                        endOfNightGraceUsed = true;
                        global::NINA.Core.Utility.Logger.Info(
                            $"AstroPM | End-of-night grace: scheduling final {es.FilterName} {es.ExposureLengthSec:F0}s sub exceeding night end by {(currentUtc.AddSeconds(es.ExposureLengthSec) - nightEnd).TotalSeconds:F0}s");
                    }

                    string filterName = es.FilterName;
                    string panelLabel = pick.PanelLabel;
                    bool isMultiPanel = target.Panels.Count > 1;

                    var pnl = pick.PanelIdx >= 0
                        ? target.Panels.FirstOrDefault(p => p.PanelIndex == pick.PanelIdx) : null;
                    double panelRa = pnl?.RaHours ?? target.RaHours;
                    double panelDec = pnl?.DecDegrees ?? target.DecDegrees;

                    if (isMultiPanel && currentPanel != null && panelLabel != currentPanel) {
                        log.Add(new SimLogEntry {
                            Command = "Slew",
                            Time = TimeZoneInfo.ConvertTimeFromUtc(currentUtc, tz).ToString("HH:mm"),
                            Target = $"{prof.DisplayName} → {panelLabel}",
                            Rotation = $"{target.RotationDeg:F1}°",
                            RA = SessionScheduler.FormatRa(panelRa),
                            DEC = SessionScheduler.FormatDec(panelDec),
                            SlotIndex = currentSlotIdx,
                            UtcTime = currentUtc,
                        });
                        subsSinceDither = 0;
                    }
                    currentPanel = panelLabel;

                    if (filterName != currentFilter) {
                        log.Add(new SimLogEntry {
                            Command = "Filter",
                            Time = TimeZoneInfo.ConvertTimeFromUtc(currentUtc, tz).ToString("HH:mm"),
                            Target = prof.DisplayName,
                            Filter = filterName,
                            SlotIndex = currentSlotIdx,
                            UtcTime = currentUtc,
                        });
                        currentFilter = filterName;
                        subsSinceDither = 0;
                    }

                    bool isBonus = state.RemainingForEs(targetIdx, pick.PanelIdx, pick.EsIdx, es) <= 0;
                    string command = isBonus ? "Bonus" : "Image";

                    int subNum = state.NextSub(prof);
                    state.WheelFilter = es.FilterName;
                    state.LastShotSeq[(prof, es.FilterName)] = ++state.ShotCounter;
                    string bin = es.BinningX == es.BinningY ? $"{es.BinningX}" : $"{es.BinningX}×{es.BinningY}";

                    double alt = currentSlotIdx >= 0 && currentSlotIdx < prof.AltitudePerSlot.Length
                        ? prof.AltitudePerSlot[currentSlotIdx] : 0;
                    double moonSep = currentSlotIdx >= 0 && currentSlotIdx < prof.MoonSepPerSlot.Length
                        ? prof.MoonSepPerSlot[currentSlotIdx] : 0;
                    double esMoonSep = currentSlotIdx >= 0 && currentSlotIdx < prof.MoonSepPerSlot.Length
                        ? prof.MoonSepPerSlot[currentSlotIdx] : 0;
                    var esSlot = currentSlotIdx >= 0 && currentSlotIdx < matrix.Slots.Count
                        ? matrix.Slots[currentSlotIdx] : null;
                    bool isMoonSafe = esSlot != null
                        && (ExoMoonExempt(prof) || SessionScheduler.IsExposureSetMoonSafe(es, esSlot, esMoonSep, prof.Constraints));
                    bool isDarkSafe = currentSlotIdx >= 0 && currentSlotIdx < matrix.Slots.Count
                        && matrix.Slots[currentSlotIdx].SunAltDeg < prof.Constraints.SunAltitudeThreshold;
                    double reqSep = 0;
                    if (esSlot != null) {
                        ObservingConstraints reqC = es.MoonAvoidanceProfile != null
                            ? new ObservingConstraints {
                                MoonAvoidanceEnabled = true,
                                MinMoonSeparationDeg = es.MoonAvoidanceProfile.MoonSeparationDeg,
                                MoonAvoidanceWidthDays = es.MoonAvoidanceProfile.MoonAvoidanceWidthDays,
                                MoonRelaxScale = es.MoonAvoidanceProfile.MoonRelaxScale,
                                MinMoonAltitude = es.MoonAvoidanceProfile.MoonMinAltitude,
                                MaxMoonAltitude = es.MoonAvoidanceProfile.MoonMaxAltitude,
                                MaxMoonIlluminationPct = es.MoonAvoidanceProfile.MaxMoonIlluminationPct,
                            }
                            : prof.Constraints;
                        reqSep = AstroCalculator.RequiredMoonSeparation(esSlot.UtcStart, esSlot.MoonAltDeg, reqC);
                    }
                    bool isLaFilter = es.HasMoonAvoidance;
                    bool isLaSafe = !isLaFilter || isMoonSafe;

                    string filterProfile = "";
                    string acceptedProfiles = "";
                    if (esSlot != null) {
                        filterProfile = SessionScheduler.GetFilterProfileName(es);
                        acceptedProfiles = SessionScheduler.GetAcceptedProfiles(es, esSlot, esMoonSep, target);
                    }

                    log.Add(new SimLogEntry {
                        Command = command,
                        Time = TimeZoneInfo.ConvertTimeFromUtc(currentUtc, tz).ToString("HH:mm"),
                        Target = prof.DisplayName,
                        Panel = panelLabel,
                        SubNum = $"#{subNum}",
                        Filter = filterName,
                        Exposure = $"{es.ExposureLengthSec:F0}s",
                        Gain = $"{es.Gain}",
                        Offset = $"{es.Offset}",
                        Bin = bin,
                        ReadoutMode = es.ReadoutMode ?? "",
                        Rotation = $"{target.RotationDeg:F1}°",
                        RA = SessionScheduler.FormatRa(panelRa),
                        DEC = SessionScheduler.FormatDec(panelDec),
                        Sort1 = sortRanks.TryGetValue(rowIdx, out var sr) ? sr[0] : "",
                        Sort2 = sr != null ? sr[1] : "",
                        Sort3 = sr != null ? sr[2] : "",
                        Sort4 = sr != null ? sr[3] : "",
                        Altitude = $"{alt:F0}°",
                        MoonSep = $"{moonSep:F0}°",
                        MoonSafe = isMoonSafe ? "Yes" : "No",
                        MoonAvoidSep = reqSep > 0 ? $"{reqSep:F0}°" : "—",
                        DarkSafe = isDarkSafe ? "Yes" : "No",
                        LaEnabled = isLaFilter ? "Yes" : "—",
                        LaSafe = isLaFilter ? (isLaSafe ? "Yes" : "No") : "—",
                        FilterProfile = filterProfile,
                        AcceptedProfiles = acceptedProfiles,
                        SlotIndex = currentSlotIdx,
                        UtcTime = currentUtc,
                    });

                    currentUtc = currentUtc.AddSeconds(es.ExposureLengthSec);
                    state.RecordExposure(prof, targetIdx, pick.PanelIdx, pick.EsIdx, es);
                    prof.AllocatedSec = state.AllocatedSec.ContainsKey(prof) ? state.AllocatedSec[prof] : 0;
                    lastEs = es;
                    lastPanelLabel = panelLabel;
                    lastPanelIdx = pick.PanelIdx;
                    lastEsIdx = pick.EsIdx;

                    if (isMultiPanel && !string.IsNullOrEmpty(panelLabel)) {
                        var ptKey = (prof, panelLabel);
                        state.PanelTimeOnTarget[ptKey] =
                            (state.PanelTimeOnTarget.ContainsKey(ptKey) ? state.PanelTimeOnTarget[ptKey] : 0)
                            + es.ExposureLengthSec;
                    }

                    subsSinceDither++;

                    // Exoplanet transit runs never dither: a dither punches a gap in the
                    // photometric time series and moves the star off its pixels.
                    if (ditherEnabled && subsSinceDither >= ditherEvery
                        && !prof.FixedWindowStartUtc.HasValue) {
                        var dSlot = SessionScheduler.GetSlotIndex(currentUtc, matrix.Slots);
                        log.Add(new SimLogEntry {
                            Command = "Dither",
                            Time = TimeZoneInfo.ConvertTimeFromUtc(currentUtc, tz).ToString("HH:mm"),
                            Target = prof.DisplayName,
                            SlotIndex = dSlot,
                            UtcTime = currentUtc,
                        });
                        subsSinceDither = 0;
                    }
                }
            }

            if (!emittedDarkStart && matrix.FirstDarkSlot >= 0) {
                var darkUtc = matrix.Slots[matrix.FirstDarkSlot].UtcStart;
                var darkTime = TimeZoneInfo.ConvertTimeFromUtc(darkUtc, tz);
                log.Add(new SimLogEntry {
                    Command = "Info", Time = darkTime.ToString("HH:mm"),
                    Target = "Astronomical darkness begins",
                    SlotIndex = matrix.FirstDarkSlot, UtcTime = darkUtc,
                });
            }
            if (!emittedDarkEnd && matrix.LastDarkSlot >= 0) {
                var dawnUtc = matrix.Slots[matrix.LastDarkSlot].UtcStart.AddMinutes(5);
                var dawnTime = TimeZoneInfo.ConvertTimeFromUtc(dawnUtc, tz);
                log.Add(new SimLogEntry {
                    Command = "Info", Time = dawnTime.ToString("HH:mm"),
                    Target = "Astronomical darkness ends",
                    SlotIndex = matrix.LastDarkSlot, UtcTime = dawnUtc,
                });
            }

            var endUtc = currentUtc < nightEnd ? currentUtc : nightEnd;
            var endLocal = TimeZoneInfo.ConvertTimeFromUtc(endUtc, tz);
            double totalMin = (endUtc - nightStart).TotalMinutes;
            int totalSubs = log.Count(e => e.Command == "Image" || e.Command == "Bonus");
            int targetCount = log.Where(e => e.Command == "Slew" && !e.Target.Contains("→"))
                .Select(e => e.Target).Distinct().Count();
            log.Add(new SimLogEntry {
                Command = "End",
                Time = endLocal.ToString("HH:mm"),
                Target = $"Session end — {endLocal:ddd MMM d, yyyy} — {totalMin:F0} min, {totalSubs} subs, {targetCount} targets",
                SlotIndex = matrix.LastUsableSlot,
                UtcTime = endUtc,
            });

            return log;
        }

        // ─── Validate ────────────────────────────────────────────────────────

        public static List<ScheduleWarning> Validate(ScheduleMatrix matrix, List<SimLogEntry> log) {
            var warnings = new List<ScheduleWarning>();

            int gapStart = -1;
            for (int s = matrix.FirstUsableSlot; s <= matrix.LastUsableSlot; s++) {
                if (matrix.SlotAssignment[s] < 0) {
                    bool anyCanImage = false;
                    for (int r = 0; r < matrix.Rows.Count; r++) {
                        if (!matrix.CanImage[r][s]) continue;
                        var row = matrix.Rows[r];
                        if (row.PreFiltered) continue;
                        bool hasAccessible = matrix.MoonDown[s] || matrix.MoonSafe[r][s]
                            ? row.Profile.RemainingTotalSec > 0
                            : row.Profile.RemainingNonLunarSec > 0;
                        if (hasAccessible) { anyCanImage = true; break; }
                    }
                    if (anyCanImage && gapStart < 0) gapStart = s;
                } else {
                    if (gapStart >= 0) {
                        double gapMin = (s - gapStart) * 5;
                        if (gapMin >= 10)
                            warnings.Add(new ScheduleWarning {
                                Severity = "error",
                                Message = $"IDLE-GAP: {gapMin:F0}min gap at slot {gapStart} with imageable targets",
                                UtcTime = matrix.Slots[gapStart].UtcStart,
                            });
                        gapStart = -1;
                    }
                }
            }

            foreach (var entry in log.Where(e => e.Command == "Image" || e.Command == "Bonus")) {
                if (entry.SlotIndex < 0 || entry.SlotIndex >= matrix.Slots.Count) continue;
                if (matrix.MoonDown[entry.SlotIndex]) continue;

                var matchRow = matrix.Rows.FirstOrDefault(r => r.Profile.DisplayName == entry.Target);
                if (matchRow == null) continue;

                bool isMoonSafe = matrix.MoonSafe[matchRow.RowIndex][entry.SlotIndex];
                if (isMoonSafe) continue;

                var laEs = matchRow.Profile.Target.Panels.SelectMany(p => p.ExposureSets)
                    .FirstOrDefault(es => es.HasMoonAvoidance && es.FilterName == entry.Filter);
                if (laEs != null) {
                    double valMoonSep = entry.SlotIndex < matchRow.Profile.MoonSepPerSlot.Length
                        ? matchRow.Profile.MoonSepPerSlot[entry.SlotIndex] : 0;
                    var valSlot = matrix.Slots[entry.SlotIndex];
                    bool esSafe = ExoMoonExempt(matchRow.Profile) || SessionScheduler.IsExposureSetMoonSafe(laEs, valSlot, valMoonSep, matchRow.Profile.Constraints);
                    if (!esSafe)
                        warnings.Add(new ScheduleWarning {
                            Severity = "error",
                            Message = $"LA-UNSAFE: {entry.Target} using LA filter {entry.Filter} at {entry.Time} (moon up, not safe)",
                            UtcTime = entry.UtcTime,
                        });
                }
            }

            var targetSubs = log.Where(e => e.Command == "Image" || e.Command == "Bonus")
                .GroupBy(e => e.Target)
                .ToDictionary(g => g.Key, g => g.Sum(e => {
                    double.TryParse(e.Exposure.TrimEnd('s'), out var sec);
                    return sec;
                }));

            foreach (var row in matrix.Rows) {
                double got = targetSubs.GetValueOrDefault(row.Profile.DisplayName);
                int assignedSlots = 0;
                for (int s2 = 0; s2 < matrix.SlotAssignment.Length; s2++)
                    if (matrix.SlotAssignment[s2] == row.RowIndex) assignedSlots++;
                double allocatedSec = assignedSlots * 300.0;
                double minSec = row.MinChunkSec;

                double originalWork = row.Profile.RemainingLunarFreeSec + row.Profile.RemainingNonLunarSec;
                if (got > 0 && allocatedSec < row.MinFloorSec && originalWork >= minSec)
                    warnings.Add(new ScheduleWarning {
                        Severity = "warn",
                        Message = $"TOTAL-MIN: {row.Profile.DisplayName} got {allocatedSec / 60:F0}min allocated but minimum is {minSec / 60:F0}min (floor {row.MinFloorSec / 60:F0}min)",
                    });

                if (got <= 0 && originalWork > 0 && row.TotalUsableSlots > 0)
                    warnings.Add(new ScheduleWarning {
                        Severity = "warn",
                        Message = $"NO-ALLOC: {row.Profile.DisplayName} has {originalWork / 60:F0}min work, {row.TotalUsableSlots * 5}min usable, got 0 subs",
                    });

                if (row.Profile.RemainingLunarFreeSec > 600 && row.MoonDownSlots > 0) {
                    bool gotMoonDown = log.Any(e =>
                        (e.Command == "Image" || e.Command == "Bonus")
                        && e.Target == row.Profile.DisplayName
                        && e.SlotIndex >= 0 && e.SlotIndex < matrix.MoonDown.Length
                        && matrix.MoonDown[e.SlotIndex]);
                    if (!gotMoonDown)
                        warnings.Add(new ScheduleWarning {
                            Severity = "warn",
                            Message = $"LA-MISS: {row.Profile.DisplayName} has {row.Profile.RemainingLunarFreeSec / 60:F0}min LA + {row.MoonDownSlots * 5}min MD but no MD alloc",
                        });
                }
            }

            // FILTER-STUCK is exempt for exoplanet transit targets: a single-filter
            // time series of hundreds of consecutive subs is exactly what they do.
            var exoTargets = new HashSet<string>(matrix.Rows
                .Where(r => r.Profile.FixedWindowStartUtc.HasValue)
                .Select(r => r.Profile.DisplayName));
            string lastFilter = null;
            string lastTarget = null;
            int consecutive = 0;
            foreach (var entry in log.Where(e => e.Command == "Image" || e.Command == "Bonus")) {
                if (exoTargets.Contains(entry.Target)) continue;
                if (entry.Target == lastTarget && entry.Filter == lastFilter) {
                    consecutive++;
                    if (consecutive == 31)
                        warnings.Add(new ScheduleWarning {
                            Severity = "warn",
                            Message = $"FILTER-STUCK: {entry.Target} on {entry.Filter} for 30+ consecutive subs at {entry.Time}",
                            UtcTime = entry.UtcTime,
                        });
                } else {
                    lastFilter = entry.Filter;
                    lastTarget = entry.Target;
                    consecutive = 1;
                }
            }

            return warnings;
        }

        // ─── DumpMatrix ──────────────────────────────────────────────────────

        public static string DumpMatrix(ScheduleMatrix matrix, TimeZoneInfo tz) {
            var sb = new System.Text.StringBuilder();
            int slotCount = matrix.Slots.Count;
            if (slotCount == 0 || matrix.FirstUsableSlot < 0) return "Empty matrix";

            int start = matrix.FirstUsableSlot;
            int end = Math.Min(matrix.LastUsableSlot, slotCount - 1);

            sb.Append("Time:   ");
            for (int s = start; s <= end; s++) {
                if ((s - start) % 6 == 0) {
                    var t = TimeZoneInfo.ConvertTimeFromUtc(matrix.Slots[s].UtcStart, tz);
                    sb.Append($"{t:HH:mm} ");
                }
            }
            sb.AppendLine();

            sb.Append("Moon:   ");
            for (int s = start; s <= end; s++) {
                if ((s - start) % 6 == 0)
                    sb.Append(matrix.MoonDown[s] ? "  MD  " : "  MU  ");
            }
            sb.AppendLine();
            sb.AppendLine(new string('-', 8 + ((end - start) / 6 + 1) * 6));

            foreach (var row in matrix.Rows) {
                int ri = row.RowIndex;
                string name = row.Profile.DisplayName;
                if (name.Length > 12) name = name.Substring(0, 12);
                sb.Append($"{name,-12} ");

                for (int s = start; s <= end; s++) {
                    if ((s - start) % 6 != 0) continue;
                    if (!matrix.CanImage[ri][s])
                        sb.Append("  ..  ");
                    else if (matrix.MoonSafe[ri][s])
                        sb.Append("  ##  ");
                    else
                        sb.Append("  %%  ");
                }
                sb.AppendLine();
            }

            sb.AppendLine(new string('-', 8 + ((end - start) / 6 + 1) * 6));

            sb.Append("Assigned:    ");
            for (int s = start; s <= end; s++) {
                if ((s - start) % 6 != 0) continue;
                int a = matrix.SlotAssignment[s];
                if (a < 0)
                    sb.Append("  --  ");
                else {
                    string tgt = matrix.Rows[a].Profile.DisplayName;
                    if (tgt.Length > 4) tgt = tgt.Substring(0, 4);
                    sb.Append($"  {tgt,-4}");
                }
            }
            sb.AppendLine();

            sb.AppendLine();
            int totalAssigned = 0, totalUnassigned = 0;
            for (int s = start; s <= end; s++) {
                if (matrix.SlotAssignment[s] >= 0) totalAssigned++;
                else totalUnassigned++;
            }
            sb.AppendLine($"Slots: {totalAssigned} assigned, {totalUnassigned} unassigned, {end - start + 1} total ({(end - start + 1) * 5}min night)");

            foreach (var row in matrix.Rows) {
                int assigned = 0;
                for (int s = start; s <= end; s++)
                    if (matrix.SlotAssignment[s] == row.RowIndex) assigned++;
                sb.AppendLine($"  {row.Profile.DisplayName}: {assigned * 5}min assigned, LA={row.Profile.RemainingLunarFreeSec / 60:F0}m NonLA={row.Profile.RemainingNonLunarSec / 60:F0}m MinTime={row.MinChunkSec / 60:F0}m");
            }

            return sb.ToString();
        }

        public static string DumpDiagnostic(ScheduleMatrix matrix, List<SortCriteria> sortChain, TimeZoneInfo tz) {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== SCHEDULE ENGINE DIAGNOSTIC ===");
            sb.AppendLine($"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Slots: {matrix.Slots.Count}, FirstUsable: {matrix.FirstUsableSlot}, LastUsable: {matrix.LastUsableSlot}");
            sb.AppendLine($"Sort chain: {string.Join(", ", sortChain)}");
            sb.AppendLine();

            int start = Math.Max(0, matrix.FirstUsableSlot);
            int end = Math.Min(matrix.LastUsableSlot, matrix.Slots.Count - 1);
            int moonDownCount = 0, moonUpCount = 0;
            for (int s = start; s <= end; s++) {
                if (matrix.MoonDown[s]) moonDownCount++; else moonUpCount++;
            }
            sb.AppendLine($"Moon-down slots: {moonDownCount} ({moonDownCount * 5}min), Moon-up slots: {moonUpCount} ({moonUpCount * 5}min)");
            if (matrix.Slots.Count > 0) {
                var mid = matrix.Slots[matrix.Slots.Count / 2];
                sb.AppendLine($"Mid-night: MoonAlt={mid.MoonAltDeg:F1}°, MoonIllum={mid.MoonIllumPct:F1}%, SunAlt={mid.SunAltDeg:F1}°");
            }
            sb.AppendLine();

            sb.AppendLine("=== TARGET ROWS (pre-paint state) ===");
            foreach (var row in matrix.Rows) {
                int ri = row.RowIndex;
                int canImageCount = 0, moonSafeCount = 0, moonDownCanImage = 0;
                for (int s = start; s <= end; s++) {
                    if (matrix.CanImage[ri][s]) {
                        canImageCount++;
                        if (matrix.MoonSafe[ri][s]) moonSafeCount++;
                        if (matrix.MoonDown[s]) moonDownCanImage++;
                    }
                }

                int safeSlots = 0, unsafeSlots = 0;
                for (int s = 0; s < matrix.Slots.Count; s++) {
                    if (!matrix.CanImage[ri][s]) continue;
                    if (matrix.MoonDown[s] || matrix.MoonSafe[ri][s]) safeSlots++;
                    else unsafeSlots++;
                }
                double laInSafe = Math.Min(row.RemainingLaSec, safeSlots * 300.0);
                double safeRemaining = Math.Max(0, safeSlots * 300.0 - laInSafe);
                double nonLaInSafe = Math.Min(row.RemainingNonLaSec, safeRemaining);
                double nonLaLeft = row.RemainingNonLaSec - nonLaInSafe;
                double accessibleSec = laInSafe + nonLaInSafe + Math.Min(nonLaLeft, unsafeSlots * 300.0);

                sb.AppendLine($"  [{ri}] {row.Profile.DisplayName}");
                sb.AppendLine($"      PeakAlt={row.PeakAltitude:F1}° Window=slot {row.FirstUsableSlot}-{row.LastUsableSlot} ({row.TotalUsableSlots} slots = {row.TotalUsableSlots * 5}min)");
                sb.AppendLine($"      LA={row.RemainingLaSec / 60:F1}min NonLA={row.RemainingNonLaSec / 60:F1}min Total={row.TotalWorkSec / 60:F1}min");
                sb.AppendLine($"      MinChunk={row.MinChunkSec / 60:F1}min ({row.MinChunkSlots} slots) Constrained={row.IsConstrained} PreFiltered={row.PreFiltered}");
                sb.AppendLine($"      CanImage={canImageCount} MoonSafe={moonSafeCount} MoonDownCanImage={moonDownCanImage}");
                sb.AppendLine($"      SafeSlots={safeSlots} UnsafeSlots={unsafeSlots} AccessibleSec={accessibleSec / 60:F1}min");
            }
            sb.AppendLine();

            sb.AppendLine("=== SORT ORDER ===");
            var sorted = ApplySortChain(matrix.Rows.Where(r => !r.PreFiltered && r.TotalWorkSec > 0).ToList(), sortChain);
            for (int i = 0; i < sorted.Count; i++) {
                sb.AppendLine($"  {i + 1}. {sorted[i].Profile.DisplayName} (peak={sorted[i].PeakAltitude:F1}° last={sorted[i].LastUsableSlot} work={sorted[i].TotalWorkSec / 60:F0}min constrained={sorted[i].IsConstrained})");
            }
            sb.AppendLine();

            sb.AppendLine("=== SLOT ASSIGNMENTS (post-paint) ===");
            foreach (var row in matrix.Rows) {
                int assigned = 0;
                int firstA = -1, lastA = -1;
                for (int s = start; s <= end; s++) {
                    if (matrix.SlotAssignment[s] == row.RowIndex) {
                        assigned++;
                        if (firstA < 0) firstA = s;
                        lastA = s;
                    }
                }
                string timeRange = assigned > 0
                    ? $"{TimeZoneInfo.ConvertTimeFromUtc(matrix.Slots[firstA].UtcStart, tz):HH:mm}-{TimeZoneInfo.ConvertTimeFromUtc(matrix.Slots[lastA].UtcStart.AddSeconds(300), tz):HH:mm}"
                    : "none";
                sb.AppendLine($"  {row.Profile.DisplayName}: {assigned} slots ({assigned * 5}min) [{timeRange}]");
            }

            sb.AppendLine();
            sb.Append(PaintTrace.ToString());

            return sb.ToString();
        }

        // ─── Helpers ─────────────────────────────────────────────────────────

        private static void EmitDarkness(List<SimLogEntry> log, ScheduleMatrix matrix,
            TimeZoneInfo tz, int slot, ref bool emittedDarkStart, ref bool emittedDarkEnd) {
            if (!emittedDarkStart && matrix.FirstDarkSlot >= 0 && slot >= matrix.FirstDarkSlot) {
                emittedDarkStart = true;
                var darkUtc = matrix.Slots[matrix.FirstDarkSlot].UtcStart;
                var darkTime = TimeZoneInfo.ConvertTimeFromUtc(darkUtc, tz);
                log.Add(new SimLogEntry {
                    Command = "Info", Time = darkTime.ToString("HH:mm"),
                    Target = "Astronomical darkness begins",
                    SlotIndex = matrix.FirstDarkSlot, UtcTime = darkUtc,
                });
            }
            if (!emittedDarkEnd && matrix.LastDarkSlot >= 0 && slot > matrix.LastDarkSlot) {
                emittedDarkEnd = true;
                var dawnUtc = matrix.Slots[matrix.LastDarkSlot].UtcStart.AddMinutes(5);
                var dawnTime = TimeZoneInfo.ConvertTimeFromUtc(dawnUtc, tz);
                log.Add(new SimLogEntry {
                    Command = "Info", Time = dawnTime.ToString("HH:mm"),
                    Target = "Astronomical darkness ends",
                    SlotIndex = matrix.LastDarkSlot, UtcTime = dawnUtc,
                });
            }
        }
    }
}
