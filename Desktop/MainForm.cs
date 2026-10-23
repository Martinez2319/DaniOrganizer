using System.Diagnostics;
using DaniOrganizer.Core;

namespace DaniOrganizer;

public sealed class MainForm : Form
{
    private static readonly Color Background = Color.FromArgb(14, 20, 29), Surface = Color.FromArgb(23, 32, 44), Ink = Color.FromArgb(235, 241, 247), Muted = Color.FromArgb(153, 171, 192), Accent = Color.FromArgb(97, 226, 186);
    private readonly TextBox source = new(), destination = new();
    private readonly RulePicker grouping = new();
    private readonly CheckBox recursive = new() { Text = "Incluir subcarpetas", AutoSize = true, Checked = true };
    private readonly Button previewButton = Button("Vista previa"), duplicateButton = Button("Buscar duplicados"), applyButton = Button("Organizar archivos", true), cancelButton = Button("Cancelar"), undoButton = Button("Deshacer selección");
    private readonly Button sourceButton = Button("Elegir carpeta"), destinationButton = Button("Cambiar destino");
    private readonly Button exclusionsButton = Button("Exclusiones (0)"), selectAllButton = Button("Marcar todos"), clearSelectionButton = Button("Desmarcar todos");
    private readonly List<string> excludedPaths = new();
    private readonly string exclusionsFile;
    private bool fillingPlan;
    private readonly Label status = new(), summary = new();
    private readonly DataGridView planGrid = Grid(), duplicateGrid = Grid(), historyGrid = Grid();
    private readonly WorkspaceTabs tabs = new();
    private readonly List<Operation> operations = new();
    private readonly bool demo;
    private readonly string journalDirectory;
    private readonly bool testMode;
    private Plan? plan;
    private CancellationTokenSource? cancellation;
    private bool busy;

    public MainForm(bool demo = false, bool testMode = false, string? journalDirectory = null)
    {
        this.demo = demo;
        this.testMode = testMode;
        this.journalDirectory = journalDirectory ?? Organizer.DefaultJournal;
        exclusionsFile = Path.Combine(Path.GetDirectoryName(this.journalDirectory)!, "exclusions.json");
        if (!demo)
        {
            try { excludedPaths.AddRange(ExclusionStore.Load(exclusionsFile)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            { MessageBox.Show("No se pudieron cargar las exclusiones. Revísalas antes de organizar.\n" + ex.Message, "DaniOrganizer"); }
        }
        Text = "DaniOrganizer · Organizador de archivos";
        Size = new(1280, 830); MinimumSize = new(1080, 720); StartPosition = FormStartPosition.CenterScreen;
        BackColor = Background; ForeColor = Ink; Font = new("Segoe UI", 10); AutoScaleMode = AutoScaleMode.Dpi;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        layout.ColumnStyles.Add(new(SizeType.Absolute, 220)); layout.ColumnStyles.Add(new(SizeType.Percent, 100)); Controls.Add(layout);
        layout.Controls.Add(Sidebar(), 0, 0);
        var main = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(28, 22, 28, 18), ColumnCount = 1, RowCount = 6 };
        main.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (int height in new[] { 88, 190, 55, 38 }) main.RowStyles.Add(new(SizeType.Absolute, height));
        main.RowStyles.Add(new(SizeType.Percent, 100)); main.RowStyles.Add(new(SizeType.Absolute, 65)); layout.Controls.Add(main, 1, 0);
        var heading = new Panel { Dock = DockStyle.Fill };
        heading.Controls.Add(Label("Cada archivo, en su lugar.", 25, Ink, new(0, 0), true));
        heading.Controls.Add(Label("Ordena tu espacio con una vista previa y un camino de vuelta.", 10, Muted, new(2, 53)));
        main.Controls.Add(heading, 0, 0);
        main.Controls.Add(FolderCard(), 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new(0, 9, 0, 0), WrapContents = false };
        actions.Controls.AddRange(new Control[] { previewButton, duplicateButton, applyButton, cancelButton }); main.Controls.Add(actions, 0, 2);
        summary.Dock = DockStyle.Fill; summary.TextAlign = ContentAlignment.MiddleLeft; summary.ForeColor = Muted;
        summary.Text = "Elige una carpeta para empezar. La vista previa no mueve archivos."; main.Controls.Add(summary, 0, 3);
        ConfigureGrid(planGrid, ("Nombre", 160), ("Categoría", 115), ("Tamaño", 88), ("Carpeta actual", 230), ("Destino propuesto", 300));
        foreach (DataGridViewColumn column in planGrid.Columns) column.ReadOnly = true;
        planGrid.ReadOnly = false;
        planGrid.Columns.Insert(0, new DataGridViewCheckBoxColumn { Name = "Include", HeaderText = "Sí", Width = 48, MinimumWidth = 48, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, ReadOnly = false, SortMode = DataGridViewColumnSortMode.NotSortable });
        planGrid.CurrentCellDirtyStateChanged += (_, _) => { if (planGrid.IsCurrentCellDirty) planGrid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        planGrid.CellValueChanged += (_, e) => { if (e.ColumnIndex == 0 && !fillingPlan) UpdateSelection(); };
        ConfigureGrid(duplicateGrid, ("Grupo", 65), ("Archivo", 180), ("Tamaño", 100), ("Ubicación", 500));
        ConfigureGrid(historyGrid, ("Fecha", 160), ("Movimientos pendientes de deshacer", 210), ("Estado", 210));
        tabs.Dock = DockStyle.Fill;
        var planPage = new Panel { Dock = DockStyle.Fill };
        var selectionBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 42, Padding = new(0, 3, 0, 0) };
        selectAllButton.Width = 140; clearSelectionButton.Width = 155;
        selectionBar.Controls.AddRange(new Control[] { selectAllButton, clearSelectionButton, new Label { Text = "Marca solo lo que quieres mover", AutoSize = true, ForeColor = Muted, Margin = new(10, 8, 0, 0) } });
        planPage.Controls.Add(planGrid); planPage.Controls.Add(selectionBar);
        AddTab("Vista previa", planPage); AddTab("Duplicados", duplicateGrid); AddTab("Historial", historyGrid); main.Controls.Add(tabs, 0, 4);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new(0, 14, 0, 0) };
        footer.ColumnStyles.Add(new(SizeType.Percent, 100)); footer.ColumnStyles.Add(new(SizeType.Absolute, 190));
        status.Dock = DockStyle.Fill; status.ForeColor = Muted; status.Text = "Listo · Todo se procesa en tu equipo.";
        footer.Controls.Add(status, 0, 0); footer.Controls.Add(undoButton, 1, 0); main.Controls.Add(footer, 0, 5);
        sourceButton.Click += (_, _) => PickSource(); destinationButton.Click += (_, _) => PickDestination();
        exclusionsButton.Click += (_, _) => EditExclusions();
        selectAllButton.Click += (_, _) => SetSelection(true); clearSelectionButton.Click += (_, _) => SetSelection(false);
        source.TextChanged += (_, _) => InvalidatePlan(); destination.TextChanged += (_, _) => InvalidatePlan();
        grouping.SelectedIndexChanged += (_, _) => InvalidatePlan(); recursive.CheckedChanged += (_, _) => InvalidatePlan();
        previewButton.Click += async (_, _) => await Preview(); duplicateButton.Click += async (_, _) => await FindDuplicates();
        applyButton.Click += async (_, _) => await Apply(); undoButton.Click += async (_, _) => await Undo();
        cancelButton.Click += (_, _) => { cancellation?.Cancel(); status.Text = "Cancelando… se conservará el registro de movimientos completados."; };
        planGrid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex != 0 && planGrid.Rows[e.RowIndex].Tag is PlannedMove move) Reveal(move.Source); };
        duplicateGrid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && duplicateGrid.Rows[e.RowIndex].Tag is string path) Reveal(path); };
        historyGrid.SelectionChanged += (_, _) => UpdateButtons();
        AllowDrop = true; DragEnter += (_, e) => { if (!busy && e.Data?.GetData(DataFormats.FileDrop) is string[] paths && paths.Length == 1 && Directory.Exists(paths[0])) e.Effect = DragDropEffects.Copy; };
        DragDrop += (_, e) => { if (!busy && e.Data?.GetData(DataFormats.FileDrop) is string[] paths && paths.Length == 1 && Directory.Exists(paths[0])) SetSource(paths[0]); };
        FormClosing += (_, e) => { if (busy) { e.Cancel = true; cancellation?.Cancel(); status.Text = "Terminando la operación. Cierra la ventana cuando finalice."; } };
        if (demo) Demo(); else RefreshHistory();
        UpdateButtons();
    }

    private Control Sidebar()
    {
        var side = new Panel { Dock = DockStyle.Fill, BackColor = Surface, Padding = new(22) };
        side.Controls.Add(Label("D / O", 28, Accent, new(23, 30), true));
        side.Controls.Add(Label("DaniOrganizer", 17, Ink, new(23, 91), true));
        side.Controls.Add(Label("TU ESPACIO, EN ORDEN", 8, Muted, new(25, 130)));
        side.Controls.Add(Label("01   Explora", 12, Ink, new(25, 210), true));
        side.Controls.Add(Label("Elige una carpeta\no arrástrala aquí.", 10, Muted, new(25, 243)));
        side.Controls.Add(Label("02   Revisa", 12, Ink, new(25, 320), true));
        side.Controls.Add(Label("Mira cada destino\nantes de organizar.", 10, Muted, new(25, 353)));
        side.Controls.Add(Label("03   Organiza", 12, Ink, new(25, 430), true));
        side.Controls.Add(Label("Vuelve atrás desde\nel historial si lo necesitas.", 10, Muted, new(25, 463)));
        var badge = Label("LOCAL · SIN NUBE\nv0.2.0 / Windows", 9, Accent, new(25, 650)); badge.AutoSize = false; badge.Height = 48; badge.Dock = DockStyle.Bottom; side.Controls.Add(badge);
        return side;
    }
    private Control FolderCard()
    {
        var card = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Surface, Padding = new(14), ColumnCount = 3, RowCount = 4 };
        card.ColumnStyles.Add(new(SizeType.Absolute, 74)); card.ColumnStyles.Add(new(SizeType.Percent, 100)); card.ColumnStyles.Add(new(SizeType.Absolute, 155));
        card.RowStyles.Add(new(SizeType.Absolute, 40)); card.RowStyles.Add(new(SizeType.Absolute, 40)); card.RowStyles.Add(new(SizeType.Absolute, 40)); card.RowStyles.Add(new(SizeType.Percent, 100));
        foreach (var box in new[] { source, destination }) { box.Dock = DockStyle.Fill; box.BackColor = Background; box.ForeColor = Ink; box.BorderStyle = BorderStyle.FixedSingle; box.Margin = new(3, 7, 10, 3); }
        source.PlaceholderText = "Selecciona o arrastra una carpeta…"; destination.PlaceholderText = "Se propone _Organizado dentro del origen";
        card.Controls.Add(new Label { Text = "Origen", AutoSize = true, Margin = new(0, 10, 0, 0), ForeColor = Muted }, 0, 0); card.Controls.Add(source, 1, 0); card.Controls.Add(sourceButton, 2, 0);
        card.Controls.Add(new Label { Text = "Destino", AutoSize = true, Margin = new(0, 10, 0, 0), ForeColor = Muted }, 0, 1); card.Controls.Add(destination, 1, 1); card.Controls.Add(destinationButton, 2, 1);
        card.Controls.Add(new Label { Text = "Agrupar", AutoSize = true, Margin = new(0, 10, 0, 0), ForeColor = Muted }, 0, 2);
        var choices = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new(0, 5, 0, 0) };
        grouping.Width = 300; grouping.Height = 28;
        recursive.Margin = new(18, 5, 0, 0); choices.Controls.AddRange(new Control[] { grouping, recursive }); card.Controls.Add(choices, 1, 2); card.SetColumnSpan(choices, 2);
        exclusionsButton.Width = 170; exclusionsButton.Height = 30; exclusionsButton.Text = $"Exclusiones ({excludedPaths.Count})";
        var exclusionsRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        exclusionsRow.Controls.Add(exclusionsButton); exclusionsRow.Controls.Add(new Label { Text = "Archivos y carpetas que deben quedarse intactos", AutoSize = true, ForeColor = Muted, Margin = new(10, 6, 0, 0) });
        card.Controls.Add(exclusionsRow, 1, 3); card.SetColumnSpan(exclusionsRow, 2);
        return card;
    }
    private static Label Label(string text, float size, Color color, Point location, bool bold = false) => new() { Text = text, AutoSize = true, Location = location, ForeColor = color, Font = new("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular) };
    private static Button Button(string text, bool primary = false)
    {
        var button = new Button { Text = text, AutoSize = false, Width = text.Length > 17 ? 175 : 145, Height = 35, FlatStyle = FlatStyle.Flat, BackColor = primary ? Accent : Surface, ForeColor = primary ? Background : Ink, Margin = new(0, 0, 9, 0), Cursor = Cursors.Hand };
        button.FlatAppearance.BorderSize = 0;
        button.Paint += (_, e) => { if (!button.Enabled) TextRenderer.DrawText(e.Graphics, button.Text, button.Font, button.ClientRectangle, Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); };
        return button;
    }
    private static DataGridView Grid() => new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, AllowUserToResizeRows = false, RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Surface, BorderStyle = BorderStyle.None, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal, GridColor = Color.FromArgb(35, 46, 61), EnableHeadersVisualStyles = false, RowTemplate = { Height = 35 } };
    private static void ConfigureGrid(DataGridView grid, params (string Name, int Width)[] columns)
    {
        grid.DefaultCellStyle = new() { BackColor = Surface, ForeColor = Ink, SelectionBackColor = Color.FromArgb(37, 72, 70), SelectionForeColor = Ink, Padding = new(6, 0, 6, 0) };
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(20, 29, 40);
        grid.ColumnHeadersDefaultCellStyle = new() { BackColor = Color.FromArgb(29, 41, 55), ForeColor = Muted, Padding = new(6), Font = new("Segoe UI", 9, FontStyle.Bold) };
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = grid.ColumnHeadersDefaultCellStyle.BackColor;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Muted;
        grid.ColumnHeadersHeight = 38; grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        foreach (var column in columns) grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = column.Name, FillWeight = column.Width, MinimumWidth = 60, SortMode = DataGridViewColumnSortMode.NotSortable });
    }
    private void AddTab(string name, Control content) => tabs.Add(name, content);
    private void PickSource() { using var dialog = new FolderBrowserDialog { Description = "Elige la carpeta que quieres organizar", UseDescriptionForTitle = true }; if (dialog.ShowDialog(this) == DialogResult.OK) SetSource(dialog.SelectedPath); }
    private void PickDestination() { using var dialog = new FolderBrowserDialog { Description = "Elige dónde guardar los archivos organizados", UseDescriptionForTitle = true }; if (dialog.ShowDialog(this) == DialogResult.OK) destination.Text = dialog.SelectedPath; }
    private void SetSource(string path) { source.Text = path; destination.Text = Path.Combine(path, "_Organizado"); }
    private Options ReadOptions()
    {
        if (string.IsNullOrWhiteSpace(source.Text) || string.IsNullOrWhiteSpace(destination.Text)) throw new ArgumentException("Selecciona el origen y el destino.");
        return new(source.Text.Trim(), destination.Text.Trim(), (Grouping)grouping.SelectedIndex, recursive.Checked, excludedPaths.ToArray());
    }
    private void EditExclusions()
    {
        using var dialog = new ExclusionDialog(excludedPaths);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { SaveExclusions(dialog.ExcludedPaths); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show(this, "No se pudieron guardar las exclusiones. No se cambiaron las reglas.\n" + ex.Message, "DaniOrganizer"); }
    }
    private void SaveExclusions(IEnumerable<string> paths)
    {
        var changed = paths.ToArray(); ExclusionStore.Save(exclusionsFile, changed);
        excludedPaths.Clear(); excludedPaths.AddRange(changed); exclusionsButton.Text = $"Exclusiones ({excludedPaths.Count})";
        InvalidatePlan(); status.Text = "Exclusiones guardadas. Genera una nueva vista previa.";
    }
    private IEnumerable<PlannedMove> CheckedMoves() => planGrid.Rows.Cast<DataGridViewRow>()
        .Where(row => row.Cells[0].Value is true && row.Tag is PlannedMove).Select(row => (PlannedMove)row.Tag!);
    private void SetSelection(bool selected)
    {
        fillingPlan = true;
        foreach (DataGridViewRow row in planGrid.Rows) row.Cells[0].Value = selected;
        fillingPlan = false; UpdateSelection();
    }
    private void UpdateSelection()
    {
        if (demo || plan == null) return;
        var selected = CheckedMoves().ToArray();
        summary.Text = $"{selected.Length:N0} de {plan.Moves.Count:N0} seleccionados · {SizeText(selected.Sum(move => move.Length))} · {plan.Warnings.Count} avisos";
        applyButton.Text = $"Organizar ({selected.Length:N0})"; UpdateButtons();
    }
    private void InvalidatePlan() { plan = null; planGrid.Rows.Clear(); duplicateGrid.Rows.Clear(); applyButton.Text = "Organizar archivos"; summary.Text = "Genera una vista previa para revisar cada movimiento."; UpdateButtons(); }
    private void UpdateButtons()
    {
        foreach (var control in new Control[] { source, destination, grouping, recursive, sourceButton, destinationButton, previewButton, duplicateButton, exclusionsButton }) control.Enabled = !busy;
        selectAllButton.Enabled = clearSelectionButton.Enabled = !busy && (demo || plan?.Moves.Count > 0); planGrid.Enabled = !busy;
        applyButton.Enabled = !busy && (demo || plan != null && CheckedMoves().Any()); cancelButton.Enabled = busy;
        undoButton.Enabled = !busy && !demo && historyGrid.SelectedRows.Count > 0 && historyGrid.SelectedRows[0].Tag is Operation op && op.Entries.Any(entry => entry.State != "Undone");
    }
    private async Task Work(string message, Func<CancellationToken, Task> action)
    {
        if (busy || demo) return; busy = true; cancellation = new(); status.Text = message; UpdateButtons();
        try { await action(cancellation.Token); }
        catch (OperationCanceledException) { status.Text = "Operación cancelada."; }
        catch (Exception ex) { if (testMode) throw; status.Text = "No se completó la operación."; MessageBox.Show(this, ex.Message, "DaniOrganizer", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        finally { busy = false; cancellation.Dispose(); cancellation = null; RefreshHistory(); UpdateButtons(); }
    }
    private async Task Preview()
    {
        await Work("Analizando archivos…", async token =>
        {
            var options = ReadOptions(); plan = await Task.Run(() => Organizer.Preview(options, token), token);
            fillingPlan = true; planGrid.Rows.Clear();
            foreach (var move in plan.Moves) { int row = planGrid.Rows.Add(true, Path.GetFileName(move.Source), Organizer.Category(Path.GetExtension(move.Source)), SizeText(move.Length), Path.GetDirectoryName(move.Source), move.Destination); planGrid.Rows[row].Tag = move; }
            fillingPlan = false; UpdateSelection();
            status.Text = "Vista previa lista. Revisa los destinos antes de organizar."; tabs.SelectedIndex = 0; ShowWarnings(plan.Warnings);
        });
    }
    private async Task FindDuplicates()
    {
        await Work("Comparando contenido con SHA-256…", async token =>
        {
            var options = ReadOptions(); var warnings = new List<string>();
            var duplicates = await Task.Run(() => Organizer.Duplicates(options, warnings, token), token);
            duplicateGrid.Rows.Clear(); int group = 0;
            foreach (var set in duplicates) { group++; foreach (var path in set.Files) { int row = duplicateGrid.Rows.Add(group, Path.GetFileName(path), SizeText(set.Length), path); duplicateGrid.Rows[row].Tag = path; } }
            summary.Text = $"{duplicates.Count:N0} grupos · {SizeText(duplicates.Sum(set => set.Length * (set.Files.Count - 1)))} en copias repetidas · No se elimina ningún archivo";
            status.Text = "Comparación terminada. Doble clic para mostrar un archivo en el Explorador."; tabs.SelectedIndex = 1; ShowWarnings(warnings);
        });
    }
    private async Task Apply(bool confirm = true)
    {
        if (plan == null) return;
        planGrid.EndEdit();
        var selectedPlan = Organizer.Select(plan, CheckedMoves().Select(move => move.Source));
        if (selectedPlan.Moves.Count == 0) return;
        if (confirm && MessageBox.Show(this, $"Se moverán {selectedPlan.Moves.Count:N0} archivos al destino mostrado.\n\nPuedes deshacer desde el historial. ¿Organizar ahora?", "Aplicar vista previa", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        await Work("Organizando…", async token =>
        {
            var op = await Task.Run(() => Organizer.Apply(selectedPlan, journalDirectory, Progress(), token));
            InvalidatePlan(); status.Text = $"{op.Entries.Count(entry => entry.State == "Moved"):N0} archivos organizados. Registro guardado en el historial.";
            tabs.SelectedIndex = 2; ShowWarnings(op.Errors);
        });
    }
    private async Task Undo(bool confirm = true)
    {
        if (historyGrid.SelectedRows.Count == 0 || historyGrid.SelectedRows[0].Tag is not Operation operation) return;
        if (confirm && MessageBox.Show(this, "Se devolverán los archivos de esta operación a sus ubicaciones originales. Se omitirán los que hayan cambiado o tengan el origen ocupado.", "Deshacer organización", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        await Work("Devolviendo archivos…", async token =>
        {
            var op = await Task.Run(() => Organizer.Undo(operation, journalDirectory, Progress(), token));
            InvalidatePlan(); tabs.SelectedIndex = 2; status.Text = $"Deshacer terminado · {op.Entries.Count(entry => entry.State != "Undone")} movimientos pendientes."; ShowWarnings(op.Errors);
        });
    }
    private IProgress<string> Progress() => new Progress<string>(file => status.Text = "Procesando: " + file);
    private void RefreshHistory()
    {
        if (demo) return;
        operations.Clear(); operations.AddRange(Organizer.History(journalDirectory)); historyGrid.Rows.Clear();
        foreach (var operation in operations) { int remaining = operation.Entries.Count(entry => entry.State != "Undone"); int row = historyGrid.Rows.Add(operation.CreatedUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm"), remaining, operation.Errors.Count > 0 ? $"{operation.Errors.Count} avisos" : remaining == 0 ? "Deshecho / sin movimientos" : "Disponible para deshacer"); historyGrid.Rows[row].Tag = operation; }
    }
    private void ShowWarnings(IReadOnlyList<string> warnings)
    {
        if (warnings.Count == 0) return;
        if (testMode) throw new InvalidOperationException(string.Join(Environment.NewLine, warnings));
        using var dialog = new Form { Text = $"DaniOrganizer · {warnings.Count} avisos", Size = new(780, 470), StartPosition = FormStartPosition.CenterParent, BackColor = Background };
        dialog.Controls.Add(new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, BackColor = Surface, ForeColor = Ink, Font = new("Consolas", 10), Text = string.Join(Environment.NewLine, warnings) }); dialog.ShowDialog(this);
    }
    private static void Reveal(string path) { if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
    public static string SizeText(long value) { string[] units = { "B", "KB", "MB", "GB", "TB" }; double size = value; int index = 0; while (size >= 1024 && index < units.Length - 1) { size /= 1024; index++; } return $"{size:0.#} {units[index]}"; }
    private void Demo()
    {
        source.Text = @"C:\Usuarios\Daniel\Descargas"; destination.Text = @"C:\Usuarios\Daniel\Descargas\_Organizado";
        foreach (var item in new[] { ("vacaciones.jpg", "Imagenes", "3.2 MB"), ("proyecto_final.pdf", "Documentos", "1.8 MB"), ("presupuesto.xlsx", "Hojas de calculo", "248 KB"), ("playlist.mp3", "Audio", "7.4 MB"), ("notas.md", "Documentos", "12 KB"), ("respaldo.zip", "Comprimidos", "126 MB"), ("demo.mp4", "Videos", "48 MB") })
            planGrid.Rows.Add(item.Item1 is not ("respaldo.zip" or "proyecto_final.pdf"), item.Item1, item.Item2, item.Item3, source.Text, Path.Combine(destination.Text, item.Item2, item.Item1));
        summary.Text = "DEMOSTRACIÓN · Marca qué archivos organizar y añade exclusiones";
        status.Text = "Vista previa · Cada movimiento quedará registrado para poder deshacerlo.";
    }
    internal async Task Smoke(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "a.txt"), "same"); File.WriteAllText(Path.Combine(directory, "b.txt"), "same"); File.WriteAllText(Path.Combine(directory, "foto.png"), "image fixture");
        SetSource(directory); await Preview();
        if (planGrid.Rows.Count != 3 || !applyButton.Enabled) throw new Exception("La vista previa no habilita la organización.");
        grouping.SelectedIndex = 2;
        if (plan != null || applyButton.Enabled) throw new Exception("Cambiar la regla no invalida la vista previa.");
        await Preview(); await FindDuplicates();
        if (duplicateGrid.Rows.Count != 2 || tabs.SelectedIndex != 1) throw new Exception("La pantalla de duplicados no muestra los archivos esperados.");
        await Apply(confirm: false);
        if (File.Exists(Path.Combine(directory, "a.txt")) || historyGrid.Rows.Count != 1 || !undoButton.Enabled) throw new Exception("La organización no actualiza el historial.");
        await Undo(confirm: false);
        if (File.ReadAllText(Path.Combine(directory, "a.txt")) != "same" || File.ReadAllText(Path.Combine(directory, "b.txt")) != "same") throw new Exception("La interfaz no deshace correctamente.");
        string protectedFolder = Path.Combine(directory, "Privado"); Directory.CreateDirectory(protectedFolder);
        File.WriteAllText(Path.Combine(protectedFolder, "secreto.txt"), "intacto");
        SaveExclusions(new[] { Path.Combine(directory, "b.txt"), protectedFolder });
        if (plan != null || applyButton.Enabled) throw new Exception("Las exclusiones no invalidan el plan.");
        using (var reopened = new MainForm(testMode: true, journalDirectory: journalDirectory))
            if (reopened.excludedPaths.Count != 2) throw new Exception("Las exclusiones no se conservan al reiniciar.");
        await Preview(); if (planGrid.Rows.Count != 2) throw new Exception("Aparecen archivos excluidos en la vista previa.");
        await FindDuplicates(); if (duplicateGrid.Rows.Count != 0) throw new Exception("Se analizaron duplicados excluidos.");
        SetSelection(false); if (applyButton.Enabled) throw new Exception("Se puede organizar sin seleccionar archivos.");
        foreach (DataGridViewRow row in planGrid.Rows) if (row.Tag is PlannedMove move && Path.GetFileName(move.Source) == "a.txt") row.Cells[0].Value = true;
        if (CheckedMoves().Count() != 1 || !applyButton.Enabled) throw new Exception("Las casillas no actualizan la selección.");
        File.WriteAllText(Path.Combine(directory, "foto.png"), "cambio sin seleccionar");
        await Apply(confirm: false);
        if (File.Exists(Path.Combine(directory, "a.txt")) || File.ReadAllText(Path.Combine(directory, "b.txt")) != "same" || File.ReadAllText(Path.Combine(directory, "foto.png")) != "cambio sin seleccionar" || File.ReadAllText(Path.Combine(protectedFolder, "secreto.txt")) != "intacto")
            throw new Exception("La organización selectiva modificó archivos desmarcados o excluidos.");
        await Undo(confirm: false);
        if (File.ReadAllText(Path.Combine(directory, "a.txt")) != "same") throw new Exception("No se pudo deshacer la selección.");
    }
}

internal sealed class WorkspaceTabs : Panel
{
    private readonly FlowLayoutPanel header = new() { Dock = DockStyle.Top, Height = 38, WrapContents = false, BackColor = Color.FromArgb(14, 20, 29) };
    private readonly Panel body = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(23, 32, 44) };
    private readonly List<Control> pages = new(); private readonly List<Button> buttons = new(); private int index;
    public WorkspaceTabs() { Controls.Add(body); Controls.Add(header); }
    public int SelectedIndex
    {
        get => index;
        set
        {
            index = value;
            for (int i = 0; i < pages.Count; i++) { pages[i].Visible = i == value; buttons[i].BackColor = i == value ? Color.FromArgb(23, 32, 44) : header.BackColor; buttons[i].ForeColor = i == value ? Color.FromArgb(97, 226, 186) : Color.FromArgb(153, 171, 192); }
        }
    }
    public void Add(string name, Control page)
    {
        int position = pages.Count; pages.Add(page); page.Dock = DockStyle.Fill; body.Controls.Add(page);
        var button = new Button { Text = name, FlatStyle = FlatStyle.Flat, Width = 150, Height = 38, Margin = Padding.Empty, Cursor = Cursors.Hand }; button.FlatAppearance.BorderSize = 0;
        button.Click += (_, _) => SelectedIndex = position; buttons.Add(button); header.Controls.Add(button); SelectedIndex = index;
    }
}

internal sealed class RulePicker : Button
{
    private readonly string[] names = { "Tipo de archivo", "Fecha de modificación (año-mes)", "Tipo y fecha de modificación" };
    private readonly ContextMenuStrip menu = new() { ShowImageMargin = false, BackColor = Color.FromArgb(23, 32, 44), ForeColor = Color.FromArgb(235, 241, 247) };
    private int selected;
    public event EventHandler? SelectedIndexChanged;
    public int SelectedIndex
    {
        get => selected;
        set { if (value < 0 || value >= names.Length) throw new ArgumentOutOfRangeException(nameof(value)); selected = value; Text = names[value] + "   ▾"; SelectedIndexChanged?.Invoke(this, EventArgs.Empty); }
    }
    public RulePicker()
    {
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderColor = Color.FromArgb(56, 73, 90); BackColor = Color.FromArgb(14, 20, 29); ForeColor = Color.FromArgb(235, 241, 247); TextAlign = ContentAlignment.MiddleLeft; Cursor = Cursors.Hand;
        for (int i = 0; i < names.Length; i++) { int index = i; menu.Items.Add(names[i], null, (_, _) => SelectedIndex = index); }
        Click += (_, _) => menu.Show(this, new Point(0, Height)); SelectedIndex = 0;
    }
    protected override void Dispose(bool disposing) { if (disposing) menu.Dispose(); base.Dispose(disposing); }
}
