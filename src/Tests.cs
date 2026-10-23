using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

static class Tests
{
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    static int checks;
    static void Check(bool condition, string name) { if (!condition) throw new Exception(name); ++checks; }
    static Dictionary<string, object> Parse(string json) { return (Dictionary<string, object>)Json.DeserializeObject(json); }
    static void Event(Bridge.StatusData state, string kind, string id = "", string tool = "Bash", string turn = "main") { Bridge.ApplyEvent(state, new Dictionary<string, object> { { "hook_event_name", kind }, { "turn_id", turn }, { "tool_use_id", id }, { "tool_name", tool } }); }
    public static void Main()
    {
        var quota = Bridge.FormatQuota(Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":99,\"resetsAt\":null}},\"rateLimitsByLimitId\":{\"other\":{\"primary\":{\"usedPercent\":10}},\"codex\":{\"primary\":{\"usedPercent\":81,\"windowDurationMins\":10080,\"resetsAt\":1793296884},\"secondary\":null}},\"rateLimitResetCredits\":{\"availableCount\":2,\"credits\":[{\"title\":\"Full reset\",\"expiresAt\":1794007887,\"status\":\"available\"}]}}"), 1792770000);
        Check(quota["remaining"] == "19%", "select the Codex bucket");
        Check(quota["tooltip"].Contains("7 天周期"), "read the actual window length");
        Check(quota["tooltip"].Contains("可用重置次数：2"), "use authoritative reset count");
        Check(quota["tooltip"].Contains("到期时间：") && quota["tooltip"].Contains("部分权益明细"), "show expiry and partial-credit details");
        var missing = Bridge.FormatQuota(Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":0,\"resetsAt\":null}},\"rateLimitResetCredits\":null}"), 1792770000);
        Check(missing["remaining"] == "100%" && missing["reset_at"] == "0", "missing reset time is unknown");
        Check(missing["tooltip"].Contains("接口未提供"), "unknown reset credits are not zero");
        bool rejected = false;
        try { Bridge.FormatQuota(Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":\"bad\"}}}"), 0); } catch (System.IO.InvalidDataException) { rejected = true; }
        Check(rejected, "reject invalid external percentages");
        var state = new Bridge.StatusData();
        Event(state, "UserPromptSubmit"); Check(state.phase == "thinking", "prompt starts model phase");
        Event(state, "PreToolUse", "a"); Event(state, "PreToolUse", "b", "apply_patch");
        Event(state, "PostToolUse", "a"); Check(state.phase == "running", "one completed parallel tool does not clear the other");
        Event(state, "PermissionRequest", "", "apply_patch"); Check(state.phase == "waiting_approval", "permission request");
        Event(state, "PostToolUse", "b", "apply_patch"); Check(state.phase == "thinking", "tool return clears permission phase");
        Event(state, "PreToolUse", "q", "request_user_input_async"); Event(state, "PostToolUse", "q", "request_user_input_async");
        Check(state.phase == "waiting_input", "asynchronous question stays pending after dispatch");
        Event(state, "UserPromptSubmit"); Check(state.phase == "thinking", "next user prompt clears the question");
        Event(state, "PreToolUse", "child", "Bash", "child-turn"); Check(state.phase == "thinking", "do not conflate subagent turns");
        Event(state, "Stop"); Check(state.phase == "completed", "stop marks completion");
        Event(state, "UserPromptSubmit"); Event(state, "Interrupt"); Check(state.phase == "interrupted", "explicit interruption");
        var observed = new Bridge.StatusData();
        Bridge.ApplyTranscript(observed, Parse("{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"real-turn\"}}"));
        Check(observed.phase == "thinking", "observed task start");
        Bridge.ApplyTranscript(observed, Parse("{\"type\":\"response_item\",\"payload\":{\"type\":\"custom_tool_call\",\"name\":\"exec\",\"call_id\":\"real-call\"}}"));
        Check(observed.phase == "running", "observed live tool call");
        Bridge.ApplyTranscript(observed, Parse("{\"type\":\"response_item\",\"payload\":{\"type\":\"custom_tool_call_output\",\"call_id\":\"real-call\"}}"));
        Check(observed.phase == "thinking", "observed tool return");
        Bridge.ApplyTranscript(observed, Parse("{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_complete\",\"turn_id\":\"real-turn\"}}"));
        Check(observed.phase == "completed", "observed task completion");
        Console.WriteLine(checks + " checks passed.");
    }
}
