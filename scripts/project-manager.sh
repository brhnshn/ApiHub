#!/usr/bin/env bash
set -Eeuo pipefail
shopt -s extglob

CONFIG_FILE="${PROJECT_MANAGER_CONFIG:-/etc/project-manager/projects.conf}"
RUN_DIR="${PROJECT_MANAGER_RUN_DIR:-/run/project-manager}"
LOG_DIR="${PROJECT_MANAGER_LOG_DIR:-/var/log/project-manager}"
MANAGER_LOG="${PROJECT_MANAGER_LOG_FILE:-/var/log/project-manager.log}"
LOCK_FILE="$RUN_DIR/lock"
NAME_PATTERN='^[a-z0-9_-]+$'

PROJECT_NAME=""
PROJECT_PATH=""
PROJECT_COMMAND=""
PROJECT_USER=""

usage() {
    echo "Usage: project-manager.sh {start|stop|restart|status} [project_name]"
    echo "       project-manager.sh delete project_name"
    echo "       project-manager.sh clean-logs"
}

trim() {
    local value="$1"
    value="${value//$'\r'/}"
    value="${value#$'\xef\xbb\xbf'}"
    value="${value##+([[:space:]])}"
    value="${value%%+([[:space:]])}"
    printf '%s' "$value"
}

log_manager() {
    local level="$1"
    local message="$2"
    local log_dir
    log_dir="$(dirname "$MANAGER_LOG")"
    mkdir -p "$log_dir"
    printf '[%s] [%s] %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$level" "$message" >> "$MANAGER_LOG"
}

ensure_runtime_dirs() {
    mkdir -p "$RUN_DIR" "$LOG_DIR"
}

acquire_lock() {
    ensure_runtime_dirs
    exec 9>"$LOCK_FILE"
    if command -v flock >/dev/null 2>&1; then
        flock -x 9
    fi
}

validate_name() {
    local name="$1"
    if [[ ! "$name" =~ $NAME_PATTERN ]]; then
        echo "Invalid project name: $name" >&2
        return 1
    fi
}

validate_user() {
    local user="$1"
    if [[ -z "$user" ]]; then
        return 0
    fi

    if [[ ! "$user" =~ ^[a-z_][a-z0-9_-]*[$]?$ && "$user" != "root" ]]; then
        echo "Invalid run user for project $PROJECT_NAME: $user" >&2
        return 1
    fi
}

pid_file_for() {
    printf '%s/%s.pid' "$RUN_DIR" "$1"
}

project_log_file_for() {
    printf '%s/%s.log' "$LOG_DIR" "$1"
}

load_project() {
    local wanted="$1"
    validate_name "$wanted"

    if [[ ! -f "$CONFIG_FILE" ]]; then
        echo "$wanted|Stopped|reason=config-not-found"
        return 1
    fi

    local line raw_name raw_path raw_command raw_user ignored
    while IFS= read -r line || [[ -n "$line" ]]; do
        line="$(trim "$line")"
        [[ -z "$line" || "$line" == \#* || "$line" == \;* ]] && continue

        IFS='|' read -r raw_name raw_path raw_command raw_user ignored <<< "$line"
        raw_name="$(trim "${raw_name:-}")"
        raw_path="$(trim "${raw_path:-}")"
        raw_command="$(trim "${raw_command:-}")"
        raw_user="$(trim "${raw_user:-root}")"

        if [[ "$raw_name" == "$wanted" ]]; then
            PROJECT_NAME="$raw_name"
            PROJECT_PATH="$raw_path"
            PROJECT_COMMAND="$raw_command"
            PROJECT_USER="${raw_user:-root}"
            return 0
        fi
    done < "$CONFIG_FILE"

    echo "$wanted|Stopped|reason=not-configured"
    return 1
}

list_project_names() {
    [[ -f "$CONFIG_FILE" ]] || return 0

    local line raw_name ignored
    while IFS= read -r line || [[ -n "$line" ]]; do
        line="$(trim "$line")"
        [[ -z "$line" || "$line" == \#* || "$line" == \;* ]] && continue
        IFS='|' read -r raw_name ignored <<< "$line"
        raw_name="$(trim "${raw_name:-}")"
        [[ "$raw_name" =~ $NAME_PATTERN ]] || continue
        printf '%s\n' "$raw_name"
    done < "$CONFIG_FILE"
}

is_pid_alive() {
    local pid="$1"
    [[ "$pid" =~ ^[0-9]+$ ]] || return 1
    kill -0 "$pid" >/dev/null 2>&1
}

read_saved_pid() {
    local pid_file
    pid_file="$(pid_file_for "$PROJECT_NAME")"
    [[ -f "$pid_file" ]] || return 1

    local pid
    pid="$(trim "$(cat "$pid_file")")"
    [[ "$pid" =~ ^[0-9]+$ ]] || return 1
    printf '%s' "$pid"
}

find_pid_by_cwd_and_command() {
    [[ -n "$PROJECT_PATH" && -n "$PROJECT_COMMAND" && -d "$PROJECT_PATH" ]] || return 1
    command -v ps >/dev/null 2>&1 || return 1
    command -v readlink >/dev/null 2>&1 || return 1

    local pid args cwd
    while read -r pid args; do
        pid="$(trim "${pid:-}")"
        args="$(trim "${args:-}")"
        [[ "$pid" =~ ^[0-9]+$ ]] || continue
        [[ "$pid" == "$$" ]] && continue
        [[ "$args" == *"$PROJECT_COMMAND"* ]] || continue
        cwd="$(readlink -f "/proc/$pid/cwd" 2>/dev/null || true)"
        if [[ "$cwd" == "$PROJECT_PATH" ]]; then
            printf '%s' "$pid"
            return 0
        fi
    done < <(ps -eo pid=,args=)

    return 1
}

get_running_pid() {
    local pid
    if pid="$(read_saved_pid 2>/dev/null)" && is_pid_alive "$pid"; then
        printf '%s' "$pid"
        return 0
    fi

    if pid="$(find_pid_by_cwd_and_command 2>/dev/null)" && is_pid_alive "$pid"; then
        printf '%s' "$pid"
        return 0
    fi

    return 1
}

status_project() {
    local name="$1"
    if ! load_project "$name"; then
        return 1
    fi

    local pid pid_file
    pid_file="$(pid_file_for "$PROJECT_NAME")"

    if pid="$(get_running_pid)"; then
        printf '%s|Running|pid=%s\n' "$PROJECT_NAME" "$pid"
        return 0
    fi

    rm -f "$pid_file"
    printf '%s|Stopped\n' "$PROJECT_NAME"
    return 0
}

start_project() {
    local name="$1"
    if ! load_project "$name" >/dev/null; then
        return 1
    fi
    if ! validate_user "$PROJECT_USER"; then
        return 1
    fi

    if [[ -z "$PROJECT_PATH" || ! -d "$PROJECT_PATH" ]]; then
        echo "$PROJECT_NAME|Stopped|reason=path-not-found"
        log_manager "error" "$PROJECT_NAME start failed: path not found ($PROJECT_PATH)"
        return 1
    fi

    if [[ -z "$PROJECT_COMMAND" ]]; then
        echo "$PROJECT_NAME|Stopped|reason=start-command-empty"
        log_manager "error" "$PROJECT_NAME start failed: empty start command"
        return 1
    fi

    local existing_pid
    if existing_pid="$(get_running_pid)"; then
        printf '%s|Running|pid=%s\n' "$PROJECT_NAME" "$existing_pid"
        return 0
    fi

    local log_file pid_file run_command exec_cmd
    log_file="$(project_log_file_for "$PROJECT_NAME")"
    pid_file="$(pid_file_for "$PROJECT_NAME")"
    
    exec_cmd="exec"
    if [[ "$PROJECT_COMMAND" == *=* && ! "$PROJECT_COMMAND" =~ ^env[[:space:]] ]]; then
        exec_cmd="exec env"
    fi
    run_command="cd $(printf '%q' "$PROJECT_PATH") && $exec_cmd $PROJECT_COMMAND"

    touch "$log_file"
    chmod 644 "$log_file"
    chown dockerpanel_api:dockerpanel_api "$log_file" 2>/dev/null || true
    log_manager "info" "$PROJECT_NAME start requested: $PROJECT_COMMAND"
    printf '[%s] [project-manager] starting %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$PROJECT_NAME" >> "$log_file"

    if [[ "$PROJECT_USER" == "root" || -z "$PROJECT_USER" ]]; then
        setsid bash -lc "$run_command" >> "$log_file" 2>&1 9>&- &
    else
        setsid sudo -u "$PROJECT_USER" -- bash -lc "$run_command" >> "$log_file" 2>&1 9>&- &
    fi

    local pid="$!"
    printf '%s\n' "$pid" > "$pid_file"

    # .NET ve Node.js uygulamalar\u0131 cold-start'ta 1 saniyeden fazla zaman alabilir;
    # 3 saniye bekleyerek yanl\u0131\u015f pozitif "process-exited" tespitini \u00f6nlüyoruz.
    sleep 3
    if is_pid_alive "$pid"; then
        printf '%s|Running|pid=%s\n' "$PROJECT_NAME" "$pid"
        return 0
    fi

    rm -f "$pid_file"
    echo "$PROJECT_NAME|Stopped|reason=process-exited"
    log_manager "error" "$PROJECT_NAME start failed: process exited immediately"
    return 1
}

stop_project() {
    local name="$1"
    if ! load_project "$name" >/dev/null; then
        return 1
    fi

    local pid pid_file log_file
    pid_file="$(pid_file_for "$PROJECT_NAME")"
    log_file="$(project_log_file_for "$PROJECT_NAME")"

    if ! pid="$(get_running_pid)"; then
        rm -f "$pid_file"
        printf '%s|Stopped\n' "$PROJECT_NAME"
        return 0
    fi

    log_manager "info" "$PROJECT_NAME stop requested"
    printf '[%s] [project-manager] stopping %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$PROJECT_NAME" >> "$log_file"

    kill -- "-$pid" >/dev/null 2>&1 || kill "$pid" >/dev/null 2>&1 || true

    for _ in {1..15}; do
        if ! is_pid_alive "$pid"; then
            rm -f "$pid_file"
            printf '%s|Stopped\n' "$PROJECT_NAME"
            return 0
        fi
        sleep 1
    done

    kill -KILL -- "-$pid" >/dev/null 2>&1 || kill -KILL "$pid" >/dev/null 2>&1 || true
    rm -f "$pid_file"
    printf '%s|Stopped\n' "$PROJECT_NAME"
    return 0
}

restart_project() {
    local name="$1"
    if ! load_project "$name" >/dev/null; then
        return 1
    fi

    if ! stop_project "$name" >/dev/null; then
        return 1
    fi

    if ! start_project "$name" >/dev/null; then
        status_project "$name" || true
        return 1
    fi

    status_project "$name"
}

delete_project() {
    local name="$1"
    if ! load_project "$name" >/dev/null; then
        return 1
    fi
    stop_project "$name" >/dev/null || true
    rm -f "$(pid_file_for "$PROJECT_NAME")"
    
    # Safely delete the physical project directory if it resides in /opt/dockerpanel/projects/
    if [[ -n "${PROJECT_PATH:-}" && "$PROJECT_PATH" == /opt/dockerpanel/projects/* && -d "$PROJECT_PATH" ]]; then
        rm -rf "$PROJECT_PATH"
        log_manager "info" "$PROJECT_NAME physical directory deleted: $PROJECT_PATH"
    fi

    printf '%s|Deleted\n' "$PROJECT_NAME"
    log_manager "info" "$PROJECT_NAME delete requested"
}

run_for_all() {
    local action="$1"
    local failed=0
    local count=0
    local name

    while IFS= read -r name; do
        [[ -z "$name" ]] && continue
        count=$((count + 1))
        if ! "${action}_project" "$name"; then
            failed=$((failed + 1))
        fi
    done < <(list_project_names)

    if [[ "$count" -eq 0 ]]; then
        echo "No configured projects found."
    fi

    [[ "$failed" -eq 0 ]]
}

main() {
    local action="${1:-}"
    local target="${2:-}"

    case "$action" in
        start|stop|restart)
            acquire_lock
            if [[ -n "$target" ]]; then
                validate_name "$target"
                "${action}_project" "$target"
            else
                run_for_all "$action"
            fi
            ;;
        status)
            if [[ -n "$target" ]]; then
                validate_name "$target"
                status_project "$target"
            else
                run_for_all "status"
            fi
            ;;
        delete)
            [[ -n "$target" ]] || { usage >&2; return 2; }
            acquire_lock
            validate_name "$target"
            delete_project "$target"
            ;;
        clean-path)
            [[ -n "$target" ]] || { usage >&2; return 2; }
            # Only allow cleaning inside /opt/dockerpanel/projects/ to prevent arbitrary deletion
            if [[ "$target" == /opt/dockerpanel/projects/* && -d "$target" ]]; then
                rm -rf "$target"
                log_manager "info" "Clean path requested and completed: $target"
            fi
            ;;
        clean-logs)
            acquire_lock
            # 1. Rotasyon artığı olan eski logları sil
            rm -f "$LOG_DIR"/*.log.* "$LOG_DIR"/*.gz "$LOG_DIR"/*.zip "$LOG_DIR"/*.rar 2>/dev/null || true
            # 2. Ana log dosyalarını sıfırla (truncate)
            for f in "$LOG_DIR"/*.log; do
                if [[ -f "$f" ]]; then
                    truncate -s 0 "$f"
                fi
            done
            # 3. Manager logunu sıfırla
            if [[ -f "$MANAGER_LOG" ]]; then
                truncate -s 0 "$MANAGER_LOG"
            fi
            echo "Logs cleaned successfully."
            ;;
        *)
            usage >&2
            return 2
            ;;
    esac
}

main "$@"
