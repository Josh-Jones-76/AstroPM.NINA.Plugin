using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace AstroPM.NINA.Plugin.Models {

    public class TargetCardModel {
        public string Name { get; set; } = "";
        public string AllocatedTime { get; set; } = "";
        public string Window { get; set; } = "";
        public string AltitudeRange { get; set; } = "";
        public string MoonSeparation { get; set; } = "";
        public Brush ColorBrush { get; set; }
        public int ProfileIndex { get; set; }
        public List<List<FilterPillModel>> PanelFilterGroups { get; set; } = new List<List<FilterPillModel>>();
        public List<FilterPillModel> VisibleFilters { get; set; } = new List<FilterPillModel>();
        public List<PanelTabModel> PanelNames { get; set; } = new List<PanelTabModel>();
        public Visibility IsMultiPanel { get; set; } = Visibility.Collapsed;
        public int SelectedPanelIndex { get; set; }
        public List<ConstraintCheckModel> ConstraintChecks { get; set; } = new List<ConstraintCheckModel>();
        public Visibility LaDefinitionVisibility { get; set; } = Visibility.Collapsed;
    }

    public class PanelTabModel {
        public string Label { get; set; } = "";
        public int PanelIndex { get; set; }
        public Brush Background { get; set; }
        public Brush Foreground { get; set; }
        public Brush BorderColor { get; set; }
    }

    public class FilterPillModel {
        public string FilterLabel { get; set; } = "";
        public string ExposureDetail { get; set; } = "";
        public string StatusIcon { get; set; } = "";
        public SolidColorBrush StatusColor { get; set; }
        public SolidColorBrush ChipColor { get; set; }
        public double ProgressPercent { get; set; }
        public bool IsLunarAvoid { get; set; }
        public ExposureSetData SourceExposureSet { get; set; }
    }

    public class ConstraintCheckModel {
        public string Icon { get; set; } = "";
        public Brush IconColor { get; set; }
        public string Label { get; set; } = "";
        public string Detail { get; set; } = "";
        public Brush DetailColor { get; set; }
    }

    public class SortChipItem {
        public string Index { get; set; } = "";
        public string Label { get; set; } = "";
        public SortCriteria Criteria { get; set; }
    }
}
