using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Lighthouse.Core.Llm;
using Lighthouse.Core.Workloads;

namespace Lighthouse.Tests;

public class LocalLlmAppsTests
{
    [Theory]
    [InlineData("llama-server.exe", @"C:\llm\bin\llama-server.exe", @"C:\llm\bin\llama-server.exe -m m.gguf", LlmKind.LlamaServer)]
    [InlineData("ollama.exe", @"C:\Users\u\AppData\Local\Programs\Ollama\ollama.exe", @"""C:\Users\u\AppData\Local\Programs\Ollama\ollama.exe"" serve", LlmKind.Ollama)]
    [InlineData("ollama.exe", @"C:\Users\u\AppData\Local\Programs\Ollama\ollama.exe", @"ollama.exe runner --model C:\blobs\sha256-1 --port 50123", LlmKind.Ollama)]
    [InlineData("ollama app.exe", @"C:\Users\u\AppData\Local\Programs\Ollama\ollama app.exe", @"""ollama app.exe""", LlmKind.Ollama)]
    [InlineData("ollama_llama_server.exe", @"C:\Users\u\AppData\Local\Programs\Ollama\lib\ollama\runners\cuda\ollama_llama_server.exe", "x", LlmKind.Ollama)]
    [InlineData("LM Studio.exe", @"C:\Users\u\AppData\Local\Programs\LM Studio\LM Studio.exe", @"""LM Studio.exe""", LlmKind.LmStudio)]
    [InlineData("node.exe", @"C:\Users\u\.lmstudio\extensions\backends\llama.cpp\node.exe", "node.exe worker.js", LlmKind.LmStudio)]
    public void The_servers_and_what_they_run_models_in_are_the_local_LLM(string name, string path, string commandLine, LlmKind kind) =>
        Assert.Equal(kind, LocalLlmApps.KindOf(name, path, commandLine));

    [Theory]
    [InlineData("ollama.exe", "ollama.exe pull qwen3:8b")] // the command-line tool downloading a model
    [InlineData("ollama.exe", "ollama.exe run qwen3:8b")]   // a chat in a terminal: a client of the server
    [InlineData("ollama.exe", null)]                         // can't read its command line: not sure, so not counted
    [InlineData("lms.exe", "lms.exe get qwen")]              // LM Studio's command-line tool
    public void Their_command_line_tools_are_not(string name, string? commandLine) =>
        Assert.Null(LocalLlmApps.KindOf(name, @"C:\Users\u\.lmstudio\bin\" + name, commandLine));

    private static ProcessFacts P(int pid, int parent, string name, string? cmd) => new(pid, parent, name, @"C:\x\" + name, cmd, 1);

    [Fact]
    public void A_llama_server_comes_first_then_Ollama_then_LM_Studio()
    {
        var llama = P(10, 1, "llama-server.exe", @"C:\x\llama-server.exe --port 8081");
        var ollama = P(20, 1, "ollama.exe", "ollama.exe serve");
        var studio = P(30, 1, "LM Studio.exe", "\"LM Studio.exe\"");
        var studioChild = P(31, 30, "LM Studio.exe", "\"LM Studio.exe\" --type=renderer");
        Assert.Equal((LlmKind.LlamaServer, 10, 8081), Of(LlmServer.Find([studioChild, studio, ollama, llama], null)));
        Assert.Equal((LlmKind.Ollama, 20, 11434), Of(LlmServer.Find([studioChild, studio, ollama], null)));
        Assert.Equal((LlmKind.LmStudio, 30, 1234), Of(LlmServer.Find([studioChild, studio], null)));
        Assert.Null(LlmServer.Find([P(40, 1, "ollama.exe", "ollama.exe pull x")], null));
    }

    private static (LlmKind, int, int)? Of(LlmServer? s) => s is null ? null : (s.Kind, s.Pid, s.Port);

    [Theory]
    [InlineData(null, "127.0.0.1", 11434)]
    [InlineData("0.0.0.0", "0.0.0.0", 11434)]
    [InlineData("0.0.0.0:8000", "0.0.0.0", 8000)]
    [InlineData("http://127.0.0.1:9000/", "127.0.0.1", 9000)]
    [InlineData("[::1]:11500", "[::1]", 11500)]
    [InlineData("gpu-box.local:11434", "gpu-box.local", 11434)]
    public void Ollama_is_found_where_OLLAMA_HOST_says(string? value, string host, int port) =>
        Assert.Equal((host, port), LlmServer.OllamaAddress(value));

    [Fact]
    public void An_Ollama_on_another_machine_is_never_asked()
    {
        var (host, port) = LlmServer.OllamaAddress("gpu-box.local:11434");
        Assert.Null(new LlmServer(1, host, port, null, null, "", Kind: LlmKind.Ollama).LocalBaseUrl);
    }
}

/// <summary>A tiny HTTP server on 127.0.0.1 answering as Ollama or LM Studio do, recording what it was sent.</summary>
internal sealed class FakeLlmServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Func<string, string, string, (int Status, string Body)> _answer;
    private readonly CancellationTokenSource _stop = new();
    public List<(string Method, string Path, string Body)> Requests { get; } = [];
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public FakeLlmServer(Func<string, string, string, (int Status, string Body)> answer)
    {
        _answer = answer;
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (OperationCanceledException) { return; }
            using (client)
            {
                var stream = client.GetStream();
                var head = new StringBuilder();
                var one = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await stream.ReadAsync(one) == 1)
                    head.Append((char)one[0]);
                string[] lines = head.ToString().Split("\r\n");
                string[] first = lines[0].Split(' ');
                int length = lines.Select(l => l.Split(':', 2)).Where(kv => kv[0].Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    .Select(kv => int.Parse(kv[1].Trim())).FirstOrDefault();
                var body = new byte[length];
                for (int read = 0; read < length;) read += await stream.ReadAsync(body.AsMemory(read));
                string text = Encoding.UTF8.GetString(body);
                lock (Requests) Requests.Add((first[0], first[1], text));
                var (status, reply) = _answer(first[0], first[1], text);
                byte[] bytes = Encoding.UTF8.GetBytes(reply);
                string header = $"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
                await stream.WriteAsync(bytes);
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}

public class LocalLlmAskTests
{
    private static readonly ChatTurn[] Question = [new("user", "Readings…\nQuestion: What is going on?")];

    [Fact]
    public async Task Ollama_is_asked_with_the_model_it_has_loaded_and_without_thinking()
    {
        using var server = new FakeLlmServer((method, path, body) => path switch
        {
            "/api/ps" => (200, """{"models":[{"name":"qwen3:8b","model":"qwen3:8b"}]}"""),
            "/api/chat" when body.Contains("\"think\":false") => (400, """{"error":"\"qwen3:8b\" does not support thinking"}"""),
            "/api/chat" => (200, """{"message":{"role":"assistant","content":"<think>hm</think>Chrome is busy. Nothing needs attention."}}"""),
            _ => (404, "{}"),
        });
        var answer = await LocalLlm.AskAsync(new LlmServer(1, "127.0.0.1", server.Port, null, null, "", Kind: LlmKind.Ollama), "a PC", Question, default);
        Assert.Equal(new LlmAnswer("Chrome is busy. Nothing needs attention.", true), answer);
        var chats = server.Requests.Where(r => r.Path == "/api/chat").ToList();
        Assert.Equal(2, chats.Count); // asked without thinking, then again plainly when that was refused
        var sent = JsonNode.Parse(chats[1].Body)!;
        Assert.Equal("qwen3:8b", sent["model"]!.GetValue<string>());
        Assert.False(sent["stream"]!.GetValue<bool>());
        Assert.Equal("system", sent["messages"]![0]!["role"]!.GetValue<string>());
        Assert.Contains("Question: What is going on?", sent["messages"]![1]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Ollama_without_a_loaded_model_uses_its_newest_one()
    {
        using var server = new FakeLlmServer((_, path, _) => path switch
        {
            "/api/ps" => (200, """{"models":[]}"""),
            "/api/tags" => (200, """{"models":[{"name":"old:1b","modified_at":"2026-01-02T00:00:00Z"},{"name":"new:8b","modified_at":"2026-10-01T00:00:00Z"}]}"""),
            _ => (200, """{"message":{"content":"Fine."}}"""),
        });
        Assert.Equal("new:8b", await LocalLlm.OllamaModelAsync($"http://127.0.0.1:{server.Port}", default));
    }

    [Fact]
    public async Task Ollama_with_no_models_says_so_and_asks_nothing()
    {
        using var server = new FakeLlmServer((_, path, _) => path is "/api/ps" or "/api/tags" ? (200, """{"models":[]}""") : (500, "{}"));
        var answer = await LocalLlm.AskAsync(new LlmServer(1, "127.0.0.1", server.Port, null, null, "", Kind: LlmKind.Ollama), "a PC", Question, default);
        Assert.False(answer.Ok);
        Assert.Contains("ollama pull", answer.Text);
        Assert.DoesNotContain(server.Requests, r => r.Path == "/api/chat");
    }

    [Fact]
    public async Task LM_Studio_is_asked_with_the_model_it_has_loaded()
    {
        using var server = new FakeLlmServer((_, path, _) => path switch
        {
            "/api/v0/models" => (200, """{"data":[{"id":"nomic-embed","type":"embeddings","state":"loaded"},{"id":"gemma-3-4b","type":"llm","state":"not-loaded"},{"id":"qwen3-8b","type":"llm","state":"loaded"}]}"""),
            "/v1/chat/completions" => (200, """{"choices":[{"message":{"role":"assistant","content":"The GPU is idle."}}]}"""),
            _ => (404, "{}"),
        });
        var answer = await LocalLlm.AskAsync(new LlmServer(1, "127.0.0.1", server.Port, null, null, "", Kind: LlmKind.LmStudio), "a PC", Question, default);
        Assert.Equal(new LlmAnswer("The GPU is idle.", true), answer);
        var sent = JsonNode.Parse(server.Requests.Single(r => r.Path == "/v1/chat/completions").Body)!;
        Assert.Equal("qwen3-8b", sent["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_long_conversation_sends_the_readings_and_the_last_eight_messages()
    {
        using var server = new FakeLlmServer((_, path, _) => path == "/v1/chat/completions"
            ? (200, """{"choices":[{"message":{"content":"Ok."}}]}""") : (404, "{}"));
        var conversation = Enumerable.Range(0, 15).Select(i => new ChatTurn(i % 2 == 0 ? "user" : "assistant", $"m{i}")).ToList();
        await LocalLlm.AskAsync(new LlmServer(1, "127.0.0.1", server.Port, null, null, ""), "a PC", conversation, default);
        var messages = JsonNode.Parse(server.Requests.Single().Body)!["messages"]!.AsArray();
        Assert.Equal(new[] { "system", "m0", "m7", "m8", "m9", "m10", "m11", "m12", "m13", "m14" },
            messages.Select((m, i) => i == 0 ? m!["role"]!.GetValue<string>() : m!["content"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Unloading_Ollama_frees_every_loaded_model()
    {
        using var server = new FakeLlmServer((_, path, _) => path == "/api/ps"
            ? (200, """{"models":[{"name":"a:8b"},{"name":"b:1b"}]}""") : (200, """{"done":true}"""));
        Assert.Equal(2, await LocalLlm.UnloadOllamaAsync($"http://127.0.0.1:{server.Port}", default));
        var unloads = server.Requests.Where(r => r.Path == "/api/generate").Select(r => JsonNode.Parse(r.Body)!).ToList();
        Assert.Equal(new[] { "a:8b", "b:1b" }, unloads.Select(u => u["model"]!.GetValue<string>()));
        Assert.All(unloads, u => Assert.Equal(0, u["keep_alive"]!.GetValue<int>()));
    }
}
