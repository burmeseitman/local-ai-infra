#!/usr/bin/env bash
# Reset a user's password, e.g. a forgotten administrator password, without losing any data.
#
#   scripts/reset-password.sh <username>
#
# You are prompted for the new password (hidden). The script writes a hash in ASP.NET Core
# Identity's v3 format (PBKDF2-HMAC-SHA512, 100,000 iterations, 16-byte salt) - the same
# format the API creates - bumps the session version so existing sessions are signed out,
# and restarts the api container. Conversations and documents are untouched.
#
# Environment (optional):
#   LOCALAI_DATA_VOLUME     Docker volume holding localai.db (default: local-ai-infrastructure_api-data)
#   LOCALAI_RESTART_API     Set to 0 to skip stopping/starting the compose "api" service
#   LOCALAI_NEW_PASSWORD    Non-interactive password (for automation only; avoid on shared machines)
set -euo pipefail

username="${1:-}"
if [[ ! "$username" =~ ^[A-Za-z0-9._-]{3,32}$ ]]; then
  echo "usage: $0 <username>   (3-32 characters: letters, digits, '.', '_' or '-')" >&2
  exit 2
fi

volume="${LOCALAI_DATA_VOLUME:-local-ai-infrastructure_api-data}"
docker volume inspect "$volume" >/dev/null 2>&1 || { echo "Docker volume '$volume' not found." >&2; exit 1; }

if [[ -n "${LOCALAI_NEW_PASSWORD:-}" ]]; then
  password="$LOCALAI_NEW_PASSWORD"
else
  read -rsp "New password for $username (10-128 characters): " password; echo
  read -rsp "Repeat the new password: " confirm; echo
  [[ "$password" == "$confirm" ]] || { echo "The passwords do not match." >&2; exit 1; }
fi
(( ${#password} >= 10 && ${#password} <= 128 )) || { echo "The password must be 10-128 characters." >&2; exit 1; }

# The password reaches Python only through its environment (never argv, which `ps` can show).
hash="$(LOCALAI_PW="$password" python3 - <<'PY'
import base64, hashlib, os, struct
password = os.environ["LOCALAI_PW"].encode("utf-8")
salt, iterations = os.urandom(16), 100_000
subkey = hashlib.pbkdf2_hmac("sha512", password, salt, iterations, 32)
# Identity v3 layout: 0x01 | PRF (2 = HMACSHA512) | iterations | salt length | salt | subkey, big-endian ints.
print(base64.b64encode(bytes([1]) + struct.pack(">III", 2, iterations, len(salt)) + salt + subkey).decode())
PY
)"
unset password confirm
[[ "$hash" =~ ^[A-Za-z0-9+/=]+$ ]] || { echo "Hashing failed." >&2; exit 1; }

compose_dir="$(cd "$(dirname "$0")/.." && pwd)"
restart="${LOCALAI_RESTART_API:-1}"
if [[ "$restart" == "1" ]]; then
  echo "Stopping the api service..."
  docker compose --project-directory "$compose_dir" stop api >/dev/null
fi

# Username and hash are validated above (no quotes possible), so they are safe in the SQL.
changed="$(docker run --rm -v "$volume":/d alpine sh -c "apk add -q sqlite >/dev/null 2>&1 && sqlite3 /d/localai.db \
  \"UPDATE users SET PasswordHash = '$hash', SessionVersion = SessionVersion + 1 WHERE NormalizedUsername = lower('$username'); SELECT changes();\"")"

if [[ "$restart" == "1" ]]; then
  echo "Starting the api service..."
  docker compose --project-directory "$compose_dir" start api >/dev/null
fi

if [[ "$changed" == "1" ]]; then
  echo "Password for '$username' was reset. Existing sessions for that user are signed out."
else
  echo "No user named '$username' was found; nothing changed." >&2
  exit 1
fi
