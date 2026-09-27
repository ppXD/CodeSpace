#!/usr/bin/env bash
# Regenerate codespace-worker.json, the seccomp profile that lets the non-root, capability-less worker build
# bubblewrap's sandbox (see backend/Dockerfile.worker), from moby v20.10.20's default profile:
#
#   curl -fsSLo /tmp/moby-default.json https://raw.githubusercontent.com/moby/moby/v20.10.20/profiles/seccomp/default.json
#   backend/deploy/seccomp/derive-codespace-worker.sh /tmp/moby-default.json
#
# The output is moby's default RESOLVED for a container with no capabilities, in the OCI runtime-spec form. moby's
# file gates rules on capabilities, architectures and kernel versions (includes / excludes / archMap), and only Docker
# and CRI-O evaluate those. containerd decodes a Localhost profile straight into the OCI LinuxSeccomp struct and drops
# every key it lacks, so each capability-gated allow would become an unconditional one there. Resolving the conditions
# here makes Docker, containerd and CRI-O apply the same filter:
#   • rules gated on a capability (includes.caps) are dropped: the worker holds none;
#   • rules gated on an architecture or kernel (includes.arches, includes.minKernel) keep their allow unconditionally:
#     runc skips a name the native architecture does not have, and the one kernel gate (ptrace, 4.8) predates every
#     kernel that runs the worker;
#   • archMap becomes an explicit little-endian architectures list, the x86_64 and aarch64 families moby's archMap
#     names; runc refuses a filter that mixes in a big-endian one (EDOM);
#   • moby's clone rules that mask out namespace flags and its clone3 ENOSYS rule are replaced by one unconditional
#     allow of the seven calls bubblewrap needs.
#
# RootlessWorkerPostureTests pins the output's SHA-256; update that pin in the same diff as any change here.
set -euo pipefail

MOBY_DEFAULT_SHA256=ce3585b856aa4a193671fd8280d0d0612a998bd3f10bdb2f8530dfa958c6197f

src="${1:?usage: $0 <moby v20.10.20 profiles/seccomp/default.json>}"
dst="$(cd "$(dirname "$0")" && pwd)/codespace-worker.json"

actual=$({ sha256sum "$src" 2>/dev/null || shasum -a 256 "$src"; } | cut -d' ' -f1)
[ "$actual" = "$MOBY_DEFAULT_SHA256" ] || { echo "✗ $src is not moby v20.10.20's default profile (sha256 $actual)"; exit 1; }

jq --tab '
  def bubblewrap: {
    names: ["clone", "clone3", "mount", "pivot_root", "setns", "umount2", "unshare"],
    action: "SCMP_ACT_ALLOW",
    comment: "CodeSpace worker: the calls bubblewrap makes to build its user-namespace sandbox, which moby reserves for CAP_SYS_ADMIN (pivot_root it denies). See backend/Dockerfile.worker."
  };
  def replaced_by_bubblewrap: (.names == ["clone"] and .action == "SCMP_ACT_ALLOW" and (.args | length) > 0) or (.names == ["clone3"] and .action == "SCMP_ACT_ERRNO");
  def needs_a_capability: (.includes.caps // []) | length > 0;
  def unresolved: ((.excludes // {}) | length > 0) or ((.includes // {}) | keys - ["arches", "caps", "minKernel"] | length > 0);
  def oci_rule: {names, action} + (if .errnoRet then {errnoRet} else {} end) + (if (.args // []) | length > 0 then {args} else {} end);

  [.syscalls[] | select((replaced_by_bubblewrap or needs_a_capability) | not)] as $kept
  | if any($kept[]; unresolved) then error("a kept rule carries a condition this script does not resolve") else . end
  | {
      defaultAction,
      architectures: [.archMap[] | select(.architecture == "SCMP_ARCH_X86_64" or .architecture == "SCMP_ARCH_AARCH64") | .architecture, .subArchitectures[]],
      syscalls: ([$kept[0] | oci_rule, bubblewrap] + [$kept[1:][] | oci_rule])
    }
' "$src" > "$dst"

echo "✓ wrote $dst (sha256 $({ sha256sum "$dst" 2>/dev/null || shasum -a 256 "$dst"; } | cut -d' ' -f1))"
