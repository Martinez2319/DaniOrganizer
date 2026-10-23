using DaniOrganizer.Core;

namespace DaniOrganizer;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Length == 1 && args[0] == "--ui-smoke")
        {
            string scratch = Path.Combine(Path.GetTempPath(), "DaniOrganizer-ui-" + Guid.NewGuid().ToString("N"));
            using var form = new MainForm(testMode: true, journalDirectory: Path.Combine(scratch, "journal"));
            form.Shown += async (_, _) =>
            {
                try { await form.Smoke(Path.Combine(scratch, "input")); Environment.ExitCode = 0; }
                catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
                finally { form.Close(); if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
            };
            Application.Run(form); return;
        }
        if (args.Length == 2 && args[0] == "--screenshot")
        {
            using var preview = new MainForm(demo: true);
            preview.Show(); Application.DoEvents();
            using var bitmap = new Bitmap(preview.Width, preview.Height);
            preview.DrawToBitmap(bitmap, new Rectangle(Point.Empty, preview.Size));
            bitmap.Save(args[1]); preview.Close(); return;
        }
        Application.Run(new MainForm());
    }
}
