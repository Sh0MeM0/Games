using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Kzm.Core
{
    public sealed class ImportOptions
    {
        /// <summary>A marker farther than this many times the median distance is flagged for checking.</summary>
        public const double FarMarkerFactor = 3.0;

        public ImportOptions(XyMode mode, int decimals)
        {
            if (mode == XyMode.Manual) throw new KzmException("Ручной способ применяется только к отдельным строкам.");
            if (decimals < 0 || decimals > 4) throw new KzmException("Знаков после запятой: от 0 до 4.");
            Mode = mode;
            Decimals = decimals;
        }

        public XyMode Mode { get; }
        public int Decimals { get; }
        /// <summary>Points closer than 0,01 m are reported (task, п. 2.1); not a user setting.</summary>
        public double DuplicateTolerance => KzmSettings.DuplicateTolerance;
    }

    public sealed class ImportItem
    {
        internal ImportItem(SourceText source) { Source = source; }

        public SourceText Source { get; }
        public ImportStatus Status { get; private set; }
        public ImportReason Reason { get; private set; }
        public string Message { get; private set; } = string.Empty;
        public double? Elevation { get; private set; }
        /// <summary>Decimal numbers of the text (for «несколько дробных чисел»).</summary>
        public IReadOnlyList<double> Candidates { get; private set; } = new double[0];
        public XY? Position { get; private set; }
        public XyMode PositionMode { get; private set; }
        public string MarkerHandle { get; private set; }
        public double? MarkerDistance { get; private set; }
        public string ExistingPointHandle { get; private set; }
        /// <summary>Whole numbers of the text (point-number candidates).</summary>
        public IReadOnlyList<string> Integers { get; private set; } = new string[0];
        /// <summary>Point number assigned by <see cref="PointNumbering"/>.</summary>
        public string Number { get; internal set; }
        /// <summary>An integer-only text used as the number of this point (not an error then).</summary>
        public ImportItem NumberFor { get; internal set; }
        /// <summary>Accepted, but worth a look (e.g. the marker is unusually far).</summary>
        public string Warning { get; private set; }
        /// <summary>The disputed reason before a manual decision (kept for the report).</summary>
        public ImportReason ResolvedFrom { get; private set; }

        public bool NeedsElevation => Status == ImportStatus.Disputed && !Elevation.HasValue;

        internal void Set(ImportStatus status, ImportReason reason, string message)
        {
            Status = status;
            Reason = reason;
            Message = message ?? string.Empty;
        }

        internal void SetElevation(double? value, IReadOnlyList<double> candidates)
        {
            Elevation = value;
            Candidates = candidates ?? new double[0];
        }

        internal void SetPosition(XY? position, XyMode mode, string markerHandle, double? markerDistance)
        {
            Position = position;
            PositionMode = mode;
            MarkerHandle = markerHandle;
            MarkerDistance = markerDistance;
        }

        internal void SetExisting(string handle) { ExistingPointHandle = handle; }

        internal void SetWarning(string warning) { Warning = warning; }

        internal void SetIntegers(IReadOnlyList<string> integers) { Integers = integers ?? new string[0]; }

        /// <summary>
        /// Способ (в) and manual corrections from the import table: the user typed the
        /// elevation and/or picked the position. With both known the row is accepted;
        /// otherwise it stays disputed and says what is still missing.
        /// </summary>
        public void Override(double? elevation, XY? position, XyMode positionMode)
        {
            if (Status == ImportStatus.Skipped) return;
            if (elevation.HasValue && !Guard.Finite(elevation.Value)) throw new KzmException("Отметка не определена.");
            if (position.HasValue && (!Guard.Finite(position.Value.X) || !Guard.Finite(position.Value.Y)))
                throw new KzmException("Координаты не определены.");
            if (!elevation.HasValue && !position.HasValue) return;
            ImportReason previous = Status == ImportStatus.Accepted ? ImportReason.None : Reason;
            if (previous != ImportReason.ManualIncomplete) ResolvedFrom = previous;
            if (elevation.HasValue) Elevation = elevation;
            if (position.HasValue)
            {
                Position = position;
                PositionMode = positionMode;
                if (positionMode != XyMode.Marker) { MarkerHandle = null; MarkerDistance = null; }
            }
            if (Elevation.HasValue && Position.HasValue)
            {
                Set(ImportStatus.Accepted, ImportReason.None, ResolvedFrom == ImportReason.None
                    ? "изменено вручную"
                    : "решено вручную: было «" + ImportReasons.Title(ResolvedFrom) + "»");
                return;
            }
            Set(ImportStatus.Disputed, ImportReason.ManualIncomplete,
                Elevation.HasValue ? "отметка задана — укажите положение (XY)" : "положение задано — введите отметку");
        }

        public void ResolveManually(double elevation, XY position, XyMode mode)
        {
            if (Status != ImportStatus.Disputed && Status != ImportStatus.Rejected)
                throw new InvalidOperationException("Вручную разбираются только спорные и отброшенные строки.");
            Override(elevation, position, mode);
        }

        /// <summary>The user unticked an accepted row: no point is created for it.</summary>
        public void Exclude()
        {
            if (Status != ImportStatus.Accepted) return;
            Set(ImportStatus.Skipped, ImportReason.ManualSkipped, "исключено вручную");
        }
    }

    /// <summary>Manual decision for one text, kept by the handle across recalculations.</summary>
    public sealed class ImportOverride
    {
        public double? Elevation { get; set; }
        public XY? Position { get; set; }
        public XyMode PositionMode { get; set; } = XyMode.Manual;
        public bool Exclude { get; set; }

        public bool IsEmpty => !Elevation.HasValue && !Position.HasValue && !Exclude;
    }

    public sealed class ImportPlan
    {
        internal ImportPlan(IReadOnlyList<ImportItem> items, ImportOptions options)
        {
            Items = items;
            Options = options;
        }

        public IReadOnlyList<ImportItem> Items { get; }
        public ImportOptions Options { get; }

        public IEnumerable<ImportItem> Accepted => Items.Where(item => item.Status == ImportStatus.Accepted);
        public IEnumerable<ImportItem> Disputed => Items.Where(item => item.Status == ImportStatus.Disputed);
        public IEnumerable<ImportItem> Rejected => Items.Where(item => item.Status == ImportStatus.Rejected);
        public IEnumerable<ImportItem> Skipped => Items.Where(item => item.Status == ImportStatus.Skipped);
    }

    public static class ImportPlanner
    {
        public static ImportPlan Plan(IReadOnlyList<SourceText> texts, IReadOnlyList<MarkerCandidate> markers,
            IReadOnlyList<ExistingPoint> existing, ImportOptions options)
        {
            Guard.List(texts, nameof(texts));
            markers = markers ?? new MarkerCandidate[0];
            existing = existing ?? new ExistingPoint[0];
            if (options == null) throw new ArgumentNullException(nameof(options));
            var imported = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var point in existing)
                if (!string.IsNullOrEmpty(point.SourceHandle) && !imported.ContainsKey(point.SourceHandle))
                    imported[point.SourceHandle] = point.Handle;

            var items = new List<ImportItem>(texts.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var text in texts)
            {
                if (text == null) throw new ArgumentException("Пустой элемент в списке текстов.", nameof(texts));
                var item = new ImportItem(text);
                items.Add(item);
                if (!seen.Add(text.Handle ?? string.Empty))
                {
                    item.Set(ImportStatus.Skipped, ImportReason.AlreadyImported, "объект выбран повторно");
                    continue;
                }
                if (!string.IsNullOrEmpty(text.Handle) && imported.TryGetValue(text.Handle, out string pointHandle))
                {
                    item.SetExisting(pointHandle);
                    item.Set(ImportStatus.Skipped, ImportReason.AlreadyImported,
                        "уже импортирован в эту поверхность (точка " + pointHandle + ")");
                    continue;
                }
                var extracted = ElevationExtractor.Extract(text.Plain);
                item.SetElevation(extracted.Value, extracted.Decimals);
                item.SetIntegers(extracted.Integers);
                item.Set(extracted.Status, extracted.Reason, extracted.Message);
                if (options.Mode == XyMode.Insertion)
                    item.SetPosition(text.Anchor, XyMode.Insertion, null, null);
            }

            if (options.Mode == XyMode.Marker) MatchMarkers(items, markers, options);
            return new ImportPlan(items, options);
        }

        /// <summary>
        /// Texts with at least one decimal number take part (point-number texts do not
        /// compete for markers). A text with several numbers still gets its marker, so that
        /// typing the right elevation is enough to accept it.
        /// </summary>
        private static void MatchMarkers(List<ImportItem> items, IReadOnlyList<MarkerCandidate> markers, ImportOptions options)
        {
            var participants = items.Where(item => item.Status == ImportStatus.Accepted ||
                                                   (item.Status == ImportStatus.Disputed && item.Reason == ImportReason.MultipleNumbers)).ToList();
            var matches = MarkerMatcher.Match(participants.Select(item => item.Source.Anchor).ToList(),
                markers.Select(marker => marker.Position).ToList(), options.DuplicateTolerance);
            for (int i = 0; i < participants.Count; i++)
            {
                var item = participants[i];
                var match = matches[i];
                if (match.Success)
                {
                    var marker = markers[match.MarkerIndex];
                    item.SetPosition(marker.Position, XyMode.Marker, marker.Handle, match.Distance);
                }
                else if (item.Status == ImportStatus.Accepted)
                {
                    item.Set(ImportStatus.Disputed, match.Failure, match.Message);
                }
            }

            // Without a search radius a far marker is not refused, but it is shown for checking.
            var distances = participants.Where(item => item.MarkerDistance.HasValue).Select(item => item.MarkerDistance.Value)
                .OrderBy(value => value).ToList();
            if (distances.Count < 3) return;
            double median = distances[distances.Count / 2];
            if (median <= 0) return;
            foreach (var item in participants.Where(item => item.MarkerDistance > median * ImportOptions.FarMarkerFactor))
                item.SetWarning("маркер далеко: " + ElevationFormat.Format(item.MarkerDistance.Value, 2) + " м (обычно " +
                                ElevationFormat.Format(median, 2) + " м) — проверьте");
        }

        /// <summary>Applies the user's decisions (by text handle) after every recalculation.</summary>
        public static void ApplyOverrides(ImportPlan plan, IReadOnlyDictionary<string, ImportOverride> overrides)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (overrides == null) return;
            foreach (var item in plan.Items)
            {
                if (!overrides.TryGetValue(item.Source.Handle, out var decision) || decision == null) continue;
                if (decision.Elevation.HasValue || decision.Position.HasValue)
                    item.Override(decision.Elevation, decision.Position, decision.PositionMode);
                if (decision.Exclude) item.Exclude();
            }
        }

        /// <summary>Pairs closer than the tolerance among accepted items and against existing points.</summary>
        public static IReadOnlyList<ImportDuplicate> FindDuplicates(ImportPlan plan, IReadOnlyList<ExistingPoint> existing)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            existing = existing ?? new ExistingPoint[0];
            var accepted = plan.Accepted.Where(item => item.Position.HasValue).ToList();
            var pairs = DuplicateFinder.Find(accepted.Select(item => item.Position.Value).ToList(),
                existing.Select(point => point.Position).ToList(), plan.Options.DuplicateTolerance);
            return pairs.Select(pair => new ImportDuplicate(
                    accepted[pair.First],
                    pair.SecondIsExisting ? null : accepted[pair.Second],
                    pair.SecondIsExisting ? existing[pair.Second] : null,
                    pair.Distance))
                .ToList();
        }
    }

    public sealed class ImportDuplicate
    {
        internal ImportDuplicate(ImportItem first, ImportItem secondNew, ExistingPoint secondExisting, double distance)
        {
            First = first;
            SecondNew = secondNew;
            SecondExisting = secondExisting;
            Distance = distance;
        }

        public ImportItem First { get; }
        public ImportItem SecondNew { get; }
        public ExistingPoint SecondExisting { get; }
        public double Distance { get; }
    }

    /// <summary>Command-line summary of one import, in the order the report is read.</summary>
    public static class ImportSummary
    {
        public static IReadOnlyList<string> Lines(ImportPlan plan, int duplicatePairs, int created, bool preview = false)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            int decimals = plan.Options.Decimals;
            var lines = new List<string>();
            int text = plan.Items.Count(item => item.Source.Kind == SourceKind.Text);
            int mtext = plan.Items.Count(item => item.Source.Kind == SourceKind.MText);
            lines.Add("Найдено текстов: " + plan.Items.Count + " (TEXT " + text + ", MTEXT " + mtext + ")");

            var accepted = plan.Accepted.ToList();
            var byMode = accepted.GroupBy(item => item.PositionMode).OrderBy(group => group.Key)
                .Select(group => XyModes.Title(group.Key) + " — " + group.Count());
            lines.Add((preview ? "Будет создано точек: " : "Принято, создано точек: ") + created + (accepted.Count > 0 ? " (XY: " + string.Join("; ", byMode) + ")" : string.Empty));
            lines.Add(Grouped("Отброшено", plan.Rejected));
            lines.Add(Grouped(preview ? "Спорных" : "Спорных, не импортировано", plan.Disputed));
            if (plan.Skipped.Any()) lines.Add(Grouped("Пропущено", plan.Skipped));
            int far = accepted.Count(item => item.Warning != null);
            if (far > 0) lines.Add("Проверьте: маркер дальше обычного — " + far + " (выделены в таблице).");
            lines.Add("Пар точек ближе " + ElevationFormat.Format(plan.Options.DuplicateTolerance, Math.Max(decimals, 2)) +
                " м: " + duplicatePairs.ToString(CultureInfo.InvariantCulture) +
                (duplicatePairs > 0 ? " — решение за вами: «Близкие точки» в панели" : string.Empty));
            return lines;
        }

        private static string Grouped(string title, IEnumerable<ImportItem> items)
        {
            var list = items.ToList();
            if (list.Count == 0) return title + ": 0";
            var parts = list.GroupBy(item => item.Reason).OrderBy(group => group.Key)
                .Select(group => ImportReasons.Title(group.Key) + " — " + group.Count());
            return title + ": " + list.Count + " (" + string.Join("; ", parts) + ")";
        }
    }
}

namespace Kzm.Core
{
    /// <summary>
    /// Номера точек при импорте: число из того же текста («т.15 145,32» → 15); иначе текст-номер
    /// («12») рядом с точкой: по маркеру — ближайший к тексту-номеру маркер должен быть маркером
    /// этой точки; по точке вставки — тексты взаимно ближайшие и не дальше 5 высот текста.
    /// Иначе — по порядку после наибольшего номера поверхности.
    /// </summary>
    public static class PointNumbering
    {
        public const double TextHeightsLimit = 5;

        public static void Assign(ImportPlan plan, int nextNumber, IReadOnlyList<MarkerCandidate> markers = null)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            var accepted = plan.Accepted.Where(item => item.Position.HasValue).ToList();
            foreach (var item in plan.Items) { item.Number = null; item.NumberFor = null; }

            foreach (var item in accepted.Where(item => item.Integers.Count == 1)) item.Number = item.Integers[0];

            var numberTexts = plan.Items.Where(item => item.Status == ImportStatus.Rejected && item.Reason == ImportReason.IntegerOnly &&
                                                       item.Integers.Count == 1).ToList();
            var waiting = accepted.Where(item => item.Number == null).ToList();
            if (numberTexts.Count > 0 && waiting.Count > 0)
            {
                if (plan.Options.Mode == XyMode.Marker && markers != null && markers.Count > 0)
                    ByMarker(numberTexts, waiting, markers);
                else
                    ByDistance(numberTexts, waiting);
            }
            foreach (var item in accepted.Where(item => item.Number == null))
                item.Number = (nextNumber++).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void ByMarker(List<ImportItem> numberTexts, List<ImportItem> waiting, IReadOnlyList<MarkerCandidate> markers)
        {
            var index = new NearestIndex(markers.Select(m => m.Position).ToList());
            var byMarker = waiting.Where(item => item.MarkerHandle != null)
                .GroupBy(item => item.MarkerHandle).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single());
            var claimed = new Dictionary<string, int>();
            var nearest = new string[numberTexts.Count];
            for (int k = 0; k < numberTexts.Count; k++)
            {
                var (marker, distance, second, secondDistance) = index.TwoNearest(numberTexts[k].Source.Anchor);
                if (marker < 0 || (second >= 0 && secondDistance - distance <= KzmSettings.DuplicateTolerance)) continue;
                nearest[k] = markers[marker].Handle;
                claimed[nearest[k]] = claimed.TryGetValue(nearest[k], out int count) ? count + 1 : 1;
            }
            for (int k = 0; k < numberTexts.Count; k++)
            {
                // Two number texts at one marker: ambiguous, numbers go in order.
                if (nearest[k] == null || claimed[nearest[k]] != 1 || !byMarker.TryGetValue(nearest[k], out var point)) continue;
                point.Number = numberTexts[k].Integers[0];
                numberTexts[k].NumberFor = point;
            }
        }

        private static void ByDistance(List<ImportItem> numberTexts, List<ImportItem> waiting)
        {
            var points = new NearestIndex(waiting.Select(item => item.Position.Value).ToList());
            var texts = new NearestIndex(numberTexts.Select(item => item.Source.Anchor).ToList());
            for (int k = 0; k < numberTexts.Count; k++)
            {
                var (point, distance, second, secondDistance) = points.TwoNearest(numberTexts[k].Source.Anchor);
                if (point < 0 || (second >= 0 && secondDistance - distance <= KzmSettings.DuplicateTolerance)) continue;
                double height = Math.Max(numberTexts[k].Source.Height, waiting[point].Source.Height);
                if (!(height > 0) || distance > TextHeightsLimit * height) continue;
                var (text, _, _, _) = texts.TwoNearest(waiting[point].Position.Value);
                if (text != k || waiting[point].Number != null) continue;
                waiting[point].Number = numberTexts[k].Integers[0];
                numberTexts[k].NumberFor = waiting[point];
            }
        }

        /// <summary>The next free number after the largest numeric one.</summary>
        public static int Next(IEnumerable<string> numbers)
        {
            int max = 0;
            foreach (var n in numbers)
                if (int.TryParse(n, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int value))
                    max = Math.Max(max, value);
            return max + 1;
        }
    }
}
