using System;
using System.Collections.Generic;
using NLog;
using NLog.Targets;

namespace InventoryKamera.ui
{
    /// <summary>A rendered log entry, including its severity and ordering.</summary>
    public sealed class UiLogEntry
    {
        internal UiLogEntry(long sequence, LogLevel level, string text, bool isReset = false)
        {
            Sequence = sequence;
            Level = level;
            Text = text;
            IsReset = isReset;
        }

        public long Sequence { get; }
        public LogLevel Level { get; }
        public string Text { get; }
        public bool IsReset { get; }
        public bool IsError => !IsReset && Level.Ordinal >= LogLevel.Error.Ordinal;
    }

    /// <summary>
    /// Buffers recent entries before a window exists. Subscribe replays that
    /// buffer and registers for future entries in one lock, preserving ordering
    /// without a gap between taking a snapshot and subscribing.
    /// </summary>
    [Target("UiLog")]
    public sealed class UiLogTarget : TargetWithLayout
    {
        public const int MaximumEntryCount = 2000;
        public const int MaximumTextLength = 500000;
        private const int MaximumEntryLength = 16384;

        private static readonly object Sync = new object();
        private static readonly LinkedList<UiLogEntry> Buffer = new LinkedList<UiLogEntry>();
        private static readonly List<Subscription> Subscribers = new List<Subscription>();
        private static int bufferedCharacters;
        private static long nextSequence;

        /// <summary>Raised for every rendered log line, on the logging thread.</summary>
        public static event Action<LogLevel, string> LineLogged;

        /// <summary>Returns a copy of the buffered lines, oldest first.</summary>
        public static IList<string> Snapshot()
        {
            lock (Sync)
            {
                var lines = new List<string>(Buffer.Count);
                foreach (var entry in Buffer)
                    lines.Add(entry.Text);
                return lines;
            }
        }

        public static IList<UiLogEntry> SnapshotEntries()
        {
            lock (Sync)
                return new List<UiLogEntry>(Buffer);
        }

        /// <summary>
        /// Replays the snapshot, then follows live entries until disposed.
        /// The callback runs on logging threads under the buffer lock; it must
        /// only enqueue work and must never synchronously invoke the UI thread.
        /// IsReset entries signal that the in-memory logs were cleared.
        /// </summary>
        public static IDisposable Subscribe(Action<UiLogEntry> callback)
        {
            if (callback == null)
                throw new ArgumentNullException(nameof(callback));

            lock (Sync)
            {
                var subscription = new Subscription(callback);
                foreach (var entry in Buffer)
                    subscription.Deliver(entry);
                Subscribers.Add(subscription);
                return subscription;
            }
        }

        /// <summary>Clears only the in-memory display; file logs are retained.</summary>
        public static void ClearBuffer()
        {
            lock (Sync)
            {
                Buffer.Clear();
                bufferedCharacters = 0;
                var reset = new UiLogEntry(++nextSequence, null, string.Empty, true);
                foreach (var subscriber in Subscribers)
                    subscriber.Deliver(reset);
            }
        }

        protected override void Write(LogEventInfo logEvent)
        {
            string line = RenderLogEvent(Layout, logEvent);
            // The timestamp/level prefix belongs to the first line. Align
            // multiline task details under its message, preserving their tabs.
            string message = logEvent.FormattedMessage;
            int messageColumn = string.IsNullOrEmpty(message) ? 0 : line.IndexOf(message, StringComparison.Ordinal);
            if (messageColumn > 0)
                line = line.Replace("\r\n", "\n").Replace('\r', '\n')
                    .Replace("\n", "\n" + new string(' ', messageColumn));
            if (line.Length > MaximumEntryLength)
                line = line.Substring(0, MaximumEntryLength) + " … [entry truncated; see log file]";

            lock (Sync)
            {
                var entry = new UiLogEntry(++nextSequence, logEvent.Level, line);
                Buffer.AddLast(entry);
                bufferedCharacters += line.Length;
                while (Buffer.Count > MaximumEntryCount || bufferedCharacters > MaximumTextLength)
                {
                    bufferedCharacters -= Buffer.First.Value.Text.Length;
                    Buffer.RemoveFirst();
                }
                foreach (var subscriber in Subscribers)
                    subscriber.Deliver(entry);
            }

            LineLogged?.Invoke(logEvent.Level, line);
        }

        private sealed class Subscription : IDisposable
        {
            private readonly Action<UiLogEntry> callback;

            public Subscription(Action<UiLogEntry> callback)
            {
                this.callback = callback;
            }

            public void Deliver(UiLogEntry entry)
            {
                // A display subscriber must never stop the scanner's logging.
                try { callback(entry); }
                catch (Exception) { }
            }

            public void Dispose()
            {
                lock (Sync)
                    Subscribers.Remove(this);
            }
        }
    }
}
