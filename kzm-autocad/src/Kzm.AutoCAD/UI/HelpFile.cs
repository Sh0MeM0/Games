using System;
using System.Diagnostics;
using System.IO;

namespace Kzm.AutoCAD.UI
{
    /// <summary>
    /// Справка с картинками (Help\index.html в папке установленного модуля) открывается в браузере по умолчанию.
    /// Модуль лежит в «…\Sh0MeM0.Kzm.bundle\Contents\&lt;год&gt;\Kzm.AutoCAD.dll», справка — в «…\Sh0MeM0.Kzm.bundle\Help».
    /// </summary>
    internal static class HelpFile
    {
        internal static string Path
        {
            get
            {
                string folder = System.IO.Path.GetDirectoryName(typeof(HelpFile).Assembly.Location) ?? string.Empty;
                return System.IO.Path.GetFullPath(System.IO.Path.Combine(folder, "..", "..", "Help", "index.html"));
            }
        }

        /// <summary>Opens the help; returns null or what went wrong (for the message).</summary>
        internal static string Open()
        {
            string path;
            try
            {
                path = Path;
            }
            catch (Exception exception) when (exception is ArgumentException || exception is NotSupportedException || exception is PathTooLongException)
            {
                return "папка модуля не найдена: " + exception.Message;
            }
            if (!File.Exists(path)) return "справка не найдена (" + path + ") — переустановите модуль.";
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                return null;
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception || exception is InvalidOperationException)
            {
                return "справка не открылась (" + path + "): " + exception.Message;
            }
        }
    }
}
