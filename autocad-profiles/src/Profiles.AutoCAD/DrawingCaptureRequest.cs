using Profiles.Core;

namespace Profiles.AutoCAD
{
    internal enum DrawingCaptureSourceMode { ModelMetres, PaperMillimetres, Custom, Calibration }

    /// <summary>Source survey units, independent of the profile's output scales.</summary>
    internal sealed class DrawingCaptureOptions
    {
        public DrawingCaptureSourceMode SourceMode { get; set; } = DrawingCaptureSourceMode.ModelMetres;
        public double MetresPerDrawingUnit { get; set; } = 1;
        public double PaperScale { get; set; } = 1000;
        public double StartStation { get; set; }
        public DrawingCaptureOptions Clone() => (DrawingCaptureOptions)MemberwiseClone();
    }

    internal sealed class DrawingCaptureRequest
    {
        public ProfileKind Kind { get; set; }
        public ProfileProject Draft { get; set; }
        public string SelectedSectionId { get; set; }
        public DrawingCaptureOptions Options { get; set; } = new DrawingCaptureOptions();
    }

    internal sealed class DrawingCaptureResult
    {
        public bool Accepted { get; set; }
        public ProfileProject Project { get; set; }
        public string SelectedSectionId { get; set; }
        public DrawingCaptureOptions Options { get; set; }
    }
}
