# Platform support and limitations

Exile Lens never reads game memory, injects code, modifies game files, intercepts packets or automates
gameplay or trading. When you press a shortcut, it sends **one** copy keystroke to the focused game (the game's
own item-copy feature) and reads the clipboard. GGG does not certify third-party tools. Their staff have said
that a tool's status can change, so check the current
[third-party policy](https://www.pathofexile.com/forum/view-thread/2077975) yourself.

| Capability | Windows 11 | Linux X11 | Linux Wayland | macOS |
|---|---|---|---|---|
| Global shortcuts | ✅ Low-level keyboard hook; consumed only while the game is focused | ✅ `XGrabKey`, grabbed only while the game is the active window (event-driven) | ⚠️ Works while the game runs through **XWayland** (the default for Steam/Proton). Native-Wayland clients: bind `ExileLens --action price-check` / `--action map-check` in the compositor (KDE, GNOME and Hyprland custom shortcuts all work) | ✅ Quartz event tap; needs the **Accessibility** permission (prompted on first use) |
| Copy keystroke | `SendInput` Ctrl+C / Ctrl+Alt+C | XTest | XTest (XWayland game) | `CGEventPost` Cmd+C / Cmd+Option+C |
| Game window detection | Process name / title / `POEWindowClass` | `_NET_ACTIVE_WINDOW`, `WM_CLASS` (incl. `steam_app_<id>`), title | Same as X11 for XWayland windows | Frontmost application (`NSWorkspace`) |
| Overlay position | Cursor / game bounds, per-monitor DPI | Cursor / game bounds | Compositor may override position | Cursor (game bounds need Screen Recording permission, not requested) |
| Transparency & rounded corners | ✅ | Needs a compositing WM | ✅ | ✅ |
| Exclusive fullscreen | Best-effort: use windowed or borderless fullscreen | Borderless recommended | Borderless recommended | Overlays cannot appear over native fullscreen Spaces; use windowed mode |
| Auto-start | `HKCU\…\Run` | XDG autostart `.desktop` | XDG autostart | LaunchAgent plist |
| Packages | Installer (Inno Setup, per-user) + portable zip | AppImage + tar.gz | AppImage + tar.gz | `.app` in DMG + zip (ad-hoc signed; right-click → Open on first launch) |

## Notes

- **Steam/Proton/Wine.** Detection matches `PathOfExile*.exe`, `steam_app_238960` (PoE 1) and `steam_app_2694490`
  (PoE 2), plus the window titles.
- **Single instance.** A second launch forwards `--action …` or "show" to the running instance through a per-user
  named pipe and exits, so shortcuts are never registered twice.
- **Pure Wayland session without XWayland.** Global shortcuts, window detection and key injection are unavailable.
  The Price Check page still works when you paste item text, and map rules are still editable.
- **Click-outside closing.** This works when the overlay has been focused. An overlay that was never clicked cannot
  observe clicks in other windows. Enable "close when the game loses focus" or press Escape instead.
- **Game copy shortcut on macOS.** The assumed shortcut is Command+C. If your client uses Control+C, report it in
  an issue; the key mapping lives in `MacKeyboardInputService`.
