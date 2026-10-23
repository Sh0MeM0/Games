using System;
using System.Collections.Generic;

namespace Profiles.Core
{
    public enum ProfileKind { Longitudinal, CrossSection }
    public enum ProfileTemplate { Reconstruction, NewChannel, Nvk }
    public enum SectionPointRole { Regular, LeftBank, Bottom, RightBank }
    public enum SectionDesignMode { Automatic, None, Trapezoid, Points }
    public enum TextAlignment { Left, Center, Right }

    public sealed class ProfileProject
    {
        public int SchemaVersion { get; set; } = 4;
        public string ProjectId { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = "Проект";
        public ProfileKind Kind { get; set; }
        public ProfileTemplate Template { get; set; } = ProfileTemplate.Reconstruction;
        public ProfileSettings Settings { get; set; } = new ProfileSettings();
        public List<LongitudinalPoint> Longitudinal { get; set; } = new List<LongitudinalPoint>();
        public List<CrossSection> Sections { get; set; } = new List<CrossSection>();
    }

    public sealed class ProfileSettings
    {
        public string Title { get; set; } = "ПРОДОЛЬНЫЙ ПРОФИЛЬ";
        public double HorizontalScale { get; set; } = 1000;
        public double VerticalScale { get; set; } = 100;
        // The original pair belongs to the longitudinal profile. A null cross
        // pair supports callers creating an old-style settings object; the UI
        // and migration save explicit, independent cross scales.
        public double? CrossHorizontalScale { get; set; }
        public double? CrossVerticalScale { get; set; }
        public double MetresPerDrawingUnit { get; set; } = 1;
        public double? Datum { get; set; }
        public double TextHeightMm { get; set; } = 2.5;
        public double TextWidthFactor { get; set; } = 0.85;
        public bool ShowEmptyRows { get; set; }
    }

    public sealed class LongitudinalPoint
    {
        public double Station { get; set; }
        public double? ProjectGround { get; set; }
        public double? PipeInvert { get; set; }
        public string PipeDescription { get; set; } = "";
        public string Foundation { get; set; } = "";
        public string PointName { get; set; } = "";
        public double? PeatDepth { get; set; }
        public double? Ground { get; set; }
        public double? LeftBank { get; set; }
        public double? RightBank { get; set; }
        public double? ExistingBottom { get; set; }
        public double? Water { get; set; }
        public double? ProjectBottom { get; set; }
        public double? BottomWidth { get; set; }
        public double? LeftSlope { get; set; }
        public double? RightSlope { get; set; }
    }

    public sealed class CrossSection
    {
        public string Name { get; set; } = "1-1";
        public SectionDesignMode DesignMode { get; set; } = SectionDesignMode.Automatic;
        public bool UsePointDistances { get; set; }
        public int? ProjectAnchorIndex { get; set; }
        public bool UseLongitudinalProjectBottom { get; set; }
        public bool UseLongitudinalWater { get; set; }
        public double? SiltThickness { get; set; }
        public double? SiltLeft { get; set; }
        public double? SiltRight { get; set; }
        // Elevations imported from the longitudinal profile; surveyed offsets are supplied separately.
        public double? LeftBank { get; set; }
        public double? RightBank { get; set; }
        public double? ExistingBottom { get; set; }
        public double? Station { get; set; }
        public double? Water { get; set; }
        public double? ProjectBottom { get; set; }
        public double? BottomWidth { get; set; }
        public double? LeftSlope { get; set; }
        public double? RightSlope { get; set; }
        public double CenterOffset { get; set; }
        public List<SectionPoint> Points { get; set; } = new List<SectionPoint>();
    }

    public sealed class SectionPoint
    {
        public SectionPointRole Role { get; set; } = SectionPointRole.Regular;
        public double? DistanceFromPrevious { get; set; }
        public bool UseLongitudinalElevation { get; set; }
        public double Offset { get; set; }
        public double? Existing { get; set; }
        public double? Design { get; set; }
    }

    public struct Point2
    {
        public Point2(double x, double y) { X = x; Y = y; }
        public double X { get; set; }
        public double Y { get; set; }
    }

    public sealed class DrawingPlan
    {
        public List<DrawEntity> Entities { get; set; } = new List<DrawEntity>();
        public List<string> Warnings { get; set; } = new List<string>();
        public double Datum { get; set; }
    }

    public abstract class DrawEntity
    {
        public string Layer { get; set; } = "PROF_TEXT";
        public short ColorIndex { get; set; } = 7;
        public double LineWeightMm { get; set; } = 0.18;
        public bool Dashed { get; set; }
    }

    public sealed class DrawPolyline : DrawEntity
    {
        public List<Point2> Points { get; set; } = new List<Point2>();
        public bool Closed { get; set; }
    }

    public sealed class DrawText : DrawEntity
    {
        public Point2 Position { get; set; }
        public string Text { get; set; } = "";
        public double Height { get; set; }
        public double WidthFactor { get; set; } = 0.85;
        public double RotationDegrees { get; set; }
        public TextAlignment Alignment { get; set; }
        public double MaxWidth { get; set; }
    }

    public sealed class DrawCircle : DrawEntity
    {
        public Point2 Center { get; set; }
        public double Radius { get; set; }
    }

    /// <summary>Simple closed polygon filled with parallel lines; spacing is in drawing units.</summary>
    public sealed class DrawHatch : DrawEntity
    {
        public List<Point2> Boundary { get; set; } = new List<Point2>();
        public double PatternSpacing { get; set; } = 1;
        public double PatternAngleDegrees { get; set; } = 45;
    }

    public sealed class ProfileValidationException : Exception
    {
        public ProfileValidationException(string message) : base(message) { }
        public ProfileValidationException(string message, Exception inner) : base(message, inner) { }
    }
}
