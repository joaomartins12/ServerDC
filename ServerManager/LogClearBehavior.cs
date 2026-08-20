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

            // START ALL must also clear before any child process can emit its first line.
            var startAll = FindButton(form, "START ALL");
            if (startAll != null)
            {
                startAll.MouseDown += delegate(object sender, MouseEventArgs e)
                {
                    if (e.Button != MouseButtons.Left) return;
                    if (!ManagerEnhancements.ClearVisibleLogsOnServerStart) return;

                    foreach (var state in tracked)
                    {
                        // Do not erase a server that was already running before START ALL.
                        if (IsRunning(state.Entry, state.ProcessField)) continue;
                        Clear(state.LogBox);
                        state.ClearedBeforeStart = true;
                    }
                };
            }

            // Actual STOPPED -> RUNNING transition watcher covers any future launch path.
            var timer = new Timer { Interval = 50 };
            timer.Tick += delegate
            {
                foreach (var state in tracked)
                {
                    var running = IsRunning(state.Entry, state.ProcessField);
                    if (!state.WasRunning && running)
                    {
                        if (ManagerEnhancements.ClearVisibleLogsOnServerStart && !state.ClearedBeforeStart)
                            Clear(state.LogBox);
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

        private static Button FindButton(Control root, string text)
        {
            foreach (Control child in root.Controls)
            {
                var button = child as Button;
                if (button != null && string.Equals(button.Text, text, StringComparison.OrdinalIgnoreCase))
                    return button;

                var nested = FindButton(child, text);
                if (nested != null) return nested;
            }
            return null;
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
