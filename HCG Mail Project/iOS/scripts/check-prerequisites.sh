#!/usr/bin/env bash
# Kept for existing instructions and CI; the checks live in doctor.sh.
exec "$(dirname "$0")/doctor.sh" "$@"
