using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

public static partial class Bridge
{
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
    static string DataDir { get { return Environment.GetEnvironmentVariable("CODEX_QUOTA_DATA_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuota"); } }
    static long UnixNow { get { return DateTimeOffset.UtcNow.ToUnixTimeSeconds(); } }
    static Dictionary<string, object> Object(object value) { return value as Dictionary<string, object>; }
    static object Field(Dictionary<string, object> obj, string key) { object value; return obj != null && obj.TryGetValue(key, out value) ? value : null; }
    static string Text(object value) { return value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture); }
    static string TimeText(object value) { return value == null ? "未知" : DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(value)).ToLocalTime().ToString("yyyy-MM-dd HH:mm"); }
    static string Flat(string value) { return value.Replace("\r", "").Replace("\n", " | "); }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool MoveFileEx(string source, string destination, int flags);

    static void WriteIni(string path, Dictionary<string, string> values)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        string temporary = path + "." + Process.GetCurrentProcess().Id + ".tmp";
        string content = "[data]\r\n" + string.Join("\r\n", values.Select(p => p.Key + "=" + p.Value)) + "\r\n";
        File.WriteAllText(temporary, content, Encoding.Unicode);
        // Redirected user folders can put two paths on different backing volumes.
        if (!MoveFileEx(temporary, path, 3)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    static string FindCodex(string explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            if (!File.Exists(explicitPath)) throw new FileNotFoundException("配置的 codex.exe 不存在", explicitPath);
            return Path.GetFullPath(explicitPath);
        }
        string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (Directory.Exists(desktop))
        {
            string newest = Directory.GetDirectories(desktop).Select(d => Path.Combine(d, "codex.exe")).Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (newest != null) return newest;
        }
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string candidate = Path.Combine(directory.Trim('"'), "codex.exe");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("未找到 codex.exe；请在插件旁的 CodexQuota.ini 中设置 codex_path。");
    }

    static Dictionary<string, object> Rpc(Process process, int id, string method, object parameters)
    {
        process.StandardInput.WriteLine(Json.Serialize(new { id = id, method = method, @params = parameters }));
        process.StandardInput.Flush();
        Stopwatch deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < 25000)
        {
            var pending = process.StandardOutput.ReadLineAsync();
            if (!pending.Wait((int)Math.Max(1, 25000 - deadline.ElapsedMilliseconds))) throw new TimeoutException("Codex 接口查询超过 25 秒。");
            if (pending.Result == null) throw new IOException("Codex 查询进程提前退出。");
            var response = Object(Json.DeserializeObject(pending.Result));
            if (Text(Field(response, "id")) != id.ToString()) continue;
            var error = Object(Field(response, "error"));
            if (error != null) throw new InvalidOperationException(Text(Field(error, "message")));
            var result = Object(Field(response, "result"));
            if (result == null) throw new InvalidDataException("Codex 返回了无效的查询结果。");
            return result;
        }
        throw new TimeoutException("Codex 接口查询超时。");
    }

    public static Dictionary<string, string> FormatQuota(Dictionary<string, object> result, long now)
    {
        var bucket = Object(Field(Object(Field(result, "rateLimitsByLimitId")), "codex")) ?? Object(Field(result, "rateLimits"));
        if (bucket == null) throw new InvalidDataException("账户未返回 Codex 额度。请确认 Codex 使用 ChatGPT 账户登录。");
        var primary = Object(Field(bucket, "primary"));
        var secondary = Object(Field(bucket, "secondary"));
        var windows = new[] { primary, secondary }.Where(w => w != null).ToArray();
        var percentages = new List<string>();
        var lines = new List<string> { "Codex 额度" };
        long nextReset = 0;
        foreach (var window in windows)
        {
            object used = Field(window, "usedPercent");
            if (!(used is int || used is long || used is decimal || used is double)) throw new InvalidDataException("额度比例不是数值。");
            double percent = Convert.ToDouble(used, CultureInfo.InvariantCulture);
            if (double.IsNaN(percent) || double.IsInfinity(percent) || percent < 0) throw new InvalidDataException("额度比例无效。");
            string remaining = Math.Max(0, 100 - percent).ToString("0.#", CultureInfo.InvariantCulture) + "%";
            percentages.Add(remaining);
            object minutes = Field(window, "windowDurationMins");
            string duration = minutes == null ? "额度周期" : Duration(Convert.ToInt64(minutes));
            object reset = Field(window, "resetsAt");
            lines.Add(duration + "：剩余 " + remaining);
            lines.Add("额度重置：" + TimeText(reset));
            if (reset != null)
            {
                long at = Convert.ToInt64(reset);
                if (nextReset == 0 || at < nextReset) nextReset = at;
            }
        }
        var credits = Object(Field(bucket, "credits"));
        if (credits != null)
        {
            if (Field(credits, "balance") != null && Text(Field(credits, "balance")) != "0") lines.Add("额外 Credits：" + Text(Field(credits, "balance")));
            if (Field(credits, "unlimited") is bool && (bool)Field(credits, "unlimited")) lines.Add("Credits：不限额");
        }
        var resets = Object(Field(result, "rateLimitResetCredits"));
        if (resets == null || Field(resets, "availableCount") == null) lines.Add("可用重置次数：未知");
        else
        {
            lines.Add("可用重置次数：" + Text(Field(resets, "availableCount")));
            object[] details = Field(resets, "credits") as object[];
            if (details == null) lines.Add("重置到期：未知");
            else foreach (object row in details)
            {
                var credit = Object(row);
                lines.Add("到期时间：" + TimeText(Field(credit, "expiresAt")));
            }
            if (details != null && details.Length < Convert.ToInt32(Field(resets, "availableCount"))) lines.Add("重置明细：" + details.Length + "/" + Text(Field(resets, "availableCount")));
        }
        lines.Add("更新：" + DateTimeOffset.FromUnixTimeSeconds(now).ToLocalTime().ToString("HH:mm:ss"));
        lines.Add("下次更新：" + DateTimeOffset.FromUnixTimeSeconds(now + 120).ToLocalTime().ToString("HH:mm:ss"));
        return new Dictionary<string, string> { { "remaining", percentages.Count == 0 ? "--" : string.Join("/", percentages) }, { "reset_at", nextReset.ToString() }, { "updated", now.ToString() }, { "tooltip", string.Join("\u2028", lines.Select(Flat)) }, { "error", "" } };
    }

    static string Duration(long minutes)
    {
        if (minutes > 0 && minutes % 1440 == 0) return (minutes / 1440) + " 天周期";
        if (minutes > 0 && minutes % 60 == 0) return (minutes / 60) + " 小时周期";
        return minutes + " 分钟周期";
    }

    static int Quota(string output, string codex)
    {
        Process process = null;
        System.Threading.Tasks.Task<string> errors = null;
        try
        {
            process = new Process { StartInfo = new ProcessStartInfo(FindCodex(codex), "app-server --listen stdio://") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 } };
            process.Start();
            errors = process.StandardError.ReadToEndAsync();
            Rpc(process, 1, "initialize", new { clientInfo = new { name = "codex_quota_trafficmonitor", title = "Codex Quota", version = "0.1.1" } });
            process.StandardInput.WriteLine("{\"method\":\"initialized\",\"params\":{}}");
            process.StandardInput.Flush();
            var result = Rpc(process, 2, "account/rateLimits/read", new { });
            WriteIni(output, FormatQuota(result, UnixNow));
            return 0;
        }
        catch (Exception error)
        {
            string message = error.Message;
            if (process != null && process.HasExited && errors != null && errors.IsCompleted) message += " " + errors.Result;
            WriteIni(output, new Dictionary<string, string> { { "remaining", "ERR" }, { "reset_at", "0" }, { "updated", UnixNow.ToString() }, { "tooltip", "额度获取失败：" + Flat(message) }, { "error", Flat(message) } });
            Console.Error.WriteLine(message);
            return 1;
        }
        finally
        {
            if (process != null)
            {
                if (!process.HasExited) { process.StandardInput.Close(); if (!process.WaitForExit(1000)) { process.Kill(); process.WaitForExit(); } }
                process.Dispose();
            }
        }
    }

    public sealed class StatusData
    {
        public string turn = "";
        public string phase = "idle";
        public Dictionary<string, string> tools = new Dictionary<string, string>();
        public Dictionary<string, string> toolNames = new Dictionary<string, string>();
        public int owner;
        public long ownerStarted;
    }

    static string PendingPhase(StatusData state)
    {
        return state.tools.Values.Contains("running") ? "running" : state.tools.Values.Contains("waiting_input") ? "waiting_input" : state.tools.Values.Contains("waiting_approval") ? "waiting_approval" : "thinking";
    }

    public static void ApplyEvent(StatusData state, Dictionary<string, object> input)
    {
        string kind = Text(Field(input, "hook_event_name"));
        string turn = Text(Field(input, "turn_id"));
        string tool = Text(Field(input, "tool_name"));
        string id = Text(Field(input, "tool_use_id"));
        if (kind == "UserPromptSubmit") { state.turn = turn; state.phase = "thinking"; state.tools.Clear(); state.toolNames.Clear(); return; }
        if (kind == "SessionStart") { state.turn = ""; state.phase = Text(Field(input, "source")) == "compact" ? "thinking" : "idle"; state.tools.Clear(); state.toolNames.Clear(); return; }
        if (state.turn != "" && turn != "" && turn != state.turn) return;
        if (state.turn == "") state.turn = turn;
        if (kind == "PreToolUse")
        {
            string key = id == "" ? tool : id;
            state.toolNames[key] = tool;
            state.tools[key] = tool.IndexOf("request_user_input", StringComparison.OrdinalIgnoreCase) >= 0 || tool.IndexOf("requestUserInput", StringComparison.OrdinalIgnoreCase) >= 0 || tool.IndexOf("elicitation", StringComparison.OrdinalIgnoreCase) >= 0 ? "waiting_input" : "running";
        }
        else if (kind == "PermissionRequest")
        {
            // PermissionRequest has no tool_use_id in the documented schema.
            string match = state.toolNames.Keys.LastOrDefault(k => state.toolNames[k] == tool && state.tools.ContainsKey(k) && state.tools[k] == "running");
            state.tools[match ?? "permission:" + tool] = "waiting_approval";
        }
        else if (kind == "PostToolUse")
        {
            string key = id == "" ? tool : id;
            state.tools.Remove(key); state.toolNames.Remove(key);
            state.tools.Remove("permission:" + tool);
        }
        else if (kind == "Stop" || kind == "Interrupt" || kind == "SessionEnd")
        {
            state.tools.Clear();
            state.toolNames.Clear();
            state.phase = kind == "Stop" ? "completed" : kind == "Interrupt" ? "interrupted" : "idle";
            return;
        }
        state.phase = PendingPhase(state);
    }

    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo { public IntPtr reserved1, peb, reserved2a, reserved2b, pid, parent; }
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr handle, int kind, out ProcessInfo data, int size, out int returned);
    static Process Owner()
    {
        Process current = Process.GetCurrentProcess();
        for (int depth = 0; depth < 6; depth++)
        {
            ProcessInfo info; int returned;
            if (NtQueryInformationProcess(current.Handle, 0, out info, Marshal.SizeOf(typeof(ProcessInfo)), out returned) != 0) return null;
            int parent = info.parent.ToInt32();
            if (parent == 0) return null;
            current = Process.GetProcessById(parent);
            if (current.ProcessName.Equals("codex", StringComparison.OrdinalIgnoreCase)) return current;
        }
        return null;
    }

    static int Hook()
    {
        var input = Object(Json.DeserializeObject(Console.In.ReadToEnd()));
        string session = Text(Field(input, "session_id"));
        if (session.Length == 0 || session.Length > 100 || session.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_')) throw new InvalidDataException("Invalid session_id.");
        string directory = Path.Combine(DataDir, "status");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, session + ".ini");
        using (var mutex = new Mutex(false, "Local\\CodexQuota-" + session))
        {
            if (!mutex.WaitOne(1500)) throw new TimeoutException("Status file is busy.");
            try
            {
                StatusData state = new StatusData();
                if (File.Exists(path))
                {
                    string data = File.ReadAllLines(path, Encoding.Unicode).First(l => l.StartsWith("state_json="));
                    state = Json.Deserialize<StatusData>(data.Substring(11));
                }
                ApplyEvent(state, input);
                Process owner = Owner();
                if (owner != null) { state.owner = owner.Id; state.ownerStarted = owner.StartTime.ToFileTimeUtc(); }
                WriteIni(path, new Dictionary<string, string> { { "phase", state.phase }, { "updated", UnixNow.ToString() }, { "updated_ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString() }, { "owner", state.owner.ToString() }, { "owner_started", state.ownerStarted.ToString() }, { "origin", "hooks" }, { "state_json", Json.Serialize(state) } });
            }
            finally { mutex.ReleaseMutex(); }
        }
        Console.WriteLine("{}");
        return 0;
    }

    public static void ApplyTranscript(StatusData state, Dictionary<string, object> record)
    {
        var payload = Object(Field(record, "payload"));
        string envelope = Text(Field(record, "type"));
        string kind = Text(Field(payload, "type"));
        string eventName = "";
        string tool = Text(Field(payload, "name"));
        string id = Text(Field(payload, "call_id"));
        if (envelope == "event_msg")
        {
            if (kind == "task_started") eventName = "UserPromptSubmit";
            else if (kind == "task_complete") eventName = "Stop";
            else return;
        }
        else if (envelope == "response_item")
        {
            if (kind == "function_call" || kind == "custom_tool_call") eventName = "PreToolUse";
            else if (kind == "function_call_output" || kind == "custom_tool_call_output")
            {
                eventName = "PostToolUse";
                if (state.toolNames.ContainsKey(id)) tool = state.toolNames[id];
            }
            else if (kind == "message" && Text(Field(payload, "role")) == "user")
            {
                foreach (string key in state.tools.Keys.Where(k => state.tools[k] == "waiting_input").ToArray()) { state.tools.Remove(key); state.toolNames.Remove(key); }
                state.phase = PendingPhase(state);
                return;
            }
            else return;
        }
        else return;
        string turn = Text(Field(payload, "turn_id"));
        ApplyEvent(state, new Dictionary<string, object> { { "hook_event_name", eventName }, { "turn_id", turn == "" ? state.turn : turn }, { "tool_name", tool }, { "tool_use_id", id } });
    }

    static int Watch(string transcript, int ownerId)
    {
        var owner = Process.GetProcessById(ownerId);
        if (!owner.ProcessName.Equals("codex", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The owner must be a running Codex process.");
        var self = Process.GetCurrentProcess();
        string session = "", previousPhase = "";
        var state = new StatusData();
        Directory.CreateDirectory(Path.Combine(DataDir, "status"));
        // shortcut: this compatibility reader targets the verified local JSONL format; prefer Hooks after reopening a chat.
        using (var stream = new FileStream(transcript, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var line = new MemoryStream())
        {
            while (!owner.HasExited)
            {
                bool changed = false;
                while (stream.Position < stream.Length)
                {
                    int value = stream.ReadByte();
                    if (value != 10) { line.WriteByte((byte)value); continue; }
                    string text = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r');
                    line.SetLength(0);
                    if (text.Length == 0) continue;
                    var record = Object(Json.DeserializeObject(text));
                    if (Text(Field(record, "type")) == "session_meta")
                    {
                        session = Text(Field(Object(Field(record, "payload")), "id"));
                        if (session.Length == 0 || session.Length > 100 || session.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_')) throw new InvalidDataException("Invalid transcript session id.");
                    }
                    ApplyTranscript(state, record);
                    changed = true;
                }
                if (changed && session != "")
                {
                    string path = Path.Combine(DataDir, "status", session + ".ini");
                    using (var mutex = new Mutex(false, "Local\\CodexQuota-" + session))
                    {
                        if (!mutex.WaitOne(1500)) throw new TimeoutException("Status file is busy.");
                        try
                        {
                            if (File.Exists(path) && File.ReadAllLines(path, Encoding.Unicode).Contains("origin=hooks")) { Console.WriteLine("Native Hooks took over."); return 0; }
                            WriteIni(path, new Dictionary<string, string> { { "phase", state.phase }, { "updated", UnixNow.ToString() }, { "owner", self.Id.ToString() }, { "owner_started", self.StartTime.ToFileTimeUtc().ToString() }, { "origin", "transcript" }, { "state_json", Json.Serialize(state) } });
                        }
                        finally { mutex.ReleaseMutex(); }
                    }
                    if (previousPhase != state.phase) { Console.WriteLine(DateTimeOffset.UtcNow.ToString("o") + " " + session + " " + state.phase); previousPhase = state.phase; }
                }
                Thread.Sleep(250);
            }
        }
        return 0;
    }

    static int ConfigureHooks(bool enable)
    {
        string codexDirectory = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        string path = Path.Combine(codexDirectory, "hooks.json");
        if (!enable && !File.Exists(path)) { Console.WriteLine("没有已安装的状态监听配置。"); return 0; }
        Directory.CreateDirectory(codexDirectory);
        string original = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "{}";
        var config = Object(Json.DeserializeObject(original));
        if (config == null) throw new InvalidDataException("hooks.json 必须是 JSON 对象；文件未修改。");
        var hooks = Object(Field(config, "hooks"));
        if (Field(config, "hooks") != null && hooks == null) throw new InvalidDataException("hooks 字段必须是 JSON 对象；文件未修改。");
        if (hooks == null) { hooks = new Dictionary<string, object>(); config["hooks"] = hooks; }
        string executable = Path.GetFullPath(System.Reflection.Assembly.GetExecutingAssembly().Location);
        string command = "\"" + executable + "\"";
        string[] events = { "SessionStart", "UserPromptSubmit", "PreToolUse", "PermissionRequest", "PostToolUse", "Stop", "Interrupt", "SessionEnd" };
        foreach (string kind in events)
        {
            object stored = Field(hooks, kind);
            if (stored != null && !(stored is object[])) throw new InvalidDataException("事件配置不是数组：" + kind);
            var groups = stored == null ? new List<object>() : new List<object>((object[])stored);
            var kept = new List<object>();
            foreach (object item in groups)
            {
                var group = Object(item);
                var handlers = Field(group, "hooks") as object[];
                if (group == null || handlers == null) throw new InvalidDataException("事件配置无效：" + kind + "；文件未修改。");
                var others = handlers.Where(h => Text(Field(Object(h), "command")) != command).ToArray();
                if (others.Length == handlers.Length) kept.Add(group);
                else if (others.Length > 0) { group["hooks"] = others; kept.Add(group); }
            }
            if (enable) kept.Add(new Dictionary<string, object> { { "hooks", new object[] { new Dictionary<string, object> { { "type", "command" }, { "command", command }, { "timeout", 3 } } } } });
            if (kept.Count > 0) hooks[kind] = kept.ToArray();
            else hooks.Remove(kind);
        }
        string rewritten = Json.Serialize(config);
        if (File.Exists(path)) File.Copy(path, path + ".codexquota-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-ffff") + ".bak");
        string temporary = path + ".codexquota.tmp";
        File.WriteAllText(temporary, rewritten, new UTF8Encoding(false));
        if (!MoveFileEx(temporary, path, 3)) throw new Win32Exception(Marshal.GetLastWin32Error());
        Console.WriteLine(enable ? "已添加 CodexQuota 状态监听 Hooks；原有配置已保留并备份。" : "已移除当前目录下 CodexQuota 的状态监听 Hooks；原有配置已保留。 ");
        Console.WriteLine(path);
        if (enable) Console.WriteLine("下一步：打开 Codex CLI，输入 /hooks，审阅并信任 CodexQuotaBridge.exe 的监听 Hooks。现有聊天可能需要重新打开。 ");
        return 0;
    }

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "enable") return ConfigureHooks(true);
            if (args.Length == 1 && args[0] == "disable") return ConfigureHooks(false);
            if (args.Length == 2 && args[0] == "monitor") return Monitor(int.Parse(args[1], CultureInfo.InvariantCulture));
            if (args.Length == 3 && args[0] == "watch") return Watch(args[1], int.Parse(args[2], CultureInfo.InvariantCulture));
            if (args.Length > 0 && args[0] == "quota") return Quota(args[1], args.Length > 2 ? args[2] : "");
            if (args.Length != 0) throw new ArgumentException("Usage: CodexQuotaBridge.exe [quota OUTPUT_INI [CODEX_EXE]]");
            return Hook();
        }
        catch (Exception error) { Console.Error.WriteLine("CodexQuota: " + error.Message); return 1; }
    }
}
