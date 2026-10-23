using System.Collections.Generic;

namespace Profiles.Core
{
    /// <summary>Synthetic, labelled examples; no user survey data.</summary>
    public static class SampleProjects
    {
        public static ProfileProject Longitudinal()
        {
            return new ProfileProject
            {
                Kind = ProfileKind.Longitudinal,
                Settings = new ProfileSettings
                {
                    Title = "УЧЕБНЫЙ ПРИМЕР — РЕКОНСТРУКЦИЯ КАНАЛА",
                    HorizontalScale = 1000,
                    VerticalScale = 100,
                    Datum = 139
                },
                Longitudinal = new List<LongitudinalPoint>
                {
                    new LongitudinalPoint { Station = 0, RightBank = 143.80, LeftBank = 143.65, ExistingBottom = 141.60, Water = 142.10, ProjectBottom = 141.20, BottomWidth = 0.6, LeftSlope = 1.5, RightSlope = 1.5 },
                    new LongitudinalPoint { Station = 25, RightBank = 143.65, LeftBank = 143.50, ExistingBottom = 141.50, Water = 142.00, ProjectBottom = 141.10, BottomWidth = 0.6, LeftSlope = 1.5, RightSlope = 1.5 },
                    new LongitudinalPoint { Station = 60, RightBank = 143.55, LeftBank = 143.45, ExistingBottom = 141.40, Water = null, ProjectBottom = 140.96, BottomWidth = 0.6, LeftSlope = 1.5, RightSlope = 1.5 },
                    new LongitudinalPoint { Station = 100, RightBank = 143.35, LeftBank = 143.30, ExistingBottom = 141.20, Water = 141.80, ProjectBottom = 140.80, BottomWidth = 0.6, LeftSlope = 1.5, RightSlope = 1.5 },
                    new LongitudinalPoint { Station = 150, RightBank = 143.20, LeftBank = 143.05, ExistingBottom = 141.05, Water = 141.65, ProjectBottom = 140.60, BottomWidth = 0.8, LeftSlope = 1.5, RightSlope = 2.0 }
                }
            };
        }

        public static ProfileProject CrossSections()
        {
            return new ProfileProject
            {
                Kind = ProfileKind.CrossSection,
                Settings = new ProfileSettings
                {
                    Title = "УЧЕБНЫЕ ПОПЕРЕЧНЫЕ ПРОФИЛИ КАНАЛА",
                    HorizontalScale = 100,
                    VerticalScale = 100,
                    Datum = 139
                },
                Sections = new List<CrossSection>
                {
                    new CrossSection
                    {
                        Name = "1-1 — проектная трапеция",
                        Station = 0,
                        CenterOffset = 0,
                        Water = 142.10,
                        ProjectBottom = 141.20,
                        BottomWidth = 0.6,
                        LeftSlope = 1.5,
                        RightSlope = 1.5,
                        Points = new List<SectionPoint>
                        {
                            new SectionPoint { Offset = -8, Existing = 143.70 },
                            new SectionPoint { Offset = -4, Existing = 143.65 },
                            new SectionPoint { Offset = -1, Existing = 141.80 },
                            new SectionPoint { Offset = 0, Existing = 141.60 },
                            new SectionPoint { Offset = 1, Existing = 141.85 },
                            new SectionPoint { Offset = 4, Existing = 143.80 },
                            new SectionPoint { Offset = 8, Existing = 143.85 }
                        }
                    },
                    new CrossSection
                    {
                        Name = "2-2 — проект по отметкам",
                        Station = 100,
                        Water = 141.80,
                        Points = new List<SectionPoint>
                        {
                            new SectionPoint { Offset = -7, Existing = 143.35, Design = 143.35 },
                            new SectionPoint { Offset = -3.2, Existing = 143.25, Design = 142.70 },
                            new SectionPoint { Offset = -0.4, Existing = 141.35, Design = 140.80 },
                            new SectionPoint { Offset = 0.4, Existing = 141.30, Design = 140.80 },
                            new SectionPoint { Offset = 3.2, Existing = 143.30, Design = 142.70 },
                            new SectionPoint { Offset = 7, Existing = 143.40, Design = 143.40 }
                        }
                    }
                }
            };
        }
    }
}
