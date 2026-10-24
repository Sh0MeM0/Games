global using DataGridView = Profiles.UI.Regression.DpiCompatibleDataGridView;

namespace Profiles.UI.Regression
{
    // Mono's WinForms implements popup behavior but predates Control.DeviceDpi.
    // Keep this compatibility adapter in the test assembly only. Windows uses
    // its actual DPI; Mono runs menu tests at 96 DPI (not a DPI-layout test).
    public class DpiCompatibleDataGridView : System.Windows.Forms.DataGridView
    {
        private static readonly System.Reflection.PropertyInfo DpiProperty =
            typeof(System.Windows.Forms.Control).GetProperty("DeviceDpi");

        public new int DeviceDpi => DpiProperty == null ? 96 : (int)DpiProperty.GetValue(this);
    }
}
