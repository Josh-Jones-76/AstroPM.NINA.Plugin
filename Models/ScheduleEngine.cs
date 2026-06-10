using System;
using System.Collections.Generic;
using System.Linq;

namespace AstroPM.NINA.Plugin.Models {

    internal enum SlotWorkType { Any, LaPreferred, NonLaPreferred }

    public enum ImagingStrategy { SharedTime, ManualPriority }

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

        public double TotalWorkSec => TierWorkSec.Length > 0 ? TierWorkSec.Sum() : 0;
        public bool HasLaWork => TierWorkSec.Length > 1 && TierWorkSec.Skip(1).Any(x => x > 0);
        public bool HasNonLaWork => TierWorkSec.Length > 0 && TierWorkSec[0] > 0;
        public bool IsConstrained { get; set; }

        public double PeakAltitude { get; set; }
        public int UserPriorityIndex { get; set; }
        public bool PreFiltered { get; set; }

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
            List<int> priorityOrder) {

            int slotCount = slots.Count;
            int rowCount = profiles.Count;

            var matrix = new ScheduleMatrix {
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

        // ─── PaintSlotsGreedy (ManualPriority strategy) ──────────────────────

        public static void PaintSlotsGreedy(
            ScheduleMatrix matrix,
            List<int> priorityOrder,
            bool bonusEnabled) {

            PaintTrace.Clear();
            PaintTrace.AppendLine("=== PAINT TRACE (Greedy / ManualPriority) ===");

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
                if (accessibleSec < row.MinChunkSec) {
                    for (int t = 0; t < row.TierWorkSec.Length; t++)
                        row.TierWorkSec[t] = 0;
                    row.PreFiltered = true;
                }
            }

            // Pass 0: Reserve exclusive windows (only one target can image)
            for (int s = 0; s < matrix.Slots.Count; s++) {
                if (matrix.SlotOverlap[s] == null || matrix.SlotOverlap[s].Contention != 1) continue;
                int ri = matrix.SlotOverlap[s].CandidateRows[0];
                var row = matrix.Rows[ri];
                if (row.PreFiltered || row.TotalWorkSec <= 0) continue;

                matrix.SlotAssignment[s] = ri;
                int tier = matrix.MoonDown[s]
                    ? row.MostRestrictiveTierWithWork()
                    : row.LeastRestrictiveSafeTierWithWork(matrix.TierSafe[ri], s);
                if (tier < 0) tier = 0;
                matrix.SlotAllocTier[s] = tier;
                matrix.SlotWorkHint[s] = tier == 0 ? SlotWorkType.NonLaPreferred
                    : matrix.MoonDown[s] ? SlotWorkType.LaPreferred : SlotWorkType.Any;
                DecrementWork(row, 300.0, matrix.SlotWorkHint[s]);
            }
            TraceSnapshot(matrix, "After Pass 0 (exclusive)");

            // Greedy allocation: walk priority order, give each target as much as it can use
            foreach (int orderIdx in priorityOrder) {
                if (orderIdx < 0 || orderIdx >= matrix.Rows.Count) continue;
                var row = matrix.Rows[orderIdx];
                if (row.PreFiltered || row.TotalWorkSec <= 0) continue;
                int ri = row.RowIndex;

                // Phase A: Moon-down slots — most restrictive tiers first
                for (int t = row.TierWorkSec.Length - 1; t >= 0; t--) {
                    if (row.TierWorkSec[t] <= 0) continue;
                    double budget = row.TierWorkSec[t];

                    for (int s = 0; s < matrix.Slots.Count && budget > 0; s++) {
                        if (!matrix.MoonDown[s]) continue;
                        if (matrix.SlotAssignment[s] >= 0) continue;
                        if (!matrix.CanImage[ri][s]) continue;

                        matrix.SlotAssignment[s] = ri;
                        matrix.SlotAllocTier[s] = t;
                        var hint = t > 0 ? SlotWorkType.LaPreferred : SlotWorkType.Any;
                        matrix.SlotWorkHint[s] = hint;
                        DecrementWork(row, 300.0, hint);
                        budget -= 300.0;
                    }
                }

                // Phase B: Moon-up slots — use tier info for safe allocation
                for (int s = 0; s < matrix.Slots.Count; s++) {
                    if (matrix.MoonDown[s]) continue;
                    if (matrix.SlotAssignment[s] >= 0) continue;
                    if (!matrix.CanImage[ri][s]) continue;
                    if (row.TotalWorkSec <= 0) break;

                    int bestTier = -1;
                    for (int t = 1; t < row.TierWorkSec.Length; t++) {
                        if (row.TierWorkSec[t] <= 0) continue;
                        if (t < matrix.TierSafe[ri].Length && matrix.TierSafe[ri][t][s]) { bestTier = t; break; }
                    }
                    if (bestTier < 0 && row.TierWorkSec.Length > 0 && row.TierWorkSec[0] > 0)
                        bestTier = 0;
                    if (bestTier < 0) continue;

                    matrix.SlotAssignment[s] = ri;
                    matrix.SlotAllocTier[s] = bestTier;
                    var hint2 = bestTier > 0 ? SlotWorkType.NonLaPreferred : SlotWorkType.Any;
                    matrix.SlotWorkHint[s] = hint2;
                    DecrementWork(row, 300.0, hint2);
                }
            }
            TraceSnapshot(matrix, "After greedy allocation");

            EnforceMinimumAllocations(matrix);
            PruneSlivers(matrix);

            // Bonus / gap fill
            if (bonusEnabled) {
                for (int s = 1; s < matrix.Slots.Count; s++) {
                    if (matrix.SlotAssignment[s] < 0 && matrix.SlotAssignment[s - 1] >= 0) {
                        int ri = matrix.SlotAssignment[s - 1];
                        if (matrix.CanImage[ri][s]) {
                            matrix.SlotAssignment[s] = ri;
                            matrix.SlotWorkHint[s] = matrix.SlotWorkHint[s - 1];
                        }
                    }
                }
                for (int s = matrix.Slots.Count - 2; s >= 0; s--) {
                    if (matrix.SlotAssignment[s] < 0 && matrix.SlotAssignment[s + 1] >= 0) {
                        int ri = matrix.SlotAssignment[s + 1];
                        if (matrix.CanImage[ri][s]) {
                            matrix.SlotAssignment[s] = ri;
                            matrix.SlotWorkHint[s] = matrix.SlotWorkHint[s + 1];
                        }
                    }
                }
            }

            DefragmentSlots(matrix);
            PruneSlivers(matrix);

            // Absorb pruned slots into adjacent targets
            for (int s = 1; s < matrix.Slots.Count; s++) {
                if (matrix.SlotAssignment[s] < 0 && matrix.SlotAssignment[s - 1] >= 0) {
                    int ri = matrix.SlotAssignment[s - 1];
                    if (matrix.CanImage[ri][s]) {
                        matrix.SlotAssignment[s] = ri;
                        matrix.SlotWorkHint[s] = matrix.SlotWorkHint[s - 1];
                    }
                }
            }
            for (int s = matrix.Slots.Count - 2; s >= 0; s--) {
                if (matrix.SlotAssignment[s] < 0 && matrix.SlotAssignment[s + 1] >= 0) {
                    int ri = matrix.SlotAssignment[s + 1];
                    if (matrix.CanImage[ri][s]) {
                        matrix.SlotAssignment[s] = ri;
                        matrix.SlotWorkHint[s] = matrix.SlotWorkHint[s + 1];
                    }
                }
            }
        }

        // ─── PaintSlots ──────────────────────────────────────────────────────

        public static void PaintSlots(
            ScheduleMatrix matrix,
            List<SortCriteria> sortChain,
            List<SortCriteria> moonDownSortChain,
            bool bonusEnabled) {

            PaintTrace.Clear();
            PaintTrace.AppendLine("=== PAINT TRACE ===");
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
                if (accessibleSec < row.MinChunkSec) {
                    row.RemainingLaSec = 0;
                    row.RemainingNonLaSec = 0;
                    row.PreFiltered = true;
                }
            }

            TraceSnapshot(matrix, "Pre-filter");

            // Pass 0: Exclusive-window pre-allocation
            {
                var activeRows = matrix.Rows.Where(r => !r.PreFiltered && r.TotalWorkSec > 0).ToList();
                for (int s = 0; s < matrix.Slots.Count; s++) {
                    int soleCandidate = -1;
                    bool multiple = false;
                    foreach (var row in activeRows) {
                        if (!matrix.CanImage[row.RowIndex][s]) continue;
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

            // Pass 1: Moon-down LA priority
            {
                var candidates = matrix.Rows
                    .Where(r => !r.PreFiltered && r.HasLaWork && r.MoonDownSlots > 0)
                    .ToList();
                PaintTrace.AppendLine($"Pass 1 candidates ({candidates.Count}): {string.Join(", ", candidates.Select(c => $"{c.Profile.DisplayName} LA={c.RemainingLaSec/60:F0}m"))}");
                var sorted = ApplySortChain(candidates, moonDownSortChain);
                PaintPassFairShare(matrix, sorted,
                    (ri, s) => matrix.MoonDown[s] && matrix.CanImage[ri][s] && matrix.SlotAssignment[s] < 0,
                    SlotWorkType.LaPreferred,
                    r => r.RemainingLaSec + r.RemainingNonLaSec);
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

                PaintPassFairShare(matrix, sorted,
                    (ri, s) => !matrix.MoonDown[s] && matrix.Rows[ri].UsableSlot[s] && matrix.SlotAssignment[s] < 0,
                    SlotWorkType.NonLaPreferred,
                    r => accessibleWork.GetValueOrDefault(r.RowIndex));
            }
            TraceSnapshot(matrix, "After Pass 3 (moon-up)");

            EnforceMinimumAllocations(matrix);
            TraceSnapshot(matrix, "After EnforceMinimum");
            PruneSlivers(matrix);
            TraceSnapshot(matrix, "After PruneSlivers");

            // Pass 4: Bonus / gap fill
            if (bonusEnabled) {
                for (int s = 1; s < matrix.Slots.Count; s++) {
                    if (matrix.SlotAssignment[s] < 0 && matrix.SlotAssignment[s - 1] >= 0) {
                        int ri = matrix.SlotAssignment[s - 1];
                        if (matrix.CanImage[ri][s]) {
                            matrix.SlotAssignment[s] = ri;
                            matrix.SlotWorkHint[s] = matrix.SlotWorkHint[s - 1];
                        }
                    }
                }
                for (int s = matrix.Slots.Count - 2; s >= 0; s--) {
                    if (matrix.SlotAssignment[s] < 0 && matrix.SlotAssignment[s + 1] >= 0) {
                        int ri = matrix.SlotAssignment[s + 1];
                        if (matrix.CanImage[ri][s]) {
                            matrix.SlotAssignment[s] = ri;
                            matrix.SlotWorkHint[s] = matrix.SlotWorkHint[s + 1];
                        }
                    }
                }
            }

            // Defragment: merge A-B-A patterns into A-A-B to reduce target transitions
            DefragmentSlots(matrix);

            PruneSlivers(matrix);

            // Absorb pruned slots into adjacent targets
            for (int s = 1; s < matrix.Slots.Count; s++) {
                if (matrix.SlotAssignment[s] < 0 && matrix.SlotAssignment[s - 1] >= 0) {
                    int ri = matrix.SlotAssignment[s - 1];
                    if (matrix.CanImage[ri][s]) {
                        matrix.SlotAssignment[s] = ri;
                        matrix.SlotWorkHint[s] = matrix.SlotWorkHint[s - 1];
                    }
                }
            }
            for (int s = matrix.Slots.Count - 2; s >= 0; s--) {
                if (matrix.SlotAssignment[s] < 0 && matrix.SlotAssignment[s + 1] >= 0) {
                    int ri = matrix.SlotAssignment[s + 1];
                    if (matrix.CanImage[ri][s]) {
                        matrix.SlotAssignment[s] = ri;
                        matrix.SlotWorkHint[s] = matrix.SlotWorkHint[s + 1];
                    }
                }
            }
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

                    // Both targets must be able to actually produce work in their
                    // swapped slots (tier-aware: moon safety, not just altitude)
                    bool canSwap = true;
                    for (int s = bSlotStart; s <= bSlotEnd && canSwap; s++)
                        if (!matrix.Rows[targetA].UsableSlot[s]) canSwap = false;
                    for (int s = a2SlotStart; s <= a2SlotEnd && canSwap; s++)
                        if (!matrix.Rows[targetB].UsableSlot[s]) canSwap = false;

                    if (!canSwap) continue;

                    int regionStart = bSlotStart;
                    for (int s = regionStart; s < regionStart + a2Len; s++)
                        matrix.SlotAssignment[s] = targetA;
                    for (int s = regionStart + a2Len; s < regionStart + a2Len + bLen; s++)
                        matrix.SlotAssignment[s] = targetB;

                    changed = true;
                    break;
                }
            }
        }

        private static void PaintPassFairShare(
            ScheduleMatrix matrix,
            List<TargetRow> sorted,
            Func<int, int, bool> slotEligible,
            SlotWorkType workHint,
            Func<TargetRow, double> demandFunc) {

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
                    if (matrix.SlotAssignment[s] == sorted[i].RowIndex) existing++;
                effectiveMins[i] = existing >= sorted[i].MinChunkSlots
                    ? 0 : Math.Min(sorted[i].MinChunkSec, demands[i]);
            }

            PaintTrace.AppendLine($"  FairShare: supply={totalSupply/60:F0}m demand={totalDemand/60:F0}m eligibleSlots={uniqueEligible.Count}");
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
            PaintTrace.AppendLine($"  Branch: {branch}");
            for (int i = 0; i < sorted.Count; i++)
                PaintTrace.AppendLine($"    [{i}] {sorted[i].Profile.DisplayName}: budget={budgets[i]/60:F1}m");

            for (int i = 0; i < sorted.Count; i++) {
                if (budgets[i] <= 0) continue;
                var row = sorted[i];
                PaintChunks(matrix, row,
                    s => slotEligible(row.RowIndex, s),
                    workHint, budgets[i]);
            }
        }

        private static void EnforceMinimumAllocations(ScheduleMatrix matrix) {
            for (int r = 0; r < matrix.Rows.Count; r++) {
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

                for (int s = lastAssigned + 1; s < matrix.SlotAssignment.Length && extended < needed; s++) {
                    if (matrix.SlotAssignment[s] >= 0) break;
                    if (!matrix.CanImage[r][s]) break;
                    matrix.SlotAssignment[s] = r;
                    matrix.SlotWorkHint[s] = SlotWorkType.Any;
                    extended++;
                }

                for (int s = firstAssigned - 1; s >= 0 && extended < needed; s--) {
                    if (matrix.SlotAssignment[s] >= 0) break;
                    if (!matrix.CanImage[r][s]) break;
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
                        if (!matrix.CanImage[r][s]) return false;
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

                if (assigned + extended < matrix.Rows[r].MinChunkSlots) {
                    for (int s = 0; s < matrix.SlotAssignment.Length; s++)
                        if (matrix.SlotAssignment[s] == r)
                            matrix.SlotAssignment[s] = -1;
                }
            }
        }

        private static void PruneSlivers(ScheduleMatrix matrix) {
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
                    if (!adjacentToGoodRun) continue;

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
                    for (int s2 = 0; s2 < matrix.SlotAssignment.Length; s2++)
                        if (matrix.SlotAssignment[s2] == ri) alreadyPainted++;
                    if (alreadyPainted >= row.MinChunkSlots)
                        continue;
                }

                int slotsToAssign = Math.Min(run.Length, (int)Math.Ceiling(workBudget / 300.0));

                if (slotsToAssign < row.MinChunkSlots && run.Length >= row.MinChunkSlots
                    && maxWorkSec >= row.MinChunkSec)
                    slotsToAssign = row.MinChunkSlots;

                for (int i = 0; i < slotsToAssign && i < run.Length; i++) {
                    int s = run.Start + i;
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

        private static void DecrementWork(TargetRow row, double sec, SlotWorkType hint) {
            if (hint == SlotWorkType.LaPreferred) {
                double laUse = Math.Min(sec, row.RemainingLaSec);
                row.RemainingLaSec -= laUse;
                row.RemainingNonLaSec = Math.Max(0, row.RemainingNonLaSec - (sec - laUse));
            } else if (hint == SlotWorkType.NonLaPreferred) {
                double nonLaUse = Math.Min(sec, row.RemainingNonLaSec);
                row.RemainingNonLaSec -= nonLaUse;
                row.RemainingLaSec = Math.Max(0, row.RemainingLaSec - (sec - nonLaUse));
            } else {
                double total = row.TotalWorkSec;
                if (total > 0) {
                    double laFrac = row.RemainingLaSec / total;
                    row.RemainingLaSec = Math.Max(0, row.RemainingLaSec - sec * laFrac);
                    row.RemainingNonLaSec = Math.Max(0, row.RemainingNonLaSec - sec * (1 - laFrac));
                }
            }
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
            ExposureSetData lastEs = null;
            string lastPanelLabel = "";
            int lastPanelIdx = -1;
            int lastEsIdx = -1;

            int lastAssignedRow = -1;
            DateTime waitStart = DateTime.MinValue;

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
                    var probe = SessionScheduler.PickExposureSet(
                        prof, targetIdx, s, matrix.Slots, state,
                        filterSwitchEnabled, filterSwitchCount, probeAllowed,
                        filterSwitchTolerance: filterSwitchTolerance);
                    if (probe.Es == null) {
                        // Find where this target's contiguous run ends
                        int runEnd = s;
                        for (int fs = s + 1; fs <= matrix.LastUsableSlot; fs++) {
                            if (matrix.SlotAssignment[fs] != rowIdx) break;
                            runEnd = fs;
                        }

                        // Scan forward to find the first slot in this run where the target CAN image
                        int firstViableSlot = -1;
                        for (int fs = s + 1; fs <= runEnd; fs++) {
                            state.FilterCycle.Remove(prof);
                            var laterProbe = SessionScheduler.PickExposureSet(
                                prof, targetIdx, fs, matrix.Slots, state,
                                filterSwitchEnabled, filterSwitchCount,
                                prof.PanelIndex.HasValue ? new HashSet<int> { prof.PanelIndex.Value } : null,
                                filterSwitchTolerance: filterSwitchTolerance);
                            if (laterProbe.Es != null) { firstViableSlot = fs; break; }
                        }
                        state.FilterCycle.Remove(prof);

                        // Determine how many leading slots to reassign
                        int reassignEnd = firstViableSlot >= 0 ? firstViableSlot : runEnd + 1;

                        int fallbackRow = -1;
                        for (int r = 0; r < matrix.Rows.Count; r++) {
                            if (r == rowIdx) continue;
                            if (!matrix.CanImage[r][s]) continue;
                            var fp = matrix.Rows[r].Profile;
                            var fAllowed = fp.PanelIndex.HasValue
                                ? new HashSet<int> { fp.PanelIndex.Value } : (HashSet<int>)null;
                            var fPick = SessionScheduler.PickExposureSet(
                                fp, r, s, matrix.Slots, state,
                                filterSwitchEnabled, filterSwitchCount, fAllowed,
                                filterSwitchTolerance: filterSwitchTolerance);
                            if (fPick.Es != null) { fallbackRow = r; break; }
                        }
                        if (fallbackRow >= 0) {
                            // Only reassign the leading unviable slots, not the entire run
                            for (int fs = s; fs < reassignEnd; fs++)
                                matrix.SlotAssignment[fs] = fallbackRow;

                            rowIdx = fallbackRow;
                            row = matrix.Rows[rowIdx];
                            prof = row.Profile;
                            target = prof.Target;
                            targetIdx = row.RowIndex;
                        }
                    }

                    // Probe and fallback calls modify FilterCycle as a side effect.
                    // Reset it so the inner while starts with a clean cycle.
                    state.FilterCycle.Remove(prof);
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

                    // Reset filter cycle so each visit starts a fresh FilterSwitchCount batch
                    state.FilterCycle.Remove(prof);

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
                        filterSwitchEnabled, filterSwitchCount, allowedPanels,
                        filterSwitchTolerance: filterSwitchTolerance,
                        targetRemainingSec: targetRemainingSec);

                    if (pick.Es == null && bonusEnabled)
                    {
                        if (lastEs != null)
                        {
                            var gapSlot = currentSlotIdx >= 0 && currentSlotIdx < matrix.Slots.Count
                                ? matrix.Slots[currentSlotIdx] : null;
                            double gapMoonSep = currentSlotIdx >= 0 && currentSlotIdx < prof.MoonSepPerSlot.Length
                                ? prof.MoonSepPerSlot[currentSlotIdx] : 0;
                            bool gapMoonOk = !lastEs.HasMoonAvoidance || (gapSlot != null
                                && SessionScheduler.IsExposureSetMoonSafe(lastEs, gapSlot, gapMoonSep, prof.Constraints));
                            if (gapMoonOk)
                                pick = (lastEs, lastPanelLabel, lastPanelIdx, lastEsIdx);
                        }
                        if (pick.Es == null)
                            pick = SessionScheduler.PickExposureSet(
                                prof, targetIdx, currentSlotIdx, matrix.Slots, state,
                                filterSwitchEnabled, filterSwitchCount, allowedPanels, includeCompleted: true,
                                filterSwitchTolerance: filterSwitchTolerance,
                                targetRemainingSec: targetRemainingSec);
                    }
                    if (pick.Es == null) break;

                    var es = pick.Es;
                    if (currentUtc.AddSeconds(es.ExposureLengthSec) > nightEnd) break;

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
                        && SessionScheduler.IsExposureSetMoonSafe(es, esSlot, esMoonSep, prof.Constraints);
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

                    if (ditherEnabled && subsSinceDither >= ditherEvery) {
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
                    bool esSafe = SessionScheduler.IsExposureSetMoonSafe(laEs, valSlot, valMoonSep, matchRow.Profile.Constraints);
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
                if (got > 0 && allocatedSec < minSec && originalWork >= minSec)
                    warnings.Add(new ScheduleWarning {
                        Severity = "warn",
                        Message = $"TOTAL-MIN: {row.Profile.DisplayName} got {allocatedSec / 60:F0}min allocated but minimum is {minSec / 60:F0}min",
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

            string lastFilter = null;
            string lastTarget = null;
            int consecutive = 0;
            foreach (var entry in log.Where(e => e.Command == "Image" || e.Command == "Bonus")) {
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
