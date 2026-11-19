#!/usr/bin/env bash
#
# 构建前检查：把「有副作用的调用藏在调试条件里」的写法拦下来。
#
# 为什么需要它：
#   0.5.0 清日志时，下面这种写法被整块当成「调试代码」删掉了 ——
#       if (!TryPlaceHeldItem(loc, player, val, tile) && Mod.Config.DebugLog)
#           Log("放不下");
#   真正的放置动作就在那个 `&& DebugLog` 的左边，删条件等于删功能，
#   而且编译、运行都不报错，只表现为「人走过去但什么都没做」（洒水器 0.5.1 就是这么坏的）。
#
# 拦两类：
#   [错误] 条件式：同一行里有 DebugLog + &&，且条件里出现了非日志调用  → 直接中止构建
#   [警告] 块式：  if (Config.DebugLog) { ... } 的块里出现了非日志调用 → 只提示，不中止
#
# 误报怎么放过：在那一行末尾写  // debug-cond-ok  （并在旁边写清理由）。
#
# 用法：
#   ./tools/check-debug-conditions.sh            # 检查当前目录下所有 .cs
#   ./tools/check-debug-conditions.sh selftest   # 自检（确认这脚本真的抓得到）
#
set -uo pipefail

# 允许出现在「日志条件」里的调用：要么本身在打日志，要么是纯判断，不可能有副作用
ALLOW='^(Log|StallLog|Trace|TraceNight|TraceTarget|TracePath|Debug|Monitor|Append[A-Za-z]*|IsNullOrEmpty|StartsWith|EndsWith|Contains|Equals|ReferenceEquals|ToString|Any|All|Count|TryGetValue|Math|Abs|Floor|Ceiling|Min|Max|MaxCharge)$'

scan_file() {  # $1 = 文件, 输出 "行号|类目|原文"
	local f="$1" n line calls bad
	while IFS=: read -r n line; do
		[ -z "${n:-}" ] && continue
		case "$line" in *debug-cond-ok*) continue ;; esac
		calls=$(printf '%s' "$line" | grep -oE '[A-Za-z_][A-Za-z0-9_]*\(' | sed 's/($//' || true)
		[ -z "$calls" ] && continue
		bad=$(printf '%s\n' "$calls" | grep -vE "$ALLOW" | head -1 || true)
		[ -z "$bad" ] && continue
		printf '%s|ERROR|%s\n' "$n" "$(printf '%s' "$line" | sed 's/^[[:space:]]*//')"
	done < <(grep -n 'DebugLog' "$f" | grep '&&' || true)
}

scan_blocks() {  # if (Config.DebugLog) { ... } 块体里混进非日志调用
	awk -v allow="$ALLOW" '
	function islog(s,   c) {
		while (match(s, /[A-Za-z_][A-Za-z0-9_]*\(/)) {
			c = substr(s, RSTART, RLENGTH - 1)
			s = substr(s, RSTART + RLENGTH)
			if (c !~ allow) return c
		}
		return ""
	}
	!inblock && /if *\(.*DebugLog.*\)/ && /\{/ {
		# 只关心「条件就是 DebugLog」的纯调试块
		if ($0 ~ /&&/) next
		inblock = 1; depth = gsub(/\{/, "{") - gsub(/\}/, "}"); start = FNR
		next
	}
	inblock {
		depth += gsub(/\{/, "{") - gsub(/\}/, "}")
		if ($0 ~ /debug-cond-ok/) { if (depth <= 0) inblock = 0; next }
		if ($0 !~ /^\s*(\/\/|\/\*)/) {
			bad = islog($0)
			if (bad != "" && bad != "Monitor")
				printf "%d|WARN|块内出现非日志调用 %s(): %s\n", FNR, bad, substr($0, 1, 80)
		}
		if (depth <= 0) inblock = 0
	}
	' "$1"
}

selftest() {
	local tmp rc=0
	tmp=$(mktemp -d)
	cat >"$tmp/bad.cs" <<'EOF'
	private void X()
	{
		if (!TryPlaceHeldItem(loc, player, val, tile) && Mod.Config.DebugLog)
		{
			Log("放不下");
		}
	}
EOF
	cat >"$tmp/ok.cs" <<'EOF'
	private void Y()
	{
		if (num > 0 && Mod.Config.DebugLog)
		{
			Log("只是日志");
		}
	}
EOF
	echo "--- 期望：bad.cs 被抓到 ---"
	scan_file "$tmp/bad.cs"
	if ! scan_file "$tmp/bad.cs" | grep -q 'ERROR'; then
		echo "!! 自检失败：抓不到已知的坏写法"; rc=1
	else
		echo "OK 抓到了"
	fi
	echo "--- 期望：ok.cs 不报 ---"
	if scan_file "$tmp/ok.cs" | grep -q 'ERROR'; then
		echo "!! 自检失败：误报纯日志条件"; scan_file "$tmp/ok.cs"; rc=1
	else
		echo "OK 没误报"
	fi
	rm -rf "$tmp"
	return $rc
}

if [ "${1:-}" = "selftest" ]; then
	selftest
	exit $?
fi

root=$(cd "$(dirname "$0")/.." && pwd)
errors=0
warns=0

while IFS= read -r f; do
	out=$(scan_file "$f"; scan_blocks "$f")
	[ -z "$out" ] && continue
	while IFS='|' read -r ln kind detail; do
		[ -z "${ln:-}" ] && continue
		if [ "$kind" = "ERROR" ]; then
			echo "❌ ${f#$root/}:$ln  $detail"
			errors=$((errors + 1))
		else
			echo "⚠️  ${f#$root/}:$ln  $detail"
			warns=$((warns + 1))
		fi
	done <<<"$out"
done < <(find "$root" -name '*.cs' -not -path '*/bin/*' -not -path '*/obj/*' | sort)

echo
if [ "$errors" -gt 0 ]; then
	cat <<'MSG'
构建中止：上面的行把「有副作用的调用」写进了调试条件里。
修法：把调用挪出来单独执行，条件里只留判断，例如
    bool ok = TryPlaceHeldItem(loc, player, val, tile);
    if (!ok && Mod.Config.DebugLog) Log("放不下");
真的是纯判断的话，在行尾加注释 // debug-cond-ok 说明理由。
MSG
	exit 1
fi

if [ "$warns" -gt 0 ]; then
	echo "通过（$warns 条警告，建议人工看一眼上面 ⚠️ 的行）"
else
	echo "通过：没有「动作藏在调试条件里」的写法"
fi
