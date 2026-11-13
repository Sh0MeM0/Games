using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Kzm.AutoCAD.UI.Panel
{
    /// <summary>
    /// Выпадающий список, который не меняет значение колесом мыши (замечание 30): при прокрутке панели курсор часто
    /// попадает на список, и слой или стиль менялся сам. Колесо над закрытым списком прокручивает панель; значение
    /// меняется только в раскрытом списке (стрелка) — там колесо листает список, как обычно.
    /// </summary>
    internal sealed class NoWheelComboBox : ComboBox
    {
        private EditHook edit;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WheelGuard.WmMouseWheel && !DroppedDown && WheelGuard.ToParent(this, ref m)) return;
            base.WndProc(ref m);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // An editable list has a text box inside: the wheel over it goes to that window.
            edit?.ReleaseHandle();
            edit = null;
            if (DropDownStyle == ComboBoxStyle.DropDownList) return;
            var info = new WheelGuard.ComboBoxInfo { Size = Marshal.SizeOf(typeof(WheelGuard.ComboBoxInfo)) };
            if (WheelGuard.GetComboBoxInfo(Handle, ref info) && info.Item != IntPtr.Zero && info.Item != Handle)
            {
                edit = new EditHook(this);
                edit.AssignHandle(info.Item);
            }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            edit?.ReleaseHandle();
            edit = null;
            base.OnHandleDestroyed(e);
        }

        private sealed class EditHook : NativeWindow
        {
            private readonly NoWheelComboBox owner;

            internal EditHook(NoWheelComboBox owner) { this.owner = owner; }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WheelGuard.WmMouseWheel && !owner.DroppedDown && WheelGuard.ToParent(owner, ref m)) return;
                base.WndProc(ref m);
            }
        }
    }

    /// <summary>Числовое поле со стрелками: колесо мыши не меняет число, а прокручивает панель (замечание 30).</summary>
    internal sealed class NoWheelNumericUpDown : NumericUpDown
    {
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (e is HandledMouseEventArgs handled) handled.Handled = true;
            WheelGuard.Scroll(this, e.Delta);
        }
    }

    internal static class WheelGuard
    {
        internal const int WmMouseWheel = 0x020A;

        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect
        {
            internal int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ComboBoxInfo
        {
            internal int Size;
            internal Rect ItemRect;
            internal Rect ButtonRect;
            internal int ButtonState;
            internal IntPtr Combo;
            internal IntPtr Item;
            internal IntPtr List;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetComboBoxInfo(IntPtr window, ref ComboBoxInfo info);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        /// <summary>Hands the wheel to the window that holds the control: the panel scrolls as over empty space.</summary>
        internal static bool ToParent(Control control, ref Message m)
        {
            var parent = control.Parent;
            if (parent == null || !parent.IsHandleCreated) return false;
            m.Result = SendMessage(parent.Handle, m.Msg, m.WParam, m.LParam);
            return true;
        }

        /// <summary>Scrolls the nearest scrolling container of the control by the wheel.</summary>
        internal static void Scroll(Control control, int delta)
        {
            for (var parent = control.Parent; parent != null; parent = parent.Parent)
            {
                if (!(parent is ScrollableControl scrollable) || !scrollable.AutoScroll || !scrollable.VerticalScroll.Visible) continue;
                int lines = SystemInformation.MouseWheelScrollLines;
                if (lines <= 0) lines = 3;
                int pixels = lines * control.Font.Height * delta / 120;
                var current = scrollable.AutoScrollPosition;
                scrollable.AutoScrollPosition = new Point(-current.X, Math.Max(0, -current.Y - pixels));
                return;
            }
        }
    }
}
