import json
import time
from pathlib import Path

root = Path(__file__).resolve().parent / 'work' / 'ui-fixture'
root.mkdir(parents=True, exist_ok=True)
now = int(time.time())
cycles = [dict(remaining='76%', cycle='7 天周期', percent=76, reset=now + 86400 * 4), dict(remaining='42%', cycle='5 小时周期', percent=42, reset=now + 7200)]
tasks = [dict(title='优化 Codex 任务栏与额度详情', phase='running'), dict(title='检查多聊天状态汇总与长标题显示是否完整', phase='thinking'), dict(title='处理项目配置', phase='waiting_input')]
for name, data in [('quota', dict(remaining='76%/42%', cycles_json=json.dumps(cycles, ensure_ascii=False), reset_count='2', reset_expiries='2026-11-07 07:31\u20282026-11-12 14:00', updated=now, error='')), ('activity', dict(label='进行中 3', active='3', items_json=json.dumps(tasks, ensure_ascii=False), updated=now))]:
    (root / (name + '.ini')).write_text('[data]\n' + '\n'.join(str(k) + '=' + str(v) for k, v in data.items()) + '\n', encoding='utf-16')
print(root)
