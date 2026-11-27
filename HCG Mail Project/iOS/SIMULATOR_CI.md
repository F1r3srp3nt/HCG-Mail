# Reliable iOS Simulator runs in CI: one simulator at a time, every stage time-limited

What HCG Mail learned getting its simulator jobs green on GitHub-hosted macOS runners, written so any HCG iOS
project can reuse it. The implementation is `scripts/run-simulator.sh`; the workflow is `.github/workflows/ios.yml`.

## What went wrong

| Run | Symptom | Real cause |
|---|---|---|
| 44932100847, 44953152990 | The iPad screenshot step made no progress for 58 min to over 2 h, then hit its time limit. The log ended at "Simulator: iPad Pro 13-inch (M5)". | The iPad was booting while two or three other simulators still ran on a 7 GB runner: the iPhone used for the self-test, its app, and **the device Simulator.app booted by itself when it was opened**. Boot and install were unbounded and their output was discarded, so the log could not say which stage hung. |
| 45058813099 | "launch timed out", although the app printed `HCG-SELFTEST: PASS` | `simctl launch --console-pty` does not print the `<bundle id>: <pid>` line a plain launch prints. The script waited for it. |
| 45063307048 | "find timed out" after 60 s | Listing the simulators took over 60 s at a load average of 541. Simulator.app was cold-booting its own default device ("iPhone 17") while we were about to boot "iPhone 17 Pro". |
| **45068033617** | **All jobs green** | One simulator at a time; Simulator.app opened only on the booted device; realistic limits. |

## The rules

1. **Only one simulator runs at a time.** Shut the others down before booting (`--exclusive`). A hosted runner has
   about 7 GB of memory and 3 cores. A second cold boot can stall the first for an hour. The same device booted
   alone took 86–214 s.
2. **Never open Simulator.app before your device has booted.** Opened first, it cold-boots its own default device.
   Open it afterwards, on that device:
   `open -a Simulator.app --args -CurrentDeviceUDID <udid>`.
3. **Reuse what is already booted.** Without an explicit device name, pick a booted or booting simulator of the
   right kind before booting another.
4. **Time-limit every stage separately** (find, shutdown-others, boot, install, launch, ready, screenshot):
   * Measure against the wall clock (`$SECONDS`), not loop iterations. On an overloaded machine `sleep 1` takes
     much longer than a second.
   * Print a progress line every minute, so a slow stage is visible while it runs.
   * Set each limit above the longest normal time seen, and the job step's limit above the sum of the stage
     limits. Then the script, not the runner, reports which stage stalled.
5. **Let the app say when it is ready.** Never wait a fixed number of seconds. The app prints a line, and the script
   waits for it:
   * `HCG-PREVIEW: started <screen>` as soon as it starts (`simctl launch --console-pty` gives no other sign);
   * `HCG-PREVIEW: ready <screen>` once the screen is really open (data loaded, view shown), or
     `HCG-PREVIEW: not ready … (why)`;
   * `HCG-SELFTEST: start` and `HCG-SELFTEST: PASS|FAIL` for the self-test.

   An app that exits before it is ready fails at once, not at the limit.
6. **On failure, report the evidence:**
   * the stage and its limit;
   * the stage's own output;
   * the simulator's state and the list of booted simulators;
   * load average and memory pressure;
   * the busiest simulator processes;
   * the device's `system.log`;
   * `CoreSimulator.log`, without the "device identity" errors that fill it on hosted runners.

   Each of these probes is itself time-limited, because CoreSimulator can be what is stuck.
7. **Clean up whatever happens.** An exit trap stops every process the script started, also on SIGINT/SIGTERM.
   A final `if: always()` step runs `xcrun simctl shutdown all`.
8. **Keep the logs.** Each stage's full output goes to `HCG_IOS_SIM_LOG_DIR`, uploaded as a workflow artifact.

## Timings seen on `macos-26` hosted runners (run 45068033617)

| Stage | iPhone 17 Pro (first boot in the job) | iPad Pro 13-inch (M5), alone | Limit |
|---|---|---|---|
| find (list simulators) | 2 s; ~100 s seen under load | 6 s | 300 s |
| boot | 142 s | 86–214 s | 1,500 s |
| install | 54 s | 19–98 s | 300 s |
| launch (until the app's start line) | 89 s first, then 12–18 s | 14–21 s | 180 s |
| ready (app reports the screen open) | 3–4 s | 3 s | 180 s |
| screenshot | 19 s | 16–19 s | 120 s |

All limits can be overridden by environment variables: `HCG_IOS_FIND_TIMEOUT`, `HCG_IOS_BOOT_TIMEOUT`,
`HCG_IOS_INSTALL_TIMEOUT`, `HCG_IOS_LAUNCH_TIMEOUT`, `HCG_IOS_READY_TIMEOUT` and `HCG_IOS_SCREENSHOT_TIMEOUT`.

## Reusing it in another HCG iOS project

1. Copy `scripts/run-simulator.sh` and the parts of `scripts/_common.sh` it uses:
   * `set -euo pipefail`;
   * `IOS_ROOT` (the project folder);
   * `BUNDLE_ID`;
   * `OUT_ROOT`;
   * `require_macos`;
   * `scripts/build-simulator.sh`, or always pass `HCG_IOS_APP=<path to the .app>`.
2. In the app, print the start and ready lines (rule 5) in your test or preview mode, with your own prefix, and
   change the two patterns in `launch_and_wait`.
   * HCG Mail prints them in `AppDelegate.cs` (`HCG-PREVIEW: started`) and `MobileApp.axaml.cs` (`ready`).
   * The self-test prints them in `SelfTest.cs`.
3. Workflow pattern: one step per device, each with its own limit and the log directory set.

   ```yaml
   - name: Screenshot on iPhone (the app reports the screen is open)
     timeout-minutes: 40            # > sum of the stage limits the device can hit
     env:
       HCG_IOS_SIM_LOG_DIR: ${{ github.workspace }}/artifacts/ios-sim-logs
     run: scripts/run-simulator.sh --exclusive --preview --screen inbox --screenshot ../artifacts/iphone.png
   - name: Screenshot on iPad
     timeout-minutes: 40
     env:
       HCG_IOS_SIM_LOG_DIR: ${{ github.workspace }}/artifacts/ios-sim-logs
     run: scripts/run-simulator.sh --ipad --exclusive --preview --screen inbox --screenshot ../artifacts/ipad.png
   - name: Shut down the simulators
     if: always()
     run: xcrun simctl shutdown all || true
   - uses: actions/upload-artifact@v4
     if: always()
     with: { name: simulator-logs, path: artifacts/ios-sim-logs, if-no-files-found: ignore }
   ```

4. If Xcode launches the app (an External Build System scheme), call the script with `--show` and do not open
   Simulator.app yourself (HCG Mail: `scripts/xcode-build.sh`).
5. Testing the script without a Mac: put stand-ins for `uname` (printing `Darwin`) and `xcrun` (a fake `simctl` that
   can hang or fail per stage) first on `PATH`. Then check that each stage times out, names itself, and leaves no
   process behind.
   * The stand-ins must behave like the real `simctl`. A stand-in that printed the pid line with `--console-pty`
     hid the bug found in run 45058813099.

## Reading a failure

| Last `HCG-SIM:` line | Look at | Usual cause |
|---|---|---|
| `find TIMED OUT` | load average, booted simulators | the machine is saturated, often by a second simulator (rules 1–2) |
| `boot TIMED OUT` | `<device>-boot.log` (bootstatus progress), booted simulators, `system.log` | another simulator booting; a runtime still installing on a fresh runner |
| `install TIMED OUT` / `FAILED` | `<device>-install.log` | a corrupt `.app`, too little disk space, or load |
| `launch TIMED OUT` | `<device>-app.log` | the app crashed before printing its start line, or it never started; check the device log for a crash |
| `ready TIMED OUT` / `FAILED` | `<device>-app.log` (`HCG-PREVIEW: not ready …` says why) | the screen did not open: data not loaded, or a UI regression |
| `screenshot TIMED OUT` | load | a saturated machine |
