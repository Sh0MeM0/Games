using System;
using System.Linq;
using System.Windows.Input;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.Windows;
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Kzm.AutoCAD
{
    internal static class KzmRibbon
    {
        private const string TabId = "SH0MEM0_KZM_TAB";
        private static bool started;
        private static bool pending;

        internal static void Start()
        {
            if (started) return;
            started = true;
            AcadApplication.SystemVariableChanged += VariableChanged;
            ComponentManager.ItemInitialized += ItemInitialized;
            Schedule();
        }

        internal static void Stop()
        {
            if (!started) return;
            AcadApplication.SystemVariableChanged -= VariableChanged;
            ComponentManager.ItemInitialized -= ItemInitialized;
            AcadApplication.Idle -= OnIdle;
            pending = false;
            started = false;
            var ribbon = ComponentManager.Ribbon;
            if (ribbon != null)
            {
                var tab = ribbon.Tabs.FirstOrDefault(item => item.Id == TabId);
                if (tab != null) ribbon.Tabs.Remove(tab);
            }
        }

        internal static void Restore()
        {
            Start();
            Schedule();
        }

        private static void VariableChanged(object sender, SystemVariableChangedEventArgs args)
        {
            if (string.Equals(args.Name, "WSCURRENT", StringComparison.OrdinalIgnoreCase))
                Schedule();
        }

        private static void ItemInitialized(object sender, RibbonItemEventArgs args)
        {
            // This also covers opening a drawing/ribbon after plugin startup.
            var ribbon = ComponentManager.Ribbon;
            if (ribbon == null || !ribbon.Tabs.Any(item => item.Id == TabId))
                Schedule();
        }

        private static void Schedule()
        {
            if (pending || !started) return;
            pending = true;
            AcadApplication.Idle += OnIdle;
        }

        private static void OnIdle(object sender, EventArgs args)
        {
            AcadApplication.Idle -= OnIdle;
            pending = false;
            try
            {
                var ribbon = ComponentManager.Ribbon;
                // ItemInitialized and workspace changes schedule a new check if
                // the user opens the ribbon later. Do not poll forever in Classic.
                if (ribbon == null || ribbon.Tabs.Any(item => item.Id == TabId)) return;
                var tab = new RibbonTab { Id = TabId, Title = "КЗМ" };
                tab.Panels.Add(Panel("Точки съёмки",
                    Button("Импорт\nотметок", "KZM_IMPORT"),
                    Button("Поверх-\nности", "KZM_SURFACE"),
                    Button("Близкие\nточки", "KZM_DUPLICATES")));
                tab.Panels.Add(Panel("Проверка",
                    Button("Отчёт", "KZM_REPORT"),
                    Button("Убрать\nпометки", "KZM_CLEARMARKS")));
                tab.Panels.Add(Panel("Настройки",
                    Button("Настройки\nчертежа", "KZM_SETTINGS"),
                    Button("Справка", "KZM_HELP")));
                ribbon.Tabs.Add(tab);
            }
            catch (System.Exception exception)
            {
                var document = AcadApplication.DocumentManager.MdiActiveDocument;
                document?.Editor.WriteMessage("\nКЗМ: вкладка ленты недоступна. " +
                    "Используйте верхнее меню «Картограмма» или команды KZM_… (KZM_HELP). " + exception.Message);
            }
        }

        private static RibbonPanel Panel(string title, params RibbonButton[] buttons)
        {
            var source = new RibbonPanelSource { Title = title };
            foreach (var button in buttons) source.Items.Add(button);
            return new RibbonPanel { Source = source };
        }

        private static RibbonButton Button(string text, string command)
        {
            return new RibbonButton
            {
                Id = "SH0MEM0_" + command,
                Text = text,
                ShowText = true,
                Size = RibbonItemSize.Large,
                Orientation = System.Windows.Controls.Orientation.Vertical,
                CommandParameter = command + " ",
                CommandHandler = new KzmCommandHandler()
            };
        }

        private sealed class KzmCommandHandler : ICommand
        {
            // CanExecuteChanged is never raised, so a button disabled while no
            // drawing was open would stay disabled. Execute checks the document.
            public bool CanExecute(object parameter) => true;

            public event EventHandler CanExecuteChanged
            {
                add { }
                remove { }
            }

            public void Execute(object parameter)
            {
                var document = AcadApplication.DocumentManager.MdiActiveDocument;
                var item = parameter as RibbonButton;
                var command = item?.CommandParameter as string ?? parameter as string;
                if (document == null || string.IsNullOrEmpty(command)) return;
                // Like the menu macros (^C^C), cancel an active command first;
                // otherwise the command name is typed into its prompt.
                document.SendStringToExecute("\x03\x03" + command, true, false, true);
            }
        }
    }
}
