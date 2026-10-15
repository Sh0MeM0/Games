import os
from pathlib import Path
import signal
import subprocess
import sys
import time

import pytest

from pdf2office import process


def child(code):
    return [sys.executable, "-c", code]


@pytest.fixture
def fast_poll(monkeypatch):
    monkeypatch.setattr(process, "_POLL_INTERVAL", 0.01)
    monkeypatch.setattr(process, "_HEARTBEAT_INTERVAL", 0.03)
    monkeypatch.setattr(process, "_TERMINATE_GRACE", 0.1)


@pytest.fixture
def children(monkeypatch):
    """Track real children and pipes without replacing their behavior."""
    started = []
    original = subprocess.Popen

    def tracked(*args, **kwargs):
        result = original(*args, **kwargs)
        started.append(result)
        return result

    monkeypatch.setattr(process.subprocess, "Popen", tracked)
    return started


def assert_reaped(children):
    assert len(children) == 1
    result = children[0]
    assert result.returncode is not None
    assert result.stdout.closed and result.stderr.closed
    if result.stdin is not None:
        assert result.stdin.closed
    if os.name != "nt":
        with pytest.raises(ChildProcessError):
            os.waitpid(result.pid, os.WNOHANG)


def test_unlimited_wait_keeps_output_and_sends_input_once(fast_poll, children, tmp_path):
    payload = bytes(range(256)) * 8192
    beats = []
    result = process.run_process(
        child("import sys,time; data=sys.stdin.buffer.read(); time.sleep(0.12); "
              "sys.stdout.buffer.write(data); sys.stderr.buffer.write(b'error\\xff'); "
              "sys.exit(7)"),
        cwd=tmp_path,
        input_data=payload,
        heartbeat=beats.append,
    )
    assert result.stdout == payload
    assert result.stderr == b"error\xff"
    assert result.returncode == 7
    assert len(beats) >= 2
    assert beats == sorted(beats)
    assert_reaped(children)


def test_cancellation_preserves_exception_and_reaps_child(fast_poll, children):
    class Cancelled(Exception):
        pass

    failure = Cancelled("User cancelled")
    started = time.monotonic()

    def cancel():
        if time.monotonic() - started >= 0.08:
            raise failure

    with pytest.raises(Cancelled) as caught:
        process.run_process(child("import time; time.sleep(10)"), check_cancel=cancel)
    assert caught.value is failure
    assert time.monotonic() - started < 2
    assert_reaped(children)


def test_cancel_large_stdin_when_child_never_reads_it(fast_poll, children):
    started = time.monotonic()

    def cancel():
        if time.monotonic() - started >= 0.08:
            raise InterruptedError("Stop")

    with pytest.raises(InterruptedError, match="Stop"):
        process.run_process(
            child("import time; time.sleep(10)"),
            input_data=b"X" * (4 * 1024 * 1024),
            check_cancel=cancel,
        )
    assert time.monotonic() - started < 2
    assert_reaped(children)


def test_heartbeat_failure_stops_child_and_propagates(fast_poll, children):
    failure = ValueError("Progress display failed")

    def heartbeat(elapsed):
        assert elapsed >= process._HEARTBEAT_INTERVAL
        raise failure

    with pytest.raises(ValueError) as caught:
        process.run_process(child("import time; time.sleep(10)"), heartbeat=heartbeat)
    assert caught.value is failure
    assert_reaped(children)


def test_explicit_timeout_keeps_captured_output(fast_poll, children):
    with pytest.raises(subprocess.TimeoutExpired) as caught:
        process.run_process(
            child("import sys,time; print('Started',flush=True); "
                  "print('Details',file=sys.stderr,flush=True); time.sleep(10)"),
            timeout=0.15,
        )
    assert caught.value.output == b"Started\n" or caught.value.output == b"Started\r\n"
    assert caught.value.stderr == b"Details\n" or caught.value.stderr == b"Details\r\n"
    assert_reaped(children)


@pytest.mark.skipif(os.name == "nt", reason="POSIX signal-handler behavior")
def test_child_ignoring_terminate_is_killed_and_reaped(fast_poll, children, tmp_path):
    ready = tmp_path / "ready"
    started = time.monotonic()

    def cancel():
        if ready.exists():
            raise InterruptedError("Stop")
        assert time.monotonic() - started < 2

    with pytest.raises(InterruptedError, match="Stop"):
        process.run_process(
            child("import pathlib,signal,time; "
                  "signal.signal(signal.SIGTERM, signal.SIG_IGN); "
                  f"pathlib.Path({str(ready)!r}).touch(); time.sleep(10)"),
            check_cancel=cancel,
        )
    assert children[0].returncode == -signal.SIGKILL
    assert_reaped(children)


def test_cancelled_before_spawn_does_not_start_child(children):
    def cancel():
        raise InterruptedError("Already cancelled")

    with pytest.raises(InterruptedError, match="Already cancelled"):
        process.run_process(child("raise AssertionError('Must not start')"), check_cancel=cancel)
    assert children == []


def test_cwd_and_literal_arguments_are_not_interpreted_by_shell(tmp_path):
    argument = "$(echo unexpected); & literal"
    result = process.run_process(
        child("import os,sys; print(os.getcwd()); print(sys.argv[1])") + [argument],
        cwd=tmp_path,
    )
    lines = result.stdout.decode().splitlines()
    assert Path(lines[0]) == tmp_path
    assert lines[1] == argument


def test_missing_executable_propagates_os_error(tmp_path):
    with pytest.raises(FileNotFoundError):
        process.run_process([str(tmp_path / "missing-converter")])
