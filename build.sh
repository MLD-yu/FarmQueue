#!/usr/bin/env bash
#
# 在 Git Bash / WorkBuddy 终端里编译 FarmQueue。
#
# 为什么要手补这么多环境变量：
#   这个 Git Bash 会话里 Windows 的环境变量整体是**空的**
#   （USERPROFILE / APPDATA / ProgramFiles / TEMP / OS 全都没有）。
#   其中 OS 缺失最致命 —— ModBuildConfig 会直接报：
#       error : The mod build package doesn't recognise OS type ''.
#   所以下面至少要保证 OS=Windows_NT。
#
# 用法：
#   ./build.sh                       # 用默认游戏路径
#   GAME_PATH="D:\\Games\\Stardew Valley" ./build.sh
#   SKIP_PRECHECK=1 ./build.sh       # 跳过「动作藏在调试条件里」的检查
#
set -euo pipefail

cd "$(dirname "$0")"

: "${GAME_PATH:=E:\\Steam\\steamapps\\common\\Stardew Valley}"

export USERPROFILE="${USERPROFILE:-C:\\Users\\21092}"
export APPDATA="${APPDATA:-$USERPROFILE\\AppData\\Roaming}"
export LOCALAPPDATA="${LOCALAPPDATA:-$USERPROFILE\\AppData\\Local}"
export ProgramData="${ProgramData:-C:\\ProgramData}"
export ProgramFiles="${ProgramFiles:-C:\\Program Files}"
export ProgramW6432="${ProgramW6432:-C:\\Program Files}"
export SystemRoot="${SystemRoot:-C:\\Windows}"
export windir="${windir:-C:\\Windows}"
export TEMP="${TEMP:-$USERPROFILE\\AppData\\Local\\Temp}"
export TMP="${TMP:-$TEMP}"
export OS=Windows_NT

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

echo "OS=$OS"
echo "GamePath=$GAME_PATH"
echo

# 构建前自查：拦「把有副作用的调用写进调试条件」的写法。
# 0.5.0 清日志时就是这么把洒水器的放置动作删掉的（编译/运行都不报错，只表现成「走过去不放」）。
# 临时跳过：SKIP_PRECHECK=1 ./build.sh
if [ "${SKIP_PRECHECK:-0}" != "1" ]; then
	bash tools/check-debug-conditions.sh
	echo
fi

dotnet build -v:m -nologo -p:GamePath="$GAME_PATH" "$@"
