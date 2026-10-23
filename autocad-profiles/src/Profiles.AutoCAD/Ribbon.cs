using System;
using System.Linq;
using System.Windows.Input;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.Windows;
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Profiles.AutoCAD
{
    internal static class ProfilesRibbon
    {
        private const string TabId = "SH0MEM0_PROFILI_TAB";
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
                var panelSource = new RibbonPanelSource { Title = "Построение профилей" };
                panelSource.Items.Add(Button("Продольный\nпрофиль", "PROFILI_LONG"));
                panelSource.Items.Add(Button("Поперечные\nпрофили", "PROFILI_CROSS"));
                var tab = new RibbonTab { Id = TabId, Title = "Профиля" };
                tab.Panels.Add(new RibbonPanel { Source = panelSource });
                ribbon.Tabs.Add(tab);
            }
            catch (System.Exception exception)
            {
                var document = AcadApplication.DocumentManager.MdiActiveDocument;
                document?.Editor.WriteMessage("\nПрофиля: вкладка ленты недоступна. " +
                    "Используйте PROFILI, PROFILI_LONG или PROFILI_CROSS. " + exception.Message);
            }
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
                CommandHandler = new ProfileCommandHandler()
            };
        }

        private sealed class ProfileCommandHandler : ICommand
        {
            public bool CanExecute(object parameter) =>
                AcadApplication.DocumentManager.MdiActiveDocument != null;

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
                document.SendStringToExecute(command, true, false, true);
            }
        }
    }
}
