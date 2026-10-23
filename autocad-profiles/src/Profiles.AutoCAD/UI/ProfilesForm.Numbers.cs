using System;
using System.Globalization;
using System.Windows.Forms;
using Profiles.Core;

namespace Profiles.AutoCAD
{
    public sealed partial class ProfilesForm
    {
        private sealed class NumberState
        {
            internal double? Value;
            internal bool Edited;
        }

        private object longEditOriginal;
        private bool longEditChanged;
        private bool longEditSetup;
        private int longEditRow = -1;
        private int longEditColumn = -1;
        private TextBox longEditingControl;

        private void DisplayNumber(TextBox input, double? value)
        {
            bool old = loading;
            loading = true;
            try
            {
                input.Tag = new NumberState { Value = value };
                string format = input == metres || input == textHeight || input == textWidth ? "0.###############" : "F2";
                input.Text = value.HasValue ? value.Value.ToString(format, CultureInfo.InvariantCulture) : "";
            }
            finally { loading = old; }
        }

        private double? ReadOptionalNumber(TextBox input, string field)
        {
            if (input.Tag is NumberState state && !state.Edited) return state.Value;
            return NumberParser.ParseOptional(input.Text, field);
        }

        private double ReadRequiredNumber(TextBox input, string field)
        {
            double? value = ReadOptionalNumber(input, field);
            return value ?? NumberParser.ParseRequired(input.Text, field);
        }

        private void MarkNumberEdited(TextBox input)
        {
            if (loading || saving) return;
            if (input.Tag is NumberState state) state.Edited = true;
            if (input == projectBottom) longitudinalProjectBottomLinked = false;
            if (input == sectionWater) longitudinalWaterLinked = false;
            if (input == centerOffset) crossGrid.SetProjectAnchor(null, null);
        }

        private void InitializeLongitudinalPrecision()
        {
            foreach (DataGridViewColumn column in longGrid.Columns)
                if (column.Name != "PipeDescription" && column.Name != "Foundation" && column.Name != "PointName")
                    column.ValueType = typeof(object);
            longGrid.CellBeginEdit += (s, e) =>
            {
                // DataGridView reuses the editor and initializes its text before
                // EditingControlShowing. That setup is not a user edit.
                if (longEditingControl != null) longEditingControl.TextChanged -= LongEditorChanged;
                longEditRow = e.RowIndex;
                longEditColumn = e.ColumnIndex;
                longEditOriginal = longGrid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value;
                longEditChanged = false;
            };
            longGrid.EditingControlShowing += (s, e) =>
            {
                if (longEditingControl != null) longEditingControl.TextChanged -= LongEditorChanged;
                longEditingControl = e.Control as TextBox;
                if (longEditingControl == null) return;
                longEditSetup = true;
                try
                {
                    if (longEditOriginal is double number)
                        longEditingControl.Text = number.ToString("0.##############################", CultureInfo.InvariantCulture);
                    else if (longEditOriginal == null) longEditingControl.Text = "";
                }
                finally { longEditSetup = false; }
                longEditChanged = false;
                longEditingControl.TextChanged += LongEditorChanged;
            };
            longGrid.CellParsing += (s, e) =>
            {
                if (e.RowIndex == longEditRow && e.ColumnIndex == longEditColumn && !longEditChanged)
                {
                    // Preserve the raw value before CellValueChanged/autosave can
                    // observe a rounded display value. Later parsers honor this flag.
                    e.Value = longEditOriginal;
                    e.ParsingApplied = true;
                }
            };
            longGrid.CellEndEdit += (s, e) =>
            {
                if (longEditingControl != null) longEditingControl.TextChanged -= LongEditorChanged;
                longEditingControl = null;
                longEditRow = longEditColumn = -1;
            };
        }

        private void LongEditorChanged(object sender, EventArgs e)
        {
            if (!longEditSetup) longEditChanged = true;
        }
    }
}
