using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace ClipBridge.WindowsAgent;

internal static class Program
{
    // ====== Firebase (Web App config) ======
    private const string FirebaseApiKey = "AIzaSyAB9sdbcVgqBO9lch8aVdtfm6cDoWRUpLc";
    private const string FirebaseProjectId = "clipbridge-3d77a";

    // ====== App settings ======
    private const int MaxHistory = 20;
    private const int MaxTextLength = 10_000; // protect against huge clipboard payloads
    private static readonly TimeSpan PushCooldown = TimeSpan.FromMilliseconds(800);

    // ====== Device ======
    private static readonly string DeviceId = Environment.MachineName;

    // ====== State ======
    private static readonly List<ClipboardItem> History = new();
    private static string? _lastText;
    private static DateTime _lastPushTimeUtc = DateTime.MinValue;

    // Firebase auth state
    private static string? _idToken;
    private static string? _uid;

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    [STAThread]
    private static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        Console.WriteLine("ClipBridge Windows Agent started.");
        Console.WriteLine($"Device ID: {DeviceId}");

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nShutting down ClipBridge...");
            Environment.Exit(0);
        };

        // Keep STA thread for Clipboard; call async login sync
        try
        {
            FirebaseAnonymousLoginAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FATAL] Firebase login failed: {ex.Message}");
            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
            return;
        }

        Console.WriteLine("Listening clipboard (text only)...");
        Console.WriteLine("Copy anything (Ctrl+C) to sync to Firebase.\n");

        while (true)
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    var text = Clipboard.GetText();

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        text = Sanitize(text);

                        if (text != _lastText)
                        {
                            _lastText = text;

                            var item = AddToHistory(text);

                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Clipboard updated. History: {History.Count}/{MaxHistory}");
                            Console.WriteLine(text);
                            Console.WriteLine("------");

                            // Debounce to avoid spamming cloud
                            if (DateTime.UtcNow - _lastPushTimeUtc > PushCooldown)
                            {
                                _lastPushTimeUtc = DateTime.UtcNow;
                                _ = PushToFirestoreAsync(item);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Clipboard can be temporarily locked by another process
                Console.WriteLine($"[WARN] Clipboard read failed: {ex.Message}");
            }

            Thread.Sleep(350);
        }
    }

    private static string Sanitize(string text)
    {
        text = text.Trim();

        if (text.Length > MaxTextLength)
            text = text[..MaxTextLength];

        return text;
    }

    private static ClipboardItem AddToHistory(string text)
    {
        var item = new ClipboardItem(
            Id: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
            Text: text,
            DeviceId: DeviceId,
            CreatedAtUtc: DateTimeOffset.UtcNow
        );

        // Prevent duplicates at the top
        if (History.Count > 0 && History[0].Text == item.Text)
            return History[0];

        History.Insert(0, item);

        if (History.Count > MaxHistory)
            History.RemoveAt(History.Count - 1);

        return item;
    }

    private static async Task FirebaseAnonymousLoginAsync()
    {
        var url = $"https://identitytoolkit.googleapis.com/v1/accounts:signUp?key={FirebaseApiKey}";

        var response = await Http.PostAsJsonAsync(url, new { returnSecureToken = true });
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        _idToken = json.GetProperty("idToken").GetString();
        _uid = json.GetProperty("localId").GetString();

        Console.WriteLine($"Firebase UID: {_uid}");
        Console.WriteLine("Firebase auth OK.\n");
    }

    private static async Task PushToFirestoreAsync(ClipboardItem item)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_idToken) || string.IsNullOrWhiteSpace(_uid))
                return;

            // POST new document to: users/{uid}/clipboard_items
            var url =
                $"https://firestore.googleapis.com/v1/projects/{FirebaseProjectId}/databases/(default)/documents/users/{_uid}/clipboard_items";

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _idToken);

            var body = new
            {
                fields = new
                {
                    text = new { stringValue = item.Text },
                    deviceId = new { stringValue = item.DeviceId },
                    createdAt = new { timestampValue = item.CreatedAtUtc.UtcDateTime }
                }
            };

            request.Content = JsonContent.Create(body);

            var response = await Http.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"[ERROR] Firestore push failed: {error}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Firestore push exception: {ex.Message}");
        }
    }

    private sealed record ClipboardItem(string Id, string Text, string DeviceId, DateTimeOffset CreatedAtUtc);
}