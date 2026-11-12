using System.Runtime.CompilerServices;
using Autodesk.AutoCAD.Runtime;
using AcadApplication = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(Kzm.AutoCAD.KzmExtension))]
[assembly: CommandClass(typeof(Kzm.AutoCAD.Commands.KzmCommands))]

namespace Kzm.AutoCAD
{
    public sealed class KzmExtension : IExtensionApplication
    {
        public void Initialize()
        {
            try { KzmClassicMenu.Start(); }
            catch (System.Exception exception) { StartupError("верхнее меню", exception); }
            try { StartRibbon(); }
            catch (System.Exception exception) { StartupError("ленту", exception); }
        }

        public void Terminate()
        {
            try { KzmClassicMenu.Stop(); } catch (System.Exception) { }
            try { KzmRibbon.Stop(); } catch (System.Exception) { }
        }

        // Keeps AdWindows out of Initialize's JIT scope: a missing ribbon must not
        // prevent the menu and the commands from loading.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void StartRibbon() => KzmRibbon.Start();

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static void RestoreRibbon() => KzmRibbon.Restore();

        private static void StartupError(string component, System.Exception exception)
        {
            var document = AcadApplication.DocumentManager.MdiActiveDocument;
            document?.Editor.WriteMessage("\nКЗМ: не удалось включить " + component + ": " + exception.Message +
                ". Выполните KZM_MENU для восстановления меню; список команд — KZM_HELP.");
        }
    }
}
