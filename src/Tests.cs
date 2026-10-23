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
        Check(quota["tooltip"].Contains("到期时间：") && quota["tooltip"].Contains("重置明细：1/2"), "show expiry and partial-credit details");
        Check(!quota["tooltip"].Contains("所有时间") && !quota["tooltip"].Contains("状态来自"), "only Codex data in tooltip");
        var missing = Bridge.FormatQuota(Parse("{\"rateLimits\":{\"primary\":{\"usedPercent\":0,\"resetsAt\":null}},\"rateLimitResetCredits\":null}"), 1792770000);
        Check(missing["remaining"] == "100%" && missing["reset_at"] == "0", "missing reset time is unknown");
        Check(missing["tooltip"].Contains("未知"), "unknown reset credits are not zero");
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
        Check(state.phase == "thinking", "asynchronous question dispatch does not mean the task is blocked");
        Event(state, "PreToolUse", "sync", "request_user_input"); Check(state.phase == "waiting_input", "synchronous input blocks until answered");
        Event(state, "PreToolUse", "parallel", "Bash"); Check(state.phase == "running", "parallel work takes priority over a pending question");
        Event(state, "PostToolUse", "parallel", "Bash"); Event(state, "PostToolUse", "sync", "request_user_input");
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
        var complete = new Bridge.Activity { Id = "one", Title = "One", Phase = "completed", Changed = 99 };
        var busy = new Bridge.Activity { Id = "two", Title = "Two", Phase = "running", Changed = 99 };
        var aggregate = Bridge.FormatActivity(new[] { complete, busy }, 100);
        Check(aggregate["active"] == "1" && aggregate["label"] == "运行中 1", "another working chat takes priority over this completed chat");
        var second = new Bridge.Activity { Id = "three", Title = "Three", Phase = "thinking", Changed = 99 };
        aggregate = Bridge.FormatActivity(new[] { complete, busy, second }, 100);
        Check(aggregate["active"] == "2" && aggregate["active_ids"] == "two,three", "all active main chats are aggregated");
        Check(aggregate["tooltip"].Contains("Two") && aggregate["tooltip"].Contains("Three"), "display monitored chat titles");
        Check(!aggregate["tooltip"].Contains("来源") && !aggregate["tooltip"].Contains("兼容"), "no implementation instructions in hover text");
        second.Phase = "waiting_input";
        aggregate = Bridge.FormatActivity(new[] { busy, second }, 100);
        Check(aggregate["label"] == "进行中 2", "working plus waiting does not imply idle");
        Check(Bridge.FormatActivity(new[] { complete }, 100)["label"] == "已完成", "completion without other activity");
        Check(Bridge.FormatActivity(new[] { complete }, 200)["label"] == "空闲", "idle only after all work is finished");
        Console.WriteLine(checks + " checks passed.");
    }
}
