using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;

namespace ServerManager
{
    internal static class LogClearBehavior
    {
        private sealed class TrackedServer
        {
            public object Entry;
            public FieldInfo ProcessField;
            public Button Toggle;
            public RichTextBox LogBox;
            public bool WasRunning;
            public bool ClearedBeforeStart;
        }

        // Keep timers alive for the lifetime of the manager form.
        private static readonly List<Timer> Timers = new List<Timer>();

        public static void Attach(MainForm form)
        {
            if (form == null) return;

            var field = typeof(MainForm).GetField("_servers", BindingFlags.Instance | BindingFlags.NonPublic);
            var servers = field == null ? null : field.GetValue(form) as IDictionary;
            if (servers == null) return;

            var tracked = new List<TrackedServer>();
            foreach (DictionaryEntry pair in servers)
            {
                var entry = pair.Value;
                if (entry == null) continue;

                var type = entry.GetType();
                var toggleField = type.GetField("ToggleButton", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var logField = type.GetField("LogBox", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var processField = type.GetField("Process", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var toggle = toggleField == null ? null : toggleField.GetValue(entry) as Button;
                var logBox = logField == null ? null : logField.GetValue(entry) as RichTextBox;
                if (toggle == null || logBox == null || processField == null) continue;

                var state = new TrackedServer
                {
                    Entry = entry,
                    ProcessField = processField,
                    Toggle = toggle,
                    LogBox = logBox,
                    WasRunning = IsRunning(entry, processField)
                };
                tracked.Add(state);

                // Individual START: clear before MainForm starts redirecting new stdout.
                toggle.MouseDown += delegate(object sender, MouseEventArgs e)
                {
                    if (e.Button != MouseButtons.Left) return;
                    if (!ManagerEnhancements.ClearVisibleLogsOnServerStart) return;
                    if (!string.Equals(toggle.Text, "START", StringComparison.OrdinalIgnoreCase)) return;

                    Clear(state.LogBox);
                    state.ClearedBeforeStart = true;
                };
            }

            // Also watch actual process transitions. This covers START ALL and any future
            // launch path that does not originate from an individual server button.
            var timer = new Timer { Interval = 50 };
            timer.Tick += delegate
            {
                foreach (var state in tracked)
                {
                    var running = IsRunning(state.Entry, state.ProcessField);
                    if (!state.WasRunning && running)
                    {
                        if (ManagerEnhancements.ClearVisibleLogsOnServerStart)
                        {
                            if (!state.ClearedBeforeStart)
                                Clear(state.LogBox);
                        }
                        state.ClearedBeforeStart = false;
                    }
                    else if (state.WasRunning && !running)
                    {
                        state.ClearedBeforeStart = false;
                    }

                    state.WasRunning = running;
                }
            };
            timer.Start();
            Timers.Add(timer);

            form.FormClosed += delegate
            {
                timer.Stop();
                Timers.Remove(timer);
                timer.Dispose();
            };
        }

        private static bool IsRunning(object entry, FieldInfo processField)
        {
            try
            {
                var process = processField.GetValue(entry) as Process;
                return process != null && !process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private static void Clear(RichTextBox logBox)
        {
            if (logBox == null || logBox.IsDisposed) return;
            try
            {
                logBox.Clear();
                logBox.SelectionStart = 0;
                logBox.SelectionLength = 0;
                logBox.Invalidate(true);
                logBox.Update();
                logBox.Refresh();
            }
            catch { }
        }
    }
}
