using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Lighthouse.Core.Model;
using Lighthouse.Core.Native;
using Lighthouse.Core.Sensors;
using Lighthouse.Core.Workloads;

namespace Lighthouse.Core.Llm;

/// <summary>One message of an Explain conversation: "user" or "assistant".</summary>
public sealed record ChatTurn(string Role, string Content);

/// <summary>What the local LLM said, or why it couldn't (<see cref="Ok"/> false: not part of the conversation).</summary>
public sealed record LlmAnswer(string Text, bool Ok);

/// <summary>
/// The local LLM: a llama-server, Ollama or LM Studio, asked through its own API on this PC only. Explain sends a
/// short digest of the last minutes and the user's question; a llama-server's key is read from its own
/// --api-key-file at call time and never stored or logged. For a game, Lighthouse can stop a llama-server (through
/// the sensor helper when it runs under another account) or unload Ollama's models; it never starts a server again
/// itself, it puts the start command on the clipboard instead.
/// </summary>
public static partial class LocalLlm
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };
    private static readonly TimeSpan QuickTimeout = TimeSpan.FromSeconds(5);

    public static string SystemPrompt(string machine) =>
        $"You are Lighthouse, the performance monitor on this PC ({machine}). You run on the PC itself, as the user's " +
        "local LLM. Use only the readings you are given. Answer in 2 to 4 short sentences of plain English: say what " +
        "is going on and whether anything needs attention, and if something should change, suggest one concrete " +
        "action. If the readings don't answer the question, say so. No preamble, no lists, no markdown.";

    /// <summary>
    /// Asks the server the conversation so far (its last message is the question). The first message carries the
    /// readings; later ones are follow-ups. Only the first message and the last 8 are sent, so a long conversation
    /// stays small.
    /// </summary>
    public static async Task<LlmAnswer> AskAsync(LlmServer server, string machine, IReadOnlyList<ChatTurn> conversation, CancellationToken ct)
    {
        if (server.LocalBaseUrl is not { } baseUrl)
            return new($"Explain only asks a server on this PC, and this one listens on {server.Host}.", false);
        var sent = conversation.Count <= 9 ? conversation : [conversation[0], .. conversation.Skip(conversation.Count - 8)];
        JsonArray Messages()
        {
            var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = SystemPrompt(machine) } };
            foreach (var turn in sent) messages.Add(new JsonObject { ["role"] = turn.Role, ["content"] = turn.Content });
            return messages;
        }
        return server.Kind switch
        {
            LlmKind.Ollama => await AskOllamaAsync(baseUrl, Messages, ct),
            LlmKind.LmStudio => await AskLmStudioAsync(baseUrl, Messages, ct),
            _ => await AskLlamaServerAsync(server, baseUrl, Messages(), ct),
        };
    }

    private static async Task<LlmAnswer> AskLlamaServerAsync(LlmServer server, string baseUrl, JsonArray messages, CancellationToken ct)
    {
        string? key = null;
        if (server.ApiKeyFile is { } keyFile)
        {
            try
            {
                // With the user's own rights: the path comes from a command line any program could have written.
                key = UserContext.Run(() => ReadKey(keyFile));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new("The local LLM's key file can't be read with your own rights, so Lighthouse won't send it.", false);
            }
        }
        var body = new JsonObject
        {
            ["model"] = "local",
            ["temperature"] = 0.3,
            ["max_tokens"] = 300,
            ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false },
            ["messages"] = messages,
        };
        var (status, json) = await PostAsync(baseUrl + "/v1/chat/completions", body, key, ct);
        if (status is < 200 or > 299) return new($"The local LLM answered {status}.", false);
        return Answer(JsonNode.Parse(json)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>());
    }

    /// <summary>
    /// Ollama's own chat API, with the model it already has loaded (else its newest), so asking doesn't load a second
    /// one. Thinking is turned off; a version that refuses that is asked again without it.
    /// </summary>
    private static async Task<LlmAnswer> AskOllamaAsync(string baseUrl, Func<JsonArray> messages, CancellationToken ct)
    {
        if (await OllamaModelAsync(baseUrl, ct) is not { } model)
            return new("Ollama has no models yet. Download one with \"ollama pull\" first.", false);
        JsonObject Body(bool noThinking)
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["messages"] = messages(),
                ["stream"] = false,
                ["options"] = new JsonObject { ["temperature"] = 0.3, ["num_predict"] = 400 },
            };
            if (noThinking) body["think"] = false;
            return body;
        }
        var (status, json) = await PostAsync(baseUrl + "/api/chat", Body(noThinking: true), null, ct);
        if (status == 400 && json.Contains("think", StringComparison.OrdinalIgnoreCase))
            (status, json) = await PostAsync(baseUrl + "/api/chat", Body(noThinking: false), null, ct);
        if (status is < 200 or > 299) return new($"Ollama answered {status}{ErrorOf(json)}.", false);
        return Answer(JsonNode.Parse(json)?["message"]?["content"]?.GetValue<string>());
    }

    /// <summary>LM Studio's OpenAI-style API, with the model it has loaded (else the first chat model it lists).</summary>
    private static async Task<LlmAnswer> AskLmStudioAsync(string baseUrl, Func<JsonArray> messages, CancellationToken ct)
    {
        if (await LmStudioModelAsync(baseUrl, ct) is not { } model)
            return new("LM Studio has no model to ask. Load one in LM Studio first.", false);
        var body = new JsonObject
        {
            ["model"] = model,
            ["temperature"] = 0.3,
            ["max_tokens"] = 600, // room for a model that thinks first; the answer itself is 2 to 4 sentences
            ["stream"] = false,
            ["messages"] = messages(),
        };
        var (status, json) = await PostAsync(baseUrl + "/v1/chat/completions", body, null, ct);
        if (status is < 200 or > 299) return new($"LM Studio answered {status}{ErrorOf(json)}.", false);
        return Answer(JsonNode.Parse(json)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>());
    }

    /// <summary>The model Ollama has in memory, else the one most recently downloaded. Null when it has none.</summary>
    public static async Task<string?> OllamaModelAsync(string baseUrl, CancellationToken ct)
    {
        var loaded = await GetAsync(baseUrl + "/api/ps", ct);
        if (loaded?["models"]?.AsArray().Select(m => m?["name"]?.GetValue<string>()).FirstOrDefault(n => n is { Length: > 0 }) is { } name)
            return name;
        var tags = await GetAsync(baseUrl + "/api/tags", ct);
        return tags?["models"]?.AsArray()
            .Select(m => (Name: m?["name"]?.GetValue<string>(), Modified: m?["modified_at"]?.GetValue<string>() ?? ""))
            .Where(m => m.Name is { Length: > 0 })
            .OrderByDescending(m => m.Modified, StringComparer.Ordinal)
            .Select(m => m.Name)
            .FirstOrDefault();
    }

    /// <summary>The chat model LM Studio has loaded (its own API says which), else the first non-embedding one it lists.</summary>
    public static async Task<string?> LmStudioModelAsync(string baseUrl, CancellationToken ct)
    {
        var mine = await GetAsync(baseUrl + "/api/v0/models", ct);
        var models = mine?["data"]?.AsArray()
            .Select(m => (Id: m?["id"]?.GetValue<string>(), State: m?["state"]?.GetValue<string>(), Type: m?["type"]?.GetValue<string>()))
            .Where(m => m.Id is { Length: > 0 } && m.Type is not "embeddings")
            .ToList() ?? [];
        if (models.FirstOrDefault(m => m.State == "loaded").Id is { } loaded) return loaded;
        if (models.Count > 0) return models[0].Id;
        var openAi = await GetAsync(baseUrl + "/v1/models", ct);
        return openAi?["data"]?.AsArray()
            .Select(m => m?["id"]?.GetValue<string>())
            .FirstOrDefault(id => id is { Length: > 0 } && !id.Contains("embed", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Unloads every model Ollama has in memory (its documented "keep_alive": 0), which frees their VRAM. The server
    /// keeps running and loads a model again when it is next asked. Returns how many were unloaded.
    /// </summary>
    public static async Task<int> UnloadOllamaAsync(string baseUrl, CancellationToken ct)
    {
        var loaded = await GetAsync(baseUrl + "/api/ps", ct);
        var names = loaded?["models"]?.AsArray().Select(m => m?["name"]?.GetValue<string>()).OfType<string>().ToList() ?? [];
        int unloaded = 0;
        foreach (string name in names)
        {
            var (status, _) = await PostAsync(baseUrl + "/api/generate", new JsonObject { ["model"] = name, ["keep_alive"] = 0 }, null, ct);
            if (status is >= 200 and <= 299) unloaded++;
        }
        return unloaded;
    }

    private static LlmAnswer Answer(string? text) =>
        StripThinking(text ?? "") is { Length: > 0 } answer ? new(answer, true) : new("The local LLM gave an empty answer.", false);

    private static async Task<(int Status, string Json)> PostAsync(string url, JsonObject body, string? key, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        if (key is { Length: > 0 }) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await Http.SendAsync(request, ct);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>A quick GET of a JSON listing; null when there is no answer or it isn't JSON.</summary>
    private static async Task<JsonNode?> GetAsync(string url, CancellationToken ct)
    {
        using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
        quick.CancelAfter(QuickTimeout);
        try
        {
            using var response = await Http.GetAsync(url, quick.Token);
            if (!response.IsSuccessStatusCode) return null;
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(quick.Token));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>": model not found", from a server's {"error": ...}; "" when there is none.</summary>
    private static string ErrorOf(string json)
    {
        try
        {
            var error = JsonNode.Parse(json)?["error"];
            string? text = error is JsonValue ? error.GetValue<string>() : error?["message"]?.GetValue<string>();
            return text is { Length: > 0 } ? ": " + (text.Length > 120 ? text[..119] + "…" : text) : "";
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            return "";
        }
    }

    public static string StripThinking(string text) => ThinkBlock().Replace(text, "").Trim();

    /// <summary>A key file holds one short line; anything bigger is not a key and is not sent anywhere.</summary>
    private static string ReadKey(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new IOException("no key file");
        if (file.Length > 4096) throw new IOException("too big to be a key");
        return File.ReadAllText(path).Trim();
    }

    /// <summary>A compact, factual picture of the last few minutes for the LLM to explain.</summary>
    public static string Digest(IReadOnlyList<Snapshot> recent, IEnumerable<string> activeAlerts)
    {
        if (recent.Count == 0) return "No data yet.";
        var last = recent[^1];
        var sb = new StringBuilder();
        sb.AppendLine($"Last {Math.Max(1, (last.Time - recent[0].Time).TotalMinutes):0} min:");
        sb.AppendLine($"CPU now {last.CpuLoad:0}% (avg {recent.Average(s => s.CpuLoad):0}%, max {recent.Max(s => s.CpuLoad):0}%), " +
                      $"temp {Fmt(last.CpuTemp, "°C")}, power {Fmt(last.CpuPower, " W")}, CCD state {last.Ccd}.");
        sb.AppendLine($"GPU now {last.GpuLoad:0}% (avg {recent.Average(s => s.GpuLoad):0}%), temp {Fmt(last.GpuTemp, "°C")}, " +
                      $"hotspot {Fmt(last.GpuHotspot, "°C")}, power {Fmt(last.GpuPower, " W")}, VRAM {last.VramUsedGb:0.0}/{last.VramTotalGb:0} GB.");
        sb.AppendLine($"RAM {last.RamUsedGb:0.0}/{last.RamTotalGb:0} GB, commit {last.CommitPct:0}%, disk active {last.DiskActivePct:0}%.");
        sb.AppendLine("Biggest workloads now (CPU % of whole machine, GPU %, RAM GB, VRAM GB):");
        foreach (var w in last.Workloads)
            sb.AppendLine($"- {w.Label}: {w.CpuPct:0.0}%, {w.GpuPct:0}%, {w.RamGb:0.0}, {w.VramGb:0.0}");
        foreach (string alert in activeAlerts) sb.AppendLine($"Active alert: {alert}");
        return sb.ToString();
    }

    private static string Fmt(double? v, string unit) => v is { } x ? $"{x:0}{unit}" : "n/a";

    /// <summary>
    /// The PowerShell line that starts the server again: in <paramref name="directory"/> (default: the server's folder),
    /// through runas when <paramref name="owner"/> is another account, which asks for that account's password.
    /// </summary>
    public static string StartCommand(string commandLine, string? owner, string? directory = null, string? currentUser = null)
    {
        string[] args = NativeMethods.SplitArgs(commandLine);
        string exe = args.Length > 0 ? args[0] : commandLine;
        string dir = directory ?? Path.GetDirectoryName(exe) ?? "";
        // Every part is a single-quoted PowerShell literal, so nothing in a command line ($, `, ;) runs on paste:
        // the command line came from a process, and any program can start one called llama-server.exe.
        string? user = owner?.Split('\\').LastOrDefault();
        if (user is { Length: > 0 } && !user.Equals(currentUser ?? Environment.UserName, StringComparison.OrdinalIgnoreCase))
            return (dir.Length > 0 ? $"cd {Literal(dir)}; " : "") +
                   $"runas /user:{Literal(user)} {Literal(commandLine.Replace("\"", "\\\""))}"; // \" is runas's own escape
        string rest = ArgumentsOf(commandLine).Trim();
        return $"Start-Process -FilePath {Literal(exe)}" + (rest.Length > 0 ? $" -ArgumentList {Literal(rest)}" : "") +
               (dir.Length > 0 ? $" -WorkingDirectory {Literal(dir)}" : "");
    }

    private static string Literal(string text) => "'" + text.Replace("'", "''") + "'";

    /// <summary>The account a start command runs under, when it uses runas.</summary>
    public static string? RunasUser(string startCommand) =>
        RunasPattern().Match(startCommand) is { Success: true } m ? m.Groups[1].Value.Replace("''", "'") : null;

    /// <summary>Everything after the program on a command line, with its leading space.</summary>
    private static string ArgumentsOf(string commandLine)
    {
        string line = commandLine.TrimStart();
        int end = line.StartsWith('"') ? line.IndexOf('"', 1) + 1 : line.IndexOf(' ');
        return end <= 0 || end >= line.Length ? "" : line[end..];
    }

    /// <summary>Stops a server this process may stop itself (its own account's). False when it can't.</summary>
    public static bool Kill(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill();
            process.WaitForExit(10_000);
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            return false;
        }
    }

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline)]
    private static partial Regex ThinkBlock();

    [GeneratedRegex(@"runas /user:'((?:[^']|'')*)'")]
    private static partial Regex RunasPattern();
}
