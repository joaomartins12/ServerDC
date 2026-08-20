using System;
using System.Collections;
using System.Reflection;
using System.Windows.Forms;

namespace ServerManager
{
    internal static class LogClearBehavior
    {
        public static void Attach(MainForm form)
        {
            if (form == null) return;

            var field = typeof(MainForm).GetField("_servers", BindingFlags.Instance | BindingFlags.NonPublic);
            var servers = field == null ? null : field.GetValue(form) as IDictionary;
            if (servers == null) return;

            foreach (DictionaryEntry pair in servers)
            {
                var entry = pair.Value;
                if (entry == null) continue;

                var type = entry.GetType();
                var toggleField = type.GetField("ToggleButton", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var logField = type.GetField("LogBox", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var toggle = toggleField == null ? null : toggleField.GetValue(entry) as Button;
                var logBox = logField == null ? null : logField.GetValue(entry) as RichTextBox;
                if (toggle == null || logBox == null) continue;

                toggle.MouseDown += delegate(object sender, MouseEventArgs e)
                {
                    if (e.Button != MouseButtons.Left) return;
                    if (!ManagerEnhancements.ClearVisibleLogsOnServerStart) return;
                    if (!string.Equals(toggle.Text, "START", StringComparison.OrdinalIgnoreCase)) return;

                    logBox.Clear();
                    logBox.Invalidate(true);
                    logBox.Update();
                };
            }
        }
    }
}
