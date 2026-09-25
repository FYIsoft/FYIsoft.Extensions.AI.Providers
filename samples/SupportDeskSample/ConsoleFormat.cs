namespace SupportDeskSample;

/// <summary>Shared console output helpers.</summary>
public static class ConsoleFormat
{
    public static void Heading(string text)
    {
        Console.WriteLine();
        Write($"== {text} ".PadRight(78, '='), ConsoleColor.Cyan);
    }

    public static void Label(string text)
    {
        Console.WriteLine();
        Write(text, ConsoleColor.Magenta);
    }

    public static void Note(string text) => Write(text, ConsoleColor.DarkGray);

    public static void Good(string text) => Write(text, ConsoleColor.Green);

    public static void Bad(string text) => Write(text, ConsoleColor.Red);

    public static void Write(string text, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ResetColor();
    }
}
