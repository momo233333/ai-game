from __future__ import annotations

import ctypes
import sys
import threading
from ctypes import wintypes

WH_KEYBOARD_LL = 13
WM_KEYDOWN = 0x0100
WM_KEYUP = 0x0101
WM_SYSKEYDOWN = 0x0104
WM_SYSKEYUP = 0x0105
WM_QUIT = 0x0012
VK_SPACE = 0x20
VK_CONTROL, VK_LCONTROL, VK_RCONTROL = 0x11, 0xA2, 0xA3
KEY_SETS = {
    "space": {VK_SPACE},
    "ctrl": {VK_CONTROL, VK_LCONTROL, VK_RCONTROL},
}
KEY_LABELS = {"space": "空格", "ctrl": "Ctrl"}


def ptt_label(key: str) -> str:
    return KEY_LABELS.get(key, key)


class KBDLLHOOKSTRUCT(ctypes.Structure):
    _fields_ = [
        ("vkCode", wintypes.DWORD),
        ("scanCode", wintypes.DWORD),
        ("flags", wintypes.DWORD),
        ("time", wintypes.DWORD),
        ("dwExtraInfo", ctypes.c_size_t),
    ]


class PttListener:
    def __init__(self, key: str = "ctrl"):
        self.key = key if key in KEY_SETS else "ctrl"
        self._vks = KEY_SETS[self.key]
        self._lock = threading.Lock()
        self._down = False
        self._stop = threading.Event()
        self._ready: threading.Event | None = None
        self._thread: threading.Thread | None = None
        self._thread_id: int | None = None
        self._hook = None
        self._callback = None
        self._error: str | None = None

    def held(self) -> bool:
        with self._lock:
            return self._down

    def _handle(self, vk: int, pressed: bool) -> None:
        if vk in self._vks:
            with self._lock:
                self._down = pressed

    def start(self) -> "PttListener":
        if sys.platform != "win32":
            raise RuntimeError("push-to-talk requires Windows")
        if self._thread and self._thread.is_alive():
            return self
        self._error = None
        self._stop.clear()
        self._ready = threading.Event()
        self._thread = threading.Thread(target=self._run, args=(self._ready,), daemon=True)
        self._thread.start()
        self._ready.wait(5.0)
        if self._error:
            raise RuntimeError(f"keyboard hook failed: {self._error}")
        return self

    def stop(self) -> None:
        self._stop.set()
        if self._thread_id:
            try:
                ctypes.windll.user32.PostThreadMessageW(self._thread_id, WM_QUIT, 0, 0)
            except Exception:
                pass
        if self._thread:
            self._thread.join(timeout=2.0)
        with self._lock:
            self._down = False

    def __enter__(self) -> "PttListener":
        return self.start()

    def __exit__(self, exc_type, exc, tb) -> bool:
        self.stop()
        return False

    def _run(self, ready: threading.Event) -> None:
        user32 = ctypes.windll.user32
        kernel32 = ctypes.windll.kernel32
        try:
            user32.SetWindowsHookExW.argtypes = [
                ctypes.c_int,
                ctypes.c_void_p,
                wintypes.HINSTANCE,
                wintypes.DWORD,
            ]
            user32.SetWindowsHookExW.restype = ctypes.c_void_p
            user32.CallNextHookEx.argtypes = [
                ctypes.c_void_p,
                ctypes.c_int,
                ctypes.c_size_t,
                ctypes.c_size_t,
            ]
            user32.CallNextHookEx.restype = ctypes.c_ssize_t
            user32.UnhookWindowsHookEx.argtypes = [ctypes.c_void_p]
            user32.UnhookWindowsHookEx.restype = wintypes.BOOL
            kernel32.GetModuleHandleW.argtypes = [wintypes.LPCWSTR]
            kernel32.GetModuleHandleW.restype = wintypes.HMODULE
            kernel32.GetCurrentThreadId.argtypes = []
            kernel32.GetCurrentThreadId.restype = wintypes.DWORD

            self._thread_id = kernel32.GetCurrentThreadId()
            cmpfunc = ctypes.WINFUNCTYPE(
                ctypes.c_ssize_t, ctypes.c_int, ctypes.c_size_t, ctypes.c_size_t
            )

            def _proc(n_code: int, w_param: int, l_param: int):
                if n_code >= 0:
                    try:
                        kb = ctypes.cast(l_param, ctypes.POINTER(KBDLLHOOKSTRUCT)).contents
                        pressed = w_param in (WM_KEYDOWN, WM_SYSKEYDOWN)
                        self._handle(int(kb.vkCode), pressed)
                    except Exception:
                        pass
                return user32.CallNextHookEx(self._hook, n_code, w_param, l_param)

            self._callback = cmpfunc(_proc)
            hook_mod = kernel32.GetModuleHandleW(None)
            self._hook = user32.SetWindowsHookExW(WH_KEYBOARD_LL, self._callback, hook_mod, 0)
            if not self._hook:
                self._error = f"SetWindowsHookExW error {ctypes.GetLastError()}"
                ready.set()
                return
            ready.set()
            msg = wintypes.MSG()
            while not self._stop.is_set():
                ret = user32.GetMessageW(ctypes.byref(msg), None, 0, 0)
                if ret <= 0:
                    break
        except Exception as exc:  # pragma: no cover - defensive
            self._error = str(exc)
            if ready:
                ready.set()
        finally:
            if self._hook:
                try:
                    user32.UnhookWindowsHookEx(self._hook)
                except Exception:
                    pass
                self._hook = None
            with self._lock:
                self._down = False
            if not ready.is_set():
                ready.set()
