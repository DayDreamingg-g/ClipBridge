using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Extensions.Configuration;

namespace ClipBridge.WindowsAgent;

internal static class Program
{
    // ====== App settings ======
    private const int MaxHistory = 20;
    private static readonly string DeviceId = Environment.MachineName;

    // ====== State ======
    private static readonly List<ClipboardItem> History = new();
    private static string? _lastText;

    private static string? _idToken;
    private static string? _uid;

    private static string FirebaseApiKey = "";
    private static string FirebaseProjectId = "";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    [STAThread]
    private static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        // ====== Load config ======
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        FirebaseApiKey = config["Firebase:ApiKey"]!;
        FirebaseProjectId = config["Firebase:ProjectId"]!;

        Console.WriteLine("ClipBridge Windows Agent started.");
        Console.WriteLine($"Device ID: {DeviceId}");

        FirebaseAnonymousLoginAsync().GetAwaiter().GetResult();

        Console.WriteLine("Listening clipboard...\n");

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

                        var item = AddToHistory(text.Trim());

                        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Updated");

                        _ = PushToFirestoreAsync(item);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARN] Clipboard read failed: {ex.Message}");
            }

            Thread.Sleep(400);
        }
    }

    private static ClipboardItem AddToHistory(string text)
    {
        var item = new ClipboardItem(
            Id: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
            Text: text,
            DeviceId: DeviceId,
            CreatedAtUtc: DateTimeOffset.UtcNow
        );

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
                    createdAt = new { timestampValue = item.CreatedAtUtc.UtcDateTime.ToString("o") }
                }
            };

            request.Content = JsonContent.Create(body);

            var response = await Http.SendAsync(request);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"[ERROR] Firestore push failed: {(int)response.StatusCode} {response.StatusCode}");
                Console.WriteLine(error);
            }
            else
            {
                Console.WriteLine("[OK] Firestore push");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Firestore push exception: {ex}");
        }
    }

    private sealed record ClipboardItem(
        string Id,
        string Text,
        string DeviceId,
        DateTimeOffset CreatedAtUtc);
}