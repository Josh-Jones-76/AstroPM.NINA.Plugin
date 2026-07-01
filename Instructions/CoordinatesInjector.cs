using NINA.Astrometry;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Platesolving;
using NINA.Sequencer.SequenceItem.Telescope;

namespace AstroPM.NINA.Plugin.Instructions {

    /// <summary>
    /// Pushes the current AstroPM target coordinates into any coordinate-bearing NINA
    /// instructions — Center, Center And Rotate, Slew to RA/Dec — that a user has dropped into
    /// our "Before/After Each Exposure" or "Before/After Target" instruction blocks, recursing
    /// through nested containers (e.g. Sequencer Powerups groups). Normally such items inherit
    /// coordinates from the nearest static IDeepSkyObjectContainer; our schedule-driven container
    /// changes target per block, so we inject the live coordinates instead. Mirrors Target
    /// Scheduler's CoordinatesInjector. See [[project_meridian_flip_target_fix]] for the related
    /// trigger-context fix.
    /// </summary>
    internal class CoordinatesInjector {
        private readonly InputTarget _target;

        public CoordinatesInjector(InputTarget target) {
            _target = target;
        }

        public void Inject(ISequenceContainer container) {
            if (container == null || _target?.InputCoordinates == null) { return; }

            foreach (ISequenceItem item in container.Items) {
                // Check CenterAndRotate before Center — it derives from Center, so a `case Center`
                // (or `is Center`) first would swallow it and its rotate handling would never run.
                if (item is CenterAndRotate centerAndRotate) {
                    centerAndRotate.Coordinates = _target.InputCoordinates;
                    centerAndRotate.Inherited = true;
                    centerAndRotate.SequenceBlockInitialize();
                } else if (item is Center center) {
                    center.Coordinates = _target.InputCoordinates;
                    center.Inherited = true;
                    center.SequenceBlockInitialize();
                } else if (item is SlewScopeToRaDec slew) {
                    slew.Coordinates = _target.InputCoordinates;
                    slew.Inherited = true;
                    slew.SequenceBlockInitialize();
                }

                if (item is ISequenceContainer subContainer) {
                    Inject(subContainer);
                }
            }
        }
    }
}
