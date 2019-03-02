using System;
using System.Collections.Concurrent;
using System.Threading;

namespace HbTest.Logging
{
    public sealed class Level
    {
        public static readonly Level Error = new Level(0, ConsoleColor.Red, "ERROR");
        public static readonly Level Info = new Level(1, ConsoleColor.Green, "INFO");
        public static readonly Level Debug = new Level(2, ConsoleColor.Magenta, "DEBUG");
        public static readonly Level Trace = new Level(3, ConsoleColor.DarkCyan, "TRACE");

        public readonly int IntValue;
        public readonly ConsoleColor Color;
        public readonly string Name;

        private Level(int intValue, ConsoleColor color, string name)
        {
            IntValue = intValue;
            Color = color;
            Name = name;
        }
    }

    public static class Logger
    {
        private const string DateFormat = "dd-MM-yyyy HH:mm:ss.ffff";

        public static Level Level = Level.Debug;
        public static bool Enabled = true;

        private struct Message
        {
            public int ThreadId;
            public DateTime Timestamp;
            public string Value;
            public Level Level;
        }

        private static readonly BlockingCollection<Message> Queue = new BlockingCollection<Message>();

        private static Thread WriterThread = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    var message = Queue.Take();
                    WriteMessageColored(message);
                }
            }
            catch (ThreadInterruptedException)
            {
                // Write remaining messages (no more should arrive) and exit cleanly
                foreach (var message in Queue)
                {
                    WriteMessageColored(message);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        });

        private static void EnqueueMessage(string message, Level level)
        {
            var msg = new Message {Timestamp = DateTime.Now, ThreadId = Thread.CurrentThread.ManagedThreadId, Level = level, Value = message};
            if (WriterThread.IsAlive)
            {
                Queue.Add(msg);
            }
            else
            {
                WriteMessageColored(msg);
            }
        }

        private static void WriteMessageColored(Message message)
        {
            if (!Enabled || message.Level.IntValue > Level.IntValue)
            {
                return;
            }

            var oldColor = Console.ForegroundColor;

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write(message.Timestamp.ToString(DateFormat));

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write(" [" + message.ThreadId + "] ");

            Console.ForegroundColor = message.Level.Color;
            Console.WriteLine(message.Level.Name + ": " + message.Value);

            Console.ForegroundColor = oldColor;
        }

        public static void StartAsyncWriter()
        {
            if (!WriterThread.IsAlive)
            {
                WriterThread.Start();
            }
        }

        public static void StopAsyncWriter()
        {
            WriterThread.Interrupt();
        }

        public static void Error(string message)
        {
            EnqueueMessage(message, Level.Error);
        }

        public static void Info(string message)
        {
            EnqueueMessage(message, Level.Info);
        }

        public static void Debug(string message)
        {
            EnqueueMessage(message, Level.Debug);
        }

        public static void Trace(string message)
        {
            EnqueueMessage(message, Level.Trace);
        }
    }
}