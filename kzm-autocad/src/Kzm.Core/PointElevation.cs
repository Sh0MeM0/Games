using System;

namespace Kzm.Core
{
    public sealed class ElevationState
    {
        internal ElevationState(double? value, bool editedByUser, string problem)
        {
            Value = value;
            EditedByUser = editedByUser;
            Problem = problem;
        }

        public double? Value { get; }
        /// <summary>The attribute text differs from the stored value: the user typed a new elevation.</summary>
        public bool EditedByUser { get; }
        public string Problem { get; }
        public bool IsValid => Value.HasValue && Problem == null;
    }

    /// <summary>
    /// A point block keeps the exact elevation in XData and shows it in the
    /// «ОТМЕТКА» attribute. The attribute is the user's editing channel
    /// (Properties / Quick Properties / double click): when its text no longer
    /// equals the formatted stored value, the typed number is the elevation.
    /// No event handlers are involved, so UNDO and copies stay consistent.
    /// </summary>
    public static class PointElevation
    {
        public static ElevationState Resolve(double? stored, string attributeText, int decimals)
        {
            string text = (attributeText ?? string.Empty).Trim();
            if (stored.HasValue && Guard.Finite(stored.Value) &&
                string.Equals(text, ElevationFormat.Format(stored.Value, decimals), StringComparison.Ordinal))
                return new ElevationState(stored.Value, false, null);
            if (ElevationFormat.TryParse(text, out double typed))
                return new ElevationState(typed, true, null);
            if (text.Length == 0)
                return new ElevationState(null, true, "отметка в атрибуте стёрта");
            return new ElevationState(null, true, "отметка в атрибуте не читается: «" + text + "»");
        }
    }

    public static class SurfaceNames
    {
        public const int MaximumLength = 128;

        public static string Validate(string name)
        {
            string trimmed = (name ?? string.Empty).Trim();
            if (trimmed.Length == 0) throw new KzmException("Имя поверхности не может быть пустым.");
            if (trimmed.Length > MaximumLength) throw new KzmException("Имя поверхности длиннее " + MaximumLength + " символов.");
            foreach (char c in trimmed)
                if (char.IsControl(c)) throw new KzmException("Имя поверхности содержит управляющие символы.");
            return trimmed;
        }
    }
}
