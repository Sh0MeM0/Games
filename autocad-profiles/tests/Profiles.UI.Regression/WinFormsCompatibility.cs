global using DataGridView = Profiles.UI.Regression.DpiCompatibleDataGridView;
global using Form = Profiles.UI.Regression.DpiCompatibleForm;

namespace Profiles.UI.Regression
{
    public class DpiCompatibleForm : System.Windows.Forms.Form
    {
        private static readonly System.Reflection.PropertyInfo DpiProperty =
            typeof(System.Windows.Forms.Control).GetProperty("DeviceDpi");
        public new int DeviceDpi => DpiProperty == null ? 96 : (int)DpiProperty.GetValue(this);
    }
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
