using System;
using System.Collections.Generic;
using System.Linq;

namespace Kzm.Core
{
    /// <summary>
    /// Несколько частей границы (замечание 31) хранятся в прежнем поле записи: ручки полилиний через «;».
    /// Запись с одной ручкой читается и пишется так же, как в прежних версиях.
    /// </summary>
    public static class BoundaryList
    {
        public const char Separator = ';';

        public static List<string> Split(string text) =>
            string.IsNullOrWhiteSpace(text)
                ? new List<string>()
                : text.Split(Separator).Select(h => h.Trim()).Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        public static string Join(IEnumerable<string> handles)
        {
            var list = (handles ?? Enumerable.Empty<string>()).Where(h => !string.IsNullOrWhiteSpace(h)).Select(h => h.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return list.Count == 0 ? null : string.Join(Separator.ToString(), list);
        }
    }
}
