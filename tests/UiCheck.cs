using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

class UiCheck
{
    [DllImport("shcore.dll")] static extern int SetProcessDpiAwareness(int awareness);
    static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static double Luminance(Color color) { return new[] { color.R, color.G, color.B }.Select(v => v / 255.0).Select(v => v <= .04045 ? v / 12.92 : Math.Pow((v + .055) / 1.055, 2.4)).Zip(new[] { .2126, .7152, .0722 }, (v,w) => v*w).Sum(); }
    [STAThread]
    static int Main(string[] args)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e) {
            Console.WriteLine("UI thread: " + e.Exception.GetType().FullName);
            Console.WriteLine(e.Exception.Message);
            Console.WriteLine(e.Exception.StackTrace);
            Environment.Exit(1);
        };
        try {
        SetProcessDpiAwareness(2); Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        var assembly = Assembly.LoadFrom(args[0]);
        Type type = assembly.GetType("Bridge+QuotaPanel");
        Directory.CreateDirectory(args[1]);
        using (var anchor = new Form())
        using (var form = (Form)Activator.CreateInstance(type, new object[] { Process.GetCurrentProcess().Id, anchor.Handle, false }))
        {
            ((Timer)type.GetField("timer", Private).GetValue(form)).Stop();
            foreach (float scale in new[] { 1f, 1.25f, 1.5f })
            foreach (bool dark in new[] { false, true })
            {
                type.GetField("scale", Private).SetValue(form, scale);
                type.GetMethod("SetFonts", Private).Invoke(form, null);
                type.GetMethod("ApplyTheme", Private).Invoke(form, new object[] { dark });
                type.GetField("signature", Private).SetValue(form, null);
                type.GetMethod("UpdateView", Private).Invoke(form, null);
                form.Location = new Point(-10000, -10000); form.Show(); Application.DoEvents();
                var labels = form.Controls.OfType<Label>().Where(l => l.Visible).ToArray();
                if (!labels.Any(l => l.Text == "76%") || !labels.Any(l => l.Text == "42%") || !labels.Any(l => l.Text == "活动任务")) throw new Exception("UI fixture was not loaded");
                for (int i = 0; i < labels.Length; i++)
                {
                    var label = labels[i];
                    using (var graphics = form.CreateGraphics()) {
                        try { graphics.MeasureString(label.Text, label.Font); }
                        catch (ArgumentException) { throw new Exception("Invalid font still attached to label: " + label.Text); }
                    }
                    if (!form.ClientRectangle.Contains(label.Bounds)) throw new Exception("Label outside panel: " + label.Text);
                    if (label.Font.Size < 12 * scale - .01f) throw new Exception("Text smaller than 12px");
                    double a = Luminance(label.ForeColor), b = Luminance(label.BackColor);
                    if ((Math.Max(a,b) + .05) / (Math.Min(a,b) + .05) < 4.5) throw new Exception("Text contrast below 4.5: " + label.Text);
                    for (int j = i + 1; j < labels.Length; j++) if (label.Bounds.IntersectsWith(labels[j].Bounds)) throw new Exception("Overlapping labels: " + label.Text + " / " + labels[j].Text);
                }
                using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, form.ClientRectangle); bitmap.Save(Path.Combine(args[1], "CodexQuota-panel-" + (dark ? "dark-" : "light-") + (int)(scale * 100) + ".png")); }
                form.Hide();
            }
        }
        Console.WriteLine("PASS: light/dark rendering at 100%, 125%, 150%; no overlapping controls or out-of-bounds labels; text contrast >= 4.5; minimum text 12px.");
        return 0;
        } catch (Exception error) {
            while (error is TargetInvocationException) error = error.InnerException;
            Console.WriteLine(error.GetType().FullName);
            Console.WriteLine(error.Message);
            Console.WriteLine(error.StackTrace);
            return 1;
        }
    }
}
