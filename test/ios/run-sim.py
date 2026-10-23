#!/usr/bin/env python3
"""Install an IBTest .app in a booted simulator and run each storyboard's self-test.

    run-sim.py <device-udid> <IBTest.app> [storyboard...]

Each run launches the app with `--storyboard <name>`, streams its console, and passes when the
app logs "IBTEST <name> ok" (lower-cased) within the timeout; "IBTEST FAIL" or a timeout fails.
"""
import subprocess, sys, threading, time

BUNDLE = "com.companyname.ibtest"
TIMEOUT = 60

def run(device, storyboard):
    subprocess.run(["xcrun", "simctl", "terminate", device, BUNDLE], capture_output=True)
    p = subprocess.Popen(["xcrun", "simctl", "launch", "--console-pty", "--terminate-running-process", device, BUNDLE, "--storyboard", storyboard],
                         stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    lines, done = [], threading.Event()
    def read():
        for line in p.stdout:
            lines.append(line.rstrip())
            if "IBTEST" in line and (f"{storyboard.lower()} ok" in line or "FAIL" in line):
                done.set()
        done.set()
    threading.Thread(target=read, daemon=True).start()
    done.wait(TIMEOUT)
    subprocess.run(["xcrun", "simctl", "terminate", device, BUNDLE], capture_output=True)
    p.kill()
    log = [l for l in lines if "IBTEST" in l or "xception" in l]
    print("\n".join(f"  {l[-200:]}" for l in log))
    return any(f"IBTEST {storyboard.lower()} ok" in l for l in log) and not any("IBTEST FAIL" in l for l in log)

def main():
    device, app, storyboards = sys.argv[1], sys.argv[2], sys.argv[3:] or ["Showcase", "Controls", "Tabbed", "Assets", "Gaps", "More"]
    subprocess.run(["xcrun", "simctl", "install", device, app], check=True)
    failed = []
    for sb in storyboards:
        print(f"== {sb}")
        if not run(device, sb):
            failed.append(sb)
    if failed:
        print("FAILED: " + ", ".join(failed))
        sys.exit(1)
    print("all storyboards ok")

if __name__ == "__main__":
    main()
