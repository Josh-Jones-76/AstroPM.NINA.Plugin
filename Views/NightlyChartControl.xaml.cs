using AstroPM.NINA.Plugin.Models;
using AstroPM.NINA.Plugin.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AstroPM.NINA.Plugin.Views {

    public partial class NightlyChartControl : UserControl {

        private List<TimeSlot> _slots;
        private List<TargetProfile> _profiles;
        private List<SimLogEntry> _log;

        private Rectangle _scrubberThumb;
        private TextBlock _scrubberTimeLabel;
        private bool _isDragging;

        private Line _nowLine;
        private Line _nowFilterLine;
        private TextBlock _nowLabel;
        private DispatcherTimer _nowTimer;
        private DateTime _lastNowUpdate;
        private int _lastLogIndex = -1;

        public static readonly DependencyProperty ScrubberEnabledProperty =
            DependencyProperty.Register("ScrubberEnabled", typeof(bool), typeof(NightlyChartControl), new PropertyMetadata(true));

        public bool ScrubberEnabled {
            get => (bool)GetValue(ScrubberEnabledProperty);
            set => SetValue(ScrubberEnabledProperty, value);
        }

        public event Action<DateTime> ScrubberMoved;
        public event Action<DateTime> NowCrossedInstruction;

        private static Color[] TargetCurveColors => SimulatorViewModel.TargetCurveColors;

        private static readonly Dictionary<string, Color> FilterColors = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase) {
            { "L", Color.FromRgb(0xDD, 0xDD, 0xDD) },
            { "Lum", Color.FromRgb(0xDD, 0xDD, 0xDD) },
            { "Luminance", Color.FromRgb(0xDD, 0xDD, 0xDD) },
            { "R", Color.FromRgb(0xE5, 0x42, 0x42) },
            { "Red", Color.FromRgb(0xE5, 0x42, 0x42) },
            { "G", Color.FromRgb(0x4C, 0xAF, 0x50) },
            { "Green", Color.FromRgb(0x4C, 0xAF, 0x50) },
            { "B", Color.FromRgb(0x42, 0x8B, 0xF5) },
            { "Blue", Color.FromRgb(0x42, 0x8B, 0xF5) },
            { "Ha", Color.FromRgb(0xCC, 0x22, 0x22) },
            { "H-alpha", Color.FromRgb(0xCC, 0x22, 0x22) },
            { "OIII", Color.FromRgb(0x00, 0x96, 0x88) },
            { "O-III", Color.FromRgb(0x00, 0x96, 0x88) },
            { "SII", Color.FromRgb(0x9B, 0x1B, 0x1B) },
            { "S-II", Color.FromRgb(0x9B, 0x1B, 0x1B) },
            { "NII", Color.FromRgb(0xAB, 0x47, 0xBC) },
        };

        public NightlyChartControl() {
            InitializeComponent();
        }

        private Func<int, Color> _colorResolver;

        public void SetData(List<TimeSlot> slots, List<TargetProfile> profiles, List<SimLogEntry> log, Func<int, Color> colorResolver = null) {
            _slots = slots;
            _profiles = profiles;
            _colorResolver = colorResolver;
            _log = log;
            TxtEmpty.Visibility = (slots != null && slots.Count > 0) ? Visibility.Collapsed : Visibility.Visible;
            DrawChart();
        }

        private void TimelineCanvas_SizeChanged(object sender, SizeChangedEventArgs e) {
            DrawChart();
        }

        private void DrawChart() {
            TimelineCanvas.Children.Clear();
            YAxisCanvas.Children.Clear();
            TimeAxisBottom.Children.Clear();
            FilterBarCanvas.Children.Clear();

            if (_slots == null || _slots.Count == 0 || _profiles == null) return;

            double w = TimelineCanvas.ActualWidth;
            double h = TimelineCanvas.ActualHeight;
            if (w < 10 || h < 10) return;

            var tz = TimeZoneInfo.Local;
            int slotCount = _slots.Count;
            var graphStart = TimeZoneInfo.ConvertTimeFromUtc(_slots[0].UtcStart, tz);
            var graphEnd = TimeZoneInfo.ConvertTimeFromUtc(_slots[slotCount - 1].UtcStart.AddMinutes(5), tz);
            double totalHours = (graphEnd - graphStart).TotalHours;

            DrawTwilightBackground(w, h, slotCount);
            DrawAltitudeGrid(w, h);
            DrawYAxis(h);
            DrawTwilightMarkers(tz, graphStart, totalHours, w, h);
            DrawTimeAxis(graphStart, graphEnd, totalHours, w, h);
            DrawMoonCurve(w, h);
            DrawScheduleBlocks(w, h, tz);
            DrawFilterBar(w, tz);
            DrawTargetAltitudeCurves(w, h);
            DrawNowLine(w, h, graphStart, totalHours, tz);
        }

        private void DrawTwilightBackground(double w, double h, int slotCount) {
            int segments = Math.Max(72, (int)(w / 2));
            double segW = w / segments;
            for (int i = 0; i < segments; i++) {
                double frac = (i + 0.5) / segments;
                int slotIdx = Math.Clamp((int)(frac * slotCount), 0, slotCount - 1);
                double sunAlt = _slots[slotIdx].SunAltDeg;

                Color color;
                if (sunAlt > 0)
                    color = LerpColor(sunAlt, 0, 12, FromHex("#3D5A80"), FromHex("#8B7535"));
                else if (sunAlt > -6)
                    color = LerpColor(sunAlt, -6, 0, FromHex("#1B2838"), FromHex("#3D5A80"));
                else if (sunAlt > -12)
                    color = LerpColor(sunAlt, -12, -6, FromHex("#0F1620"), FromHex("#1B2838"));
                else if (sunAlt > -18)
                    color = LerpColor(sunAlt, -18, -12, FromHex("#080C12"), FromHex("#0F1620"));
                else
                    color = FromHex("#080C12");

                var rect = new Rectangle { Width = segW + 1, Height = h, Fill = new SolidColorBrush(color) };
                Canvas.SetLeft(rect, i * segW);
                Canvas.SetTop(rect, 0);
                TimelineCanvas.Children.Add(rect);
            }
        }

        private void DrawAltitudeGrid(double w, double h) {
            var gridBrush = new SolidColorBrush(Color.FromArgb(60, 0x88, 0x88, 0x88));
            foreach (int altDeg in new[] { 30, 60 }) {
                double y = h - (altDeg / 90.0) * h;
                var line = new Line {
                    X1 = 0, X2 = w, Y1 = y, Y2 = y,
                    Stroke = gridBrush, StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 4, 4 },
                    IsHitTestVisible = false,
                };
                TimelineCanvas.Children.Add(line);
            }
        }

        private void DrawYAxis(double h) {
            double yAxisW = YAxisCanvas.ActualWidth > 5 ? YAxisCanvas.ActualWidth : 38;
            var labelBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            foreach (int altDeg in new[] { 0, 30, 60, 90 }) {
                double y = h - (altDeg / 90.0) * h;
                var label = new TextBlock {
                    Text = $"{altDeg}°", FontSize = 10, Foreground = labelBrush,
                    TextAlignment = TextAlignment.Right, Width = yAxisW - 4,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(label, 0);
                Canvas.SetTop(label, y - (altDeg == 0 ? 14 : 7));
                YAxisCanvas.Children.Add(label);
            }
        }

        private void DrawTwilightMarkers(TimeZoneInfo tz, DateTime graphStart, double totalHours, double w, double h) {
            var lineBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
            var labelBrush = new SolidColorBrush(Color.FromArgb(170, 200, 200, 220));

            var levels = new (double threshold, string label)[] {
                (-6.0, "Civil"), (-12.0, "Nautical"), (-18.0, "Astro"),
            };

            foreach (var (threshold, label) in levels) {
                double prevSun = _slots[0].SunAltDeg;
                for (int i = 1; i < _slots.Count; i++) {
                    double curSun = _slots[i].SunAltDeg;
                    bool crossed = (prevSun >= threshold && curSun < threshold) ||
                                   (prevSun < threshold && curSun >= threshold);
                    if (crossed) {
                        double frac01 = (prevSun - threshold) / (prevSun - curSun);
                        double crossFrac = ((i - 1) + frac01) / _slots.Count;
                        double x = crossFrac * w;

                        TimelineCanvas.Children.Add(new Line {
                            X1 = x, X2 = x, Y1 = 0, Y2 = h,
                            Stroke = lineBrush, StrokeThickness = 1, IsHitTestVisible = false,
                        });

                        var twLabel = new TextBlock {
                            Text = label, FontSize = 9, FontWeight = FontWeights.SemiBold,
                            Foreground = labelBrush, IsHitTestVisible = false,
                        };
                        Canvas.SetLeft(twLabel, x + 3);
                        Canvas.SetTop(twLabel, h - 15);
                        TimelineCanvas.Children.Add(twLabel);
                    }
                    prevSun = curSun;
                }
            }
        }

        private void DrawTimeAxis(DateTime graphStart, DateTime graphEnd, double totalHours, double w, double h) {
            var tickBrush = new SolidColorBrush(Color.FromArgb(50, 255, 255, 255));
            var labelBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

            var firstTick = new DateTime(graphStart.Year, graphStart.Month, graphStart.Day,
                graphStart.Hour, 0, 0);
            if (firstTick <= graphStart) firstTick = firstTick.AddHours(1);

            for (var tickTime = firstTick; tickTime < graphEnd; tickTime = tickTime.AddHours(1)) {
                double frac = (tickTime - graphStart).TotalHours / totalHours;
                if (frac < 0.01 || frac > 0.99) continue;
                double x = frac * w;

                TimelineCanvas.Children.Add(new Line {
                    X1 = x, X2 = x, Y1 = 0, Y2 = h,
                    Stroke = tickBrush, StrokeThickness = 1, IsHitTestVisible = false,
                });

                var label = new TextBlock {
                    Text = $"{tickTime.Hour:D2}:00", FontSize = 10,
                    Foreground = labelBrush, IsHitTestVisible = false,
                };
                Canvas.SetLeft(label, x - 16);
                Canvas.SetTop(label, 2);
                TimeAxisBottom.Children.Add(label);
            }
        }

        private void DrawMoonCurve(double w, double h) {
            bool anyAbove = _slots.Any(s => s.MoonAltDeg > 0);
            if (!anyAbove) return;

            var moonPoints = new PointCollection(_slots.Count);
            for (int i = 0; i < _slots.Count; i++) {
                double alt = Math.Max(0, _slots[i].MoonAltDeg);
                double x = (double)i / _slots.Count * w;
                double y = h - (alt / 90.0) * h;
                moonPoints.Add(new Point(x, y));
            }

            var fillPoints = new PointCollection(moonPoints);
            fillPoints.Add(new Point(w, h));
            fillPoints.Add(new Point(0, h));

            TimelineCanvas.Children.Add(new Polygon {
                Points = fillPoints,
                Fill = new SolidColorBrush(Color.FromArgb(60, 0xCC, 0x33, 0x33)),
                IsHitTestVisible = false,
            });

            TimelineCanvas.Children.Add(new Polyline {
                Points = moonPoints,
                Stroke = new SolidColorBrush(Color.FromArgb(150, 0xCC, 0x33, 0x33)),
                StrokeThickness = 1.5, IsHitTestVisible = false,
            });

            // Label at the peak of the moon curve
            double peakAlt = 0;
            int peakIdx = 0;
            for (int i = 0; i < _slots.Count; i++) {
                if (_slots[i].MoonAltDeg > peakAlt) {
                    peakAlt = _slots[i].MoonAltDeg;
                    peakIdx = i;
                }
            }
            if (peakAlt > 5) {
                double labelX = (double)peakIdx / _slots.Count * w;
                double labelY = h - (peakAlt / 90.0) * h;
                var moonLabel = new TextBlock {
                    Text = "Moon", FontSize = 9, FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromArgb(180, 0xCC, 0x33, 0x33)),
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(moonLabel, labelX - 14);
                Canvas.SetTop(moonLabel, labelY - 14);
                TimelineCanvas.Children.Add(moonLabel);
            }
        }

        private void DrawScheduleBlocks(double w, double h, TimeZoneInfo tz) {
            if (_log == null || _log.Count == 0) return;

            var labelBrush = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255));
            var panelLabelBrush = new SolidColorBrush(Color.FromArgb(180, 200, 220, 255));

            DateTime timelineStart = _slots[0].UtcStart;
            DateTime timelineEnd = _slots[_slots.Count - 1].UtcStart.AddSeconds(300);
            double timelineSpanSec = (timelineEnd - timelineStart).TotalSeconds;

            var targetGroups = BuildTargetGroups(timelineStart, timelineSpanSec, w);

            foreach (var group in targetGroups) {
                int targetIdx = -1;
                for (int p = 0; p < _profiles.Count; p++) {
                    if (_profiles[p].DisplayName == group.TargetName
                        || _profiles[p].Target.TargetName == group.TargetName) { targetIdx = p; break; }
                }
                if (targetIdx < 0) continue;

                var baseColor = _colorResolver != null ? _colorResolver(targetIdx) : TargetCurveColors[targetIdx % TargetCurveColors.Length];
                var blockFill = new SolidColorBrush(Color.FromArgb(80, baseColor.R, baseColor.G, baseColor.B));
                var blockStroke = new SolidColorBrush(Color.FromArgb(140, baseColor.R, baseColor.G, baseColor.B));

                foreach (var run in group.MergedRuns) {
                    double rx0 = (run.StartUtc - timelineStart).TotalSeconds / timelineSpanSec * w;
                    double rx1 = (run.EndUtc - timelineStart).TotalSeconds / timelineSpanSec * w;
                    double rw = Math.Max(1, rx1 - rx0);

                    var rect = new Rectangle {
                        Width = rw, Height = h, Fill = blockFill,
                        Stroke = blockStroke, StrokeThickness = 1, IsHitTestVisible = false,
                    };
                    Canvas.SetLeft(rect, rx0);
                    Canvas.SetTop(rect, 0);
                    TimelineCanvas.Children.Add(rect);
                }

                double x0 = (group.FirstUtc - timelineStart).TotalSeconds / timelineSpanSec * w;
                double x1 = (group.LastEndUtc - timelineStart).TotalSeconds / timelineSpanSec * w;
                double blockW = x1 - x0;
                bool hasMultiplePanels = group.Panels.Count > 1;

                if (hasMultiplePanels && blockW > 40) {
                    if (blockW > 50) {
                        var nameLabel = new TextBlock {
                            Text = group.TargetName, FontSize = blockW > 100 ? 11 : 9,
                            Foreground = labelBrush, TextTrimming = TextTrimming.CharacterEllipsis,
                            MaxWidth = blockW - 6, IsHitTestVisible = false,
                        };
                        Canvas.SetLeft(nameLabel, x0 + 3);
                        Canvas.SetTop(nameLabel, 4);
                        TimelineCanvas.Children.Add(nameLabel);
                    }

                    var panelDivBrush = new SolidColorBrush(Color.FromArgb(100, baseColor.R, baseColor.G, baseColor.B));
                    for (int pi = 0; pi < group.Panels.Count; pi++) {
                        double cx = (group.Panels[pi].StartUtc - timelineStart).TotalSeconds / timelineSpanSec * w;
                        double nextX = pi < group.Panels.Count - 1
                            ? (group.Panels[pi + 1].StartUtc - timelineStart).TotalSeconds / timelineSpanSec * w
                            : x1;

                        if (pi > 0) {
                            TimelineCanvas.Children.Add(new Line {
                                X1 = cx, X2 = cx, Y1 = 0, Y2 = h,
                                Stroke = panelDivBrush, StrokeThickness = 1,
                                StrokeDashArray = new DoubleCollection { 3, 3 },
                                IsHitTestVisible = false,
                            });
                        }

                        double panelW = nextX - cx;
                        if (panelW > 22) {
                            var pLabel = new TextBlock {
                                Text = group.Panels[pi].Name, FontSize = 9,
                                Foreground = panelLabelBrush, IsHitTestVisible = false,
                            };
                            Canvas.SetLeft(pLabel, cx + 3);
                            Canvas.SetTop(pLabel, 20);
                            TimelineCanvas.Children.Add(pLabel);
                        }
                    }
                } else if (blockW > 30) {
                    var label = new TextBlock {
                        Text = group.TargetName, FontSize = blockW > 100 ? 11 : 9,
                        Foreground = labelBrush, TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = blockW - 6, IsHitTestVisible = false,
                    };
                    Canvas.SetLeft(label, x0 + 3);
                    Canvas.SetTop(label, 4);
                    TimelineCanvas.Children.Add(label);
                }
            }
        }

        private void DrawFilterBar(double w, TimeZoneInfo tz) {
            if (_log == null || _log.Count == 0) return;

            double barHeight = FilterBarCanvas.ActualHeight > 0 ? FilterBarCanvas.ActualHeight : 20;
            double barW = FilterBarCanvas.ActualWidth > 0 ? FilterBarCanvas.ActualWidth : w;

            var filterColorMap = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
            foreach (var prof in _profiles) {
                foreach (var panel in prof.Target.Panels) {
                    foreach (var es in panel.ExposureSets) {
                        string name = es.FilterName ?? "—";
                        if (!filterColorMap.ContainsKey(name))
                            filterColorMap[name] = GetFilterColor(name);
                    }
                }
            }

            DateTime timelineStart = _slots[0].UtcStart;
            DateTime timelineEnd = _slots[_slots.Count - 1].UtcStart.AddSeconds(300);
            double timelineSpanSec = (timelineEnd - timelineStart).TotalSeconds;

            var segments = new List<(string FilterName, int ImageCount, double LabelX0, double LabelX1,
                List<(double X0, double X1)> Rects)>();
            var bonusSpans = new List<(double X0, double X1)>();

            string curFilter = null;
            string lastKnownFilter = "";
            int segImageCount = 0;
            double segLabelX0 = 0, segLabelX1 = 0;
            var segRects = new List<(double X0, double X1)>();
            double runX0 = 0, runX1 = 0;
            bool inRun = false;

            void FlushRun() {
                if (inRun) { segRects.Add((runX0, runX1)); segLabelX1 = runX1; }
                inRun = false;
            }

            void FlushSegment() {
                FlushRun();
                if (curFilter != null && segRects.Count > 0)
                    segments.Add((curFilter, segImageCount, segLabelX0, segLabelX1,
                        new List<(double, double)>(segRects)));
                segRects.Clear();
                segImageCount = 0;
            }

            for (int ei = 0; ei < _log.Count; ei++) {
                var entry = _log[ei];

                if ((entry.Command == "Image" || entry.Command == "Bonus") && entry.UtcTime != default && !string.IsNullOrEmpty(entry.Filter)) {
                    double expSec = 300;
                    if (!string.IsNullOrEmpty(entry.Exposure) && double.TryParse(entry.Exposure.TrimEnd('s'), out var parsed))
                        expSec = parsed;

                    double x0 = (entry.UtcTime - timelineStart).TotalSeconds / timelineSpanSec * barW;
                    double x1 = (entry.UtcTime.AddSeconds(expSec) - timelineStart).TotalSeconds / timelineSpanSec * barW;

                    if (entry.Filter != curFilter) {
                        FlushSegment();
                        curFilter = entry.Filter;
                        segLabelX0 = x0;
                    }

                    if (!inRun) { runX0 = x0; runX1 = x1; inRun = true; }
                    else { runX1 = x1; }
                    segImageCount++;
                    lastKnownFilter = entry.Filter;
                    if (entry.Command == "Bonus") bonusSpans.Add((x0, x1));
                } else if (entry.Command == "Info" && inRun && entry.UtcTime != default) {
                    double infoX = (entry.UtcTime - timelineStart).TotalSeconds / timelineSpanSec * barW;
                    if (infoX - runX1 > barW * 10.0 / (timelineSpanSec / 60))
                        FlushSegment();
                } else if ((entry.Command == "Dither" || entry.Command == "Filter") && entry.UtcTime != default) {
                    double durSec = 10;
                    if (ei + 1 < _log.Count)
                        durSec = Math.Clamp((_log[ei + 1].UtcTime - entry.UtcTime).TotalSeconds, 1, 300);

                    double x0 = (entry.UtcTime - timelineStart).TotalSeconds / timelineSpanSec * barW;
                    double x1 = (entry.UtcTime.AddSeconds(durSec) - timelineStart).TotalSeconds / timelineSpanSec * barW;

                    if (entry.Command == "Filter" && !string.IsNullOrEmpty(entry.Filter)) {
                        FlushSegment();
                        curFilter = entry.Filter;
                        segLabelX0 = x0;
                        lastKnownFilter = entry.Filter;
                        runX0 = x0; runX1 = x1; inRun = true;
                    } else {
                        string ditherFilter = curFilter ?? lastKnownFilter;
                        if (!string.IsNullOrEmpty(ditherFilter)) {
                            if (!inRun) { runX0 = x0; runX1 = x1; inRun = true; }
                            else { runX1 = x1; }
                        }
                    }
                } else {
                    FlushRun();
                }
            }
            FlushSegment();

            var labelBrush = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255));

            foreach (var seg in segments) {
                if (!filterColorMap.TryGetValue(seg.FilterName, out var col)) continue;
                var fill = new SolidColorBrush(col);

                foreach (var (rx0, rx1) in seg.Rects) {
                    var rect = new Rectangle {
                        Width = Math.Max(1, rx1 - rx0), Height = barHeight,
                        Fill = fill, IsHitTestVisible = false,
                    };
                    Canvas.SetLeft(rect, rx0);
                    Canvas.SetTop(rect, 0);
                    FilterBarCanvas.Children.Add(rect);
                }

                double labelSpan = seg.LabelX1 - seg.LabelX0;
                if (labelSpan > 30) {
                    var label = new TextBlock {
                        Text = $"{seg.FilterName} x{seg.ImageCount}", FontSize = 9,
                        Foreground = labelBrush, TextTrimming = TextTrimming.CharacterEllipsis,
                        MaxWidth = labelSpan - 4, IsHitTestVisible = false,
                    };
                    Canvas.SetLeft(label, seg.LabelX0 + 2);
                    Canvas.SetTop(label, (barHeight - 13) / 2);
                    FilterBarCanvas.Children.Add(label);
                }
            }

            if (bonusSpans.Count > 0) {
                var merged = new List<(double X0, double X1)>();
                var cur = bonusSpans[0];
                for (int i = 1; i < bonusSpans.Count; i++) {
                    if (bonusSpans[i].X0 <= cur.X1 + 1)
                        cur.X1 = bonusSpans[i].X1;
                    else { merged.Add(cur); cur = bonusSpans[i]; }
                }
                merged.Add(cur);

                int bonusCount = bonusSpans.Count;
                var bonusLabelBrush = new SolidColorBrush(Color.FromRgb(0x81, 0xC7, 0x84));
                foreach (var (bx0, bx1) in merged) {
                    double span = bx1 - bx0;
                    if (span > 30) {
                        var lbl = new TextBlock {
                            Text = $"Bonus x{bonusCount}", FontSize = 9,
                            Foreground = bonusLabelBrush, IsHitTestVisible = false,
                        };
                        Canvas.SetLeft(lbl, bx0 + 2);
                        Canvas.SetTop(lbl, (barHeight - 13) / 2);
                        FilterBarCanvas.Children.Add(lbl);
                    }
                }
            }
        }

        private void DrawTargetAltitudeCurves(double w, double h) {
            for (int p = 0; p < _profiles.Count; p++) {
                var prof = _profiles[p];
                var curveColor = _colorResolver != null ? _colorResolver(p) : TargetCurveColors[p % TargetCurveColors.Length];

                var points = new PointCollection(_slots.Count);
                for (int i = 0; i < _slots.Count; i++) {
                    double alt = prof.AltitudePerSlot[i];
                    double x = (double)i / _slots.Count * w;
                    double y = h - (alt / 90.0) * h;
                    points.Add(new Point(x, y));
                }

                TimelineCanvas.Children.Add(new Polyline {
                    Points = points,
                    Stroke = new SolidColorBrush(Color.FromArgb(180, curveColor.R, curveColor.G, curveColor.B)),
                    StrokeThickness = 1.5, IsHitTestVisible = false,
                });
            }
        }

        private void DrawNowLine(double w, double h, DateTime graphStart, double totalHours, TimeZoneInfo tz) {
            var nowBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F));
            double filterBarH = FilterBarCanvas.ActualHeight > 0 ? FilterBarCanvas.ActualHeight : 20;

            _nowLine = new Line { Y1 = 0, Y2 = h, Stroke = nowBrush, StrokeThickness = 1.5, IsHitTestVisible = false };
            _nowLabel = new TextBlock {
                Text = "NOW", FontSize = 8, FontWeight = FontWeights.Bold,
                Foreground = nowBrush, IsHitTestVisible = false,
            };
            _nowFilterLine = new Line { Y1 = 0, Y2 = filterBarH, Stroke = nowBrush, StrokeThickness = 1.5, IsHitTestVisible = false };

            TimelineCanvas.Children.Add(_nowLine);
            TimelineCanvas.Children.Add(_nowLabel);
            FilterBarCanvas.Children.Add(_nowFilterLine);

            PositionNowLine(w, h, graphStart, totalHours, tz);
            _lastLogIndex = FindCurrentLogIndex();
            StartNowTimer();
        }

        private void PositionNowLine(double w, double h, DateTime graphStart, double totalHours, TimeZoneInfo tz) {
            if (_nowLine == null) return;

            var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            double frac = (localNow - graphStart).TotalHours / totalHours;

            if (frac < 0 || frac > 1) {
                _nowLine.Visibility = Visibility.Collapsed;
                _nowLabel.Visibility = Visibility.Collapsed;
                _nowFilterLine.Visibility = Visibility.Collapsed;
                return;
            }

            _nowLine.Visibility = Visibility.Visible;
            _nowLabel.Visibility = Visibility.Visible;
            _nowFilterLine.Visibility = Visibility.Visible;

            double x = frac * w;
            _nowLine.X1 = x; _nowLine.X2 = x;
            _nowFilterLine.X1 = x; _nowFilterLine.X2 = x;
            Canvas.SetLeft(_nowLabel, x - 10);
            Canvas.SetTop(_nowLabel, h - 28);
        }

        private void StartNowTimer() {
            if (_nowTimer != null) return;
            _nowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            _nowTimer.Tick += NowTimer_Tick;
            _nowTimer.Start();
        }

        private void NowTimer_Tick(object sender, EventArgs e) {
            if (_slots == null || _slots.Count == 0) return;

            double w = TimelineCanvas.ActualWidth;
            double h = TimelineCanvas.ActualHeight;
            if (w < 10 || h < 10) return;

            var tz = TimeZoneInfo.Local;
            var graphStart = TimeZoneInfo.ConvertTimeFromUtc(_slots[0].UtcStart, tz);
            var graphEnd = TimeZoneInfo.ConvertTimeFromUtc(_slots[_slots.Count - 1].UtcStart.AddMinutes(5), tz);
            double totalHours = (graphEnd - graphStart).TotalHours;

            PositionNowLine(w, h, graphStart, totalHours, tz);

            int currentIdx = FindCurrentLogIndex();
            if (currentIdx != _lastLogIndex && currentIdx >= 0) {
                _lastLogIndex = currentIdx;
                NowCrossedInstruction?.Invoke(_log[currentIdx].UtcTime);
            }
        }

        private int FindCurrentLogIndex() {
            if (_log == null || _log.Count == 0) return -1;
            var utcNow = DateTime.UtcNow;
            int best = -1;
            for (int i = 0; i < _log.Count; i++) {
                if (_log[i].UtcTime == default) continue;
                if (_log[i].UtcTime <= utcNow) best = i;
                else break;
            }
            return best;
        }

        public void StopNowTimer() {
            _nowTimer?.Stop();
            _nowTimer = null;
        }

        private class TargetGroup {
            public string TargetName;
            public DateTime FirstUtc;
            public DateTime LastEndUtc;
            public List<(string Name, DateTime StartUtc)> Panels = new List<(string, DateTime)>();
            public List<(DateTime StartUtc, DateTime EndUtc)> MergedRuns = new List<(DateTime, DateTime)>();
        }

        private List<TargetGroup> BuildTargetGroups(DateTime timelineStart, double timelineSpanSec, double w) {
            var groups = new List<TargetGroup>();
            TargetGroup cur = null;
            DateTime runStart = default, runEnd = default;
            bool inRun = false;

            void FlushRun() {
                if (inRun && cur != null) {
                    cur.MergedRuns.Add((runStart, runEnd));
                    if (cur.FirstUtc == default) cur.FirstUtc = runStart;
                    cur.LastEndUtc = runEnd;
                }
                inRun = false;
            }

            for (int ei = 0; ei < _log.Count; ei++) {
                var entry = _log[ei];

                if (entry.Command == "Slew") {
                    bool isPanelSlew = entry.Target.Contains(" → ") && cur != null && entry.Target.StartsWith(cur.TargetName);
                    if (isPanelSlew) {
                        FlushRun();
                        string panelName = entry.Target.Substring(entry.Target.LastIndexOf("→ ") + 2).Trim();
                        if (cur.Panels.All(p => p.Name != panelName))
                            cur.Panels.Add((panelName, entry.UtcTime));
                    } else {
                        FlushRun();
                        if (cur != null && cur.MergedRuns.Count > 0)
                            groups.Add(cur);
                        cur = new TargetGroup { TargetName = entry.Target };
                    }
                } else if (entry.Command == "Info" && inRun) {
                    if (entry.UtcTime != default && (entry.UtcTime - runEnd).TotalMinutes > 10)
                        FlushRun();
                } else if ((entry.Command == "Image" || entry.Command == "Bonus" || entry.Command == "Dither" || entry.Command == "Filter")
                    && cur != null && entry.Target == cur.TargetName) {
                    double durSec;
                    if ((entry.Command == "Image" || entry.Command == "Bonus") && !string.IsNullOrEmpty(entry.Exposure)
                        && double.TryParse(entry.Exposure.TrimEnd('s'), out var exp))
                        durSec = exp;
                    else {
                        durSec = 10;
                        if (ei + 1 < _log.Count)
                            durSec = Math.Clamp((_log[ei + 1].UtcTime - entry.UtcTime).TotalSeconds, 1, 300);
                    }

                    var entryEnd = entry.UtcTime.AddSeconds(durSec);
                    if (!inRun) { runStart = entry.UtcTime; runEnd = entryEnd; inRun = true; }
                    else { runEnd = entryEnd; }

                    if ((entry.Command == "Image" || entry.Command == "Bonus") && !string.IsNullOrEmpty(entry.Panel)
                        && cur.Panels.All(p => p.Name != entry.Panel))
                        cur.Panels.Add((entry.Panel, entry.UtcTime));
                } else {
                    FlushRun();
                }
            }
            FlushRun();
            if (cur != null && cur.MergedRuns.Count > 0) groups.Add(cur);

            return groups;
        }

        private static Color GetFilterColor(string filterName) {
            if (FilterColors.TryGetValue(filterName, out var c)) return c;
            int hash = filterName.GetHashCode();
            return Color.FromRgb((byte)(80 + (hash & 0xFF) % 150),
                                 (byte)(80 + ((hash >> 8) & 0xFF) % 150),
                                 (byte)(80 + ((hash >> 16) & 0xFF) % 150));
        }

        private static Color LerpColor(double value, double min, double max, Color fromColor, Color toColor) {
            double t = Math.Clamp((value - min) / (max - min), 0.0, 1.0);
            return Color.FromRgb(
                (byte)(fromColor.R + (toColor.R - fromColor.R) * t),
                (byte)(fromColor.G + (toColor.G - fromColor.G) * t),
                (byte)(fromColor.B + (toColor.B - fromColor.B) * t));
        }

        private static Color FromHex(string hex) {
            return (Color)ColorConverter.ConvertFromString(hex);
        }

        // ── Scrubber ──

        public void SetScrubberPosition(double frac) {
            if (_slots == null || _slots.Count == 0) return;
            double w = TimelineCanvas.ActualWidth;
            double h = TimelineCanvas.ActualHeight;
            if (w < 10) return;

            double x = Math.Clamp(frac, 0, 1) * w;
            var tz = TimeZoneInfo.Local;
            var graphStart = TimeZoneInfo.ConvertTimeFromUtc(_slots[0].UtcStart, tz);
            var graphEnd = TimeZoneInfo.ConvertTimeFromUtc(_slots[_slots.Count - 1].UtcStart.AddMinutes(5), tz);
            var scrubTime = graphStart.AddHours(frac * (graphEnd - graphStart).TotalHours);

            DrawScrubberThumb(x, h, scrubTime);
        }

        private void DrawScrubberThumb(double x, double h, DateTime localTime) {
            if (_scrubberThumb != null) TimelineCanvas.Children.Remove(_scrubberThumb);
            if (_scrubberTimeLabel != null) TimelineCanvas.Children.Remove(_scrubberTimeLabel);

            _scrubberThumb = new Rectangle {
                Width = 2, Height = h,
                Fill = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(_scrubberThumb, x - 1);
            Canvas.SetTop(_scrubberThumb, 0);
            TimelineCanvas.Children.Add(_scrubberThumb);

            _scrubberTimeLabel = new TextBlock {
                Text = localTime.ToString("HH:mm"),
                FontSize = 9, Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(180, 30, 30, 30)),
                Padding = new Thickness(3, 1, 3, 1),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(_scrubberTimeLabel, x - 16);
            Canvas.SetTop(_scrubberTimeLabel, h - 16);
            TimelineCanvas.Children.Add(_scrubberTimeLabel);
        }

        private void Scrubber_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) {
            if (!ScrubberEnabled) return;
            if (_slots == null || _slots.Count == 0) return;
            _isDragging = true;
            TimelineCanvas.CaptureMouse();
            UpdateScrubberFromMouse(e.GetPosition(TimelineCanvas).X);
        }

        private void Scrubber_MouseMove(object sender, System.Windows.Input.MouseEventArgs e) {
            if (!ScrubberEnabled || !_isDragging) return;
            UpdateScrubberFromMouse(e.GetPosition(TimelineCanvas).X);
        }

        private void Scrubber_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e) {
            if (!ScrubberEnabled) return;
            _isDragging = false;
            TimelineCanvas.ReleaseMouseCapture();
        }

        private void UpdateScrubberFromMouse(double mouseX) {
            double w = TimelineCanvas.ActualWidth;
            double h = TimelineCanvas.ActualHeight;
            if (w < 10 || _slots == null || _slots.Count == 0) return;

            double frac = Math.Clamp(mouseX / w, 0, 1);
            var tz = TimeZoneInfo.Local;
            var graphStart = TimeZoneInfo.ConvertTimeFromUtc(_slots[0].UtcStart, tz);
            var graphEnd = TimeZoneInfo.ConvertTimeFromUtc(_slots[_slots.Count - 1].UtcStart.AddMinutes(5), tz);
            var scrubTime = graphStart.AddHours(frac * (graphEnd - graphStart).TotalHours);

            DrawScrubberThumb(mouseX, h, scrubTime);

            var utcStart = _slots[0].UtcStart;
            var utcEnd = _slots[_slots.Count - 1].UtcStart.AddSeconds(300);
            var scrubUtc = utcStart.AddSeconds(frac * (utcEnd - utcStart).TotalSeconds);
            ScrubberMoved?.Invoke(scrubUtc);
        }
    }
}
