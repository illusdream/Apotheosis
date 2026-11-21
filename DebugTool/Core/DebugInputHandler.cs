#if DEBUG
#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Numerics;
using Hexa.NET.ImGui;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.GameInput;
using Terraria.ModLoader;

namespace Apotheosis.DebugTool.Core
{
    /// <summary>
    /// FNA 原生键鼠与文本输入到 Dear ImGui 的桥接器，同时负责 <c>Ctrl + `</c> 热键检测与泰拉瑞亚输入隔离。
    /// </summary>
    [NoJIT]
    internal sealed class DebugInputHandler : IDisposable
    {
        // 注意：字段类型不直接引用 ImGuiKey 枚举，避免 tML 加载期 GetLoadableTypes() 提前解析 Hexa.NET.ImGui
        private static (Keys FnaKey, int ImKeyRaw)[]? _keyMap;

        private static (Keys FnaKey, int ImKeyRaw)[] GetKeyMap() => _keyMap ??= new[]
        {
            (Keys.Tab, (int)ImGuiKey.Tab),
            (Keys.Left, (int)ImGuiKey.LeftArrow),
            (Keys.Right, (int)ImGuiKey.RightArrow),
            (Keys.Up, (int)ImGuiKey.UpArrow),
            (Keys.Down, (int)ImGuiKey.DownArrow),
            (Keys.PageUp, (int)ImGuiKey.PageUp),
            (Keys.PageDown, (int)ImGuiKey.PageDown),
            (Keys.Home, (int)ImGuiKey.Home),
            (Keys.End, (int)ImGuiKey.End),
            (Keys.Insert, (int)ImGuiKey.Insert),
            (Keys.Delete, (int)ImGuiKey.Delete),
            (Keys.Back, (int)ImGuiKey.Backspace),
            (Keys.Space, (int)ImGuiKey.Space),
            (Keys.Enter, (int)ImGuiKey.Enter),
            (Keys.Escape, (int)ImGuiKey.Escape),
            (Keys.LeftControl, (int)ImGuiKey.LeftCtrl),
            (Keys.RightControl, (int)ImGuiKey.RightCtrl),
            (Keys.LeftShift, (int)ImGuiKey.LeftShift),
            (Keys.RightShift, (int)ImGuiKey.RightShift),
            (Keys.LeftAlt, (int)ImGuiKey.LeftAlt),
            (Keys.RightAlt, (int)ImGuiKey.RightAlt),
            (Keys.LeftWindows, (int)ImGuiKey.LeftSuper),
            (Keys.RightWindows, (int)ImGuiKey.RightSuper),
            (Keys.Apps, (int)ImGuiKey.Menu),
            (Keys.D0, (int)ImGuiKey.Key0), (Keys.D1, (int)ImGuiKey.Key1), (Keys.D2, (int)ImGuiKey.Key2),
            (Keys.D3, (int)ImGuiKey.Key3), (Keys.D4, (int)ImGuiKey.Key4), (Keys.D5, (int)ImGuiKey.Key5),
            (Keys.D6, (int)ImGuiKey.Key6), (Keys.D7, (int)ImGuiKey.Key7), (Keys.D8, (int)ImGuiKey.Key8),
            (Keys.D9, (int)ImGuiKey.Key9),
            (Keys.A, (int)ImGuiKey.A), (Keys.B, (int)ImGuiKey.B), (Keys.C, (int)ImGuiKey.C), (Keys.D, (int)ImGuiKey.D),
            (Keys.E, (int)ImGuiKey.E), (Keys.F, (int)ImGuiKey.F), (Keys.G, (int)ImGuiKey.G), (Keys.H, (int)ImGuiKey.H),
            (Keys.I, (int)ImGuiKey.I), (Keys.J, (int)ImGuiKey.J), (Keys.K, (int)ImGuiKey.K), (Keys.L, (int)ImGuiKey.L),
            (Keys.M, (int)ImGuiKey.M), (Keys.N, (int)ImGuiKey.N), (Keys.O, (int)ImGuiKey.O), (Keys.P, (int)ImGuiKey.P),
            (Keys.Q, (int)ImGuiKey.Q), (Keys.R, (int)ImGuiKey.R), (Keys.S, (int)ImGuiKey.S), (Keys.T, (int)ImGuiKey.T),
            (Keys.U, (int)ImGuiKey.U), (Keys.V, (int)ImGuiKey.V), (Keys.W, (int)ImGuiKey.W), (Keys.X, (int)ImGuiKey.X),
            (Keys.Y, (int)ImGuiKey.Y), (Keys.Z, (int)ImGuiKey.Z),
            (Keys.F1, (int)ImGuiKey.F1), (Keys.F2, (int)ImGuiKey.F2), (Keys.F3, (int)ImGuiKey.F3), (Keys.F4, (int)ImGuiKey.F4),
            (Keys.F5, (int)ImGuiKey.F5), (Keys.F6, (int)ImGuiKey.F6), (Keys.F7, (int)ImGuiKey.F7), (Keys.F8, (int)ImGuiKey.F8),
            (Keys.F9, (int)ImGuiKey.F9), (Keys.F10, (int)ImGuiKey.F10), (Keys.F11, (int)ImGuiKey.F11), (Keys.F12, (int)ImGuiKey.F12),
            (Keys.OemQuotes, (int)ImGuiKey.Apostrophe),
            (Keys.OemComma, (int)ImGuiKey.Comma),
            (Keys.OemMinus, (int)ImGuiKey.Minus),
            (Keys.OemPeriod, (int)ImGuiKey.Period),
            (Keys.OemQuestion, (int)ImGuiKey.Slash),
            (Keys.OemSemicolon, (int)ImGuiKey.Semicolon),
            (Keys.OemPlus, (int)ImGuiKey.Equal),
            (Keys.OemOpenBrackets, (int)ImGuiKey.LeftBracket),
            (Keys.OemPipe, (int)ImGuiKey.Backslash),
            (Keys.OemCloseBrackets, (int)ImGuiKey.RightBracket),
            (Keys.OemTilde, (int)ImGuiKey.GraveAccent),
            (Keys.CapsLock, (int)ImGuiKey.CapsLock),
            (Keys.Scroll, (int)ImGuiKey.ScrollLock),
            (Keys.NumLock, (int)ImGuiKey.NumLock),
            (Keys.PrintScreen, (int)ImGuiKey.PrintScreen),
            (Keys.Pause, (int)ImGuiKey.Pause),
            (Keys.NumPad0, (int)ImGuiKey.Keypad0), (Keys.NumPad1, (int)ImGuiKey.Keypad1),
            (Keys.NumPad2, (int)ImGuiKey.Keypad2), (Keys.NumPad3, (int)ImGuiKey.Keypad3),
            (Keys.NumPad4, (int)ImGuiKey.Keypad4), (Keys.NumPad5, (int)ImGuiKey.Keypad5),
            (Keys.NumPad6, (int)ImGuiKey.Keypad6), (Keys.NumPad7, (int)ImGuiKey.Keypad7),
            (Keys.NumPad8, (int)ImGuiKey.Keypad8), (Keys.NumPad9, (int)ImGuiKey.Keypad9),
            (Keys.Decimal, (int)ImGuiKey.KeypadDecimal),
            (Keys.Divide, (int)ImGuiKey.KeypadDivide),
            (Keys.Multiply, (int)ImGuiKey.KeypadMultiply),
            (Keys.Subtract, (int)ImGuiKey.KeypadSubtract),
            (Keys.Add, (int)ImGuiKey.KeypadAdd),
        };

        private readonly ConcurrentQueue<char> _pendingChars = new();
        private bool _prevTildeDown;
        private int _prevScrollWheel;
        private bool _scrollInitialized;
        private bool _textInputSubscribed;
        private bool _weStartedTextInput;
        private bool _weBlockedGameInput;
        private bool _panelVisible;
        private bool _disposed;

        public bool WantCaptureMouse { get; private set; }
        public bool WantCaptureKeyboard { get; private set; }
        public bool WantTextInput { get; private set; }
        public Vector2 LastMousePos { get; private set; }

        public void Attach()
        {
            if (_textInputSubscribed)
                return;

            TextInputEXT.TextInput += OnTextInput;
            _textInputSubscribed = true;
        }

        /// <summary>
        /// 检测 <c>Ctrl + `</c> 切换热键（仅在游戏窗口激活时触发上升沿）。
        /// </summary>
        public bool PollToggleHotkey()
        {
            if (Main.instance is null || !Main.instance.IsActive)
            {
                _prevTildeDown = false;
                return false;
            }

            KeyboardState kb = Keyboard.GetState();
            bool ctrlDown = kb.IsKeyDown(Keys.LeftControl) || kb.IsKeyDown(Keys.RightControl);
            bool tildeDown = kb.IsKeyDown(Keys.OemTilde);

            bool triggered = ctrlDown && tildeDown && !_prevTildeDown;
            _prevTildeDown = tildeDown;
            return triggered;
        }

        /// <summary>
        /// 将当前帧 FNA 键鼠与文本事件写入 <see cref="ImGuiIOPtr"/>。
        /// </summary>
        public void UpdateImGuiInput(ImGuiIOPtr io, bool isVisible, int backBufferWidth, int backBufferHeight)
        {
            _panelVisible = isVisible;

            if (!isVisible || Main.instance is null || !Main.instance.IsActive)
            {
                // 清空未消费字符并恢复文本输入状态
                while (_pendingChars.TryDequeue(out _)) { }
                SyncTextInputActive(false);

                WantCaptureMouse = false;
                WantCaptureKeyboard = false;
                WantTextInput = false;
                _scrollInitialized = false;
                return;
            }

            MouseState mouse = Mouse.GetState();
            KeyboardState kb = Keyboard.GetState();

            // 鼠标坐标从窗口客户区换算至 BackBuffer 像素坐标（不受 Main.UIScale 影响）
            float mouseX = mouse.X;
            float mouseY = mouse.Y;
            var bounds = Main.instance.Window.ClientBounds;
            if (bounds.Width > 0 && bounds.Height > 0 &&
                (bounds.Width != backBufferWidth || bounds.Height != backBufferHeight))
            {
                mouseX = mouse.X * ((float)backBufferWidth / bounds.Width);
                mouseY = mouse.Y * ((float)backBufferHeight / bounds.Height);
            }

            LastMousePos = new Vector2(mouseX, mouseY);
            io.AddMousePosEvent(mouseX, mouseY);

            io.AddMouseButtonEvent(0, mouse.LeftButton == ButtonState.Pressed);
            io.AddMouseButtonEvent(1, mouse.RightButton == ButtonState.Pressed);
            io.AddMouseButtonEvent(2, mouse.MiddleButton == ButtonState.Pressed);
            io.AddMouseButtonEvent(3, mouse.XButton1 == ButtonState.Pressed);
            io.AddMouseButtonEvent(4, mouse.XButton2 == ButtonState.Pressed);

            if (!_scrollInitialized)
            {
                _prevScrollWheel = mouse.ScrollWheelValue;
                _scrollInitialized = true;
            }
            else
            {
                int wheelDelta = mouse.ScrollWheelValue - _prevScrollWheel;
                _prevScrollWheel = mouse.ScrollWheelValue;
                if (wheelDelta != 0)
                    io.AddMouseWheelEvent(0f, wheelDelta / 120f);
            }

            bool ctrl = kb.IsKeyDown(Keys.LeftControl) || kb.IsKeyDown(Keys.RightControl);
            bool shift = kb.IsKeyDown(Keys.LeftShift) || kb.IsKeyDown(Keys.RightShift);
            bool alt = kb.IsKeyDown(Keys.LeftAlt) || kb.IsKeyDown(Keys.RightAlt);
            bool super = kb.IsKeyDown(Keys.LeftWindows) || kb.IsKeyDown(Keys.RightWindows);

            io.AddKeyEvent(ImGuiKey.ModCtrl, ctrl);
            io.AddKeyEvent(ImGuiKey.ModShift, shift);
            io.AddKeyEvent(ImGuiKey.ModAlt, alt);
            io.AddKeyEvent(ImGuiKey.ModSuper, super);

            var keyMap = GetKeyMap();
            for (int i = 0; i < keyMap.Length; i++)
            {
                var (fnaKey, imKeyRaw) = keyMap[i];
                io.AddKeyEvent((ImGuiKey)imKeyRaw, kb.IsKeyDown(fnaKey));
            }

            while (_pendingChars.TryDequeue(out char ch))
            {
                if (ch >= ' ' && ch != 127)
                    io.AddInputCharacter(ch);
            }

            WantCaptureMouse = io.WantCaptureMouse;
            WantCaptureKeyboard = io.WantCaptureKeyboard;
            WantTextInput = io.WantTextInput;

            SyncTextInputActive(WantTextInput);
        }

        /// <summary>
        /// 当面板捕获键鼠时，隔离泰拉瑞亚原版输入，防止点面板时挥舞武器、切物品栏或触发移动快捷键。
        /// </summary>
        public void ApplyTerrariaInputIsolation(bool isVisible)
        {
            if (!isVisible)
            {
                ReleaseTerrariaInputLock();
                return;
            }

            if (WantCaptureMouse)
            {
                if (!Main.gameMenu && Main.myPlayer >= 0 && Main.myPlayer < Main.player.Length)
                {
                    Player? localPlayer = Main.player[Main.myPlayer];
                    if (localPlayer is not null)
                        localPlayer.mouseInterface = true;
                }

                Main.mouseLeft = false;
                Main.mouseRight = false;
                Main.mouseLeftRelease = true;
                Main.mouseRightRelease = true;
                PlayerInput.ScrollWheelDelta = 0;
                PlayerInput.ScrollWheelDeltaForUI = 0;
            }

            if (WantCaptureKeyboard)
            {
                Main.blockInput = true;
                _weBlockedGameInput = true;
            }
            else if (_weBlockedGameInput)
            {
                Main.blockInput = false;
                _weBlockedGameInput = false;
            }
        }

        public void ReleaseTerrariaInputLock()
        {
            if (_weBlockedGameInput)
            {
                Main.blockInput = false;
                _weBlockedGameInput = false;
            }

            SyncTextInputActive(false);
        }

        private void OnTextInput(char c)
        {
            if (!_panelVisible || !WantTextInput)
                return;

            // 忽略 Ctrl+` 自身产生的反引号
            if (c == '`' && (Keyboard.GetState().IsKeyDown(Keys.LeftControl) || Keyboard.GetState().IsKeyDown(Keys.RightControl)))
                return;

            _pendingChars.Enqueue(c);
        }

        private void SyncTextInputActive(bool wantTextInput)
        {
            if (wantTextInput)
            {
                if (!TextInputEXT.IsTextInputActive())
                {
                    TextInputEXT.StartTextInput();
                    _weStartedTextInput = true;
                }
            }
            else if (_weStartedTextInput)
            {
                if (!Main.drawingPlayerChat && TextInputEXT.IsTextInputActive())
                    TextInputEXT.StopTextInput();

                _weStartedTextInput = false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            ReleaseTerrariaInputLock();

            if (_textInputSubscribed)
            {
                TextInputEXT.TextInput -= OnTextInput;
                _textInputSubscribed = false;
            }
        }
    }
}
#endif
