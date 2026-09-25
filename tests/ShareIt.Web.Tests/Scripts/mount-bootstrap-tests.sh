#!/usr/bin/env bash
# Run against the generated Bash script; requires Linux, Bash and util-linux script.
set -euo pipefail
script_path=$(realpath "$1")
task_root=$(mktemp -d /tmp/shareit-mount-tests.XXXXXXXX)
case "$task_root" in /tmp/shareit-mount-tests.*) trap 'rm -rf -- "$task_root"' EXIT ;; *) exit 1 ;; esac
mkdir "$task_root/bin"
ln -s /bin/mkdir "$task_root/bin/mkdir"
cat >"$task_root/bin/uname" <<'STUB'
#!/bin/bash
printf '%s\n' "$SHAREIT_TEST_OS"
STUB
cat >"$task_root/bin/fusermount3" <<'STUB'
#!/bin/bash
exit 0
STUB
cat >"$task_root/bin/rclone" <<'STUB'
#!/bin/bash
set -eu
if [[ "$1" == nfsmount && "$2" == --help ]]; then
  [[ "$SHAREIT_TEST_CASE" != old_rclone ]]
elif [[ "$1" == obscure ]]; then
  [[ "$2" == - && "$3" == --config && "$4" == /dev/null ]]
  IFS= read -r pin
  [[ "$pin" == 0047 ]] || exit 65
  [[ "$SHAREIT_TEST_CASE" != obscure ]] || exit 23
  printf 'obscured-value\n'
else
  printf '%s\n' "$@" >"$SHAREIT_TEST_RESULT"
  printf '%s\n' "$RCLONE_WEBDAV_PASS" >>"$SHAREIT_TEST_RESULT"
  [[ "$SHAREIT_TEST_CASE" != mount ]] || exit 37
fi
STUB
chmod +x "$task_root/bin/"*

run_case() {
  local scenario=$1 os=$2 expected=$3 message=$4 pin=${5:-0047} status=0
  local home_dir="$task_root/$scenario" output="$task_root/$scenario.out"
  mkdir "$home_dir"
  # stdin of Bash contains the script; the hidden PIN comes from the separate PTY.
  printf '%s\n' "$pin" | env PATH="$task_root/bin" HOME="$home_dir" SHELL=/bin/bash \
    SHAREIT_SCRIPT="$script_path" SHAREIT_TEST_OS="$os" SHAREIT_TEST_CASE="$scenario" \
    SHAREIT_TEST_RESULT="$home_dir/result" RCLONE_WEBDAV_PASS=previous-value \
    /usr/bin/timeout 10 /usr/bin/script -q -e -E never -c '/bin/cat "$SHAREIT_SCRIPT" | /bin/bash' /dev/null >"$output" 2>&1 || status=$?
  if [[ "$status" != "$expected" ]] || ! grep -Fq "$message" "$output"; then
    cat "$output" >&2
    printf '%s: expected exit %s, got %s\n' "$scenario" "$expected" "$status" >&2
    exit 1
  fi
  if grep -Fq 0047 "$output"; then printf 'PIN leaked in terminal output.\n' >&2; exit 1; fi
  if [[ "$scenario" == linux || "$scenario" == mac || "$scenario" == mount ]]; then
    local result="$home_dir/result"
    grep -Fxq "$([[ "$os" == Darwin ]] && printf nfsmount || printf mount)" "$result"
    grep -Fxq "$home_dir/ShareIt-abc-123" "$result"
    grep -Fxq -- --read-only "$result"
    grep -Fxq -- --dir-cache-time "$result"
    grep -Fxq -- 15s "$result"
    grep -Fxq -- --poll-interval "$result"
    grep -Fxq -- /dev/null "$result"
    grep -Fxq -- abc-123 "$result"
    grep -Fxq -- obscured-value "$result"
    grep -Fxq "https://example.test/a'\$(echo unexpected); #/dav/abc-123/" "$result"
    if grep -Eq '0047|--webdav-pass' "$result"; then exit 1; fi
  else
    [[ ! -e "$home_dir/result" && ! -d "$home_dir/ShareIt-abc-123" ]]
  fi
  printf '%s passed\n' "$scenario"
}

run_case linux Linux 0 'Press Ctrl+C'
run_case mac Darwin 0 'Press Ctrl+C'
run_case unsupported MINGW64_NT 1 'Windows PowerShell'
run_case pin Linux 1 'four PIN digits' 123
run_case obscure Linux 1 'Could not prepare'
run_case mount Linux 37 'Press Ctrl+C'
run_case old_rclone Darwin 1 'nfsmount support'
mv "$task_root/bin/rclone" "$task_root/rclone"
run_case missing_rclone Linux 1 'Install rclone'
mv "$task_root/rclone" "$task_root/bin/rclone"
mv "$task_root/bin/fusermount3" "$task_root/fusermount3"
run_case missing_fuse Linux 1 'FUSE 3'
mv "$task_root/fusermount3" "$task_root/bin/fusermount3"

# A session without a controlling terminal must fail instead of reading the script as a PIN.
status=0
env PATH="$task_root/bin" HOME="$task_root" SHAREIT_TEST_OS=Linux \
  /usr/bin/setsid --wait /bin/bash "$script_path" >"$task_root/no-tty.out" 2>&1 || status=$?
[[ "$status" == 1 ]]
grep -Fq 'interactive terminal' "$task_root/no-tty.out"
printf 'no_terminal passed\n'

# Download truncation before invocation must never prompt, obscure credentials or mount.
sed '$d' "$script_path" >"$task_root/truncated.sh"
env PATH="$task_root/bin" HOME="$task_root" /bin/bash "$task_root/truncated.sh" >"$task_root/truncated.out" 2>&1
[[ ! -s "$task_root/truncated.out" ]]
printf 'truncated_download passed\nAll Bash mount scenarios passed.\n'
