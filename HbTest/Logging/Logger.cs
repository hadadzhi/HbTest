using System;
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

        private static void WriteLogMessage(string message, Level level)
        {
            if (!Enabled || level.IntValue > Level.IntValue)
            {
                return;
            }

            var oldColor = Console.ForegroundColor;

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write(DateTime.Now.ToString(DateFormat));

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write(" [" + Thread.CurrentThread.ManagedThreadId + "] ");

            Console.ForegroundColor = level.Color;
            Console.WriteLine(level.Name + ": " + message);

            Console.ForegroundColor = oldColor;
        }

        public static void Error(string message)
        {
            WriteLogMessage(message, Level.Error);
        }

        public static void Info(string message)
        {
            WriteLogMessage(message, Level.Info);
        }

        public static void Debug(string message)
        {
            WriteLogMessage(message, Level.Debug);
        }

        public static void Trace(string message)
        {
            WriteLogMessage(message, Level.Trace);
        }
    }
}