using System.Text.RegularExpressions;
using Lighthouse.Core.Native;

namespace Lighthouse.Core.Workloads;

/// <summary>Games are whatever runs from a store's game folder, minus launchers, crash reporters and anti-cheat.</summary>
public static class GameRules
{
    private static readonly string[] Roots =
    [
        @"\steamapps\common\", @"\Epic Games\", @"\EA Games\", @"\Riot Games\", @"\GOG Galaxy\Games\",
        @"\Ubisoft Game Launcher\games\", @"\XboxGames\",
    ];

    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Steamworks Shared", "DirectXRedist", "Launcher", "Epic Online Services", "EA Desktop", "Riot Client",
    };

    private static readonly string[] HelperWords =
    [
        "crash", "report", "launcher", "easyanticheat", "anticheat", "battleye", "redist", "setup", "install",
        "helper", "service", "overlay", "update", "prereq", "dxsetup", "cefprocess", "webhelper", "bugsplat",
    ];

    public static bool TryGetGame(string? path, string exeName, out string game)
    {
        game = "";
        if (string.IsNullOrEmpty(path)) return false;
        foreach (string root in Roots)
        {
            int at = path.IndexOf(root, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            string rest = path[(at + root.Length)..];
            int slash = rest.IndexOf('\\');
            if (slash <= 0) return false;
            string folder = rest[..slash];
            if (NotGames.Contains(folder)) return false;
            string lower = exeName.ToLowerInvariant();
            if (HelperWords.Any(w => lower.Contains(w, StringComparison.Ordinal))) return false;
            game = folder;
            return true;
        }
        return false;
    }
}

/// <summary>The local LLM servers Lighthouse knows: llama.cpp's llama-server, Ollama and LM Studio.</summary>
public enum LlmKind { LlamaServer, Ollama, LmStudio }

/// <summary>
/// Which processes are a local LLM (its row, the moon, the VRAM warning, Explain). Only the servers and what they run
/// models in: Ollama's command-line tool ("ollama pull") and LM Studio's ("lms") are tools working on it, not it.
/// </summary>
public static class LocalLlmApps
{
    public static LlmKind? KindOf(string name, string? path, string? commandLine)
    {
        string lower = name.ToLowerInvariant();
        if (lower == "llama-server.exe") return LlmKind.LlamaServer;
        if (lower is "ollama app.exe" or "ollama_llama_server.exe") return LlmKind.Ollama;
        if (lower == "ollama.exe")
            return commandLine is not null && NativeMethods.SplitArgs(commandLine) is [_, "serve" or "runner", ..] ? LlmKind.Ollama : null;
        if (lower == "lms.exe") return null;
        if (lower == "lm studio.exe" || (path ?? "").Contains(@"\.lmstudio", StringComparison.OrdinalIgnoreCase))
            return LlmKind.LmStudio;
        return null;
    }
}

/// <summary>A running local LLM server: a llama-server read from its command line, Ollama's server or LM Studio.</summary>
public sealed partial record LlmServer(int Pid, string Host, int Port, string? ApiKeyFile, string? Model, string CommandLine,
    long CreateTime = 0, string? Owner = null, LlmKind Kind = LlmKind.LlamaServer)
{
    public const int OllamaPort = 11434;
    public const int LmStudioPort = 1234;

    /// <summary>"llama-server", "Ollama", "LM Studio": for messages.</summary>
    public string AppName => Kind switch
    {
        LlmKind.Ollama => "Ollama",
        LlmKind.LmStudio => "LM Studio",
        _ => "llama-server",
    };

    /// <summary>
    /// The server among these processes: a llama-server first, then Ollama's (<c>ollama serve</c>, at OLLAMA_HOST or
    /// its default port), then LM Studio's main process (its server's default port; <c>lmStudioPort</c> in the
    /// settings changes it).
    /// </summary>
    public static LlmServer? Find(IEnumerable<ProcessFacts> processes, string? ollamaHost)
    {
        var all = processes.ToList();
        if (all.FirstOrDefault(f => f.Name.Equals("llama-server.exe", StringComparison.OrdinalIgnoreCase) && f.CommandLine is not null) is { } llama
            && Parse(llama.Pid, llama.CommandLine!) is { } server)
            return server with { CreateTime = llama.CreateTime, Owner = llama.Owner };
        if (all.FirstOrDefault(f => f.Name.Equals("ollama.exe", StringComparison.OrdinalIgnoreCase) && f.CommandLine is { } cmd
                                    && NativeMethods.SplitArgs(cmd) is [_, "serve", ..]) is { } ollama)
        {
            var (host, port) = OllamaAddress(ollamaHost);
            return new LlmServer(ollama.Pid, host, port, null, null, ollama.CommandLine!, ollama.CreateTime, ollama.Owner, LlmKind.Ollama);
        }
        var studio = all.Where(f => f.Name.Equals("LM Studio.exe", StringComparison.OrdinalIgnoreCase)).ToList();
        if (studio.FirstOrDefault(f => !studio.Any(o => o.Pid == f.ParentPid)) is { } main)
            return new LlmServer(main.Pid, "127.0.0.1", LmStudioPort, null, null, main.CommandLine ?? "", main.CreateTime, main.Owner, LlmKind.LmStudio);
        return null;
    }

    /// <summary>
    /// Where Ollama listens, from OLLAMA_HOST as Ollama reads it: "0.0.0.0", "0.0.0.0:11434", "http://127.0.0.1:8000",
    /// "[::1]:11434". Unset or unreadable is 127.0.0.1 on its default port.
    /// </summary>
    public static (string Host, int Port) OllamaAddress(string? value)
    {
        string v = (value ?? "").Trim();
        if (v.Length == 0) return ("127.0.0.1", OllamaPort);
        int scheme = v.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) v = v[(scheme + 3)..];
        v = v.TrimEnd('/');
        if (v.StartsWith('['))
        {
            int close = v.IndexOf(']');
            if (close < 0) return ("127.0.0.1", OllamaPort);
            string host6 = v[..(close + 1)];
            return (host6, v.Length > close + 2 && v[close + 1] == ':' && int.TryParse(v[(close + 2)..], out int p6) ? p6 : OllamaPort);
        }
        int colon = v.LastIndexOf(':');
        if (colon < 0) return (v, OllamaPort);
        return (v[..colon], int.TryParse(v[(colon + 1)..], out int p) ? p : OllamaPort);
    }

    /// <summary>
    /// The server's address when it listens on this PC, else null: Explain talks to nothing else, because any program
    /// can start a process called llama-server.exe and name a host on its command line (or set OLLAMA_HOST). A server
    /// listening on every address (0.0.0.0) is reached on 127.0.0.1.
    /// </summary>
    public string? LocalBaseUrl => Port is < 1 or > 65535 ? null : Host.ToLowerInvariant() switch
    {
        "127.0.0.1" or "localhost" or "0.0.0.0" => $"http://127.0.0.1:{Port}",
        "::1" or "[::1]" or "::" or "[::]" => $"http://[::1]:{Port}",
        _ => null,
    };

    /// <summary>"Qwen3-30B-A3B" from "Qwen3-30B-A3B-UD-Q4_K_XL.gguf".</summary>
    public string? ModelShort
    {
        get
        {
            if (Model is null) return null;
            string stem = Path.GetFileNameWithoutExtension(Model);
            var kept = stem.Split('-').TakeWhile(t => !QuantToken().IsMatch(t)).ToList();
            return kept.Count > 0 ? string.Join("-", kept) : stem;
        }
    }

    public static LlmServer? Parse(int pid, string commandLine)
    {
        string[] args = NativeMethods.SplitArgs(commandLine);
        if (args.Length == 0) return null;
        string host = "127.0.0.1";
        int port = 8080;
        string? keyFile = null, model = null;
        for (int i = 1; i < args.Length - 1; i++)
        {
            switch (args[i])
            {
                case "--host": host = args[i + 1]; break;
                case "--port": int.TryParse(args[i + 1], out port); break;
                case "--api-key-file": keyFile = args[i + 1]; break;
                case "-m" or "--model": model = args[i + 1]; break;
            }
        }
        return new LlmServer(pid, host, port, keyFile, model, commandLine);
    }

    [GeneratedRegex(@"^(UD|I?Q\d.*|BF16|F16|F32|GGUF)$", RegexOptions.IgnoreCase)]
    private static partial Regex QuantToken();
}
