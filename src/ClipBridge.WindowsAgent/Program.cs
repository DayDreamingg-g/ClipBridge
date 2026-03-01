using System.Text;
using System.Windows.Forms;

namespace ClipBridge.WindowsAgent;

internal static class Program
{
    private const int MaxHistory = 20;

    private static readonly List<ClipboardItem> History = new();
    private static string? _lastText;
    private static readonly string DeviceId = Environment.MachineName;

    [STAThread]
    private static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        Console.WriteLine("ClipBridge Windows Agent started.");
        Console.WriteLine($"Device ID: {DeviceId}");
        Console.WriteLine("Listening clipboard (text only)...");
        Console.WriteLine("Press Ctrl+C something to see logs.\n");

        while (true)
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    var text = Clipboard.GetText();

                    if (!string.IsNullOrWhiteSpace(text) && text != _lastText)
                    {
                        _lastText = text;

                        AddToHistory(text);

                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Clipboard updated. History: {History.Count}/{MaxHistory}");
                        Console.WriteLine(text);
                        Console.WriteLine("------");
                    }
                }
            }
            catch (Exception ex)
            {
                // Clipboard can be temporarily locked by another process
                Console.WriteLine($"[WARN] Clipboard read failed: {ex.Message}");
            }

            Thread.Sleep(400);
        }
    }

    private static void AddToHistory(string text)
    {
        var item = new ClipboardItem(
            Id: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
            Text: text,
            DeviceId: DeviceId,
            CreatedAtUtc: DateTimeOffset.UtcNow
        );

        // Prevent duplicates on top
        if (History.Count > 0 && History[0].Text == item.Text)
            return;

        History.Insert(0, item);

        if (History.Count > MaxHistory)
            History.RemoveAt(History.Count - 1);
    }

    private sealed record ClipboardItem(string Id, string Text, string DeviceId, DateTimeOffset CreatedAtUtc);
}