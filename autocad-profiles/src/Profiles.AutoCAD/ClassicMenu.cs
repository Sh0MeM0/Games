using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Profiles.AutoCAD
{
    /// <summary>
    /// Keeps this bundle's partial CUI menu available in classic workspaces.
    /// The CUIX is also an independent autoloader component, so its menu does
    /// not depend on AdWindows or the managed ribbon being available.
    /// </summary>
    internal static class ProfilesClassicMenu
    {
        internal const string MenuGroupName = "SH0MEM0_PROFILES";
        private const string MenuCaption = "Профиля";
        private const int MaximumAttempts = 5;
        private static bool started;
        private static bool pending;
        private static int attempts;
        private static DateTime retryAfterUtc;

        internal static void Start()
        {
            if (started) return;
            started = true;
            AcadApplication.SystemVariableChanged += VariableChanged;
            AcadApplication.DocumentManager.DocumentCreated += DocumentChanged;
            AcadApplication.DocumentManager.DocumentActivated += DocumentChanged;
            Schedule();
        }

        internal static void Stop()
        {
            if (!started) return;
            AcadApplication.SystemVariableChanged -= VariableChanged;
            AcadApplication.DocumentManager.DocumentCreated -= DocumentChanged;
            AcadApplication.DocumentManager.DocumentActivated -= DocumentChanged;
            CancelPending();
            started = false;
            // Do not unload a menu or rewrite a workspace while AutoCAD exits.
        }

        internal static void Restore()
        {
            Start();
            AcadApplication.SetSystemVariable("MENUBAR", 1);
            CancelPending();
            try
            {
                EnsureMenu();
                Report("Меню «Профиля» восстановлено в верхней строке меню.");
            }
            catch (System.Exception exception)
            {
                Report("Меню пока не восстановлено: " + exception.Message +
                    " Повторная попытка будет выполнена после завершения команды.");
                Schedule();
            }
        }

        private static void VariableChanged(object sender, SystemVariableChangedEventArgs args)
        {
            if (string.Equals(args.Name, "WSCURRENT", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args.Name, "MENUNAME", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(args.Name, "MENUBAR", StringComparison.OrdinalIgnoreCase))
                Schedule();
        }

        private static void DocumentChanged(object sender, DocumentCollectionEventArgs args) => Schedule();

        private static void Schedule()
        {
            if (!started || pending) return;
            attempts = 0;
            retryAfterUtc = DateTime.MinValue;
            pending = true;
            AcadApplication.Idle += OnIdle;
        }

        private static void CancelPending()
        {
            if (pending) AcadApplication.Idle -= OnIdle;
            pending = false;
        }

        private static void OnIdle(object sender, EventArgs args)
        {
            if (DateTime.UtcNow < retryAfterUtc) return;
            var document = AcadApplication.DocumentManager.MdiActiveDocument;
            if (document != null && !document.Editor.IsQuiescent) return;
            attempts++;
            try
            {
                EnsureMenu();
                CancelPending();
            }
            catch (System.Exception exception)
            {
                if (attempts < MaximumAttempts)
                {
                    retryAfterUtc = DateTime.UtcNow.AddSeconds(2);
                    return;
                }
                CancelPending();
                Report("Не удалось подключить верхнее меню: " + exception.Message +
                    " Для повторной попытки выполните PROFILI_MENU. " +
                    "Построение также доступно через PROFILI_LONG и PROFILI_CROSS.");
            }
        }

        private static void EnsureMenu()
        {
            // Late-bound ActiveX is part of every supported Windows AutoCAD.
            // It avoids redistributing or version-binding an Interop assembly.
            dynamic application = AcadApplication.AcadApplication;
            dynamic group = FindOwnGroup(application);
            if (group == null)
            {
                string menuFile = MenuFilePath();
                if (!File.Exists(menuFile))
                    throw new FileNotFoundException("Не найден Profiles.cuix. Переустановите модуль «Профиля».", menuFile);
                AcadApplication.LoadPartialMenu(menuFile);
                group = FindOwnGroup(application);
                if (group == null)
                    throw new InvalidOperationException("AutoCAD ещё не подключил файл меню «Профиля».");
            }

            // Look only in our own partial group. Never add a popup to, modify,
            // save, or reload the user's main CUI or another extension's menu.
            dynamic menus = group.Menus;
            int count = Convert.ToInt32(menus.Count, CultureInfo.InvariantCulture);
            for (int index = 0; index < count; index++)
            {
                dynamic menu = menus.Item(index);
                string caption = Convert.ToString(menu.Name, CultureInfo.InvariantCulture) ?? string.Empty;
                if (!string.Equals(caption.Replace("&", string.Empty).Trim(), MenuCaption, StringComparison.Ordinal))
                    continue;
                if (!Convert.ToBoolean(menu.OnMenuBar, CultureInfo.InvariantCulture))
                {
                    int insertion = Convert.ToInt32(application.MenuBar.Count, CultureInfo.InvariantCulture);
                    menu.InsertInMenuBar(insertion);
                    if (!Convert.ToBoolean(menu.OnMenuBar, CultureInfo.InvariantCulture))
                        throw new InvalidOperationException("AutoCAD ещё не добавил «Профиля» в строку меню.");
                }
                return;
            }
            throw new InvalidOperationException("В файле меню отсутствует раздел «Профиля». Переустановите модуль.");
        }

        private static object FindOwnGroup(dynamic application)
        {
            dynamic groups = application.MenuGroups;
            int count = Convert.ToInt32(groups.Count, CultureInfo.InvariantCulture);
            for (int index = 0; index < count; index++)
            {
                dynamic group = groups.Item(index);
                string name = Convert.ToString(group.Name, CultureInfo.InvariantCulture);
                if (string.Equals(name, MenuGroupName, StringComparison.OrdinalIgnoreCase))
                    return group;
            }
            return null;
        }

        private static string MenuFilePath()
        {
            // .../Contents/<AutoCAD year>/Profiles.AutoCAD.dll -> Contents/Profiles.cuix
            string assemblyFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            DirectoryInfo contents = Directory.GetParent(assemblyFolder);
            if (contents == null) throw new InvalidOperationException("Не удалось найти папку установленного модуля.");
            return Path.Combine(contents.FullName, "Profiles.cuix");
        }

        private static void Report(string message)
        {
            var document = AcadApplication.DocumentManager.MdiActiveDocument;
            document?.Editor.WriteMessage("\nПрофиля: " + message);
        }
    }
}
