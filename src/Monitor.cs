using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static partial class Bridge
{
    public sealed class Activity
    {
        public string Id, Title, Phase;
        public long Changed;
    }

    public static Dictionary<string, string> FormatActivity(IEnumerable<Activity> entries, long now)
    {
        var all = entries.ToArray();
        int running = all.Count(a => a.Phase == "running"), thinking = all.Count(a => a.Phase == "thinking");
        int input = all.Count(a => a.Phase == "waiting_input"), approval = all.Count(a => a.Phase == "waiting_approval");
        int waiting = input + approval;
        int active = running + thinking + waiting;
        string label = running + thinking > 0 && waiting > 0 ? "进行中 " + active : running > 0 ? "运行中 " + (running + thinking) : thinking > 0 ? "思考中 " + thinking : input > 0 ? "等待交互 " + waiting : approval > 0 ? "批准/执行 " + approval : all.Any(a => a.Phase == "failed" && now < a.Changed + 60) ? "任务失败" : all.Any(a => a.Phase == "interrupted" && now < a.Changed + 60) ? "已中断" : all.Any(a => a.Phase == "completed" && now < a.Changed + 60) ? "已完成" : "空闲";
        var counts = new List<string>();
        if (running > 0) counts.Add("运行 " + running);
        if (thinking > 0) counts.Add("思考 " + thinking);
        if (input > 0) counts.Add("等待 " + input);
        if (approval > 0) counts.Add("批准/执行 " + approval);
        var lines = new List<string> { "任务：" + (active == 0 ? label : string.Join(" · ", counts)) };
        var busy = all.Where(a => a.Phase == "running" || a.Phase == "thinking" || a.Phase == "waiting_input" || a.Phase == "waiting_approval").ToArray();
        foreach (var item in busy.Take(3))
        {
            string title = Flat(item.Title);
            if (title.Length > 36) title = title.Substring(0, 36) + "…";
            string phase = item.Phase == "running" ? "运行" : item.Phase == "thinking" ? "思考" : item.Phase == "waiting_approval" ? "批准/执行" : "等待";
            lines.Add("· " + title + "（" + phase + "）");
        }
        if (busy.Length > 3) lines.Add("另有 " + (busy.Length - 3) + " 项活动任务");
        return new Dictionary<string, string> { { "label", label }, { "tooltip", string.Join("\u2028", lines) }, { "active", active.ToString() }, { "running", running.ToString() }, { "thinking", thinking.ToString() }, { "waiting", waiting.ToString() }, { "active_ids", string.Join(",", busy.Select(a => a.Id)) }, { "loaded", all.Length.ToString() } };
    }

    sealed class Catalog : IDisposable
    {
        IntPtr database;
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_open_v2(byte[] path, out IntPtr db, int flags, IntPtr vfs);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int size, out IntPtr statement, IntPtr tail);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_step(IntPtr statement);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_column_count(IntPtr statement);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr sqlite3_column_text16(IntPtr statement, int column);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr sqlite3_errmsg16(IntPtr db);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_finalize(IntPtr statement);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)] static extern int sqlite3_close(IntPtr db);
        public Catalog(string directory)
        {
            string state = Path.Combine(directory, "state_5.sqlite"), history = Path.Combine(directory, "thread_history_1.sqlite");
            if (!File.Exists(state) || !File.Exists(history)) throw new FileNotFoundException("Codex 状态数据库不存在。");
            if (sqlite3_open_v2(Encoding.UTF8.GetBytes(state + "\0"), out database, 1 | 0x40 | 0x8000, IntPtr.Zero) != 0) throw new IOException(Marshal.PtrToStringUni(sqlite3_errmsg16(database)));
            string historyUri = new Uri(history).AbsoluteUri + "?mode=ro";
            Query("ATTACH DATABASE '" + historyUri.Replace("'", "''") + "' AS history");
        }
        public List<string[]> Query(string sql)
        {
            IntPtr statement;
            if (sqlite3_prepare_v2(database, Encoding.UTF8.GetBytes(sql + "\0"), -1, out statement, IntPtr.Zero) != 0) throw new IOException(Marshal.PtrToStringUni(sqlite3_errmsg16(database)));
            try
            {
                var rows = new List<string[]>(); int result;
                while ((result = sqlite3_step(statement)) == 100)
                {
                    var row = new string[sqlite3_column_count(statement)];
                    for (int i = 0; i < row.Length; i++) row[i] = Marshal.PtrToStringUni(sqlite3_column_text16(statement, i));
                    rows.Add(row);
                }
                if (result != 101) throw new IOException(Marshal.PtrToStringUni(sqlite3_errmsg16(database)));
                return rows;
            }
            finally { sqlite3_finalize(statement); }
        }
        public void Dispose() { sqlite3_close(database); }
    }

    sealed class ActiveTranscript : IDisposable
    {
        public readonly string Turn;
        public readonly StatusData State = new StatusData();
        public long LatestMilliseconds;
        readonly FileStream stream;
        readonly MemoryStream line = new MemoryStream();
        public ActiveTranscript(string path, string turn, long offset, long started)
        {
            Turn = turn; State.turn = turn; State.phase = "thinking"; LatestMilliseconds = started * 1000;
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); stream.Position = offset;
        }
        public void ReadNew()
        {
            while (stream.Position < stream.Length)
            {
                int value = stream.ReadByte();
                if (value != 10) { line.WriteByte((byte)value); continue; }
                string text = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r'); line.SetLength(0);
                if (text.Length == 0) continue;
                var record = Object(Json.DeserializeObject(text));
                string timestamp = Text(Field(record, "timestamp"));
                if (timestamp != "") LatestMilliseconds = DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();
                ApplyTranscript(State, record);
            }
        }
        public void Dispose() { stream.Dispose(); line.Dispose(); }
    }

    static bool SessionLoaded(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete)) { file.Lock(0, 1); file.Unlock(0, 1); return false; }
        }
        catch (IOException error) { int code = error.HResult & 65535; if (code == 32 || code == 33) return true; throw; }
    }

    static int Monitor(int hostId)
    {
        string codexDirectory = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var host = Process.GetProcessById(hostId); var self = Process.GetCurrentProcess();
        var readers = new Dictionary<string, ActiveTranscript>();
        string previous = ""; long written = 0;
        using (var catalog = new Catalog(codexDirectory))
        {
            try
            {
                while (!host.HasExited)
                {
                    var rows = catalog.Query(@"SELECT t.id,t.rollout_path,COALESCE(NULLIF(t.name,''),t.title),COALESCE(u.turn_id,''),COALESCE(u.status,'idle'),COALESCE(u.rollout_byte_offset,0),COALESCE(u.started_at,0),COALESCE(u.completed_at,0)
FROM threads t LEFT JOIN history.thread_turns u ON u.thread_id=t.id AND u.rollout_ordinal=(SELECT MAX(v.rollout_ordinal) FROM history.thread_turns v WHERE v.thread_id=t.id)
WHERE t.archived=0 AND (t.thread_source='user' OR t.thread_source IS NULL) AND t.source NOT LIKE '%subagent%' ORDER BY t.id");
                    var entries = new List<Activity>(); var live = new HashSet<string>();
                    foreach (var row in rows)
                    {
                        string id = row[0];
                        if (!SessionLoaded(Path.Combine(codexDirectory, "thread-writer-locks", id + ".lock"))) continue;
                        string phase = row[4] == "inProgress" ? "thinking" : row[4];
                        long changed = long.Parse(row[7], CultureInfo.InvariantCulture);
                        if (row[4] == "inProgress")
                        {
                            live.Add(id); ActiveTranscript reader;
                            if (!readers.TryGetValue(id, out reader) || reader.Turn != row[3])
                            {
                                if (reader != null) reader.Dispose();
                                reader = new ActiveTranscript(row[1], row[3], long.Parse(row[5], CultureInfo.InvariantCulture), long.Parse(row[6], CultureInfo.InvariantCulture)); readers[id] = reader;
                            }
                            reader.ReadNew(); phase = reader.State.phase; changed = reader.LatestMilliseconds / 1000;
                            string hookPath = Path.Combine(DataDir, "status", id + ".ini");
                            if (File.Exists(hookPath))
                            {
                                var hook = File.ReadAllLines(hookPath, Encoding.Unicode).Where(l => l.IndexOf('=') > 0).ToDictionary(l => l.Substring(0, l.IndexOf('=')), l => l.Substring(l.IndexOf('=') + 1));
                                string origin, at, data;
                                if (hook.TryGetValue("origin", out origin) && origin == "hooks" && hook.TryGetValue("updated_ms", out at) && long.Parse(at, CultureInfo.InvariantCulture) >= reader.LatestMilliseconds && hook.TryGetValue("state_json", out data))
                                {
                                    var hookState = Json.Deserialize<StatusData>(data);
                                    if (hookState.turn == row[3] && (hookState.phase == "waiting_input" || hookState.phase == "waiting_approval")) phase = hookState.phase;
                                }
                            }
                        }
                        entries.Add(new Activity { Id = id, Title = row[2], Phase = phase, Changed = changed });
                    }
                    foreach (string id in readers.Keys.Where(id => !live.Contains(id)).ToArray()) { readers[id].Dispose(); readers.Remove(id); }
                    var summary = FormatActivity(entries, UnixNow);
                    summary["monitored_ids"] = string.Join(",", entries.Select(a => a.Id));
                    string signature = Json.Serialize(summary);
                    if (signature != previous || UnixNow >= written + 5)
                    {
                        summary["updated"] = UnixNow.ToString(); summary["owner"] = self.Id.ToString(); summary["owner_started"] = self.StartTime.ToFileTimeUtc().ToString();
                        WriteIni(Path.Combine(DataDir, "activity.ini"), summary); previous = signature; written = UnixNow;
                    }
                    Thread.Sleep(500);
                }
            }
            finally { foreach (var reader in readers.Values) reader.Dispose(); }
        }
        return 0;
    }
}
