using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

/// <summary>Reads editor values without calling SaveTo (which can write credentials).</summary>
internal static class SettingsEditState
{
    internal static string Capture(Control root)
    {
        var state = new StringBuilder();
        void Value(object? value)
        {
            var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            state.Append(text.Length).Append(':').Append(text);
        }
        void Visit(Control control)
        {
            if (control is SettingsPage page) Value(page.PendingEditState);
            switch (control)
            {
                case NumericUpDown number: Value(number.Value); return;
                case TextBoxBase text: Value(text.Text); return;
                case ComboBox combo: Value(combo.SelectedIndex); Value(combo.Text); return;
                case CheckBox check: Value(check.Checked); return;
                case RadioButton radio: Value(radio.Checked); return;
                case TrackBar track: Value(track.Value); return;
                case CheckedListBox list:
                    foreach (var item in list.CheckedItems) Value(item);
                    return;
                case ListBox list:
                    foreach (var item in list.Items) Value(item);
                    return;
                case DataGridView grid:
                    Value(grid.Rows.Count);
                    foreach (DataGridViewRow row in grid.Rows)
                    {
                        foreach (DataGridViewCell cell in row.Cells)
                            if (cell.Value is not System.Drawing.Image) Value(cell.Value);
                        if (row.Tag != null)
                        {
                            Value(row.Tag);
                            // Runs from a timer: a tag that cannot be serialised must not end the app
                            try { Value(JsonSerializer.Serialize(row.Tag, row.Tag.GetType())); }
                            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException) { }
                        }
                    }
                    if (grid.IsCurrentCellInEditMode && grid.EditingControl is TextBoxBase editor) Value(editor.Text);
                    return;
                case Button button:
                    // Deferred reset/forget actions expose their state through their button or swatch.
                    Value(button.Text); Value(button.Enabled); Value(button.BackColor.ToArgb());
                    return;
                case PictureBox swatch: Value(swatch.BackColor.ToArgb()); return;
                case Panel panel when panel.Controls.Count == 0: Value(panel.BackColor.ToArgb()); return;
            }
            Value(control.GetType().Name);
            foreach (Control child in control.Controls) Visit(child);
        }
        Visit(root);
        return state.ToString();
    }
}
