using System;
using System.IO;
using System.Text;
using Kzm.Core;

namespace Kzm.AutoCAD.Drawing
{
    /// <summary>
    /// Шаблон по умолчанию (задание п. 8): файл %APPDATA%\Sh0MeM0\Kzm\defaults.txt.
    /// Из него берутся настройки нового чертежа и параметры новой картограммы.
    /// </summary>
    internal static class KzmDefaults
    {
        private static DateTime loadedStamp = DateTime.MinValue;
        private static (KzmSettings Settings, CartogramInfo Template)? cached;

        internal static string FilePath =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sh0MeM0", "Kzm", "defaults.txt");

        internal static bool Exists => File.Exists(FilePath);

        /// <summary>Null when there is no template; a damaged file is reported in <paramref name="problem"/>.</summary>
        internal static (KzmSettings Settings, CartogramInfo Template)? Load(out string problem)
        {
            problem = null;
            try
            {
                if (!File.Exists(FilePath)) { cached = null; return null; }
                var stamp = File.GetLastWriteTimeUtc(FilePath);
                if (cached.HasValue && stamp == loadedStamp) return Copy(cached.Value);
                var value = DefaultsCodec.Decode(File.ReadAllText(FilePath, Encoding.UTF8));
                cached = value;
                loadedStamp = stamp;
                return Copy(value);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is KzmException)
            {
                problem = "шаблон по умолчанию (" + FilePath + ") не читается: " + exception.Message;
                return null;
            }
        }

        internal static (KzmSettings Settings, CartogramInfo Template)? Load() => Load(out _);

        private static (KzmSettings, CartogramInfo) Copy((KzmSettings Settings, CartogramInfo Template) value) => (value.Settings.Clone(), value.Template.Clone());

        internal static void Save(KzmSettings settings, CartogramInfo template)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, DefaultsCodec.Encode(settings, template ?? new CartogramInfo()), new UTF8Encoding(false));
            cached = null;
        }

        internal static void Reset()
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
            cached = null;
        }
    }
}
