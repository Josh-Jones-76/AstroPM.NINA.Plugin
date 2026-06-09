using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Core.Utility.WindowService;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.WPF.Base.Mediator;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.Image.Interfaces;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using Newtonsoft.Json;
using NINA.Sequencer.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Platesolving;
using NINA.Astrometry;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AstroPM.NINA.Plugin.Instructions {

    /// <summary>
    /// Internal child items used by TargetInstructionSet (a SequenceContainer).
    /// These are NOT exported via MEF — they don't appear in the NINA toolbox.
    /// TargetInstructionSet executes them directly and manually fires parent triggers between exposures.
    /// </summary>

    // ── Placeholder to prevent NINA from skipping an empty container ──
    [JsonObject(MemberSerialization.OptIn)]
    public class AstroPMPlaceholderItem : SequenceItem {
        public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            return Task.CompletedTask;
        }
        public override object Clone() => new AstroPMPlaceholderItem();
        public override string ToString() => "AstroPM Placeholder";
    }

    // ── Wait until a block's start time ──
    internal class AstroPMWaitItem : SequenceItem {
        private readonly TargetBlock _block;
        private readonly Action<string, TargetBlock> _updateStatus;

        public AstroPMWaitItem(TargetBlock block, Action<string, TargetBlock> updateStatus) {
            _block = block;
            _updateStatus = updateStatus;
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            while (DateTime.UtcNow < _block.UtcStart) {
                token.ThrowIfCancellationRequested();
                var waitSec = (_block.UtcStart - DateTime.UtcNow).TotalSeconds;
                progress?.Report(new ApplicationStatus {
                    Status = $"Astro PM: Waiting {waitSec / 60.0:F0} min for {_block.TargetName}..."
                });
                _updateStatus?.Invoke("Wait", _block);
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(waitSec, 30)), token);
            }
        }

        public override object Clone() => new AstroPMWaitItem(_block, _updateStatus);
        public override string ToString() => $"AstroPM Wait: {_block.TargetName}";
    }

    // ── Slew, plate-solve center, optionally rotate ──
    internal class AstroPMSlewCenterItem : SequenceItem {
        private readonly TargetBlock _block;
        private readonly IProfileService _profileService;
        private readonly ITelescopeMediator _telescopeMediator;
        private readonly IImagingMediator _imagingMediator;
        private readonly IRotatorMediator _rotatorMediator;
        private readonly IFilterWheelMediator _filterWheelMediator;
        private readonly IGuiderMediator _guiderMediator;
        private readonly IDomeMediator _domeMediator;
        private readonly IDomeFollower _domeFollower;
        private readonly IPlateSolverFactory _plateSolverFactory;
        private readonly IWindowServiceFactory _windowServiceFactory;
        private readonly Action<string, TargetBlock> _updateStatus;

        public AstroPMSlewCenterItem(TargetBlock block,
            IProfileService profileService, ITelescopeMediator telescopeMediator,
            IImagingMediator imagingMediator, IRotatorMediator rotatorMediator,
            IFilterWheelMediator filterWheelMediator, IGuiderMediator guiderMediator,
            IDomeMediator domeMediator, IDomeFollower domeFollower,
            IPlateSolverFactory plateSolverFactory, IWindowServiceFactory windowServiceFactory,
            Action<string, TargetBlock> updateStatus) {
            _block = block;
            _profileService = profileService;
            _telescopeMediator = telescopeMediator;
            _imagingMediator = imagingMediator;
            _rotatorMediator = rotatorMediator;
            _filterWheelMediator = filterWheelMediator;
            _guiderMediator = guiderMediator;
            _domeMediator = domeMediator;
            _domeFollower = domeFollower;
            _plateSolverFactory = plateSolverFactory;
            _windowServiceFactory = windowServiceFactory;
            _updateStatus = updateStatus;
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            progress?.Report(new ApplicationStatus { Status = $"Astro PM: Slewing & centering {_block.TargetName}..." });
            _updateStatus?.Invoke("Slew", _block);

            global::NINA.Core.Utility.Logger.Info(
                $"AstroPM | Slew starting: {_block.TargetName} RA={_block.RaHours:F4}h Dec={_block.DecDegrees:F4}° Rot={_block.RotationDeg:F1}°");

            var inputCoords = new InputCoordinates(
                new Coordinates(
                    Angle.ByHours(_block.RaHours),
                    Angle.ByDegree(_block.DecDegrees),
                    Epoch.J2000));

            bool useRotator = Math.Abs(_block.RotationDeg) > 0.01
                && _rotatorMediator.GetInfo()?.Connected == true;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (useRotator) {
                global::NINA.Core.Utility.Logger.Info($"AstroPM | Slew mode: CenterAndRotate (PA={_block.RotationDeg:F1}°)");
                var slewCenterRotate = new CenterAndRotate(_profileService, _telescopeMediator,
                    _imagingMediator, _rotatorMediator, _filterWheelMediator, _guiderMediator,
                    _domeMediator, _domeFollower, _plateSolverFactory, _windowServiceFactory);
                slewCenterRotate.Coordinates = inputCoords;
                slewCenterRotate.PositionAngle = _block.RotationDeg;
                await slewCenterRotate.Execute(progress, token);
            } else {
                global::NINA.Core.Utility.Logger.Info(
                    $"AstroPM | Slew mode: Center only (rotator {(_rotatorMediator.GetInfo()?.Connected == true ? "connected but PA~0" : "not connected")})");
                var slewCenter = new Center(_profileService, _telescopeMediator,
                    _imagingMediator, _filterWheelMediator, _guiderMediator,
                    _domeMediator, _domeFollower, _plateSolverFactory, _windowServiceFactory);
                slewCenter.Coordinates = inputCoords;
                await slewCenter.Execute(progress, token);
            }
            sw.Stop();
            global::NINA.Core.Utility.Logger.Info($"AstroPM | Slew complete: {_block.TargetName} ({sw.Elapsed.TotalSeconds:F1}s)");
        }

        public override object Clone() => new AstroPMSlewCenterItem(_block,
            _profileService, _telescopeMediator, _imagingMediator, _rotatorMediator,
            _filterWheelMediator, _guiderMediator, _domeMediator, _domeFollower,
            _plateSolverFactory, _windowServiceFactory, _updateStatus);
        public override string ToString() => $"AstroPM Slew: {_block.TargetName}";
    }

    // ── Start guiding ──
    internal class AstroPMStartGuidingItem : SequenceItem {
        private readonly TargetBlock _block;
        private readonly IGuiderMediator _guiderMediator;
        private readonly Action<string, TargetBlock> _updateStatus;

        public AstroPMStartGuidingItem(TargetBlock block, IGuiderMediator guiderMediator,
            Action<string, TargetBlock> updateStatus) {
            _block = block;
            _guiderMediator = guiderMediator;
            _updateStatus = updateStatus;
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            var guiderInfo = _guiderMediator.GetInfo();
            if (guiderInfo?.Connected != true) {
                global::NINA.Core.Utility.Logger.Info($"AstroPM | Guider not connected, skipping guide start for {_block.TargetName}");
                return;
            }

            progress?.Report(new ApplicationStatus { Status = $"Astro PM: Starting guiding for {_block.TargetName}..." });
            _updateStatus?.Invoke("Guide", _block);
            try {
                global::NINA.Core.Utility.Logger.Info($"AstroPM | Starting guiding for {_block.TargetName}...");
                var result = await _guiderMediator.StartGuiding(false, progress, token);
                if (result) {
                    global::NINA.Core.Utility.Logger.Info($"AstroPM | Guiding started successfully for {_block.TargetName}");
                } else {
                    global::NINA.Core.Utility.Logger.Warning($"AstroPM | Guiding failed to start for {_block.TargetName}");
                }
            } catch (Exception ex) {
                global::NINA.Core.Utility.Logger.Warning($"AstroPM | Guiding error for {_block.TargetName}: {ex.Message}");
            }
        }

        public override object Clone() => new AstroPMStartGuidingItem(_block, _guiderMediator, _updateStatus);
        public override string ToString() => $"AstroPM Guide: {_block.TargetName}";
    }

    // ── Dither (if guider connected) ──
    internal class AstroPMDitherItem : SequenceItem {
        private readonly TargetBlock _block;
        private readonly IGuiderMediator _guiderMediator;
        private readonly Action<string, TargetBlock> _updateStatus;

        public AstroPMDitherItem(TargetBlock block, IGuiderMediator guiderMediator,
            Action<string, TargetBlock> updateStatus) {
            _block = block;
            _guiderMediator = guiderMediator;
            _updateStatus = updateStatus;
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            var guiderInfo = _guiderMediator.GetInfo();
            if (guiderInfo?.Connected != true) {
                global::NINA.Core.Utility.Logger.Info($"AstroPM | Guider not connected, skipping dither for {_block.TargetName}");
                return;
            }

            progress?.Report(new ApplicationStatus { Status = $"Astro PM: Dithering {_block.TargetName}..." });
            _updateStatus?.Invoke("Dither", _block);
            try {
                global::NINA.Core.Utility.Logger.Info($"AstroPM | Dithering {_block.TargetName}...");
                var result = await _guiderMediator.Dither(token);
                if (result) {
                    global::NINA.Core.Utility.Logger.Info($"AstroPM | Dither complete for {_block.TargetName}");
                } else {
                    global::NINA.Core.Utility.Logger.Warning($"AstroPM | Dither failed for {_block.TargetName}");
                }
            } catch (Exception ex) {
                global::NINA.Core.Utility.Logger.Warning($"AstroPM | Dither error for {_block.TargetName}: {ex.Message}");
            }
        }

        public override object Clone() => new AstroPMDitherItem(_block, _guiderMediator, _updateStatus);
        public override string ToString() => $"AstroPM Dither: {_block.TargetName}";
    }

    // ── Take a single exposure (the critical item — triggers fire between these) ──
    internal class AstroPMTakeExposureItem : SequenceItem, IExposureItem {
        private readonly TargetBlock _block;
        private readonly string _filterName;
        private readonly double _exposureSec;
        private readonly int _gain;
        private readonly int _offset;
        private readonly int _binX;
        private readonly int _binY;
        private readonly int _subNumber;
        private readonly DateTime _blockEndUtc;
        private readonly DateTime _sessionEndUtc;
        private readonly IProfileService _profileService;
        private readonly IFilterWheelMediator _filterWheelMediator;
        private readonly IImagingMediator _imagingMediator;
        private readonly IImageSaveMediator _imageSaveMediator;
        private readonly IImageHistoryVM _imageHistoryVM;
        private readonly Action<string, TargetBlock, string, string, string, string> _updateExposureStatus;
        private readonly Action _onCaptured;

        public AstroPMTakeExposureItem(TargetBlock block, string filterName, double exposureSec,
            int gain, int offset, int binX, int binY, int subNumber,
            DateTime blockEndUtc, DateTime sessionEndUtc,
            IProfileService profileService, IFilterWheelMediator filterWheelMediator,
            IImagingMediator imagingMediator, IImageSaveMediator imageSaveMediator,
            IImageHistoryVM imageHistoryVM,
            Action<string, TargetBlock, string, string, string, string> updateExposureStatus,
            Action onCaptured) {
            _block = block;
            _filterName = filterName;
            _exposureSec = exposureSec;
            _gain = gain;
            _offset = offset;
            _binX = binX;
            _binY = binY;
            _subNumber = subNumber;
            _blockEndUtc = blockEndUtc;
            _sessionEndUtc = sessionEndUtc;
            _profileService = profileService;
            _filterWheelMediator = filterWheelMediator;
            _imagingMediator = imagingMediator;
            _imageSaveMediator = imageSaveMediator;
            _imageHistoryVM = imageHistoryVM;
            _updateExposureStatus = updateExposureStatus;
            _onCaptured = onCaptured;
        }

        /// <summary>The filter name for this exposure — used by the parent container for status tracking.</summary>
        public string FilterName => _filterName;

        /// <summary>The block this exposure belongs to — used by the parent container for status updates.</summary>
        public TargetBlock Block => _block;

        // ── IExposureItem implementation — allows NINA triggers (AutofocusAfterExposures, etc.)
        //    to recognize this as a LIGHT exposure and count it toward their thresholds. ──
        public double ExposureTime { get => _exposureSec; set { } }
        public int Gain { get => _gain; set { } }
        public int Offset { get => _offset; set { } }
        public string ImageType { get => "LIGHT"; set { } }
        public BinningMode Binning { get => new BinningMode((short)_binX, (short)_binY); set { } }

        private FilterInfo _resolvedFilter;
        private bool _filterSwitched;

        /// <summary>
        /// Switch the filter wheel before triggers run, so NINA's AutofocusAfterFilterChange
        /// sees the new filter on the physical wheel when it evaluates.
        /// </summary>
        public async Task SwitchFilterAsync(IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (_filterSwitched) return;

            var ninaFilters = _profileService.ActiveProfile.FilterWheelSettings.FilterWheelFilters;
            _resolvedFilter = ninaFilters?.FirstOrDefault(f =>
                string.Equals(f.Name, _filterName, StringComparison.OrdinalIgnoreCase));
            if (_resolvedFilter == null)
                _resolvedFilter = ninaFilters?.FirstOrDefault(f =>
                    f.Name != null && f.Name.StartsWith(_filterName, StringComparison.OrdinalIgnoreCase));
            if (_resolvedFilter == null)
                _resolvedFilter = ninaFilters?.FirstOrDefault(f =>
                    f.Name != null && _filterName.StartsWith(f.Name, StringComparison.OrdinalIgnoreCase));

            if (_resolvedFilter != null) {
                _updateExposureStatus?.Invoke("Filter", _block, _filterName, "", "", "");
                progress?.Report(new ApplicationStatus {
                    Status = $"Astro PM: Switching to {_filterName} filter..."
                });
                global::NINA.Core.Utility.Logger.Info($"AstroPM | Filter switch: {_filterName} for {_block.TargetName} #{_subNumber}");
                await _filterWheelMediator.ChangeFilter(_resolvedFilter, token);
            } else {
                global::NINA.Core.Utility.Logger.Warning(
                    $"AstroPM | Filter '{_filterName}' not found in NINA filter wheel. " +
                    $"Available: {string.Join(", ", ninaFilters?.Select(f => f.Name) ?? Array.Empty<string>())}");
            }

            _filterSwitched = true;
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            // Skip if past the block/session end
            if (DateTime.UtcNow >= _blockEndUtc) {
                global::NINA.Core.Utility.Logger.Info(
                    $"AstroPM | Exposure skipped (past block end): {_block.TargetName} {_filterName} #{_subNumber}");
                return;
            }
            if (DateTime.UtcNow >= _sessionEndUtc) {
                global::NINA.Core.Utility.Logger.Info(
                    $"AstroPM | Exposure skipped (past session end): {_block.TargetName} {_filterName} #{_subNumber}");
                return;
            }
            if (DateTime.UtcNow.AddSeconds(_exposureSec) > _blockEndUtc) {
                global::NINA.Core.Utility.Logger.Info(
                    $"AstroPM | Exposure skipped (would exceed block end by {(DateTime.UtcNow.AddSeconds(_exposureSec) - _blockEndUtc).TotalSeconds:F0}s): {_block.TargetName} {_filterName} {_exposureSec:F0}s #{_subNumber}");
                return;
            }

            // Switch filter if not already done in pre-trigger phase
            if (!_filterSwitched) {
                await SwitchFilterAsync(progress, token);
            }

            // Update live status
            progress?.Report(new ApplicationStatus {
                Status = $"Astro PM: {_block.TargetName} — {_filterName} {_exposureSec:F0}s"
            });

            string goStr = "";
            if (_gain > 0 || _offset > 0) {
                var goParts = new List<string>();
                if (_gain > 0) goParts.Add($"G{_gain}");
                if (_offset > 0) goParts.Add($"O{_offset}");
                goParts.Add($"Bin {_binX}×{_binY}");
                goStr = string.Join("  ", goParts);
            }
            _updateExposureStatus?.Invoke("Image", _block, _filterName,
                $"{_exposureSec:F0}s", $"{_subNumber}", goStr);

            // Capture — use CaptureImage (not CaptureAndPrepareImage) to follow NINA's
            // standard pipeline: capture → add to history → prepare → save.
            var captureSequence = new CaptureSequence(_exposureSec, "LIGHT",
                _resolvedFilter, new BinningMode((short)_binX, (short)_binY), 1) {
                Gain = _gain,
                Offset = _offset,
            };

            var exposureData = await _imagingMediator.CaptureImage(captureSequence, token, progress);

            if (exposureData == null) {
                global::NINA.Core.Utility.Logger.Warning(
                    $"AstroPM | Capture returned null: {_block.TargetName} {_filterName} {_exposureSec:F0}s #{_subNumber}");
                return;
            }

            {
                // Register in NINA's image history — this is what AutofocusAfterExposures counts!
                _imageHistoryVM.Add(exposureData.MetaData.Image.Id, "LIGHT");

                // Convert to image data, prepare for display, and save to disk
                try {
                    var imageData = await exposureData.ToImageData(progress, token);
                    var prepareTask = _imagingMediator.PrepareImage(imageData,
                        new PrepareImageParameters(true, true), token);

                    // Populate statistics in history
                    _imageHistoryVM.PopulateStatistics(exposureData.MetaData.Image.Id,
                        await imageData.Statistics);

                    // Set target metadata
                    imageData.MetaData.Target.Name = _block.TargetName;
                    imageData.MetaData.Target.Coordinates = new Coordinates(
                        Angle.ByHours(_block.RaHours),
                        Angle.ByDegree(_block.DecDegrees),
                        Epoch.J2000);
                    imageData.MetaData.Target.PositionAngle = _block.RotationDeg;

                    // Save the image to disk
                    await _imageSaveMediator.Enqueue(imageData, prepareTask, progress, token);
                } catch (Exception ex) {
                    global::NINA.Core.Utility.Logger.Warning(
                        $"AstroPM | Image save error for {_block.TargetName}: {ex.Message}");
                }

                global::NINA.Core.Utility.Logger.Info(
                    $"AstroPM | Captured: {_block.TargetName} {_filterName} {_exposureSec:F0}s G{_gain} O{_offset}");
                _onCaptured?.Invoke();
            }
        }

        public override object Clone() => new AstroPMTakeExposureItem(_block, _filterName, _exposureSec,
            _gain, _offset, _binX, _binY, _subNumber, _blockEndUtc, _sessionEndUtc,
            _profileService, _filterWheelMediator, _imagingMediator, _imageSaveMediator,
            _imageHistoryVM, _updateExposureStatus, _onCaptured);
        public override string ToString() => $"AstroPM Expose: {_block.TargetName} {_filterName} {_exposureSec:F0}s #{_subNumber}";
    }
}
