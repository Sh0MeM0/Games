"""Run native converters without imposing a duration limit.

Communication happens once in a worker thread so even a large stdin write is
cancellable on Windows. The caller remains in its own thread and can report
progress or raise its normal cancellation exception.
"""

from __future__ import annotations

import os
from pathlib import Path
import subprocess
import threading
import time
from collections.abc import Callable, Sequence


_POLL_INTERVAL = 0.2
_HEARTBEAT_INTERVAL = 30.0
_TERMINATE_GRACE = 1.0


def _stop_process(process: subprocess.Popen) -> None:
    """Stop and reap a child, allowing a short grace period before killing it."""
    if process.poll() is None:
        try:
            process.terminate()
        except ProcessLookupError:
            pass  # The process finished between poll() and terminate().
        try:
            process.wait(timeout=_TERMINATE_GRACE)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait()
    else:
        process.wait()


def run_process(
    command: Sequence[str | os.PathLike],
    *,
    cwd: str | Path | None = None,
    input_data: bytes | None = None,
    check_cancel: Callable[[], None] | None = None,
    heartbeat: Callable[[float], None] | None = None,
    timeout: float | None = None,
) -> subprocess.CompletedProcess:
    """Return a completed command with byte stdout/stderr, without a shell.

    By default there is no execution time limit. ``check_cancel`` runs at
    least every polling interval while waiting and should raise to cancel;
    its exception is propagated after the child is stopped and reaped.
    ``heartbeat`` receives elapsed seconds every 30 seconds. Its exceptions
    receive the same cleanup. A nonzero child exit code is returned normally.

    ``timeout`` is intended for short capability queries, not conversions.
    Expiry raises ``subprocess.TimeoutExpired`` with any captured output.
    """
    if check_cancel is not None:
        check_cancel()
    creationflags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
    started = time.monotonic()
    finished = threading.Event()
    captured: dict = {}
    with subprocess.Popen(
        command,
        cwd=cwd,
        stdin=subprocess.PIPE if input_data is not None else subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        creationflags=creationflags,
    ) as process:

        def communicate() -> None:
            try:
                captured["stdout"], captured["stderr"] = process.communicate(input_data)
            except BaseException as error:
                captured["error"] = error
            finally:
                finished.set()

        reader = threading.Thread(target=communicate, name="pdf-master-process")
        reader.start()
        next_heartbeat = _HEARTBEAT_INTERVAL
        try:
            while True:
                if check_cancel is not None:
                    check_cancel()
                elapsed = time.monotonic() - started
                if finished.is_set():
                    break
                if timeout is not None and elapsed >= timeout:
                    raise subprocess.TimeoutExpired(command, timeout)
                if heartbeat is not None and elapsed >= next_heartbeat:
                    heartbeat(elapsed)
                    next_heartbeat = elapsed + _HEARTBEAT_INTERVAL
                wait_for = _POLL_INTERVAL
                if timeout is not None:
                    wait_for = min(wait_for, max(0.0, timeout - elapsed))
                finished.wait(wait_for)
            if "error" in captured:
                raise captured["error"]
        except BaseException as error:
            _stop_process(process)
            reader.join()
            if isinstance(error, subprocess.TimeoutExpired):
                error.output = captured.get("stdout", b"")
                error.stderr = captured.get("stderr", b"")
            raise
        finally:
            reader.join()
        return subprocess.CompletedProcess(
            command, process.returncode, captured["stdout"], captured["stderr"]
        )
