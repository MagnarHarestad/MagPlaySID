namespace System.Windows.Forms;

/// <summary>WinForms' key codes, with WinForms' own values, so code that compares and subtracts them
/// (D0..D9, NumPad0..NumPad9) works unchanged.</summary>
[Flags]
public enum Keys
{
    KeyCode = 0x0000FFFF,
    Modifiers = unchecked((int)0xFFFF0000),
    None = 0,
    LButton = 1,
    RButton = 2,
    Cancel = 3,
    MButton = 4,
    Back = 8,
    Tab = 9,
    Clear = 12,
    Return = 13,
    Enter = 13,
    ShiftKey = 16,
    ControlKey = 17,
    Menu = 18,
    Pause = 19,
    CapsLock = 20,
    Capital = 20,
    Escape = 27,
    Space = 32,
    PageUp = 33,
    Prior = 33,
    PageDown = 34,
    Next = 34,
    End = 35,
    Home = 36,
    Left = 37,
    Up = 38,
    Right = 39,
    Down = 40,
    Insert = 45,
    Delete = 46,
    D0 = 48, D1, D2, D3, D4, D5, D6, D7, D8, D9,
    A = 65, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    LWin = 91,
    RWin = 92,
    Apps = 93,
    NumPad0 = 96, NumPad1, NumPad2, NumPad3, NumPad4, NumPad5, NumPad6, NumPad7, NumPad8, NumPad9,
    Multiply = 106,
    Add = 107,
    Separator = 108,
    Subtract = 109,
    Decimal = 110,
    Divide = 111,
    F1 = 112, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12, F13, F14, F15, F16, F17, F18, F19, F20, F21, F22, F23, F24,
    NumLock = 144,
    Scroll = 145,
    LShiftKey = 160,
    RShiftKey = 161,
    LControlKey = 162,
    RControlKey = 163,
    LMenu = 164,
    RMenu = 165,
    OemSemicolon = 186,
    Oem1 = 186,
    Oemplus = 187,
    Oemcomma = 188,
    OemMinus = 189,
    OemPeriod = 190,
    OemQuestion = 191,
    Oem2 = 191,
    Oemtilde = 192,
    Oem3 = 192,
    OemOpenBrackets = 219,
    Oem4 = 219,
    OemPipe = 220,
    Oem5 = 220,
    OemCloseBrackets = 221,
    Oem6 = 221,
    OemQuotes = 222,
    Oem7 = 222,
    Oem8 = 223,
    OemBackslash = 226,
    Oem102 = 226,
    Shift = 0x00010000,
    Control = 0x00020000,
    Alt = 0x00040000,
}

[Flags]
public enum MouseButtons
{
    None = 0,
    Left = 0x00100000,
    Right = 0x00200000,
    Middle = 0x00400000,
    XButton1 = 0x00800000,
    XButton2 = 0x01000000,
}

public enum Orientation
{
    Horizontal,
    Vertical,
}

public sealed class LayoutEventArgs(object? affectedComponent = null, string? affectedProperty = null) : EventArgs
{
    public object? AffectedComponent { get; } = affectedComponent;
    public string? AffectedProperty { get; } = affectedProperty;
}
