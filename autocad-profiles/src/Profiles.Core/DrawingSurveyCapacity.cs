using System.Collections.Generic;
using System.Linq;

namespace Profiles.Core
{
    /// <summary>Capture budget for the complete project, including inactive profile tabs.</summary>
    public sealed class DrawingSurveyCapacity
    {
        private readonly HashSet<double> stations = new HashSet<double>();
        public int RemainingPoints { get; private set; }
        public int RemainingSections { get; private set; }

        public static DrawingSurveyCapacity FromProject(ProfileProject source)
        {
            var project = ProjectFile.Clone(source);
            int points = project.Longitudinal.Count + project.Sections.Sum(section => section.Points.Count);
            int sections = project.Sections.Count;
            foreach (var profile in project.AdditionalProfiles)
            {
                points += profile.Longitudinal.Count + profile.Sections.Sum(section => section.Points.Count);
                sections += profile.Sections.Count;
            }
            var result = new DrawingSurveyCapacity { RemainingPoints = 10000 - points, RemainingSections = 200 - sections };
            foreach (var point in project.Longitudinal) result.stations.Add(point.Station);
            return result;
        }

        /// <summary>Reserve a new active-profile station; an overlay consumes no additional point.</summary>
        public bool TryAddLongitudinalStation(double station)
        {
            ProfileValidation.Range(station, -1000000000, 1000000000, "Пикетаж набираемой точки");
            if (stations.Contains(station)) return true;
            if (RemainingPoints <= 0) return false;
            stations.Add(station);
            RemainingPoints--;
            return true;
        }
    }
}
