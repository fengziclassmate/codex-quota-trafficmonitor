import datetime
import json
import msvcrt
import os
import sqlite3
import subprocess
import time
from pathlib import Path

root = Path(__file__).resolve().parent.parent
fixture = root / 'work' / 'monitor-regression'
fixture.mkdir(parents=True, exist_ok=True)
home = fixture / ('home-' + str(os.getpid()))
data = home / 'cache'
locks = home / 'thread-writer-locks'
locks.mkdir(parents=True)
data.mkdir()
state = sqlite3.connect(home / 'state_5.sqlite')
history = sqlite3.connect(home / 'thread_history_1.sqlite')
state.execute('CREATE TABLE threads(id TEXT PRIMARY KEY,rollout_path TEXT,name TEXT,title TEXT,archived INTEGER,thread_source TEXT,source TEXT)')
history.execute('CREATE TABLE thread_turns(thread_id TEXT,turn_id TEXT,status TEXT,rollout_ordinal INTEGER,rollout_byte_offset INTEGER,started_at INTEGER,completed_at INTEGER)')
held = []

def add_session(suffix, source='vscode', thread_source='user', extended_path=False):
    sid = '00000000-0000-0000-0000-' + suffix.zfill(12)
    turn = 'turn-' + suffix
    path = home / (suffix + '.jsonl')
    stamp = datetime.datetime.now(datetime.timezone.utc).isoformat()
    records = [dict(type='event_msg', timestamp=stamp, payload=dict(type='task_started', turn_id=turn)), dict(type='response_item', timestamp=stamp, payload=dict(type='custom_tool_call', name='exec', call_id='call-' + suffix))]
    path.write_text(''.join(json.dumps(r) + '\n' for r in records), encoding='utf-8')
    rollout_path = '\\\\?\\' + str(path) if extended_path else str(path)
    state.execute('INSERT INTO threads VALUES(?,?,?,?,0,?,?)', (sid, rollout_path, suffix, suffix, thread_source, source))
    state.commit()
    history.execute("INSERT INTO thread_turns VALUES(?,?,'inProgress',1,0,?,NULL)", (sid, turn, int(time.time())))
    history.commit()
    handle = (locks / (sid + '.lock')).open('w+b')
    msvcrt.locking(handle.fileno(), msvcrt.LK_NBLCK, 1)
    held.append(handle)
    return sid

def wait_active(expected):
    deadline = time.monotonic() + 5
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise AssertionError('Monitor exited: ' + (data / 'stderr.log').read_text(encoding='utf-8', errors='replace'))
        path = data / 'activity.ini'
        if path.exists():
            fields = dict(line.split('=', 1) for line in path.read_text(encoding='utf-16').splitlines() if '=' in line)
            actual = set(filter(None, fields['active_ids'].split(',')))
            if actual == expected:
                return fields
        time.sleep(.1)
    raise AssertionError((expected, actual))

a = add_session('1')
program = root / 'plugins' / 'CodexQuotaBridge.exe'
process = subprocess.Popen([str(program), 'monitor', str(os.getpid())], env=dict(os.environ, CODEX_HOME=str(home), CODEX_QUOTA_DATA_DIR=str(data)), stdout=subprocess.DEVNULL, stderr=open(data / 'stderr.log', 'w', encoding='utf-8'), creationflags=subprocess.CREATE_NO_WINDOW)
try:
    wait_active({a})
    b = add_session('2', extended_path=True)
    wait_active({a, b})
    history.execute("UPDATE thread_turns SET status='completed',completed_at=? WHERE thread_id=?", (int(time.time()), a))
    history.commit()
    fields = wait_active({b})
    assert fields['label'] != '空闲' and fields['active'] == '1'
    add_session('3', '{"subagent":{"other":"guardian"}}', 'guardian_review')
    fields = wait_active({b})
    assert fields['active'] == '1'
    held[1].close()
    wait_active(set())
    assert process.poll() is None
    print('PASS: Windows extended paths, automatic new-chat discovery, completed+busy aggregation, guardian exclusion, closed-session removal.')
finally:
    if process.poll() is None:
        process.terminate()
    process.wait()
    for handle in held:
        if not handle.closed:
            handle.close()
    state.close()
    history.close()
