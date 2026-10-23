namespace DaniOrganizer;

internal sealed class ExclusionDialog : Form
{
    private readonly ListBox paths = new() { Dock = DockStyle.Fill, SelectionMode = SelectionMode.MultiExtended, HorizontalScrollbar = true, BackColor = Color.FromArgb(23, 32, 44), ForeColor = Color.WhiteSmoke, BorderStyle = BorderStyle.None };
    public IReadOnlyList<string> ExcludedPaths => paths.Items.Cast<string>().ToArray();
    public ExclusionDialog(IEnumerable<string> existing)
    {
        Text = "Archivos y carpetas que NO se organizarán"; Size = new(850, 490); MinimumSize = new(700, 400); StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(14, 20, 29); ForeColor = Color.WhiteSmoke; Font = new("Segoe UI", 10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(18), ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new(SizeType.Absolute, 55)); layout.RowStyles.Add(new(SizeType.Absolute, 45)); layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 52)); Controls.Add(layout);
        layout.Controls.Add(new Label { Text = "Añade archivos o carpetas que quieras dejar intactos.\nExcluir una carpeta también excluye todos sus archivos y subcarpetas.", Dock = DockStyle.Fill }, 0, 0);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var files = MakeButton("Añadir archivos…"); var folder = MakeButton("Añadir carpeta…"); var remove = MakeButton("Quitar de la lista");
        toolbar.Controls.AddRange(new Control[] { files, folder, remove }); layout.Controls.Add(toolbar, 0, 1); layout.Controls.Add(paths, 0, 2);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new(0, 12, 0, 0) };
        var save = MakeButton("Guardar exclusiones"); var cancel = MakeButton("Cancelar"); save.DialogResult = DialogResult.OK; cancel.DialogResult = DialogResult.Cancel;
        footer.Controls.AddRange(new Control[] { save, cancel }); layout.Controls.Add(footer, 0, 3); AcceptButton = save; CancelButton = cancel;
        foreach (string path in existing) Add(path);
        files.Click += (_, _) => { using var picker = new OpenFileDialog { Multiselect = true, Title = "Elige los archivos que no quieres organizar", Filter = "Todos los archivos|*.*" }; if (picker.ShowDialog(this) == DialogResult.OK) foreach (string path in picker.FileNames) Add(path); };
        folder.Click += (_, _) => { using var picker = new FolderBrowserDialog { Description = "Elige una carpeta que no quieres organizar", UseDescriptionForTitle = true }; if (picker.ShowDialog(this) == DialogResult.OK) Add(picker.SelectedPath); };
        remove.Click += (_, _) => { foreach (var item in paths.SelectedItems.Cast<string>().ToArray()) paths.Items.Remove(item); };
    }
    private void Add(string path) { path = Path.GetFullPath(path); if (!paths.Items.Cast<string>().Contains(path, StringComparer.OrdinalIgnoreCase)) paths.Items.Add(path); }
    private static Button MakeButton(string text) { var button = new Button { Text = text, Width = 175, Height = 32, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(29, 41, 55), ForeColor = Color.WhiteSmoke }; button.FlatAppearance.BorderSize = 0; return button; }
}
