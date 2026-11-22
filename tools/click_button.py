"""按窗口标题找 WinForms 窗口，再按按钮文本点一下。仅用于本机自动化验证。"""
import sys, time
import win32gui, win32con

def children(hwnd):
    out = []
    def cb(h, _):
        out.append(h)
    win32gui.EnumChildWindows(hwnd, cb, None)
    return out

def main():
    title_part, btn_text = sys.argv[1], sys.argv[2]
    target = None
    def top(h, _):
        nonlocal target
        if win32gui.IsWindowVisible(h):
            t = win32gui.GetWindowText(h)
            if title_part in t:
                target = h
    win32gui.EnumWindows(top, None)
    if not target:
        print("WINDOW_NOT_FOUND"); return 2
    for h in children(target):
        t = win32gui.GetWindowText(h)
        if btn_text in t:
            win32gui.PostMessage(h, win32con.BM_CLICK, 0, 0)
            print("CLICKED", t); return 0
    print("BUTTON_NOT_FOUND"); return 3

sys.exit(main())
